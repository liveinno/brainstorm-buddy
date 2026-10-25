using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Context;

/// <summary>
/// Оценка входного бюджета LLM-контекста по модели. Лимит берём из известных
/// профилей провайдера; неизвестная модель → консервативные 32768 (спека
/// контекста встречи: при неизвестном лимите — оценка 32768).
/// </summary>
public static class ContextBudgetResolver
{
    public const int UnknownModelLimit = 32768;

    /// <summary>Известные модели → контекст (токены). Подстроки в имени модели.</summary>
    private static readonly (string NamePart, int Ctx)[] Known =
    {
        ("gpt-5", 400_000), ("gpt-4.1", 1_047_576), ("gpt-4o", 128_000),
        ("o1", 200_000), ("o3", 200_000), ("o4-mini", 200_000),
        ("claude", 200_000), ("gemini-1.5", 1_000_000), ("gemini-2", 1_000_000),
        ("deepseek-v3", 65_536), ("deepseek-v4", 128_000), ("deepseek", 65_536),
        ("llama-3", 131_072), ("llama3", 131_072), ("llama", 8_192),
        ("qwen", 131_072), ("mistral", 128_000), ("nemotron", 131_072),
        ("large-v3", 0), // whisper-модели не для чата — не матчим
    };

    /// <summary>Входной бюджет запроса: min(contextModel, разумный потолок отправки).
    /// Неизвестная/локальная модель → 32768.</summary>
    public static int InputBudget(string? chatModel, string? baseUrl = null)
    {
        var name = chatModel?.ToLowerInvariant() ?? "";
        // Локальные Ollama/LM Studio часто ограничены 8–32k.
        var local = baseUrl != null &&
            (baseUrl.Contains("localhost") || baseUrl.Contains("127.0.0.1") || baseUrl.Contains("host.docker"));
        foreach (var (part, ctx) in Known)
        {
            if (part == "large-v3") continue;
            if (name.Contains(part))
                return local ? Math.Min(ctx, 32768) : Math.Min(ctx, 131_072);
        }
        return local ? 8192 : UnknownModelLimit;
    }

    /// <summary>Грубая оценка токенов: ~4 символа/токен (ru/en смешанный).</summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);
}

/// <summary>Назначение LLM-вызова — ручное сообщение, фоновая речь, извлечение
/// фактов, фоновая сводка. Определяет правила системного промпта (правило
/// молчания — только для фоновой речи).</summary>
public enum AskPurpose { ManualChat, BackgroundSpeech, Extraction, Summary }

/// <summary>Состояние контекста одной встречи: накопительная сводка, свежий
/// дословный хвост, счётчик новых пар и позиция сводки. Персистится как
/// снапшот (метаданные встречи) — переживает перезапуск.</summary>
public sealed class MeetingContextState
{
    /// <summary>Сводка старой части беседы (≤ ~2000 токенов), null — сводки ещё нет.</summary>
    public string? RollingSummary { get; set; }
    /// <summary>Сколько пар из хвоста уже вошло в RollingSummary (сводка покрывает
    /// [0..SummaryCoveredPairs)).</summary>
    public int SummaryCoveredPairs { get; set; }
    /// <summary>Пар, добавленных после последней сводки (триггер ≥8).</summary>
    public int PairsSinceSummary { get; set; }
    /// <summary>UTC-время последней сводки (триггер «не чаще 30с»).</summary>
    public DateTime LastSummaryUtc { get; set; }
    /// <summary>Признак вынужденного отброса старого контекста до первой сводки
    /// («часть раннего контекста пока недоступна»).</summary>
    public bool EarlyContextDropped { get; set; }
    /// <summary>Идёт ли сейчас фоновая сводка (не более 1 задания на встречу).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool SummaryInFlight { get; set; }
}

/// <summary>План входного контекста для одного LLM-запроса.</summary>
public sealed record ContextPlan(
    /// <summary>Сводка старой части — вставляется в system prompt.</summary>
    string? SummaryBlock,
    /// <summary>Дословный хвост истории (последние пары).</summary>
    IReadOnlyList<ChatMessage> Tail,
    /// <summary>Оценка входных токенов (system+summary+tail+вопрос+вложения+recall).</summary>
    int EstimatedInputTokens,
    /// <summary>Входной бюджет модели.</summary>
    int InputBudget,
    /// <summary>Собрать ли фоновую сводку после этого запроса.</summary>
    bool ScheduleSummary);

