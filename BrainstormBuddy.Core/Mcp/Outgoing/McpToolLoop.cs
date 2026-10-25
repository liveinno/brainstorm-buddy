using System.Text.Json;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>Причина остановки tool-цикла.</summary>
public enum McpLoopStopReason
{
    /// <summary>Модель вернула финальный текст без вызовов.</summary>
    FinalText,
    /// <summary>Достигнут явный лимит шагов (задан вызовом; по умолчанию безлимит).</summary>
    MaxSteps,
    /// <summary>Повторные чтения без новых данных — прогресса нет.</summary>
    NoProgress,
    /// <summary>Превышен денежный бюджет (USD). Legacy-имя; см. BudgetExhausted.</summary>
    BudgetExceeded,
    /// <summary>LLM на паузе (IsLlmPausedFunc) — вызов не уходил.</summary>
    LlmPaused,
    /// <summary>Запрошена явная пауза job'а (PauseToken).</summary>
    Paused,
    /// <summary>Ошибка LLM/транспорта.</summary>
    Error,
    /// <summary>Отмена вызывающим.</summary>
    Cancelled,
    /// <summary>Исчерпан токеновый бюджет режима (prompt+completion, §6.5). Результат сохранён.</summary>
    BudgetExhausted,
    /// <summary>Исчерпан лимит активного времени режима. Результат сохранён.</summary>
    TimeExhausted,
    /// <summary>LLM на паузе ИЛИ исходящий MCP выключен в конфиге (Mcp.Enabled=false)
    /// — платные шаги запрещены, прогресс не теряется (§6.5).</summary>
    LlmMcpDisabled,
}

/// <summary>Сводка одного выполненного вызова инструмента для карточки ответа.</summary>
public sealed record McpToolCallSummary(
    string AdapterName, string ServerName, string ToolName,
    bool Success, bool Denied, long DurationMs)
{
    /// <summary>Изменяющее действие после обрыва/повтора с неизвестным исходом —
    /// НЕ выполнено повторно (защита от дубля), результат отмечен неопределённым.</summary>
    public bool Uncertain { get; init; }
}

/// <summary>Шов к LLM для tool-цикла (OpenAiClient.AskWithToolsAsync / фейк в тестах).</summary>
public interface IMcpToolLlm
{
    Task<ToolAskResult> AskAsync(
        IReadOnlyList<ToolLoopMessage> messages,
        IReadOnlyList<ToolSpecDto> tools,
        int maxTokens, CancellationToken ct);
}

