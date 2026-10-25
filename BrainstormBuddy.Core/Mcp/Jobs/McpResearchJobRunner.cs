using System.Text.Json;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;
using BrainstormBuddy.Core.Mcp.Outgoing;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Core.Mcp.Jobs;

/// <summary>
/// Фоновый исполнитель исходящих MCP-исследований: берёт McpJob из стора,
/// прогоняет McpToolLoop, на ожиданиях (approval/budget/connection/pause)
/// персистит ResumeToken — после рестарта выполненные вызовы не повторяются.
/// </summary>
public sealed class McpResearchJobRunner : IAsyncDisposable
{
    /// <summary>ArgsJson задачи вида kind="research"/"search"/"resume".</summary>
    public sealed record ResearchArgs(
        string Question,
        string? SystemPrompt = null,
        List<ToolLoopMessage>? History = null,
        /// <summary>Явный лимит шагов (null = безлимит; бюджеты режима регулируют).</summary>
        int? MaxSteps = null,
        /// <summary>Штатный максимум одного ответа (§6.5: 25 000, зажато окном модели).</summary>
        int MaxTokens = 25_000,
        double MaxCostUsd = 0,
        /// <summary>Режим задачи: «research» (1М токенов/45 мин) или «normal» (200К/10 мин).</summary>
        string? Mode = "normal",
        /// <summary>Продолжать платную работу самому после рестарта процесса.</summary>
        bool AutoResume = false);

    /// <summary>Делегаты UI/приложения — пробрасываются из McpChatOrchestrator.</summary>
    public sealed class Handlers
    {
        /// <summary>Подтверждение опасного вызова (диалог в UI). null → считаем отказом.</summary>
        public Func<McpOutgoingServer, McpToolSpec, string, McpActionRisk, Task<bool>>? ApprovalFunc { get; init; }
        /// <summary>Подтверждение продолжения после исчерпания бюджета.</summary>
        public Func<McpJob, Task<bool>>? BudgetExtendFunc { get; init; }
        /// <summary>Пауза LLM приложения (overlay). null → нет паузы.</summary>
        public Func<bool>? IsLlmPausedFunc { get; init; }
        /// <summary>Исходящий MCP выключен (Mcp.Enabled=false). null → нет блокировки.</summary>
        public Func<bool>? IsMcpDisabledFunc { get; init; }
        /// <summary>Оценка стоимости (promptTokens, completionTokens) → USD.</summary>
        public Func<int, int, double>? CostPerTokenUsd { get; init; }
        /// <summary>Системный промпт по умолчанию для research-job.</summary>
        public Func<string>? SystemPromptFunc { get; init; }
        /// <summary>Входное окно модели (ContextBudgetResolver.InputBudget) — зажимает MaxTokens.</summary>
        public Func<int>? ContextWindowFunc { get; init; }
    }

    private sealed record ResumeTokenDto(
        List<ToolLoopMessage> Messages,
        List<McpLoopSnapshot.ExecutedKey> Executed,
        int Step,
        double CostUsd,
        int BudgetExtensions,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        long ActiveMs = 0,
        bool Estimated = false);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly int MaxConnectionRetries = 3;

    private readonly IMcpJobStore _store;
    private readonly IMcpToolTransport _transport;
    private readonly IMcpToolLlm _llm;
    private readonly Func<IReadOnlyList<McpOutgoingServer>> _servers;
    private readonly McpJournal _journal;
    private readonly Handlers _handlers;
    private readonly LoggingService? _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _pump;
    private CancellationTokenSource? _cts;

    /// <summary>Текущий job для Pause/Resume снаружи (один исполнитель = один job за раз).</summary>
    private readonly object _pauseGate = new();
    private PauseTokenSource? _paused;

    private readonly IReadOnlyList<IMcpJobExecutor> _executors;

    public McpResearchJobRunner(
        IMcpJobStore store, IMcpToolTransport transport, IMcpToolLlm llm,
        Func<IReadOnlyList<McpOutgoingServer>> servers, McpJournal journal,
        Handlers handlers, LoggingService? logger = null,
        IReadOnlyList<IMcpJobExecutor>? executors = null)
    {
        _store = store;
        _transport = transport;
        _llm = llm;
        _servers = servers;
        _journal = journal;
        _handlers = handlers;
        _logger = logger;
        _executors = executors ?? Array.Empty<IMcpJobExecutor>();
    }

