using System.Threading;

namespace BrainstormBuddy.Ai;

/// <summary>
/// Раздельный учёт LLM-расхода (§8.1 аудита 15.10).
/// <list type="bullet">
/// <item><b>Встреча</b> — траты на ответы пользователю (ручные вопросы, речь, MCP).</item>
/// <item><b>Фон</b> — «Документы и память»: извлечение фактов, консолидация памяти,
/// сжатие контекста встречи. Это НЕ траты пользователя на ответы, и путать счётчики нельзя.</item>
/// </list>
/// Контракт вызова: ОДИН вызов <see cref="Add"/> на ОДИН итоговый <see cref="AskResult"/>.
/// OpenAiClient аккумулирует usage внутренних ретраев (пустой ответ, SSE→буфер) в этот
/// результат, поэтому каждый реальный HTTP-запрос с usage-блоком учтён ровно однажды;
/// повторы фоновых джоб — отдельные реальные запросы, и каждый учитывается один раз.
/// <para>
/// Привязка к встрече: <see cref="Add"/> с непустым meetingKey, который
/// не равен ключу текущей встречи (<see cref="BeginMeeting"/> / <see cref="Reset(string)"/>),
/// попадает в фон, а не в счётчик встречи. Поздний spend старой встречи (ask в полёте на
/// момент смены чата или фоновое задание, доехавшее после сброса) не должен надувать
/// «Потрачено за встречу» новой встречи.
/// </para>
/// <para>
/// Нули при <see cref="AskResult.UsageReported"/> == false — это «провайдер не сообщил»,
/// а не «потрачено ноль»: взводится <see cref="UsageUnknown"/>, и UI показывает
/// «расход недоступен» вместо ложного нуля.
/// </para>
/// </summary>
public sealed class TokenLedger
{
    private int _meetingPrompt, _meetingCompletion, _bgPrompt, _bgCompletion;
    private int _usageUnknown;
    // Ключ активной встречи (memory-встреча): spend ask'а, стартовавшего в ней,
    // попадает в счётчик встречи только пока ключ совпадает (см. Add).
    private string? _currentMeetingKey;

    public int MeetingPromptTokens => Volatile.Read(ref _meetingPrompt);
    public int MeetingCompletionTokens => Volatile.Read(ref _meetingCompletion);
    public int MeetingTokens => MeetingPromptTokens + MeetingCompletionTokens;
    public int BackgroundPromptTokens => Volatile.Read(ref _bgPrompt);
    public int BackgroundCompletionTokens => Volatile.Read(ref _bgCompletion);
    public int BackgroundTokens => BackgroundPromptTokens + BackgroundCompletionTokens;
    /// <summary>Провайдер не сообщил usage хотя бы раз (ответы при этом были).</summary>
    public bool UsageUnknown => Volatile.Read(ref _usageUnknown) != 0;
    /// <summary>Счётчики показывать как «расход недоступен»: usage неизвестен, а цифр нет.</summary>
    public bool UsageNotReported => UsageUnknown && MeetingTokens == 0 && BackgroundTokens == 0;

    /// <summary>Новая встреча: сброс счётчиков и ключа активной встречи.</summary>
    /// <param name="key">Ключ НОВОЙ встречи (если известен сразу): поздний spend старой
    /// уйдёт в фон, а не в счётчик новой.</param>
    public void Reset(string? key = null)
    {
        Volatile.Write(ref _meetingPrompt, 0);
        Volatile.Write(ref _meetingCompletion, 0);
        Volatile.Write(ref _bgPrompt, 0);
        Volatile.Write(ref _bgCompletion, 0);
        Volatile.Write(ref _usageUnknown, 0);
        Volatile.Write(ref _currentMeetingKey, key);
    }

    /// <summary>Смена активной встречи без сброса счётчиков (новая память-встреча
    /// началась в том же сеансе приложения). null — учёт без привязки
    /// (память выключена / встреча не создана).</summary>
    public void BeginMeeting(string? key) => Volatile.Write(ref _currentMeetingKey, key);

    /// <summary>
    /// Учесть расход одного реального запроса. <paramref name="background"/> = true →
    /// фон («Документы и память»), иначе — счётчик встречи.
    /// <paramref name="meetingKey"/> — встреча, которой принадлежит spend (null — без
    /// привязки, память выключена/встреча ещё не создана). Если ключ не совпадает с
    /// активной встречью, расход уходит в фон: ask в полёте на момент смены чата или
    /// фоновое задание, доехавшее после сброса, не должны надувать «Потрачено за встречу».
    /// Возвращает false, если запрос не потратил токенов (отмена/ошибка): вызывающий
    /// код может не дёргать дисплей.
    /// </summary>
    public bool Add(AskResult? res, bool background = false, string? meetingKey = null)
    {
        if (res == null) return false;
        var prompt = res.PromptTokens;
        var completion = res.CompletionTokens;
        // R4 (аудит 2026-10-16): spend пришёл за НЕтекущую встречу — ручной ask стартовал
        // до смены чата, а ответ вернулся после; либо фоновое задание доехало после
        // сброса. Счётчик текущей встречи такую трату не увеличивает, но фон/итог
        // сохраняют её (провайдер реально списала за оба запроса).
        var staleMeeting = meetingKey != null &&
            !string.Equals(Volatile.Read(ref _currentMeetingKey), meetingKey, StringComparison.Ordinal);
        if (!res.UsageReported && prompt == 0 && completion == 0)
        {
            // Ответ/ошибка без usage-блока: цифр нет — честный NA, не «0 потрачено».
            Volatile.Write(ref _usageUnknown, 1);
            return false;
        }
        if (!res.UsageReported)
        {
            // На всякий случай: usage не сообщён, но токены есть — считаем их и
            // всё равно помечаем, что часть данных «не сообщена».
            Volatile.Write(ref _usageUnknown, 1);
        }
        if (background || staleMeeting)
        {
            if (prompt != 0) Interlocked.Add(ref _bgPrompt, prompt);
            if (completion != 0) Interlocked.Add(ref _bgCompletion, completion);
        }
        else
        {
            if (prompt != 0) Interlocked.Add(ref _meetingPrompt, prompt);
            if (completion != 0) Interlocked.Add(ref _meetingCompletion, completion);
        }
        return prompt != 0 || completion != 0;
    }
}
