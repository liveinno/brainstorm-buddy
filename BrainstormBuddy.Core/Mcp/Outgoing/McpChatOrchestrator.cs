using System.Text;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Images;
using BrainstormBuddy.Core.Mcp.Jobs;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>Ответ оркестратора для карточки чата.</summary>
public sealed record McpChatAnswer(
    string? Text,
    IReadOnlyList<McpToolCallSummary> ToolCalls,
    IReadOnlyList<string> Sources,
    double CostUsd,
    McpLoopStopReason StopReason,
    McpError? Error)
{
    /// <summary>Использованные токены запроса (usage или оценка).</summary>
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    /// <summary>true → есть оценочные запросы без usage.</summary>
    public bool Estimated { get; init; }
    /// <summary>Активное время цикла.</summary>
    public TimeSpan ElapsedActive { get; init; }
    /// <summary>Пояснение бюджета (BudgetExhausted/TimeExhausted).</summary>
    public string? BudgetReason { get; init; }
    /// <summary>true → бюджет исчерпан, но можно продолжить тем же checkpoint'ом.</summary>
    public bool CanContinue { get; init; }
    /// <summary>Сквозной идентификатор запроса (12 hex) — для расширенного журнала.</summary>
    public string? RequestId { get; init; }
    /// <summary>Источники встроенного поиска, выданные модели именно в этом
    /// ответе (снимок состояния запроса, не общий список транспорта).</summary>
    public IReadOnlyList<string> Citations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Точка входа исходящего MCP для чата Buddy. Собирает инструменты включённых
/// серверов, прогоняет bounded tool-loop и возвращает финальный ответ + сводку
/// вызовов для карточки. Плюс фоновые research-job через McpResearchJobRunner.
/// </summary>
public sealed class McpChatOrchestrator : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<McpOutgoingServer>> _servers;
    private readonly McpClientManager _clients;
    private readonly McpToolLoop _loop;
    private readonly IMcpToolLlm _llm;
    private readonly McpJournal _journal;
    private readonly LoggingService? _logger;
    private readonly IMcpToolTransport _transport;
    /// <summary>Управляем ли клиент-менеджером (null в ctor → создали свой → dispose
    /// наш; переданный снаружи → владелец снаружи, не трогаем).</summary>
    private readonly bool _ownsClients;

    /// <summary>Пауза LLM приложения — вызов не уходит, чат ждёт/останавливается.</summary>
    public Func<bool>? IsLlmPausedFunc { get; set; }
    /// <summary>Исходящий MCP выключен (Mcp.Enabled=false) — платные шаги запрещены.</summary>
    public Func<bool>? IsMcpDisabledFunc { get; set; }
    /// <summary>Встроенный поиск Buddy (память/встречи) доступен без внешнего MCP.
    /// true → в tool-loop добавляется синтетический сервер <see cref="BuiltinMemoryTools.Server"/>.</summary>
    public Func<bool>? BuiltinToolsEnabled { get; set; }
    /// <summary>Бюджет дополнительного поиска модели (токены, уже урезанный под окно).
    /// Передаётся встроенному транспорту на каждый запрос — общий расход между вызовами.</summary>
    public Func<int>? BuiltinExtraBudgetTokens { get; set; }
    /// <summary>Подтверждение опасного действия в UI (диалог): → разрешить?</summary>
    public Func<McpOutgoingServer, McpToolSpec, string, McpActionRisk, Task<bool>>? ApprovalFunc { get; set; }
    /// <summary>Статус-строки для карточки («Ищу по встречам», «Изучено 18/46»).</summary>
    public Action<string?, int?, int?>? Progress { get; set; }
    /// <summary>Системный промпт по умолчанию для MCP-запросов чата.</summary>
    public Func<string>? SystemPromptFunc { get; set; }
    /// <summary>Оценка стоимости (prompt, completion) → USD.</summary>
    public Func<int, int, double>? CostPerTokenUsd { get; set; }
    /// <summary>Потолок стоимости одного чат-запроса (0 = без лимита).</summary>
    public double ChatMaxCostUsd { get; set; }
    /// <summary>Входное окно модели в токенах (ContextBudgetResolver.InputBudget) —
    /// зажимает штатный максимум ответа 25 000 под реальное окно.</summary>
    public Func<int>? ContextWindowFunc { get; set; }

    private readonly PauseTokenSource _chatPause = new();

    public McpChatOrchestrator(
        Func<IReadOnlyList<McpOutgoingServer>> servers,
        IMcpToolLlm llm,
        McpJournal journal,
        McpClientManager? clients = null,
        IMcpToolTransport? transport = null,
        LoggingService? logger = null)
    {
        _servers = servers;
        _llm = llm;
        _journal = journal;
        _clients = clients ?? new McpClientManager(logger: logger);
        _transport = transport ?? _clients;
        _loop = new McpToolLoop(llm, _transport, journal);
        _logger = logger;
        _ownsClients = clients == null;
    }

    /// <summary>Клиент-менеджер (для статусов серверов в настройках).</summary>
    public McpClientManager Clients => _clients;
    /// <summary>Явная пауза/возобновление синхронного чата с инструментами.</summary>
    public void PauseChat() => _chatPause.Pause();
    public void ResumeChat() => _chatPause.Resume();

    /// <summary>
    /// Вопрос → ответ с инструментами. history — предыдущие user/assistant сообщения
    /// чата (ChatMessage). Если включённых серверов с инструментами нет — loop не
    /// нужен: вернётся null, coordinator идёт обычным AskAsync.
    /// </summary>
    public async Task<McpChatAnswer?> AskWithToolsAsync(
        string question, IReadOnlyList<ChatMessage>? history = null,
        string? systemPrompt = null, int? maxSteps = null, int maxTokens = 25_000,
        string mode = "normal",
        IReadOnlyList<ChatImagePayload>? images = null,
        string? requestId = null,
        Action<string?, int?, int?>? progress = null,
        CancellationToken ct = default)
    {
        var previousRequestId = _journal.CurrentRequestId;
        requestId ??= NewRequestId();
        _journal.CurrentRequestId = requestId;
        // Встроенный транспорт хранит состояние НА ЗАПРОС (AsyncLocal): бюджет,
        // счётчики, источники и дедуп не пересекаются у одновременных вопросов.
        // state — единственный источник итоговых цитат именно этого ответа.
        BuiltinMemoryTools.RequestState? builtinState = null;
        if (BuiltinToolsEnabled?.Invoke() == true && _transport is BuiltinMemoryTools builtin)
            builtinState = builtin.BeginRequest(requestId, BuiltinExtraBudgetTokens?.Invoke() ?? 32_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var servers = _servers().Where(s => s.Enabled).ToList();
            if (BuiltinToolsEnabled?.Invoke() == true)
                servers.Add(BuiltinMemoryTools.Server);
            if (servers.Count == 0)
            {
                _journal.AppendEvent("chat", state: "begin", toolCount: 0,
                    detail: "servers=0", durationMs: sw.ElapsedMilliseconds, requestId: requestId);
                _journal.AppendEvent("chat", state: "end", detail: "stop=no-servers",
                    durationMs: sw.ElapsedMilliseconds, requestId: requestId);
                return null;
            }

            var tools = new List<McpToolSpec>();
            var byId = servers.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var s in servers)
            {
                try
                {
                    tools.AddRange(await _transport.ListToolsAsync(s, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.Warn($"MCP чат: сервер «{s.Name}» недоступен: {ex.Message}", "Mcp");
                    _journal.AppendEvent("error", server: s.Name, detail: ex.Message,
                        stack: ex.StackTrace, isError: true, state: "tools/list",
                        errorKind: McpOutgoingError.Classify(ex).Kind.ToString(),
                        requestId: requestId);
                    byId.Remove(s.Id);
                }
            }
            if (tools.Count == 0)
            {
                // нет инструментов — обычный путь
                _journal.AppendEvent("chat", state: "begin", toolCount: 0,
                    detail: $"servers={servers.Count}", durationMs: sw.ElapsedMilliseconds,
                    requestId: requestId);
                _journal.AppendEvent("chat", state: "end", detail: "stop=no-tools",
                    durationMs: sw.ElapsedMilliseconds, requestId: requestId);
                return null;
            }

            _journal.AppendEvent("chat", state: "begin", toolCount: tools.Count,
                tools: tools.Select(t => t.AdapterName).ToList(),
                detail: $"servers={servers.Count}", durationMs: sw.ElapsedMilliseconds,
                requestId: requestId);
            _journal.AppendEvent("tools/offered", state: "offered", toolCount: tools.Count,
                tools: tools.Select(t => t.AdapterName).ToList(),
                detail: $"servers={servers.Count}", requestId: requestId);

            var messages = new List<ToolLoopMessage>
            {
                ToolLoopMessage.System(systemPrompt ?? SystemPromptFunc?.Invoke() ??
                    "Ты — Buddy, ассистент встреч. Доступны встроенный поиск по памяти " +
                    "и прошлым встречам, а также инструменты внешних MCP-серверов: " +
                    "используй их, когда они помогают ответить. Отвечай кратко по-русски."),
            };
            if (history != null)
                foreach (var m in history)
                    messages.Add(new ToolLoopMessage(m.Role, m.Content));
            messages.Add(images is { Count: > 0 }
                ? ToolLoopMessage.UserWithImages(question, images)
                : ToolLoopMessage.User(question));

            var result = await _loop.RunAsync(new McpToolLoopRequest
            {
                Messages = messages,
                Tools = tools,
                Servers = byId,
                MaxSteps = maxSteps,
                Mode = mode,
                MaxTokens = maxTokens,
                MaxCostUsd = ChatMaxCostUsd,
                CostPerTokenUsd = CostPerTokenUsd,
                ApprovalFunc = ApprovalFunc,
                IsLlmPausedFunc = IsLlmPausedFunc,
                IsMcpDisabledFunc = IsMcpDisabledFunc,
                Progress = progress ?? Progress,
                ContextWindowFunc = ContextWindowFunc,
                Pause = _chatPause.Token,
                WaitWhilePaused = true,
            }, ct).ConfigureAwait(false);

            // Остановка по лимиту/шагам/отсутствию прогресса не должна давать
            // пустую карточку: результаты поиска уже есть, поэтому просим модель
            // сформулировать ответ без инструментов. Это не новый поиск и не
            // продолжение цикла — один финальный запрос по найденному.
            if (NeedsLimitedFinalization(result))
                await FinalizeLimitedAnswerAsync(result, question, requestId, ct).ConfigureAwait(false);

            var sources = result.ToolCalls
                .Where(c => c.Success)
                .Select(c => $"{c.ServerName}/{c.ToolName}")
                .Distinct()
                .ToList();
            _journal.AppendEvent("chat", state: "end",
                detail: $"stop={result.StopReason} calls={result.ToolCalls.Count}"
                    + (result.Error == null ? "" : $" error={result.Error.Code}: {result.Error.Message}"),
                durationMs: sw.ElapsedMilliseconds, isError: result.Error != null,
                requestId: requestId);
            // Причина завершения tool-loop + расход встроенного бюджета — по тому
            // же rid, что автоподбор и журнал MCP (сквозная диагностика запроса).
            var builtinSpent = builtinState != null
                ? $" builtinTokens={builtinState.SpentTokens}/{builtinState.BudgetTokens}" +
                  $" builtinMemoryTokens={builtinState.MemorySpentTokens}" +
                  $" builtinMeetingTokens={builtinState.MeetingSpentTokens}" +
                  $" builtinReadTokens={builtinState.ReadSpentTokens}" +
                  $" builtinCalls={builtinState.SearchCallCount}" +
                  $" builtinStop={BuiltinStopReason(builtinState, result)}"
                : "";
            _logger?.Info($"MEMDIAG rid={requestId} toolloop stop={result.StopReason} " +
                $"calls={result.ToolCalls.Count} prompt={result.PromptTokens} " +
                $"completion={result.CompletionTokens} elapsed={sw.ElapsedMilliseconds}ms{builtinSpent}" +
                (result.BudgetReason is { Length: > 0 } br ? $" budget={br}" : ""), "Memory");
            return new McpChatAnswer(result.FinalText, result.ToolCalls, sources,
                result.CostUsd, result.StopReason, result.Error)
            {
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                Estimated = result.Estimated,
                ElapsedActive = result.ElapsedActive,
                BudgetReason = result.BudgetReason,
                CanContinue = result.CanContinue,
                RequestId = requestId,
                Citations = builtinState?.Citations ?? Array.Empty<string>(),
            };
        }
        finally
        {
            _journal.CurrentRequestId = previousRequestId;
        }
    }

    /// <summary>
    /// Причина останова встроенного поиска: собственная (лимит/бюджет/нет новых
    /// результатов) либо причина останова цикла - чтобы «-» не выдавался за причину.
    /// </summary>
    private static string BuiltinStopReason(BuiltinMemoryTools.RequestState state, McpToolLoopResult result)
    {
        if (!string.IsNullOrEmpty(state.LastStopReason)) return state.LastStopReason!;
        return result.StopReason == McpLoopStopReason.FinalText
            ? "model-done"
            : result.StopReason.ToString();
    }

    /// <summary>Пора ли сформировать ответ по уже найденному: цикл остановлен
    /// лимитом/шагами/прогрессом, инструменты отработали, но текста нет.</summary>
    private static bool NeedsLimitedFinalization(McpToolLoopResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.FinalText)) return false;
        if (result.StopReason is McpLoopStopReason.Cancelled or McpLoopStopReason.Paused
            or McpLoopStopReason.LlmPaused or McpLoopStopReason.LlmMcpDisabled) return false;
        return result.ToolCalls.Any(c => c.Success);
    }

    /// <summary>
    /// Финальный запрос без инструментов после останова цикла. Пользователь
    /// получает ответ по фактически найденным фрагментам с явной пометкой об
    /// ограничении, а не пустую карточку. Если финальный запрос тоже не удался —
    /// отдаём компактную выжимку результатов.
    /// </summary>
    private async Task FinalizeLimitedAnswerAsync(
        McpToolLoopResult result, string question, string? requestId, CancellationToken ct)
    {
        var digest = BuildLimitedDigest(result.Messages);
        var fallback = BuildLimitedFallback(digest, result.StopReason, requestId);
        try
        {
            var system = result.Messages.FirstOrDefault(m => m.Role == "system")?.Content
                ?? "Ты — Buddy, ассистент встреч. Отвечай кратко по-русски.";
            var finalMessages = new List<ToolLoopMessage>
            {
                ToolLoopMessage.System(system + "\n\n" + UiText.Tr(
                    "Поиск уже остановлен по лимиту. Не вызывай инструменты. Ответь на исходный " +
                    "вопрос только по фрагментам ниже. Если ответа в них нет — прямо скажи: " +
                    "«в проверенных источниках нет данных». В конце одной строкой укажи, что поиск " +
                    "был ограничен и часть источников могла не попасть в ответ.",
                    "The search has already stopped at its limit. Do not call tools. Answer the " +
                    "original question only from the fragments below. If they contain no answer, " +
                    "say directly: \"the checked sources contain no data\". End with one line " +
                    "noting that the search was limited and some sources may be missing.")),
                ToolLoopMessage.User(question + "\n\n" + UiText.Tr(
                    "Найденные фрагменты (поиск остановлен):\n", "Found fragments (search stopped):\n")
                    + digest),
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var final = await _llm.AskAsync(finalMessages, Array.Empty<ToolSpecDto>(), 1500, ct)
                .ConfigureAwait(false);
            result.PromptTokens += final.PromptTokens;
            result.CompletionTokens += final.CompletionTokens;
            if (!final.UsageReported) result.Estimated = true;
            result.CostUsd += CostPerTokenUsd?.Invoke(final.PromptTokens, final.CompletionTokens) ?? 0;
            if (final.Error == null && !string.IsNullOrWhiteSpace(final.AssistantContent))
                result.FinalText = final.AssistantContent.Trim();
            else
                result.FinalText = fallback;
            _journal.AppendEvent("chat", state: "finalize",
                detail: $"stop={result.StopReason} chars={result.FinalText?.Length ?? 0}"
                    + (final.Error == null ? "" : $" error={final.Error.Code}: {final.Error.Message}"),
                durationMs: sw.ElapsedMilliseconds, isError: final.Error != null,
                requestId: requestId);
            _logger?.Info($"MEMDIAG rid={requestId} finalize stop={result.StopReason} " +
                $"prompt={final.PromptTokens} completion={final.CompletionTokens} " +
                $"answerChars={result.FinalText?.Length ?? 0} " +
                $"{(final.Error == null ? "ok" : "error=" + final.Error.Code)}", "Memory");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.FinalText = fallback;
            _logger?.Warn($"MCP финальный ответ после лимита не сформирован: {ex.Message}", "Mcp");
            _journal.AppendEvent("error", state: "finalize", detail: ex.Message,
                stack: ex.StackTrace, isError: true, requestId: requestId);
        }
    }

    /// <summary>Компактная выжимка успешных tool-ответов (до 8 штук по 1400 символов).</summary>
    private string BuildLimitedDigest(IReadOnlyList<ToolLoopMessage> messages)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in messages)
            if (m.ToolCalls != null)
                foreach (var call in m.ToolCalls)
                    names[call.Id] = call.Name;
        var sb = new StringBuilder();
        var taken = 0;
        foreach (var m in messages)
        {
            if (m.Role != "tool" || string.IsNullOrWhiteSpace(m.Content)) continue;
            var text = McpJournal.Redact(m.Content.Trim(), _journal.RegisteredSecrets);
            if (text.StartsWith("ERROR", StringComparison.Ordinal)) continue;
            var name = m.ToolCallId != null && names.TryGetValue(m.ToolCallId, out var n)
                ? n : "tool";
            if (text.Length > 1400) text = text[..1400] + "…";
            sb.Append("## ").Append(name).AppendLine().AppendLine(text).AppendLine();
            if (++taken >= 8) break;
        }
        return sb.Length == 0 ? UiText.Tr("(результатов нет)", "(no results)") : sb.ToString();
    }

    /// <summary>Текст на случай, если и финальный запрос к модели не удался.</summary>
    private static string BuildLimitedFallback(string digest, McpLoopStopReason stop, string? requestId)
    {
        var sb = new StringBuilder();
        sb.AppendLine(UiText.Tr("Поиск остановлен по лимиту; итоговый ответ модели не получен.",
            "The search stopped at its limit; the final model answer was not received."));
        sb.AppendLine(UiText.Tr("Фактические результаты поиска:", "Actual search results:"));
        var clipped = digest.Length > 3000 ? digest[..3000] + "…" : digest;
        sb.AppendLine(clipped.Trim());
        sb.AppendLine();
        sb.Append(UiText.Tr("Причина остановки: ", "Stop reason: ")).Append(stop);
        if (!string.IsNullOrWhiteSpace(requestId))
            sb.Append(" · requestId ").Append(requestId);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Сквозной идентификатор запроса: 12 hex-символов.</summary>
    private static string NewRequestId()
        => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Запустить research-job раннер (вызывать один раз при старте приложения).
    /// executors — исполнители kinds без LLM (transcribe/extract_facts).</summary>
    public McpResearchJobRunner CreateJobRunner(IMcpJobStore store,
        McpResearchJobRunner.Handlers? handlers = null,
        IReadOnlyList<IMcpJobExecutor>? executors = null)
        => new(store, _transport, _llm, _servers, _journal,
            handlers ?? new McpResearchJobRunner.Handlers
            {
                ApprovalFunc = ApprovalFunc != null
                    ? (server, spec, args, risk) => ApprovalFunc(server, spec, args, risk)
                    : null,
                IsLlmPausedFunc = IsLlmPausedFunc != null ? () => IsLlmPausedFunc() : null,
                IsMcpDisabledFunc = IsMcpDisabledFunc != null ? () => IsMcpDisabledFunc() : null,
                CostPerTokenUsd = CostPerTokenUsd != null ? (p, c) => CostPerTokenUsd(p, c) : null,
                SystemPromptFunc = SystemPromptFunc != null ? () => SystemPromptFunc() : null,
                ContextWindowFunc = ContextWindowFunc != null ? () => ContextWindowFunc() : null,
            },
            _logger, executors);

    /// <summary>Перечитать конфигурацию серверов (настройки изменились).</summary>
    public async Task RefreshServersAsync()
    {
        foreach (var s in _servers())
            await _clients.DisconnectAsync(s).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _chatPause.Resume();
        if (_ownsClients)
            await _clients.DisposeAsync().ConfigureAwait(false);
    }
}