/// <summary>Параметры одной итерации цикла.</summary>
public sealed class McpToolLoopRequest
{
    /// <summary>Полная история сообщений (system/user/…). Loop дописывает assistant/tool.</summary>
    public required List<ToolLoopMessage> Messages { get; init; }
    public required IReadOnlyList<McpToolSpec> Tools { get; init; }
    /// <summary>Серверы по ServerId (для trust/approval/cost-mode).</summary>
    public required IReadOnlyDictionary<string, McpOutgoingServer> Servers { get; init; }
    /// <summary>Явный лимит шагов (ТЗ §6.5): null = безлимит, режим определяет бюджеты.</summary>
    public int? MaxSteps { get; init; }
    /// <summary>Режим задачи («normal»/«research») — дефолтные бюджеты McpTaskBudget.ForMode.</summary>
    public string? Mode { get; init; }
    /// <summary>Пользовательский предел бюджета; мержится с дефолтом режима (меньший выигрывает).</summary>
    public McpTaskBudget? Budget { get; init; }
    /// <summary>Оценка токенов сообщения без usage (chars→tokens). null → ContextBudgetResolver (~4.0).</summary>
    public Func<ToolLoopMessage, int>? EstimateTokensFunc { get; init; }
    /// <summary>Штатный максимум ОДНОГО ответа (§6.5: 25 000 с поправкой на окно
    /// модели через ContextWindowFunc; запрос может задать меньше).</summary>
    public int MaxTokens { get; init; } = 25_000;
    /// <summary>Потолок расходов в USD (0 = без лимита). Оценка — через CostPerTokenUsd.</summary>
    public double MaxCostUsd { get; init; }
    /// <summary>Оценка стоимости: (promptTokens, completionTokens) → USD. null = только MaxSteps.</summary>
    public Func<int, int, double>? CostPerTokenUsd { get; init; }
    /// <summary>Подтверждение опасного действия: (сервер, инструмент, argsJson, риск) → разрешить?</summary>
    public Func<McpOutgoingServer, McpToolSpec, string, McpActionRisk, Task<bool>>? ApprovalFunc { get; init; }
    /// <summary>Пауза LLM приложения (overlay печатает/микрофон) — запрос вообще не уходит.</summary>
    public Func<bool>? IsLlmPausedFunc { get; init; }
    /// <summary>Исходящий MCP выключен (Mcp.Enabled=false) — платные шаги запрещены.</summary>
    public Func<bool>? IsMcpDisabledFunc { get; init; }
    /// <summary>Входное окно модели в токенах (ContextBudgetResolver.InputBudget); null →
    /// без поправки. MaxTokens зажимается в (window − оценка контекста − 2048), min 1024.</summary>
    public Func<int>? ContextWindowFunc { get; init; }
    /// <summary>Прогресс для UI: (stage, done, total). Серверные progress-уведомления проксируются.</summary>
    public Action<string?, int?, int?>? Progress { get; init; }
    /// <summary>Дамп состояния для ResumeToken (вызывается после каждого выполненного вызова).</summary>
    public Func<McpLoopSnapshot, Task>? SnapshotFunc { get; init; }
    /// <summary>Снимок восстановления: уже выполненные вызовы/стоимость/сообщения из ResumeToken.</summary>
    public McpLoopSnapshot? Resume { get; init; }
    /// <summary>Job-ид для журнала (null для синхронного чата).</summary>
    public string? JobId { get; init; }
    /// <summary>Пауза: в чате — ждать снятия; в job — новая пауза = отмена следующих шагов.</summary>
    public PauseToken Pause { get; init; } = PauseToken.None;
    /// <summary>true → при паузе ждать Resume() (чат); false → прерывать цикл (job).</summary>
    public bool WaitWhilePaused { get; init; }
}

/// <summary>Состояние для ResumeToken: сообщения + выполненные вызовы + накопленная стоимость.</summary>
public sealed class McpLoopSnapshot
{
    public List<ToolLoopMessage> Messages { get; set; } = new();
    public List<ExecutedKey> Executed { get; set; } = new();
    public int Step { get; set; }
    public double CostUsd { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    /// <summary>Накопленное активное время цикла, мс (ожидание паузы не считается).</summary>
    public long ActiveMs { get; set; }
    /// <summary>Хотя бы один запрос посчитан оценкой (usage не пришёл).</summary>
    public bool Estimated { get; set; }

    /// <summary>Ключ выполненного вызова: Risk=-1 → legacy-снимок (считаем изменяющим);
    /// ResultHash — хэш ответа чтения (детект «без новых данных»).</summary>
    public sealed record ExecutedKey(string AdapterName, string ArgsJson,
        int Risk = -1, string? ResultHash = null);
}

/// <summary>Результат прогона цикла.</summary>
public sealed class McpToolLoopResult
{
    public string? FinalText { get; set; }
    public McpLoopStopReason StopReason { get; set; }
    public McpError? Error { get; set; }
    public List<McpToolCallSummary> ToolCalls { get; } = new();
    /// <summary>Финальная история (исходные + assistant/tool сообщения цикла).</summary>
    public List<ToolLoopMessage> Messages { get; } = new();
    public double CostUsd { get; set; }
    public int Steps { get; set; }
    /// <summary>Накопленные токены запроса (usage или оценка).</summary>
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    /// <summary>Хотя бы один запрос без usage — расход оценочный.</summary>
    public bool Estimated { get; set; }
    /// <summary>Активное время цикла (ожидание паузы не считается).</summary>
    public TimeSpan ElapsedActive { get; set; }
    /// <summary>Пояснение бюджета при останове BudgetExhausted/TimeExhausted.</summary>
    public string? BudgetReason { get; set; }
    /// <summary>true → job можно продолжить тем же checkpoint'ом (новый бюджет).</summary>
    public bool CanContinue { get; set; }

    /// <summary>Выполненные вызовы (adapter+args) — точные ключи no-progress для ResumeToken.</summary>
    internal List<McpLoopSnapshot.ExecutedKey> ExecutedKeys { get; set; } = new();