/// <summary>
/// Менеджер контекста текущей встречи: хвост дословных пар + накопительная сводка.
/// Потокобезопасен (внутренний lock). Модель-независим — LLM-вызов делается
/// снаружи через делегат (App подставляет ApiClient с background=true).
/// </summary>
public sealed class MeetingContextManager
{
    /// <summary>Сколько последних пар всегда остаётся дословным хвостом.</summary>
    public const int VerbatimTailPairs = 6;
    /// <summary>Минимум новых пар между сводками.</summary>
    public const int MinPairsBetweenSummaries = 8;
    /// <summary>Минимальный интервал между сводками одной встречи.</summary>
    public static readonly TimeSpan MinSummaryInterval = TimeSpan.FromSeconds(30);
    /// <summary>Порог входного бюджета, при котором пора сворачивать (60%).</summary>
    public const double TriggerFraction = 0.60;
    /// <summary>Резерв под ответ (не более лимита ответа) + служебный запас.</summary>
    public const int ReserveSlack = 2048;

    /// <summary>Аварийный предел _tail (пар): предохранитель памяти, когда сводка
    /// хронически не публикуется (провайдер падает). Режем самое старое с флагом
    /// EarlyContextDropped — контекст честно помечается недоступным, а не теряется молча.</summary>
    private const int HardTailCapPairs = 120;
    private const int HardTailCapMsgs = HardTailCapPairs * 2;

    private readonly object _lock = new();
    private readonly List<ChatMessage> _tail = new();
    private readonly string? _meetingKey;
    private MeetingContextState _state;

    public MeetingContextManager(string? meetingKey = null, MeetingContextState? restored = null)
    {
        _meetingKey = meetingKey;
        _state = restored ?? new MeetingContextState();
    }

    /// <summary>Живая копия состояния (для статуса/снапшота).</summary>
    public MeetingContextState State { get { lock (_lock) return Snapshot(); } }

    /// <summary>Добавить завершённую пару (вопрос+ответ) в хвост встречи.</summary>
    public void AddPair(string userText, string answerText)
    {
        lock (_lock)
        {
            _tail.Add(ChatMessage.User(userText));
            _tail.Add(ChatMessage.Assistant(answerText));
            _state.PairsSinceSummary++;
        }
    }

    /// <summary>То же, но вопрос уже собран (может нести Images — ТЗ image-chat:
    /// в хвосте встречи живут ссылки, payload резолвится при отправке).</summary>
    public void AddPair(ChatMessage user, string answerText)
    {
        lock (_lock)
        {
            _tail.Add(user);
            _tail.Add(ChatMessage.Assistant(answerText));
            _state.PairsSinceSummary++;
        }
    }

    /// <summary>Собрать план запроса: summary-блок + хвост, который помещается
    /// в бюджет вместе с резервом ответа.</summary>
    public ContextPlan BuildPlan(string question, string systemPrompt,
        int maxResponseTokens, string? recallBlock, string? attachmentsBlock,
        string chatModel, string? baseUrl)
    {
        var budget = ContextBudgetResolver.InputBudget(chatModel, baseUrl);
        var reserve = Math.Min(maxResponseTokens, 25000) + ReserveSlack;
        var fixedTokens = ContextBudgetResolver.EstimateTokens(systemPrompt)
            + ContextBudgetResolver.EstimateTokens(recallBlock)
            + ContextBudgetResolver.EstimateTokens(attachmentsBlock)
            + ContextBudgetResolver.EstimateTokens(question);

        lock (_lock)
        {
            var summary = _state.RollingSummary;
            var summaryTokens = ContextBudgetResolver.EstimateTokens(summary);
            // Хвост: последние сообщения, но не меньше VerbatimTailPairs*2
            // (пока они покрыты сводкой — дословный хвост только сверх покрытого).
            int capacity = budget - reserve - fixedTokens - summaryTokens;
            var tail = TakeTailWithin(capacity);
            var estimated = fixedTokens + summaryTokens + Estimate(tail);
            // Триггер фоновой сводки: заполнение >60% ИЛИ непокрытые пары хвоста
            // сами не влезают в бюджет — сворачиваем старое заранее.
            var schedule = ShouldSummarize(estimated, budget);
            return new ContextPlan(summary, tail, estimated, budget, schedule);
        }
    }

    /// <summary>
    /// Снимок пар, которые можно свернуть в сводку: всё, кроме последних
    /// VerbatimTailPairs пар. Возвращает КОПИЮ на момент вызова: реплики, пришедшие
    /// во время генерации сводки, в снимок не попадают и в <see cref="PublishSummary"/>
    /// удаляются только они (см. coveredPairs) — mid-flight сообщения выживают.
    /// </summary>
    public List<ChatMessage> TakeSummarizableSnapshot()
    {
        lock (_lock)
        {
            var pairs = _tail.Count / 2;
            var take = Math.Max(0, pairs - VerbatimTailPairs) * 2;
            return _tail.GetRange(0, take);
        }
    }

