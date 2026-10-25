using System.Text.Json;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>Состояние исходящего MCP-подключения (для карточки/статуса).</summary>
public enum McpServerConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

/// <summary>Снимок состояния сервера для диагностики/UI.</summary>
public sealed record McpServerState(
    McpServerConnectionStatus Status,
    string? Error,
    int ToolCount,
    DateTime? LastErrorUtc);

/// <summary>
/// Инструмент внешнего MCP-сервера после адаптации: уникальное безопасное имя для
/// LLM + оригинальное имя для вызова + оценка риска по аннотациям протокола.
/// </summary>
public sealed record McpToolSpec(
    string AdapterName,
    string ServerId,
    string ServerName,
    string OriginalName,
    string? Description,
    JsonElement ParametersJsonSchema,
    McpActionRisk Risk);

/// <summary>Свернутый результат tools/call для передачи в LLM.</summary>
public sealed record McpToolCallResult(
    string Content,
    bool IsError,
    long DurationMs);

/// <summary>Прогресс из progress-уведомлений MCP-сервера (done/total могут отсутствовать).</summary>
public readonly record struct McpToolProgress(double? Done, double? Total, string? Message);

/// <summary>
/// Шов транспорта для вызова инструментов. Реализация — McpClientManager (SDK),
/// в тестах — фейк без реального MCP-сервера.
/// </summary>
public interface IMcpToolTransport
{
    /// <summary>Подключиться (лениво, с кешем) и вернуть инструменты сервера.</summary>
    Task<IReadOnlyList<McpToolSpec>> ListToolsAsync(McpOutgoingServer server, CancellationToken ct);

    /// <summary>Вызвать инструмент по оригинальному имени. Исключения = транспортные сбои.</summary>
    Task<McpToolCallResult> CallToolAsync(
        McpOutgoingServer server, McpToolSpec tool, string argsJson,
        IProgress<McpToolProgress>? progress, CancellationToken ct);
}

/// <summary>Маппинг строкового trust исходящего сервера на уровень доверия контрактов.</summary>
public static class McpOutgoingTrust
{
    public static McpTrustLevel Parse(string? trust) => trust switch
    {
        "own" => McpTrustLevel.Own,
        "trusted" => McpTrustLevel.TrustedRemote,
        _ => McpTrustLevel.ThirdParty,
    };
}

/// <summary>
/// Нужно ли подтверждение пользователя для вызова инструмента внешнего сервера:
/// ReadOnly и явно ApprovedTools — нет; Destructive — всегда; Mutating — если
/// сервер не «own». Additive трактуем как Mutating (аннотаций «additive» в MCP нет).
/// </summary>
public static class McpOutgoingApproval
{
    public static bool NeedsApproval(McpOutgoingServer server, McpToolSpec tool)
    {
        if (tool.Risk == McpActionRisk.ReadOnly) return false;
        if (server.ApprovedTools.Contains(tool.AdapterName) ||
            server.ApprovedTools.Contains(tool.OriginalName))
            return false;
        if (tool.Risk == McpActionRisk.Destructive) return true;
        return McpOutgoingTrust.Parse(server.Trust) != McpTrustLevel.Own;
    }
}

/// <summary>
/// Имена инструментов для OpenAI function-calling: {server}__{tool}, алфавит
/// ^[a-zA-Z0-9_-]+$, уникальность в пределах сессии суффиксом «-2», «-3»…
/// </summary>
public static class McpToolNaming
{
    public const int MaxNameLength = 64;

    /// <summary>Любая строка → безопасное имя функции (пустая → «tool»).</summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "tool";
        var chars = raw.Trim().Select(c =>
            c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'
                ? c : '_').ToArray();
        var s = new string(chars);
        return s.Length > MaxNameLength ? s[..MaxNameLength] : s;
    }

    /// <summary>
    /// Адаптерное имя «{server}__{tool}» с гарантией уникальности: при коллизии —
    /// суффикс «-2» и т.д. (used пополняется вызывающим).
    /// </summary>
    public static string MakeAdapterName(string serverName, string toolName, ISet<string> used)
    {
        var baseName = Sanitize(serverName) + "__" + Sanitize(toolName);
        if (baseName.Length > MaxNameLength)
            baseName = baseName[..MaxNameLength];
        var name = baseName;
        for (var i = 2; !used.Add(name); i++)
            name = $"{baseName}-{i}";
        return name;
    }
}

/// <summary>Режим задачи: определяет бюджеты по умолчанию (ТЗ §6.5).</summary>
public enum McpTaskMode
{
    /// <summary>Чат/обычный запрос: ≤200К токенов, ≤10 мин активной работы.</summary>
    Normal,
    /// <summary>Исследование: ≤1М токенов, ≤45 мин активной работы.</summary>
    Research,
}

