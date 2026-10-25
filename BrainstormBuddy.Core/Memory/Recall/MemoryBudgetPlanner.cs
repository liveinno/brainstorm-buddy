using BrainstormBuddy.Config;
using BrainstormBuddy.Context;

namespace BrainstormBuddy.Memory.Recall;

/// <summary>
/// Бюджеты поиска на один ответ (спека «поиск по памяти и встречам», п.2):
/// общий автоподбор 8000 токенов делится 5/8 на факты памяти и 3/8 на фрагменты
/// встреч; дополнительный поиск модели — отдельные 32000. Для моделей с малым
/// окном оба бюджета урезаются по фактическому входному окну
/// (<see cref="ContextBudgetResolver.InputBudget"/>): системные инструкции,
/// история чата/документы и резерв ответа вычитаются вызывающим через
/// <paramref name="reservedTokens"/>.
/// </summary>
public sealed record MemoryBudgets(
    int Window,
    int Reserved,
    int AutoTokens,
    int MemoryTokens,
    int MeetingTokens,
    int ExtraSearchTokens,
    bool Clamped)
{
    /// <summary>Сколько зарезервировано под ответ модели (не занимать поиском).</summary>
    public const int AnswerReserve = 2048;

    /// <summary>
    /// Посчитать бюджеты. <paramref name="reservedTokens"/> — уже занятое окно
    /// (system prompt, история, вложения, результаты инструментов); 0 — только
    /// резерв ответа.
    /// </summary>
    public static MemoryBudgets Plan(MemoryConfig cfg, string? chatModel, string? baseUrl,
        int reservedTokens)
    {
        var window = ContextBudgetResolver.InputBudget(chatModel, baseUrl);
        var reserved = Math.Max(0, reservedTokens);
        // Что осталось на поиск после занятого контекста и резерва ответа.
        var available = Math.Max(1024, window - reserved - AnswerReserve);

        var autoWanted = Math.Max(500, cfg.AutoRecallBudgetTokens);
        var extraWanted = Math.Max(1000, cfg.ExtraSearchBudgetTokens);

        // Автоподбор — первая половина доступного окна; дополнительный поиск
        // получает остаток (не больше настроенных 32000). Оба не выходят за окно.
        var autoCap = Math.Max(1024, available / 2);
        var auto = Math.Min(autoWanted, autoCap);
        var extraCap = Math.Max(1000, available - auto);
        var extra = Math.Min(extraWanted, extraCap);

        // Автоподбор: 5/8 фактам, 3/8 встречам (сообщения о встречах длиннее,
        // но факты — основная опора ответа).
        var memory = auto * 5 / 8;
        var meetings = auto - memory;

        return new MemoryBudgets(window, reserved, auto, memory, meetings, extra,
            Clamped: auto < autoWanted || extra < extraWanted);
    }
}