using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Core.Mcp;

/// <summary>
/// Хранилище MCP-заданий: in-memory индекс + append-only журнал
/// %AppData%\BrainstormBuddy\mcp\jobs.jsonl. Каждая запись в журнале — полный
/// снимок задания после перехода состояния; при старте журнал реплеится
/// (последняя запись по id побеждает). Задания, застрявшие в Queued/Running
/// при аварийном выходе, при реплее переводятся в Failed("process restarted")
/// — иначе бы висели вечно без исполнителя.
/// </summary>
public sealed class McpJobStore : BrainstormBuddy.Core.Mcp.Jobs.IMcpJobCancelSource
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _journalPath;
    private readonly Dictionary<string, McpJob> _jobs = new(StringComparer.Ordinal);
    /// <summary>idempotencyKey → jobId. Ключ глобален на хранилище (префикс connectionId при записи).</summary>
    private readonly Dictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    /// <summary>Живая отмена выполняющихся заданий: Cancel() срабатывает на токене.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _cancelTokens = new(StringComparer.Ordinal);

    public McpJobStore(string appDataDir)
    {
        var dir = Path.Combine(appDataDir, "mcp");
        Directory.CreateDirectory(dir);
        _journalPath = Path.Combine(dir, "jobs.jsonl");
        Replay();
    }

    public string JournalPath => _journalPath;

    /// <summary>
    /// Идемпотентная постановка: повтор с тем же (connectionId, idempotencyKey)
    /// возвращает СУЩЕСТВУЮЩЕЕ задание, а не дубль. Пустой idempotencyKey —
    /// обычный enqueue (каждый раз новый id). resumable=true — задание переживает
    /// рестарт процесса (Running → WaitingResume/Queued по autoResume, не Failed).
    /// </summary>
    public McpJob Enqueue(string connectionId, string kind, string argsJson, string? idempotencyKey,
        bool resumable = false, bool autoResume = false)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var key = IdemKey(connectionId, idempotencyKey!);
                if (_idempotency.TryGetValue(key, out var existingId) &&
                    _jobs.TryGetValue(existingId, out var existing))
                    return existing;
            }
            var job = new McpJob
            {
                Id = "mcpjob-" + Guid.NewGuid().ToString("N"),
                ConnectionId = connectionId,
                Kind = kind,
                State = McpJobState.Queued,
                ArgsJson = argsJson,
                IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
                Resumable = resumable,
                AutoResume = resumable && autoResume,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            _jobs[job.Id] = job;
            if (!string.IsNullOrEmpty(idempotencyKey))
                _idempotency[IdemKey(connectionId, idempotencyKey!)] = job.Id;
            AppendLocked(job);
            return job;
        }
    }

    /// <summary>
    /// Адаптерная постановка полной записи (IMcpJobStore.EnqueueAsync): id задан
    /// вызывающим. Идемпотентность — по (connectionId, idempotencyKey); при
    /// совпадении возвращается существующая запись (created=false).
    /// </summary>
    public (McpJob Job, bool Created) Enqueue(McpJob job)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(job.IdempotencyKey))
            {
                var key = IdemKey(job.ConnectionId, job.IdempotencyKey!);
                if (_idempotency.TryGetValue(key, out var existingId) &&
                    _jobs.TryGetValue(existingId, out var existing))
                    return (existing, false);
            }
            _jobs[job.Id] = job;
            if (!string.IsNullOrEmpty(job.IdempotencyKey))
                _idempotency[IdemKey(job.ConnectionId, job.IdempotencyKey!)] = job.Id;
            AppendLocked(job);
            return (job, true);
        }
    }

    /// <summary>Полный снимок job'а (ResumeToken/Checkpoint/Stage…) в журнал —
    /// точка персиста IMcpJobStore.SaveAsync. Terminal-статус не откатывается.</summary>
    public void Save(McpJob job)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(job.Id, out var cur) &&
                IsTerminal(cur.State) && !IsTerminal(job.State))
                return; // завершённое задание не воскрешаем автосохранением
            job.UpdatedUtc = DateTime.UtcNow;
            _jobs[job.Id] = job;
            AppendLocked(job);
        }
    }

    /// <summary>Линейный поиск по idempotency-ключу (глобальный, без привязки к
    /// connectionId — удобен для UI/повторного enqueue после рестарта).</summary>
    public McpJob? FindByIdempotencyKey(string key)
    {
        lock (_gate)
            return _jobs.Values.FirstOrDefault(j => j.IdempotencyKey == key);
    }

    public McpJob? Get(string jobId)
    {
        lock (_gate)
            return _jobs.TryGetValue(jobId, out var j) ? j : null;
    }

    public IReadOnlyList<McpJob> List(string? connectionId = null)
    {
        lock (_gate)
            return _jobs.Values
                .Where(j => connectionId == null || j.ConnectionId == connectionId)
                .OrderByDescending(j => j.CreatedUtc).ToList();
    }

    /// <summary>Переход состояния с журналированием. Terminal-состояния не откатываются назад
    /// (для повторного запуска — Requeue). checkpoint — непрозрачная позиция runner'а.</summary>
    public void Update(string jobId, McpJobState state,
        string? stage = null, int? done = null, int? total = null,
        string? resultJson = null, string? error = null, string? checkpoint = null)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return;
            if (IsTerminal(job.State) && state < McpJobState.Succeeded) return; // не воскрешаем завершённые
            job.State = state;
            if (stage != null) job.Stage = stage;
            if (done.HasValue) job.Done = done;
            if (total.HasValue) job.Total = total;
            if (resultJson != null) job.ResultJson = resultJson;
            if (error != null) job.Error = error;
            if (checkpoint != null) job.Checkpoint = checkpoint;
            job.UpdatedUtc = DateTime.UtcNow;
            AppendLocked(job);
        }
    }

    /// <summary>Повторная постановка (resume/continue): Failed/Cancelled/Paused/
    /// Waiting*/NeedsAttention → Queued. Checkpoint и ResumeToken сохраняются —
    /// runner продолжает с места. При continue после WaitingBudget/WaitingResume
    /// счётчик бюджетных продлений увеличивается — новый бюджет добавляется, иначе
    /// job мгновенно остановился бы на том же исчерпанном пределе (§6.5).
    /// false — нет задания или состояние не требует рестарта
    /// (Queued/Running/Succeeded — уже активно или отдали результат).</summary>
    public bool Requeue(string jobId)
    {
        CancellationTokenSource? oldCts;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return false;
            if (job.State is McpJobState.Queued or McpJobState.Running or McpJobState.Succeeded)
                return false;
            if (job.State is McpJobState.WaitingBudget or McpJobState.WaitingResume)
                BumpBudgetExtensionsLocked(job);
            job.State = McpJobState.Queued;
            job.Error = null;
            job.UpdatedUtc = DateTime.UtcNow;
            AppendLocked(job);
            // Перезапуск после Cancel — новый прогон не должен наследовать отмену.
            oldCts = _cancelTokens.Remove(jobId, out var o) ? o : null;
        }
        oldCts?.Dispose();
        return true;
    }

    /// <summary>+1 к budgetExtensions в ResumeToken и Checkpoint (continue = новый
    /// бюджет поверх израсходованного). Поля без схемы — структурный патч JSON.</summary>
    private static void BumpBudgetExtensionsLocked(McpJob job)
    {
        static string? Bump(string? json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            try
            {
                var node = JsonNode.Parse(json);
                if (node is not JsonObject obj) return json;
                var cur = obj["budgetExtensions"]?.GetValue<int?>() ?? 0;
                obj["budgetExtensions"] = cur + 1;
                return obj.ToJsonString();
            }
            catch (JsonException) { return json; }
        }
        job.ResumeToken = Bump(job.ResumeToken);
        job.Checkpoint = Bump(job.Checkpoint);
    }

    /// <summary>Токен отмены job'а: создаётся при первом обращении; Cancel срабатывает на нём.
    /// При перепостановке (Requeue) старый токен выбрасывается — новый прогон получает новый.</summary>
    public CancellationToken CancelToken(string jobId)
    {
        lock (_gate)
        {
            if (!_cancelTokens.TryGetValue(jobId, out var cts))
                _cancelTokens[jobId] = cts = new CancellationTokenSource();
            return cts.Token;
        }
    }

    /// <summary>Отмена незавершённого задания. false — нет такого или уже в terminal.</summary>
    public bool Cancel(string jobId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return false;
            if (IsTerminal(job.State)) return false;
            job.State = McpJobState.Cancelled;
            job.UpdatedUtc = DateTime.UtcNow;
            AppendLocked(job);
            _cancelTokens.TryGetValue(jobId, out cts);
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    public static bool IsTerminal(McpJobState s)
        => s is McpJobState.Succeeded or McpJobState.Failed or McpJobState.Cancelled;

    // ── журнал ──────────────────────────────────────────────────────────────

    private void AppendLocked(McpJob job)
    {
        var line = JsonSerializer.Serialize(job, JsonOpts);
        File.AppendAllText(_journalPath, line + Environment.NewLine);
    }

    private void Replay()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                McpJob? job;
                try { job = JsonSerializer.Deserialize<McpJob>(line); }
                catch { continue; } // битый хвост после крэша — пропускаем строку
                if (job == null || string.IsNullOrEmpty(job.Id)) continue;
                // Аварийный выход. Resumable: Running → WaitingResume (autoResume →
                // Queued — платная работа продолжается только если постановщик разрешил);
                // Queued resumable не стартовало — остаётся в очереди. Остальные
                // незавершённые → Failed("process restarted") как раньше.
                if (!IsTerminal(job.State))
                {
                    if (job.Resumable)
                    {
                        if (job.State is not (McpJobState.Queued or McpJobState.WaitingResume))
                            job.State = job.AutoResume ? McpJobState.Queued : McpJobState.WaitingResume;
                    }
                    else
                    {
                        job.State = McpJobState.Failed;
                        job.Error ??= "process restarted before completion";
                    }
                }
                _jobs[job.Id] = job;
                if (!string.IsNullOrEmpty(job.IdempotencyKey))
                    _idempotency[IdemKey(job.ConnectionId, job.IdempotencyKey)] = job.Id;
            }
        }
        catch { /* журнал недоступен — работаем с пустым */ }
    }

    private static string IdemKey(string connectionId, string idempotencyKey)
        => connectionId + "\n" + idempotencyKey;
}