/// <summary>
/// Бюджет задачи. null-поле = «нет явного лимита» → берётся дефолт режима.
/// Пользовательский меньший предел выигрывает (MergeWith → min).
/// </summary>
public sealed record McpTaskBudget(
    int? MaxTotalTokens,
    int? MaxActiveSeconds,
    int? MaxSteps,
    double MaxCostUsd = 0)
{
    public static readonly McpTaskBudget None = new(null, null, null, 0);

    /// <summary>Дефолты режима (ТЗ §6.5).</summary>
    public static McpTaskBudget ForMode(McpTaskMode mode) => mode switch
    {
        McpTaskMode.Research => new McpTaskBudget(1_000_000, 45 * 60, null, 0),
        _ => new McpTaskBudget(200_000, 10 * 60, null, 0),
    };

    /// <summary>Разбор свободной строки режима («research» → Research, иначе Normal).</summary>
    public static McpTaskMode ParseMode(string? mode)
        => string.Equals(mode, "research", StringComparison.OrdinalIgnoreCase)
            ? McpTaskMode.Research : McpTaskMode.Normal;

    /// <summary>Слияние с пользовательским пределом: по каждому полю берётся
    /// меньший из заданных (null = без лимита → проигрывает числу).</summary>
    public McpTaskBudget MergeWith(McpTaskBudget? user) => user == null ? this : new(
        Min(MaxTotalTokens, user.MaxTotalTokens),
        Min(MaxActiveSeconds, user.MaxActiveSeconds),
        Min(MaxSteps, user.MaxSteps),
        user.MaxCostUsd > 0 && (MaxCostUsd <= 0 || user.MaxCostUsd < MaxCostUsd)
            ? user.MaxCostUsd : MaxCostUsd);

    private static int? Min(int? a, int? b)
        => a == null ? b : b == null ? a : Math.Min(a.Value, b.Value);
}

/// <summary>Накопленный расход job'а (копится в checkpoint, переживает рестарт).</summary>
public sealed class McpBudgetUsed
{
    /// <summary>Суммарные prompt-токены с учётом повторной передачи контекста
    /// (каждый запрос пересылает историю — usage.prompt учитывается каждый раз).</summary>
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    /// <summary>Часть токенов посчитана оценкой (usage не пришёл) — честный флаг.</summary>
    public bool Estimated { get; set; }
    /// <summary>Активная работа цикла/исполнителя (паузы не считаются).</summary>
    public double ActiveSeconds { get; set; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// Чекпоинт job'а (поле McpJob.Checkpoint, JSON). Общая обёртка для всех kinds:
/// этап/счётчики + необработанные sourceIds + накопленный бюджет + данные
/// исполнителя (ExecutorData — непрозрачный JSON kind'а; для research —
/// McpLoopSnapshot). Schema=1.
/// </summary>
public sealed class McpJobCheckpoint
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    /// <summary>Job, которому принадлежит снапшот (guard при continue чужим jobId).</summary>
    public string? JobId { get; set; }
    /// <summary>Человекочитаемая стадия («Изучено 18 из 46 встреч»).</summary>
    public string? Stage { get; set; }
    public int? Done { get; set; }
    public int? Total { get; set; }
    /// <summary>Обработанные источники (documentId/meetingId/путь) — пропуск при resume.</summary>
    public List<string> ProcessedIds { get; set; } = new();
    /// <summary>Промежуточные результаты вызовов (ид → краткий итог), без секретов.</summary>
    public Dictionary<string, string> Results { get; set; } = new(StringComparer.Ordinal);
    public McpBudgetUsed? BudgetUsed { get; set; }
    public double CostUsd { get; set; }
    /// <summary>Счётчик продлений бюджета («Продолжить» после WaitingBudget).</summary>
    public int BudgetExtensions { get; set; }
    /// <summary>Курсор следующей порции (для paging-исполнителей); null = нет.</summary>
    public string? NextCursor { get; set; }
    /// <summary>Снапшот tool-loop (research): сообщения + выполненные ключи + шаг.</summary>
    public McpLoopSnapshot? Loop { get; set; }
    /// <summary>Необработанные данные исполнителя (для kinds без typed-снапшота).</summary>
    public JsonElement? ExecutorData { get; set; }
}

/// <summary>Прогресс job'а для UI/статуса («Изучено 18 из 46 встреч»).</summary>
public sealed record McpJobProgress(string? Stage, int? Done, int? Total, string? Message)
{
    /// <summary>Процент только при известном total; иначе null.</summary>
    public int? Percent => Done is { } d && Total is > 0 ? (int)Math.Min(100, d * 100L / Total.Value) : null;
}

/// <summary>Маркер «можно продолжить» в результате/статусе job'а при WaitingBudget/WaitingResume.</summary>
public sealed record McpJobContinuation(bool CanContinue, string Reason)
{
    public static readonly McpJobContinuation Budget = new(true, "budget_exhausted");
    public static readonly McpJobContinuation Restarted = new(true, "process_restarted");
}