    /// <summary>Снимок для ResumeToken job'а.</summary>
    public McpLoopSnapshot ToSnapshot()
        => new()
        {
            Messages = new List<ToolLoopMessage>(Messages),
            Executed = new List<McpLoopSnapshot.ExecutedKey>(ExecutedKeys),
            Step = Steps,
            CostUsd = CostUsd,
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,
            ActiveMs = (long)ElapsedActive.TotalMilliseconds,
            Estimated = Estimated,
        };
}

/// <summary>
/// Ограниченный цикл «LLM ↔ MCP-инструменты»: вызов → подтверждение (gate) →
/// tools/call → role:tool ответ → повтор. Стоп: финальный текст, MaxSteps,
/// бюджет, повтор tool+args (нет прогресса), пауза, отмена.
/// </summary>
public sealed class McpToolLoop
{
    private const int MaxToolResultChars = 32_000;

    /// <summary>Сколько символов результата попадает в превью записи журнала.</summary>
    private const int MaxResultPreviewChars = 1000;

    private readonly IMcpToolLlm _llm;
    private readonly IMcpToolTransport _transport;
    private readonly McpJournal? _journal;
    private readonly McpToolResultStore _toolResults;

    /// <param name="toolResultsDir">Каталог полных результатов инструментов
    /// (%AppData%\BrainstormBuddy\mcp\tool-results по умолчанию).</param>
    public McpToolLoop(IMcpToolLlm llm, IMcpToolTransport transport, McpJournal? journal = null,
        string? toolResultsDir = null)
    {
        _llm = llm;
        _transport = transport;
        _journal = journal;
        _toolResults = new McpToolResultStore(toolResultsDir
            ?? McpToolResultStore.DefaultDir(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrainstormBuddy")));
    }

    public async Task<McpToolLoopResult> RunAsync(McpToolLoopRequest request, CancellationToken ct)
    {
        var result = new McpToolLoopResult();
        var messages = request.Messages;
        // Изменяющие вызовы (risk>=Additive и legacy -1): повтор с неизвестным исходом
        // не выполняем дважды → Uncertain.
        var executedRisk = new Dictionary<string, int>(StringComparer.Ordinal);
        var executedKeys = new List<McpLoopSnapshot.ExecutedKey>();
        // Loop-guard чтений: последний read-ключ, серия одинаковых ответов, хэши.
        var readHashByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastReadKey = null;
        string? lastReadHash = null;
        var readStreak = 0;
        var readNudged = false;
        var mutRepeat = 0;
        var costUsd = 0.0;
        var promptTokens = 0;
        var completionTokens = 0;
        var estimated = false;
        long activeMs = 0;
        string? budgetReason = null;

        // Эффективные пределы: дефолт режима, смёрженный с пользовательским (меньший выигрывает).
        var budget = McpTaskBudget.ForMode(McpTaskBudget.ParseMode(request.Mode))
            .MergeWith(request.Budget);
        var maxSteps = request.MaxSteps is { } rs
            ? (budget.MaxSteps is { } bs ? Math.Min(rs, bs) : rs)
            : budget.MaxSteps;
        var maxCost = request.MaxCostUsd > 0
            ? (budget.MaxCostUsd > 0 ? Math.Min(request.MaxCostUsd, budget.MaxCostUsd) : request.MaxCostUsd)
            : budget.MaxCostUsd;

        if (request.Resume != null)
        {
            foreach (var k in request.Resume.Executed)
            {
                var rk = Key(k.AdapterName, k.ArgsJson);
                if (k.Risk == -2) { /* poll-вызов: повтор не блокируем */ }
                else if (k.Risk > (int)McpActionRisk.ReadOnly || k.Risk < 0)
                    executedRisk[rk] = k.Risk;
                else if (k.ResultHash != null)
                    readHashByKey[rk] = k.ResultHash;
                executedKeys.Add(k);
            }
            costUsd = request.Resume.CostUsd;
            promptTokens = request.Resume.PromptTokens;
            completionTokens = request.Resume.CompletionTokens;
            activeMs = request.Resume.ActiveMs;
            estimated = request.Resume.Estimated;
            result.Steps = request.Resume.Step;
        }
        var specByAdapter = new Dictionary<string, McpToolSpec>(StringComparer.Ordinal);
        foreach (var t in request.Tools) specByAdapter[t.AdapterName] = t;
        // Успешные вызовы инструментов: их фактический результат не должен
        // пропасть, даже если финальный запрос к модели не удался.
        var okResults = new List<(string Tool, string Content)>();
        var llmTools = request.Tools
            .Select(t => new ToolSpecDto(t.AdapterName, t.Description, t.ParametersJsonSchema))
            .ToList();

        // Что реально уходит модели (адаптерные имена) — для расширенного журнала.
        _journal?.AppendEvent("tools/offered", jobId: request.JobId,
            toolCount: llmTools.Count, tools: llmTools.Select(t => t.Name).ToList(),
            state: "offered");

        static string Key(string adapter, string argsJson) => adapter + "\n" + argsJson;

        // Оценка токенов, когда провайдер не вернул usage (повторная передача
        // контекста учитывается — оценивается вся текущая история).
        int Estimate(IReadOnlyList<ToolLoopMessage> ms)
        {
            var f = request.EstimateTokensFunc;
            var sum = 0;
            foreach (var m in ms)
                sum += f?.Invoke(m)
                    ?? BrainstormBuddy.Context.ContextBudgetResolver.EstimateTokens(
                        (m.Content ?? "") + string.Concat(
                            m.ToolCalls?.Select(c => c.ArgsJson) ?? Enumerable.Empty<string>()));
            return sum;
        }

        // Finish — локальная функция: забирает живые счётчики/messages из замыкания.
        McpToolLoopResult Finish()
        {
            result.CostUsd = costUsd;
            result.PromptTokens = promptTokens;
            result.CompletionTokens = completionTokens;
            result.Estimated = estimated;
            result.ElapsedActive = TimeSpan.FromMilliseconds(activeMs);
            result.BudgetReason = budgetReason;
            result.CanContinue = result.StopReason is McpLoopStopReason.BudgetExceeded
                or McpLoopStopReason.BudgetExhausted or McpLoopStopReason.TimeExhausted
                or McpLoopStopReason.MaxSteps or McpLoopStopReason.Paused
                or McpLoopStopReason.LlmPaused or McpLoopStopReason.LlmMcpDisabled;
            result.ExecutedKeys = new List<McpLoopSnapshot.ExecutedKey>(executedKeys);
            result.Messages.Clear();
            result.Messages.AddRange(messages);
            return result;
        }
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Пауза LLM или выключенный исходящий MCP → стоп без платного запроса.
                var mcpOff = request.IsMcpDisabledFunc?.Invoke() == true;
                if (request.IsLlmPausedFunc?.Invoke() == true || mcpOff)
                {
                    result.StopReason = mcpOff
                        ? McpLoopStopReason.LlmMcpDisabled
                        : McpLoopStopReason.LlmPaused;
                    result.Error = McpError.LlmPaused;
                    return Finish();
                }
                // Явная пауза job'а: ждать (чат) или прервать (job).
                if (request.Pause.IsPauseRequested)
                {
                    if (request.WaitWhilePaused)
                        await request.Pause.WaitWhilePausedAsync();
                    if (request.Pause.IsPauseRequested)
                    {
                        result.StopReason = McpLoopStopReason.Paused;
                        return Finish();
                    }
                }

                // Лимит активного времени режима (ожидание паузы не засчитано).
                if (budget.MaxActiveSeconds is { } mas &&
                    TimeSpan.FromMilliseconds(activeMs) >= TimeSpan.FromSeconds(mas))
                {
                    budgetReason = $"time: {mas}s";
                    result.StopReason = McpLoopStopReason.TimeExhausted;
                    return Finish();
                }
                if (maxSteps is { } ms && result.Steps >= ms)
                {
                    result.StopReason = McpLoopStopReason.MaxSteps;
                    return Finish();
                }
                result.Steps++;

                request.Progress?.Invoke("Запрос к LLM", result.Steps, maxSteps);
                // Поправка на окно модели: ответ помещается в остаток входного бюджета
                // (контекст уже занят историей), штатный потолок — 25k. Ниже 1024 не жмём.
                var maxTok = request.MaxTokens;
                if (request.ContextWindowFunc?.Invoke() is { } win && win > 0)
                    maxTok = Math.Max(1024, Math.Min(maxTok, win - Estimate(messages) - 2048));
                // Проекция расхода ДО платного запроса: оценка всего контекста
                // + ожидаемых токенов ответа. Уже за пределом → стоп, запрос не уходит.
                if (budget.MaxTotalTokens is { } mtt)
                {
                    var projected = promptTokens + completionTokens
                        + Estimate(messages) + maxTok;
                    if (projected > mtt)
                    {
                        budgetReason = $"tokens: {promptTokens + completionTokens}/{mtt}";
                        result.StopReason = McpLoopStopReason.BudgetExhausted;
                        _journal?.Append("budget", request.JobId, null, null,
                            "stop", null, "BUDGET", budgetReason);
                        return Finish();
                    }
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ask = await _llm.AskAsync(messages, llmTools, maxTok, ct).ConfigureAwait(false);
                activeMs += sw.ElapsedMilliseconds;
                _journal?.Append("llm", request.JobId, null, null,
                    ask.Error == null ? "ok" : "error", sw.ElapsedMilliseconds,
                    ask.Error?.Code, null, ask.TotalTokens,
                    ask.Error == null ? null : $"{ask.Error.Code}: {ask.Error.Message}");

                if (ask.Error != null)
                {
                    result.Error = ask.Error;
                    var cancelled = ask.Error.Code == "CANCELLED";
                    result.StopReason = cancelled
                        ? McpLoopStopReason.Cancelled : McpLoopStopReason.Error;
                    // Инструменты могли уже выполниться (задача создана, тикет изменён):
                    // терять их результат из-за отказа финального запроса нельзя —
                    // отдаём пользователю фактические данные вызовов.
                    // Отмена пользователем — не сбой: UI показывает нейтральное
                    // «Остановлено», предупреждение о неполученном ответе модели
                    // в чат/историю не пишется.
                    if (!cancelled)
                        result.FinalText = BuildToolResultFallback(okResults, ask.Error);
                    return Finish();
                }
                // Учёт расхода: usage из ответа; нет usage → честная оценка контекста.
                if (ask.PromptTokens > 0 || ask.CompletionTokens > 0)
                {
                    promptTokens += ask.PromptTokens;
                    completionTokens += ask.CompletionTokens;
                }
                else
                {
                    estimated = true;
                    promptTokens += Estimate(messages);
                    completionTokens += Math.Max(1, request.EstimateTokensFunc != null
                        ? request.EstimateTokensFunc(
                            ToolLoopMessage.Assistant(ask.AssistantContent, null))
                        : BrainstormBuddy.Context.ContextBudgetResolver.EstimateTokens(
                            ask.AssistantContent ?? ""));
                }
                // Провайдер молча прислал нули (usage-блок отсутствовал) — это не
                // «ноль потрачено», а «расход неизвестен»: вся сумма цикла помечается
                // оценочной, и счётчик токенов покажет «расход недоступен».
                if (!ask.UsageReported) estimated = true;
                costUsd += request.CostPerTokenUsd?.Invoke(ask.PromptTokens, ask.CompletionTokens) ?? 0;

                if (maxCost > 0 && costUsd >= maxCost)
                {
                    budgetReason = $"usd: {costUsd:F4}/{maxCost:F4}";
                    result.StopReason = McpLoopStopReason.BudgetExceeded;
                    _journal?.Append("budget", request.JobId, null, null,
                        "stop", null, "BUDGET", budgetReason);
                    return Finish();
                }
                if (budget.MaxTotalTokens is { } mtt2 &&
                    promptTokens + completionTokens > mtt2)
                {
                    budgetReason = $"tokens: {promptTokens + completionTokens}/{mtt2}";
                    result.StopReason = McpLoopStopReason.BudgetExhausted;
                    _journal?.Append("budget", request.JobId, null, null,
                        "stop", null, "BUDGET", budgetReason);
                    return Finish();
                }

                messages.Add(ToolLoopMessage.Assistant(ask.AssistantContent, ask.ToolCalls));

                if (ask.ToolCalls.Count == 0)
                {
                    result.FinalText = ask.AssistantContent;
                    result.StopReason = McpLoopStopReason.FinalText;
                    return Finish();
                }

                foreach (var call in ask.ToolCalls)
                {
                    // Валидность аргументов: кривой JSON — ошибка в tool-ответ, цикл живёт.
                    var argsJson = string.IsNullOrWhiteSpace(call.ArgsJson) ? "{}" : call.ArgsJson;
                    var argsValid = IsJson(argsJson);

                    var key = Key(call.Name, argsJson);

                    if (!specByAdapter.TryGetValue(call.Name, out var spec) ||
                        !request.Servers.TryGetValue(spec.ServerId, out var server))
                    {
                        messages.Add(ToolLoopMessage.ToolResult(call.Id,
                            $"ERROR: инструмент «{call.Name}» не найден или сервер отключён."));
                        result.ToolCalls.Add(new McpToolCallSummary(call.Name, "?", call.Name, false, false, 0));
                        continue;
                    }

                    var isPoll = spec.OriginalName.Contains("status", StringComparison.OrdinalIgnoreCase)
                        || spec.OriginalName.Contains("poll", StringComparison.OrdinalIgnoreCase)
                        || spec.OriginalName.Contains("progress", StringComparison.OrdinalIgnoreCase);

                    // Loop-guard (§6.5): повтор ЧТЕНИЯ без новых данных —
                    // сначала толчок «меняй план», после 3-й подряд итерации — стоп.
                    // Опрос job_status/прогресса в счёт не входит.
                    if (!isPoll && spec.Risk <= McpActionRisk.ReadOnly &&
                        key == lastReadKey && readHashByKey.TryGetValue(key, out var ph)
                        && ph == lastReadHash)
                    {
                        if (readNudged || mutRepeat >= 2)
                        {
                            result.StopReason = McpLoopStopReason.NoProgress;
                            return Finish();
                        }
                        readNudged = true;
                        messages.Add(ToolLoopMessage.ToolResult(call.Id,
                            "NO_NEW_DATA: этот же запрос уже выполнялся и ответ не изменился. " +
                            "Смени план: используй другой инструмент/аргументы или заверши ответ текстом."));
                        continue;
                    }

                    // Повтор ИЗМЕНЯЮЩЕГО действия: результат первого вызова неизвестен
                    // → не дублировать. Один толчок-объяснение, второй повтор — стоп.
                    if (!isPoll && executedRisk.ContainsKey(key))
                    {
                        mutRepeat++;
                        messages.Add(ToolLoopMessage.ToolResult(call.Id,
                            $"UNCERTAIN: вызов «{call.Name}» уже выполнялся — результат " +
                            "неизвестен/возможно применён. Повтор НЕ выполнен (защита от дубля). " +
                            "Проверь чтением или уточни у пользователя."));
                        result.ToolCalls.Add(new McpToolCallSummary(call.Name, spec.ServerName,
                            spec.OriginalName, false, false, 0) { Uncertain = true });
                        if (mutRepeat >= 2 || readNudged)
                        {
                            result.StopReason = McpLoopStopReason.NoProgress;
                            return Finish();
                        }
                        continue;
                    }

                    if (!argsValid)
                    {
                        messages.Add(ToolLoopMessage.ToolResult(call.Id,
                            "ERROR: некорректный JSON аргументов."));
                        result.ToolCalls.Add(new McpToolCallSummary(spec.AdapterName, spec.ServerName,
                            spec.OriginalName, false, false, 0));
                        continue;
                    }

                    // Gate подтверждения: риск + trust + ApprovedTools.
                    if (McpOutgoingApproval.NeedsApproval(server, spec))
                    {
                        request.Progress?.Invoke($"Подтверждение «{spec.OriginalName}»", null, null);
                        _journal?.AppendEvent("approval", server: spec.ServerName,
                            tool: spec.OriginalName, argsJson: argsJson, approval: "requested",
                            jobId: request.JobId, state: "requested");
                        bool approved;
                        try
                        {
                            approved = request.ApprovalFunc == null ||
                                await request.ApprovalFunc(server, spec, argsJson, spec.Risk).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _journal?.AppendEvent("approval", server: spec.ServerName,
                                tool: spec.OriginalName, approval: "cancelled",
                                jobId: request.JobId, state: "cancelled");
                            throw;
                        }
                        _journal?.AppendEvent("approval", server: spec.ServerName,
                            tool: spec.OriginalName, argsJson: argsJson,
                            approval: approved ? "approved" : "denied",
                            jobId: request.JobId, state: approved ? "approved" : "denied");
                        if (!approved)
                        {
                            _journal?.Append("tool", request.JobId, spec.ServerName, spec.OriginalName,
                                "denied", null, McpError.ApprovalDenied.Code, argsJson);
                            messages.Add(ToolLoopMessage.ToolResult(call.Id,
                                $"ERROR: вызов «{spec.OriginalName}» отклонён пользователем (APPROVAL_DENIED). " +
                                "Не повторяй это действие, продолжай без него или сообщи пользователю."));
                            result.ToolCalls.Add(new McpToolCallSummary(spec.AdapterName, spec.ServerName,
                                spec.OriginalName, false, true, 0));
                            continue; // модель информирована, цикл продолжается
                        }
                    }

                    request.Progress?.Invoke(FriendlyStage(spec), null, null);
                    McpToolCallResult callResult;
                    _journal?.AppendEvent("tools/call", server: spec.ServerName, tool: spec.AdapterName,
                        argsJson: argsJson, jobId: request.JobId, state: "begin");
                    try
                    {
                        var progress = new Progress<McpToolProgress>(p =>
                            request.Progress?.Invoke(
                                string.IsNullOrWhiteSpace(p.Message) ? spec.OriginalName : p.Message,
                                p.Done is { } d ? (int)d : null,
                                p.Total is { } t ? (int)t : null));
                        callResult = await _transport.CallToolAsync(server, spec, argsJson, progress, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _journal?.Append("tool", request.JobId, spec.ServerName, spec.OriginalName,
                            "transport-error", null, "TRANSPORT", argsJson);
                        // Запись об ошибке вызова ведёт транспорт (McpClientManager):
                        // там же HTTP-статус. Дублировать нельзя — иначе одна
                        // неудача даёт две записи об ошибке.
                        messages.Add(ToolLoopMessage.ToolResult(call.Id,
                            $"ERROR: транспорт MCP-сервера «{server.Name}»: {ex.Message}"));
                        result.ToolCalls.Add(new McpToolCallSummary(spec.AdapterName, spec.ServerName,
                            spec.OriginalName, false, false, 0));
                        continue;
                    }

                    _journal?.Append("tool", request.JobId, spec.ServerName, spec.OriginalName,
                        callResult.IsError ? "tool-error" : "ok", callResult.DurationMs,
                        callResult.IsError ? "TOOL_ERROR" : null, argsJson, callResult.Content.Length);

                    // Расширенный след вызова: длительность, признак ошибки и превью
                    // результата (обрезанное, секреты вырезаны).
                    var longResult = callResult.Content.Length > MaxResultPreviewChars;
                    var resultFile = longResult
                        ? _journal?.SpillResult(spec.ServerName, spec.OriginalName, callResult.Content)
                        : null;
                    var preview = longResult
                        ? callResult.Content[..MaxResultPreviewChars]
                        : callResult.Content;
                    _journal?.AppendEvent("tools/call", server: spec.ServerName, tool: spec.AdapterName,
                        durationMs: callResult.DurationMs, isError: callResult.IsError,
                        resultPreview: preview, truncated: longResult, resultFile: resultFile,
                        jobId: request.JobId, state: "result");

                    var content = callResult.Content;
                    if (content.Length > MaxToolResultChars)
                    {
                        // Спека §6.3: полный результат — вне контекста на диске,
                        // в контекст — фрагмент + указатель на buddy_read_tool_result.
                        var serverCut = LooksServerTruncated(content);
                        var entry = _toolResults.Save(content,
                            toolName: spec.ServerName + "." + spec.OriginalName,
                            jobId: request.JobId, serverTruncated: serverCut);
                        var frag = content[..MaxToolResultChars];
                        var hint = serverCut
                            ? "исходный сервер сам ограничил ответ — полного текста может не быть"
                            : "полный текст сохранён, дочитывай постранично";
                        content = frag + "\n…[result saved]\n" + JsonSerializer.Serialize(new
                        {
                            toolResultId = entry.Id,
                            totalChars = entry.TotalChars,
                            hasMore = true,
                            truncated = true,
                            reason = serverCut ? "server" : "context-limit",
                            readMoreTool = "buddy_read_tool_result",
                            hint,
                        });
                    }
                    messages.Add(ToolLoopMessage.ToolResult(call.Id,
                        callResult.IsError ? "ERROR: " + content : content));
                    result.ToolCalls.Add(new McpToolCallSummary(spec.AdapterName, spec.ServerName,
                        spec.OriginalName, !callResult.IsError, false, callResult.DurationMs));
                    if (!callResult.IsError)
                        okResults.Add((spec.ServerName + "." + spec.OriginalName, content));

                    // Время работы инструмента — в активный счётчик; сброс толчков/повторов.
                    activeMs += callResult.DurationMs;
                    readNudged = false;
                    mutRepeat = 0;

                    // Loop-guard: хэш ответа чтения + серия подряд; изменяющие — в executedRisk.
                    var hash = isPoll ? null : Hash(callResult.Content);
                    if (spec.Risk <= McpActionRisk.ReadOnly && !isPoll)
                    {
                        readHashByKey[key] = hash!;
                        if (key == lastReadKey && hash == lastReadHash) readStreak++;
                        else { lastReadKey = key; lastReadHash = hash; readStreak = 1; }
                        if (readStreak >= 3)
                        {
                            result.StopReason = McpLoopStopReason.NoProgress;
                            return Finish();
                        }
                    }
                    else
                    {
                        lastReadKey = null;
                        if (!isPoll) executedRisk[key] = (int)spec.Risk;
                    }
                    executedKeys.Add(new McpLoopSnapshot.ExecutedKey(
                        call.Name, argsJson, isPoll ? -2 : (int)spec.Risk, hash));

                    // Точка снапшота: выполненные вызовы не повторяем после рестарта.
                    if (request.SnapshotFunc != null)
                    {
                        var snap = new McpLoopSnapshot
                        {
                            Messages = new List<ToolLoopMessage>(messages),
                            Executed = new List<McpLoopSnapshot.ExecutedKey>(executedKeys),
                            Step = result.Steps,
                            CostUsd = costUsd,
                            PromptTokens = promptTokens,
                            CompletionTokens = completionTokens,
                            ActiveMs = activeMs,
                            Estimated = estimated,
                        };
                        await request.SnapshotFunc(snap).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.StopReason = McpLoopStopReason.Cancelled;
            return Finish();
        }
    }

    private static bool IsJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Запасной текст ответа, когда финальный запрос к модели не удался, но
    /// инструменты уже отработали. Пользователь должен видеть фактический
    /// результат (номер/ссылку созданной задачи), а не пустоту.
    /// </summary>
    private string? BuildToolResultFallback(
        List<(string Tool, string Content)> okResults, McpError error)
    {
        if (okResults.Count == 0) return null;
        var secrets = _journal?.RegisteredSecrets;
        var sb = new System.Text.StringBuilder();
        sb.Append("[WARN] Финальный ответ модели не получен (")
          .Append(error.Code).Append(": ").Append(error.Message).AppendLine(").");
        sb.AppendLine("Инструменты выполнены, фактические результаты:");
        foreach (var (tool, content) in okResults)
        {
            // Тот же секрет, что маскируется в журнале, не должен попасть
            // в ответ чата (он сохраняется в истории встреч).
            var text = McpJournal.Redact((content ?? "").Trim(), secrets);
            if (text.Length > 600) text = text[..600] + "…";
            sb.Append("* ").Append(tool).Append(" → ").AppendLine(text);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Эвристика «сервер сам обрезал»: …/[truncated] в хвосте результата.</summary>
    private static bool LooksServerTruncated(string content)
    {
        var tail = content.Length > 200 ? content[^200..] : content;
        return tail.Contains("…", StringComparison.Ordinal) ||
               tail.Contains("[truncated", StringComparison.OrdinalIgnoreCase) ||
               tail.Contains("truncated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Стабильный хэш содержимого ответа (FNV-1a) — переживает рестарт процесса.</summary>
    private static string Hash(string s)
    {
        unchecked
        {
            var h = 1469598103934665603UL;
            foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Понятный этап для встроенного поиска: сырое имя memory_search
    /// в пузыре выглядело технической строкой (спека «сделай поведение понятным»).</summary>
    private static string FriendlyStage(McpToolSpec spec) => spec.ServerId == BuiltinMemoryTools.ServerId
        ? spec.OriginalName switch
        {
            BuiltinMemoryTools.OriginalMemorySearch => UiText.Tr("Ищу в памяти", "Searching memory"),
            BuiltinMemoryTools.OriginalMeetingsSearch => UiText.Tr("Проверяю прошлые встречи", "Checking past meetings"),
            BuiltinMemoryTools.OriginalSourceRead => UiText.Tr("Читаю найденные источники", "Reading found sources"),
            _ => spec.OriginalName,
        }
        : spec.OriginalName;

}
