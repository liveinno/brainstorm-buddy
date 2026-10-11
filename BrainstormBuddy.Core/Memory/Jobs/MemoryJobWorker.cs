using BrainstormBuddy.Config;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Jobs;

/// <summary>
/// Фоновый worker memory-pipeline. Строго один job за раз — порядок важен
/// (consolidation/embed зависят от результата extraction). Цикл:
/// RequeueStale → ClaimJob → dispatch по <see cref="MemoryJob.Kind"/> → FinishJob.
/// Запускается из App как фоновая задача (Task.Run(worker.RunAsync)).
/// Ошибка handler'а → retry с backoff 30s·attempts; после <see cref="MaxAttempts"/>
/// попыток job уходит в failed. Незнакомый kind — сразу failed.
/// Memory-ошибки не должны ломать приложение: луп ловит всё и пишет в лог.
/// </summary>
public sealed class MemoryJobWorker
{
    internal const int MaxAttempts = 5;
    private const string LogCategory = "Memory";
    private static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(30);

    private readonly IMemoryStore _store;
    private readonly IReadOnlyDictionary<string, Func<MemoryJob, Task<string?>>> _handlers;
    private readonly MemoryConfig _cfg;
    private readonly LoggingService? _log;
    private readonly TimeSpan _idleDelay;

    /// <summary>
    /// <paramref name="handlers"/> — dispatch по Kind; handler возвращает null при
    /// успехе или строку ошибки (worker сам решает retry/fail).
    /// </summary>
    public MemoryJobWorker(IMemoryStore store,
        IReadOnlyDictionary<string, Func<MemoryJob, Task<string?>>> handlers,
        MemoryConfig cfg, LoggingService? log = null, TimeSpan? idleDelay = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _log = log;
        _idleDelay = idleDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Фоновый луп до отмены. Пустая очередь → короткий sleep и снова poll.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log?.Info("memory job worker started", LogCategory);
        while (!ct.IsCancellationRequested)
        {
            bool didWork = false;
            try
            {
                didWork = await ProcessNextAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Memory не должна ронять host-приложение.
                _log?.Error("memory job loop error", ex, LogCategory);
            }

            if (!didWork)
            {
                try { await Task.Delay(_idleDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        _log?.Info("memory job worker stopped", LogCategory);
    }

    /// <summary>
    /// Одна итерация: requeue зависших running → claim → выполнить → finish.
    /// Возвращает true, если job был обработан (false = очередь пуста).
    /// </summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var stale = TimeSpan.FromSeconds(Math.Max(1, _cfg.JobStaleSec));
        _store.RequeueStale(stale);

        var job = _store.ClaimJob(stale);
        if (job == null)
            return false;

        if (!_handlers.TryGetValue(job.Kind, out var handler))
        {
            _log?.Warn($"memory job {job.Id}: unknown kind '{job.Kind}'", LogCategory);
            _store.FinishJob(job.Id, $"unknown kind '{job.Kind}'");
            return true;
        }

        string? error;
        try
        {
            error = await handler(job).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Error($"memory job {job.Id} ({job.Kind}) handler failed", ex, LogCategory);
            error = ex.Message;
        }

        if (error == null)
        {
            _store.FinishJob(job.Id);
            return true;
        }

        // Отмена (shutdown/ct) — job возвращаем в pending без расхода attempt
        // и без backoff: AskAsync при отмене возвращает Canceled/ошибку, а не бросает.
        if (ct.IsCancellationRequested)
        {
            job.Status = "pending";
            job.Error = "canceled";
            job.RunAfterUtc = null;
            return true;
        }

        // ClaimJob уже инкрементировал Attempts: 1..MaxAttempts.
        if (job.Attempts >= MaxAttempts)
        {
            _store.FinishJob(job.Id, error);
            _log?.Warn($"memory job {job.Id} ({job.Kind}) failed permanently " +
                $"after {job.Attempts} attempts: {error}", LogCategory);
        }
        else
        {
            // Retry с экспоненциальным backoff: RunAfterUtc = now + 30s·attempts.
            // Persistence: InMemoryMemoryStore персистит мутации по ссылке;
            // SqliteMemoryStore — identity-map, поля сливаются write-back'ом
            // на следующих ClaimJob/ListJobs/RequeueStale (подтверждено M2).
            job.Status = "pending";
            job.Error = error;
            job.RunAfterUtc = DateTime.UtcNow + RetryBase * job.Attempts;
        }
        return true;
    }
}
