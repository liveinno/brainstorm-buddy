using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Context;

/// <summary>
/// Чистая логика фоновой сводки контекста встречи: сборка промпта и кумулятивное
/// слияние. Вынесено из App, чтобы (а) сжатие было тестируемо без окна/сети и
/// (б) ранние решения переживали много циклов: каждая новая сводка строится
/// ВЕРХУШКОЙ над предыдущей, а не вместо неё (аудит 15.10: ранний контекст
/// терялся после 2-3 сжатий).
/// </summary>
public static class ContextSummarizer
{
    /// <summary>Системная роль для запроса сводки (сырой базы, без правил памяти).</summary>
    public const string SystemPrompt =
        "Ты сокращаешь стенограмму встречи до технической сводки. Пиши по-русски, " +
        "фактами, без приветствий и пересказа «что это за встреча».";

    /// <summary>Бюджет ответа фоновой сводки: ~120 слов сводки + запас. Отдельный
    /// от Advanced.MaxResponseTokens (25000) — фон не должен стоить как чат.</summary>
    public const int DefaultMaxTokens = 2000;

    /// <summary>Потолок бюджета одного повтора: выше уже нет смысла для сводки,
    /// а стоимость фонового запроса остаётся ограниченной.</summary>
    public const int RetryMaxTokens = 4096;

    /// <summary>Всего попыток на одну сводку (первая + один повтор).</summary>
    public const int MaxAttempts = 2;

    /// <summary>
    /// Повторять запрос сводки имеет смысл только когда бюджет реально исчерпан
    /// (<see cref="EmptyAnswerKind.LengthExhausted"/>). Ошибки, отмена, молчание
    /// модели и провайдерский кап повтором не лечатся.
    /// </summary>
    public static bool ShouldRetryForBudget(AskResult? result) =>
        result != null
        && !result.Canceled
        && result.Error == null
        && string.IsNullOrWhiteSpace(result.Content)
        && result.EmptyKind == EmptyAnswerKind.LengthExhausted;

    /// <summary>Удвоить бюджет, не выходя за <see cref="RetryMaxTokens"/> и не
    /// опускаясь ниже <see cref="DefaultMaxTokens"/>.</summary>
    public static int NextBudget(int previous)
    {
        var doubled = previous >= RetryMaxTokens / 2 ? RetryMaxTokens : previous * 2;
        return Math.Min(RetryMaxTokens, Math.Max(DefaultMaxTokens, doubled));
    }

    /// <summary>
    /// Запрос сводки с ОДНИМ повтором при исчерпанном бюджете (аудит 16.10 §5):
    /// reasoning-модель иногда съедает весь лимит на скрытые размышления и отдаёт
    /// пустой content, хотя следующий, более щедрый запрос отвечает. onUsage
    /// вызывается на КАЖДОЙ попытке — оплаченный usage не теряется.
    /// </summary>
    public static async Task<AskResult> AskWithBudgetRetryAsync(
        Func<int, Task<AskResult>> ask,
        int initialMaxTokens,
        Action<AskResult>? onUsage = null,
        CancellationToken ct = default)
    {
        var budget = initialMaxTokens > 0 ? initialMaxTokens : DefaultMaxTokens;
        var result = await ask(budget).ConfigureAwait(false);
        onUsage?.Invoke(result);
        for (var attempt = 1; attempt < MaxAttempts && ShouldRetryForBudget(result); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            budget = NextBudget(budget);
            result = await ask(budget).ConfigureAwait(false);
            onUsage?.Invoke(result);
        }
        return result;
    }

    /// <summary>Порядок ролей в промпте: user-реплики с тире, ответы — текстом.</summary>
    public static string RenderTranscript(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in messages)
            sb.AppendLine(m.Role == "user" ? $"— {m.Content}" : m.Content);
        return sb.ToString();
    }

    /// <summary>
    /// Промпт сжатия снимка. <paramref name="priorSummary"/> — текущая сводка
    /// (null на первом цикле): она идёт ВХОДОМ первым блоком, поэтому решения
    /// начала встречи остаются в контексте и после серии сжатий.
    /// </summary>
    public static string BuildPrompt(string? priorSummary, IReadOnlyList<ChatMessage> snapshot)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Ниже — реплики живого созвона в хронологическом порядке.")
          .AppendLine("Сожми их в краткую сводку для будущего контекста: имена/роли собеседников, ")
          .AppendLine("факты и решения, заданные вопросы, обещания, договорённости. Без воды, до ~120 слов.")
          .AppendLine();
        if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            // Предыдущая сводка — база: новый текст ДОПОЛНЯЕТ её, а не заменяет.
            sb.AppendLine("=== ПРЕДЫДУЩАЯ СВОДКА (сохрани все решения/факты из неё) ===")
              .AppendLine(priorSummary.Trim())
              .AppendLine("=== КОНЕЦ ПРЕДЫДУЩЕЙ СВОДКИ ===")
              .AppendLine();
        }
        sb.AppendLine("=== НОВЫЕ РЕПЛИКИ ===")
          .Append(RenderTranscript(snapshot));
        return sb.ToString();
    }

    /// <summary>
    /// Кумулятивное слияние: пустой/пробелный ответ модели → оставляем прошлую
    /// сводку как есть (молча ничего не «сжимаем»), иначе добавляем новый блок
    /// к предыдущему. Размер ограничен <paramref name="maxChars"/>: при переполнении
    /// обрезается НАЧАЛО (самые старые блоки), свежие решения сохраняются.
    /// </summary>
    public static string MergeSummary(string? priorSummary, string? nextSummary, int maxChars = 6000)
    {
        var next = string.IsNullOrWhiteSpace(nextSummary) ? null : nextSummary.Trim();
        var prior = string.IsNullOrWhiteSpace(priorSummary) ? null : priorSummary.Trim();
        var merged = prior == null ? next : next == null ? prior : prior + "\n\n" + next;
        if (merged == null) return "";
        if (merged.Length <= maxChars) return merged;
        // Отбрасываем самые старые блоки целиком, пока не влезем.
        while (merged.Length > maxChars)
        {
            var cut = merged.IndexOf("\n\n", StringComparison.Ordinal);
            if (cut < 0) { merged = merged[^maxChars..]; break; }
            merged = merged[(cut + 2)..];
        }
        return merged;
    }
}