    /// <summary>
    /// Опубликовать готовую сводку и удалить покрытые ею пары из хвоста.
    /// Удаляется РОВНО снимок, снятый в <see cref="TakeSummarizableSnapshot"/>
    /// (<paramref name="coveredPairs"/> пар): реплики, пришедшие во время сводки,
    /// остаются в хвосте и уйдут в следующую — их нельзя терять молча.
    /// </summary>
    public void PublishSummary(string summary, int coveredPairs)
    {
        lock (_lock)
        {
            _state.RollingSummary = summary;
            _state.LastSummaryUtc = DateTime.UtcNow;
            _state.PairsSinceSummary = 0;
            _state.SummaryInFlight = false;
            // Удаляется РОВНО снимок (coveredPairs пар) — он всё ещё является
            // ПРЕФИКСОМ _tail: реплики только добавляются в конец, а
            // TakeSummarizableSnapshot снимал первые пары на момент вызова.
            // Климпить по «tail минус дословное окно» НЕЛЬЗЯ: mid-flight пары,
            // пришедшие во время генерации, иначе попали бы под удаление,
            // не находясь в сводке (аудит §8.2 — молчаливая потеря реплик).
            var drop = Math.Min(Math.Max(0, coveredPairs) * 2, _tail.Count);
            if (drop > 0)
            {
                // Префикс _tail после удаления снимка сводкой НЕ покрыт —
                // эти пары уйдут в следующую сводку, молча не выбрасываем.
                _tail.RemoveRange(0, drop);
                _coveredFrontMsgs = 0;
            }
            // Сводка покрыла ранний контекст → «недоступной» части больше нет.
            _state.EarlyContextDropped = false;
        }
    }
    /// <summary>Отметить факт запуска сводки (не больше 1 на встречу).</summary>
    public bool TryMarkSummaryInFlight()
    {
        lock (_lock)
        {
            if (_state.SummaryInFlight) return false;
            _state.SummaryInFlight = true;
            return true;
        }
    }

    /// <summary>Подсадка состояния после перезапуска: сводка из sidecar + хвост,
    /// восстановленный из jsonl-журнала. Вызывается ОДИН раз на старте, до первого
    /// Append — иначе свежие пары затирает парами прошлой сессии.</summary>
    public void Restore(MeetingContextState? state, IReadOnlyList<(string Question, string Answer)> tailPairs)
    {
        lock (_lock)
        {
            if (state != null) _state = state;
            _tail.Clear();
            foreach (var (q, a) in tailPairs)
            {
                _tail.Add(ChatMessage.User(q));
                _tail.Add(ChatMessage.Assistant(a));
            }
            // Восстановленный хвост не считаем «новыми парами» — иначе первая
            // же пара после рестарта триггered бы сводку по старому материалу.
            _state.PairsSinceSummary = 0;
            _state.SummaryInFlight = false;
        }
    }

    /// <summary>Снять флаг сводки после ошибки/отмены.</summary>
    public void MarkSummaryDone() { lock (_lock) _state.SummaryInFlight = false; }

    /// <summary>Хвост переполнен до первой сводки — старое пришлось отбросить.
    /// Запрос снабжается пометкой «часть раннего контекста недоступна».</summary>
    public void MarkEarlyContextDropped() { lock (_lock) _state.EarlyContextDropped = true; }