    /// <summary>Запуск фонового цикла (идемпотентно).</summary>
    public void Start()
    {
        if (_pump != null) return;
        _cts = new CancellationTokenSource();
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Пинок после enqueue/изменения конфига — без ожидания опроса.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Поставить текущий job на паузу (новая итерация loop не стартует, токен персистится).</summary>
    public void Pause()
    {
        lock (_pauseGate) _paused ??= new PauseTokenSource();
        _paused?.Pause();
    }

    /// <summary>Снять паузу (job дойдёт до следующего WaitingConnection/queued-опроса и продолжит).</summary>
    public void Resume()
    {
        lock (_pauseGate) { _paused?.Resume(); _paused = null; }
        Wake();
    }

    /// <summary>Идемпотентная постановка research-задачи (ключ = IdempotencyKey).
    /// Resumable по умолчанию: job переживает перезапуск (чекпоинт в ResumeToken).</summary>
    public async Task<McpJob> EnqueueResearchAsync(ResearchArgs args, string idempotencyKey,
        string connectionId = "", CancellationToken ct = default)
    {
        var (job, created) = await _store.EnqueueAsync(new McpJob
        {
            Id = Guid.NewGuid().ToString("N"),
            ConnectionId = connectionId,
            Kind = "research",
            State = McpJobState.Queued,
            ArgsJson = JsonSerializer.Serialize(args, JsonOpts),
            IdempotencyKey = idempotencyKey,
            Resumable = true,
            AutoResume = args.AutoResume,
        }, ct).ConfigureAwait(false);
        if (created) Wake();
        return job;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            McpJob? job = null;
            try { job = await _store.GetNextAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.Error("MCP job store read failed", ex, "Mcp");
            }

            if (job == null)
            {
                try { await _wake.WaitAsync(PollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            try
            {
                await RunJobAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Остановка приложения: job остаётся незавершённым → поднимется после рестарта.
                break;
            }
            catch (OperationCanceledException)
            {
                // Отмена конкретного job'а (Cancel(jobId)) — задание в terminal-статусе,
                // насос живёт. Полдоделанный прогресс остаётся в ResumeToken.
                job.State = McpJobState.Cancelled;
                job.UpdatedUtc = DateTime.UtcNow;
                await _store.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error($"MCP job {job.Id} crashed", ex, "Mcp");
                await TransitionAsync(job, McpJobState.NeedsAttention, $"runner: {ex.Message}", ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RunJobAsync(McpJob job, CancellationToken ct)
    {
        // Живая отмена конкретного job'а: стор с IMcpJobCancelSource даёт токен,
        // иначе — лёгкий опрос job.State (сторы делят объект задания с раннером).
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reg = _store is IMcpJobCancelSource cancelSrc
            ? cancelSrc.CancelToken(job.Id).Register(static s =>
                ((CancellationTokenSource)s!).Cancel(), jobCts)
            : default;
        if (_store is not IMcpJobCancelSource)
            _ = WatchJobCancelAsync(job, jobCts);
        ct = jobCts.Token;

        // kinds «transcribe»/«extract_facts» — внешние исполнители, без LLM.
        var executor = _executors.FirstOrDefault(e =>
            string.Equals(e.Kind, job.Kind, StringComparison.Ordinal));
        if (executor != null)
        {
            await RunExecutorJobAsync(job, executor, ct).ConfigureAwait(false);
            return;
        }

        // «search»/«resume» идут тем же LLM-путём; «resume» — continue с checkpoint'а.
        if (job.Kind is not ("research" or "search" or "resume"))
        {
            await TransitionAsync(job, McpJobState.NeedsAttention, $"неизвестный kind «{job.Kind}»", ct);
            return;
        }

        var args = Deserialize<ResearchArgs>(job.ArgsJson) ?? new ResearchArgs(Question: "");
        var token = Deserialize<ResumeTokenDto>(job.ResumeToken);

        // Пауза LLM / выключенный исходящий MCP: job ждёт, платных шагов нет.
        // Прогресс (checkpoint/ResumeToken) сохранён — не теряем.
        while (_handlers.IsLlmPausedFunc?.Invoke() == true ||
               _handlers.IsMcpDisabledFunc?.Invoke() == true)
        {
            var reason = _handlers.IsMcpDisabledFunc?.Invoke() == true
                ? "исходящий MCP выключен" : "LLM на паузе";
            await TransitionAsync(job, McpJobState.Paused, reason, ct);
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        PauseTokenSource? pts;
        lock (_pauseGate) pts = _paused;
        if (pts?.IsPaused == true)
        {
            await TransitionAsync(job, McpJobState.Paused, "пауза пользователя", ct);
            return; // store вернёт job снова; Resume() пнут очередь.
        }

        var servers = _servers().Where(s => s.Enabled).ToList();
        var byId = servers.ToDictionary(s => s.Id, StringComparer.Ordinal);

        // Инструменты всех активных серверов; сбойный сервер — пропускаем,
        // но если НИЧЕГО не подключилось — job ждёт связи.
        var tools = new List<McpToolSpec>();
        var connected = 0;
        foreach (var s in servers)
        {
            try
            {
                tools.AddRange(await _transport.ListToolsAsync(s, ct).ConfigureAwait(false));
                connected++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Warn($"MCP job {job.Id}: сервер «{s.Name}» недоступен: {ex.Message}", "Mcp");
            }
        }
        if (servers.Count > 0 && connected == 0)
        {
            var retries = (token?.Step ?? 0) == 0 ? MaxConnectionRetries : 1;
            job.Stage = "Ожидание подключения к MCP-серверу";
            await TransitionAsync(job, McpJobState.WaitingConnection,
                $"нет доступных серверов (retry ≤{retries})", ct);
            await Task.Delay(ConnectionRetryDelay, ct).ConfigureAwait(false);
            await TransitionAsync(job, McpJobState.Queued, "retry после WaitingConnection", ct);
            return;
        }

        var messages = token?.Messages ?? new List<ToolLoopMessage>();
        if (messages.Count == 0)
        {
            var sys = args.SystemPrompt ?? _handlers.SystemPromptFunc?.Invoke() ??
                "Ты — Buddy, ассистент встреч. Можешь вызывать инструменты MCP-серверов. " +
                "Отвечай по-русски кратко, указывай источники.";
            messages.Add(ToolLoopMessage.System(sys));
            if (args.History != null) messages.AddRange(args.History);
            messages.Add(ToolLoopMessage.User(args.Question));
        }

        var budgetExtensions = token?.BudgetExtensions ?? 0;
        var loop = new McpToolLoop(_llm, _transport, _journal);
        var request = new McpToolLoopRequest
        {
            Messages = messages,
            Tools = tools,
            Servers = byId,
            MaxSteps = args.MaxSteps,
            Mode = args.Mode ?? (job.Kind == "search" ? "normal" : "research"),
            MaxTokens = args.MaxTokens,
            MaxCostUsd = args.MaxCostUsd,
            CostPerTokenUsd = _handlers.CostPerTokenUsd,
            ContextWindowFunc = _handlers.ContextWindowFunc,
            IsLlmPausedFunc = _handlers.IsLlmPausedFunc,
            IsMcpDisabledFunc = _handlers.IsMcpDisabledFunc,
            ApprovalFunc = async (server, spec, callArgs, risk) =>
            {
                job.Stage = $"Подтверждение «{spec.OriginalName}»";
                await TransitionAsync(job, McpJobState.WaitingApproval,
                    $"{server.Name}: {spec.OriginalName}", ct);
                var ok = _handlers.ApprovalFunc != null &&
                    await _handlers.ApprovalFunc(server, spec, callArgs, risk).ConfigureAwait(false);
                await TransitionAsync(job, McpJobState.Running,
                    ok ? "approval: разрешено" : "approval: отказано", ct);
                return ok;
            },
            Progress = (stage, done, total) =>
            {
                job.Stage = stage;
                job.Done = done;
                job.Total = total;
                job.UpdatedUtc = DateTime.UtcNow;
            },
            SnapshotFunc = async snap =>
            {
                job.ResumeToken = Serialize(new ResumeTokenDto(
                    snap.Messages, snap.Executed, snap.Step, snap.CostUsd,
                    budgetExtensions, snap.PromptTokens, snap.CompletionTokens,
                    snap.ActiveMs, snap.Estimated));
                job.State = McpJobState.Running;
                job.UpdatedUtc = DateTime.UtcNow;
                await _store.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
            },
            Resume = token == null ? null : new McpLoopSnapshot
            {
                Messages = token.Messages,
                Executed = token.Executed,
                Step = token.Step,
                CostUsd = token.CostUsd,
                PromptTokens = token.PromptTokens,
                CompletionTokens = token.CompletionTokens,
                ActiveMs = token.ActiveMs,
                Estimated = token.Estimated,
            },
            JobId = job.Id,
            Pause = pts?.Token ?? PauseToken.None,
        };

        job.State = McpJobState.Running;
        job.Stage = "Исследование";
        await _store.SaveAsync(job, ct).ConfigureAwait(false);

        var result = await loop.RunAsync(request, ct).ConfigureAwait(false);

        switch (result.StopReason)
        {
            case McpLoopStopReason.FinalText:
                job.ResultJson = JsonSerializer.Serialize(new
                {
                    answer = result.FinalText,
                    toolCalls = result.ToolCalls,
                    costUsd = result.CostUsd,
                }, JsonOpts);
                job.ResumeToken = null;
                job.Stage = null;
                await TransitionAsync(job, McpJobState.Succeeded, null, ct);
                break;

            case McpLoopStopReason.Paused:
            case McpLoopStopReason.LlmPaused:
            case McpLoopStopReason.LlmMcpDisabled:
                SaveSnapshot(job, result, budgetExtensions);
                await TransitionAsync(job, McpJobState.Paused,
                    result.StopReason switch
                    {
                        McpLoopStopReason.LlmPaused => "LLM на паузе",
                        McpLoopStopReason.LlmMcpDisabled => "исходящий MCP выключен",
                        _ => "пауза пользователя",
                    }, ct);
                break;

            case McpLoopStopReason.BudgetExceeded:
            case McpLoopStopReason.BudgetExhausted:
            case McpLoopStopReason.TimeExhausted:
                job.Stage = "Бюджет исчерпан";
                SaveSnapshot(job, result, budgetExtensions);
                // Промежуточный результат сохраняется, НЕ обрыв без следа;
                // canContinue — маркер «Продолжить» для того же jobId+checkpoint.
                job.ResultJson = JsonSerializer.Serialize(new
                {
                    answer = result.FinalText,
                    toolCalls = result.ToolCalls,
                    costUsd = result.CostUsd,
                    reason = result.StopReason.ToString(),
                    budgetReason = result.BudgetReason,
                    canContinue = true,
                }, JsonOpts);
                await TransitionAsync(job, McpJobState.WaitingBudget,
                    $"потрачено ${result.CostUsd:F2} ({result.StopReason})", ct);
                if (_handlers.BudgetExtendFunc != null &&
                    await _handlers.BudgetExtendFunc(job).ConfigureAwait(false))
                {
                    var t2 = Deserialize<ResumeTokenDto>(job.ResumeToken)!;
                    job.ResumeToken = Serialize(t2 with { BudgetExtensions = t2.BudgetExtensions + 1 });
                    await TransitionAsync(job, McpJobState.Queued, "бюджет продлён", ct);
                }
                break;

            case McpLoopStopReason.MaxSteps:
            case McpLoopStopReason.NoProgress:
                SaveSnapshot(job, result, budgetExtensions);
                job.ResultJson = JsonSerializer.Serialize(new
                {
                    answer = result.FinalText,
                    toolCalls = result.ToolCalls,
                    costUsd = result.CostUsd,
                    reason = result.StopReason.ToString(),
                    canContinue = result.CanContinue,
                }, JsonOpts);
                await TransitionAsync(job, McpJobState.NeedsAttention,
                    result.StopReason == McpLoopStopReason.MaxSteps
                        ? $"достигнут лимит {request.MaxSteps} шагов"
                        : "повторные чтения без новых данных", ct);
                break;

            case McpLoopStopReason.Cancelled:
                await TransitionAsync(job, McpJobState.Cancelled, null, ct);
                break;

            default:
                job.Error = result.Error?.Message ?? "unknown";
                await TransitionAsync(job, McpJobState.Failed, result.Error?.Code, ct);
                break;
        }
    }

    /// <summary>ResumeToken + Checkpoint (budgetUsed/этап) — resume продолжает с места останова.</summary>
    private static void SaveSnapshot(McpJob job, McpToolLoopResult result, int budgetExtensions)
    {
        var snap = result.ToSnapshot();
        job.ResumeToken = Serialize(new ResumeTokenDto(
            snap.Messages, snap.Executed, snap.Step, snap.CostUsd, budgetExtensions,
            snap.PromptTokens, snap.CompletionTokens, snap.ActiveMs, snap.Estimated));
        job.Checkpoint = Serialize(new McpJobCheckpoint
        {
            Schema = McpJobCheckpoint.CurrentSchema,
            JobId = job.Id,
            Stage = job.Stage,
            Done = job.Done,
            Total = job.Total,
            BudgetUsed = new McpBudgetUsed
            {
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                Estimated = result.Estimated,
                ActiveSeconds = result.ElapsedActive.TotalSeconds,
            },
            CostUsd = result.CostUsd,
            BudgetExtensions = budgetExtensions,
            Loop = snap,
        });
    }

    /// <summary>
    /// Исполнение kinds без LLM (transcribe/extract_facts) через IMcpJobExecutor:
    /// прогресс в job.Stage/Done/Total, чекпоинт сохраняется самим исполнителем
    /// (job.Checkpoint + SaveAsync), исключение одного источника не роняет job.
    /// </summary>
    private async Task RunExecutorJobAsync(McpJob job, IMcpJobExecutor executor, CancellationToken ct)
    {
        PauseTokenSource? pts;
        lock (_pauseGate) pts = _paused;
        if (pts?.IsPaused == true)
        {
            await TransitionAsync(job, McpJobState.Paused, "пауза пользователя", ct);
            return;
        }

        job.State = McpJobState.Running;
        job.Stage ??= executor.Kind;
        job.UpdatedUtc = DateTime.UtcNow;
        await _store.SaveAsync(job, ct).ConfigureAwait(false);

        var ctx = new McpJobRunContext
        {
            Job = job,
            ReportProgress = (stage, done, total) =>
            {
                job.Stage = stage;
                job.Done = done;
                job.Total = total;
                job.UpdatedUtc = DateTime.UtcNow;
            },
            SaveAsync = async () =>
            {
                job.UpdatedUtc = DateTime.UtcNow;
                await _store.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
            },
            Log = s => _logger?.Warn($"MCP job {job.Id}: {s}", "Mcp"),
        };

        McpJobExecutorResult res;
        try
        {
            res = await executor.RunAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Error($"MCP job {job.Id} executor {executor.Kind} crashed", ex, "Mcp");
            res = new McpJobExecutorResult(false, null, ex.Message);
        }

        if (res.Success)
        {
            job.ResultJson = res.ResultJson;
            job.Stage = null;
            await TransitionAsync(job, McpJobState.Succeeded, null, ct);
        }
        else
        {
            job.ResultJson = res.ResultJson;
            await TransitionAsync(job, McpJobState.Failed, res.Error, ct);
        }
        _journal.Append("job", job.Id, job.ConnectionId, executor.Kind,
            res.Success ? "ok" : "error", null, res.Success ? null : "EXECUTOR", null);
    }

    /// <summary>Опросный путь отмены для сторов без IMcpJobCancelSource:
    /// job.State → Cancelled (McpJobStore.Cancel меняет тот же объект задания).</summary>
    private static async Task WatchJobCancelAsync(McpJob job, CancellationTokenSource jobCts)
    {
        try
        {
            while (!jobCts.IsCancellationRequested)
            {
                await Task.Delay(500, jobCts.Token).ConfigureAwait(false);
                if (job.State == McpJobState.Cancelled) { jobCts.Cancel(); break; }
            }
        }
        catch (OperationCanceledException) { /* job закончился — опрос остановлен */ }
        catch (ObjectDisposedException) { }
    }

    private async Task TransitionAsync(McpJob job, McpJobState to, string? note, CancellationToken ct)
    {
        var from = job.State;
        if (from == to) { job.UpdatedUtc = DateTime.UtcNow; await _store.SaveAsync(job, ct); return; }
        job.State = to;
        job.UpdatedUtc = DateTime.UtcNow;
        await _store.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _store.AppendTransitionAsync(job, from, to, note, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Debug($"job transition audit: {ex.Message}", "Mcp");
        }
        _journal.Append("job", job.Id, job.ConnectionId, null, to.ToString(), null, note);
    }

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch (JsonException) { return default; }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_pump != null)
        {
            try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _wake.Dispose();
    }
}