    /// <summary>Полный сброс (новая встреча / очистка истории).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _tail.Clear();
            _coveredFrontMsgs = 0;
            _state = new MeetingContextState();
        }
    }

    private bool ShouldSummarize(int estimatedTokens, int budget)
    {
        if (_state.SummaryInFlight) return false;
        if (_state.PairsSinceSummary < MinPairsBetweenSummaries) return false;
        if (DateTime.UtcNow - _state.LastSummaryUtc < MinSummaryInterval) return false;
        var pairs = _tail.Count / 2;
        if (pairs <= VerbatimTailPairs) return false;
        // Триггер: 60% входного бюджета ИЛИ пары уже выталкиваются из
        // дословного хвоста — сворачиваем заранее, пока контекст не потерян.
        return estimatedTokens >= budget * TriggerFraction
            || pairs - VerbatimTailPairs >= 1;
    }

    private int _coveredFrontMsgs;

    private List<ChatMessage> TakeTailWithin(int capacityTokens)
    {
        // В запрос идёт ТОЛЬКО последние VerbatimTailPairs пар (дословный
        // свежий хвост). Старшее покрывается RollingSummary; если сводки
        // ещё нет — старшее отбрасывается с флагом EarlyContextDropped
        // («часть раннего контекста пока недоступна»), а триггер сводки
        // ловит это по заполнению бюджета на следующих запросах.
        var tail = new List<ChatMessage>();
        var maxItems = Math.Min(_tail.Count, VerbatimTailPairs * 2);
        var used = 0;
        for (int i = _tail.Count - 1; i >= 0 && tail.Count < maxItems; i--)
        {
            var m = _tail[i];
            var t = ContextBudgetResolver.EstimateTokens(m.Content);
            if (used + t > capacityTokens && tail.Count > 0) break; // ≥1 сообщение всегда
            tail.Insert(0, m);
            used += t;
        }
        var dropped = _tail.Count - tail.Count;
        if (dropped > 0)
        {
            // §8.2: молча удаляем ТОЛЬКО то, что уже покрыто сводкой (начало
            // хвоста после Restore). Непокрытые сводкой сообщения остаются в
            // _tail до следующей публикации — иначе ранние реплики теряются
            // без следа (mid-flight и просто «ещё не сведённые» пары).
            var silentMin = Math.Min(dropped, _coveredFrontMsgs);
            if (silentMin > 0)
            {
                _tail.RemoveRange(0, silentMin);
                _coveredFrontMsgs -= silentMin;
            }
            var truncated = dropped - silentMin;
            if (truncated > 0)
            {
                // Старшее выпало из дословного хвоста, но сводка его ещё не
                // покрыла — честный флаг («часть раннего контекста недоступна»),
                // а не тихая потеря. Триггер сводки покроет их на следующем чанке.
                _state.EarlyContextDropped = true;
            }
            // Аварийный предохранитель памяти: если сводка хронически падает,
            // _tail не должен расти бесконечно — режем САМОЕ старое с флагом.
            if (_tail.Count > HardTailCapMsgs)
            {
                var extra = _tail.Count - HardTailCapMsgs;
                _tail.RemoveRange(0, extra);
                _coveredFrontMsgs = Math.Max(0, _coveredFrontMsgs - extra);
                _state.EarlyContextDropped = true;
            }
        }
        return tail;
    }

    private static int Estimate(IReadOnlyList<ChatMessage> msgs)
    {
        var s = 0;
        foreach (var m in msgs) s += ContextBudgetResolver.EstimateTokens(m.Content);
        return s;
    }

    private MeetingContextState Snapshot() => new()
    {
        RollingSummary = _state.RollingSummary,
        SummaryCoveredPairs = _state.SummaryCoveredPairs,
        PairsSinceSummary = _state.PairsSinceSummary,
        LastSummaryUtc = _state.LastSummaryUtc,
        EarlyContextDropped = _state.EarlyContextDropped,
    };
}

/// <summary>
/// Буфер речи за период паузы LLM: реплики продолжают приходить от STT и
/// копятся здесь (пузыри транскрипта при этом показываются сразу). На снятии
/// паузы буфер превращается в ОДИН контекстный запрос — очереди ответов
/// на старые фразы нет.
/// </summary>
public sealed class LlmPauseBuffer
{
    private readonly object _lock = new();
    private readonly List<(DateTime At, string Label, string Text, string[]? ImageIds)> _items = new();

    /// <summary>Добавить реплику, пришедшую во время паузы.
    /// imageIds — картинки, отправленные с репликой (буферизуются вместе с текстом,
    /// спека image-chat §9: пауза LLM буферизует снапшот с imageIds).</summary>
    public void Add(string label, string text, string[]? imageIds = null)
    {
        lock (_lock) _items.Add((DateTime.Now, label, text, imageIds));
    }

    public bool IsEmpty { get { lock (_lock) return _items.Count == 0; } }

    /// <summary>Бросить накопленное (новая встреча/очистка истории).</summary>
    public void Clear() { lock (_lock) _items.Clear(); }

    /// <summary>Все imageIds накопленных реплик в порядке добавления (для слива
    /// одним запросом — картинки периода паузы едут вместе с объединённым текстом).</summary>
    public IReadOnlyList<string> SnapshotImageIds()
    {
        lock (_lock)
            return _items.Where(i => i.ImageIds is { Length: > 0 })
                         .SelectMany(i => i.ImageIds!).ToList();
    }

    /// <summary>Один объединённый текст периода паузы для LLM (или null, если пусто).
    /// Формат: метка-инструкция + строки «[Динамик] …» — модель отвечает на
    /// актуальный последний вопрос с учётом всего периода. Текст инструкции — ТОЛЬКО
    /// для LLM, в чат/журнал он не попадает (см. App.DrainPauseBufferAsync).</summary>
    public string? DrainAsSingleRequest(out int itemCount)
    {
        lock (_lock)
        {
            itemCount = _items.Count;
            if (_items.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            sb.Append("[Пока LLM был выключен/на паузе, в разговоре прозвучало — ответь на самый актуальный/последний вопрос с учётом всего этого контекста:]\n");
            foreach (var (_, label, text, _) in _items)
                sb.Append(label).Append(' ').Append(text).Append('\n');
            _items.Clear();
            return sb.ToString().TrimEnd();
        }
    }
}

