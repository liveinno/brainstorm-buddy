using System.Collections.Concurrent;
using BrainstormBuddy.Core.Attachments;
using BrainstormBuddy.Memory;
using BrainstormBuddy.Services;

namespace BrainstormBuddy;

/// <summary>
/// <summary>
/// Фоновый воркер очереди fact_jobs (<see cref="AttachmentStore.ListFactJobs"/>):
/// один job за раз, <see cref="AttachmentFactExtractor.RunAsync"/> сам пишет
/// прогресс в БД и стучит в onJobUpdated после каждой части — воркер транслирует
/// это в <see cref="JobUpdated"/> подписчикам (чип «Факты: …» в оверлее, строка
/// файла в истории встречи, карточка в Центре памяти). Просыпается по
/// <see cref="Kick"/> (после постановки задания и после Commit picker'а) и при
/// старте, если очередь не пуста. Interrupted-задания (помечены
/// <see cref="AttachmentStore.MarkInterruptedJobs"/> при старте) воркер доезжает
/// сам — экстрактор продолжает их с RequestsDone, части не переотправляются.
/// Отмена — <see cref="CancelAsync"/>: Queued/Interrupted помечаются Canceled и
/// пропускаются при dequeue, Running получает отмену по своему токену.
/// </summary>
public sealed class AttachmentFactWorker : IDisposable
{
    private const string LogCategory = "Attach";

    /// <summary>Смена статуса/прогресса задания — прилетает в фон-потоке, подписчик сам маршалит на UI.
    /// Стреляет после каждой части (живой %), не только на переходах статуса.</summary>
    public event Action<FactJob>? JobUpdated;

    private readonly AttachmentStore _attachments;
    private readonly AttachmentFactExtractor _extractor;
    private readonly Func<long?> _memMeetingId;
    private readonly Func<string?> _reviewMode;
    private readonly LoggingService? _log;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    /// <summary>jobId → linked CTS текущего Running-задания (для CancelAsync).</summary>
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private Task? _loop;
    private bool _disposed;

    /// <param name="memMeetingId">Id встречи в mem_meetings на момент выполнения
    /// задания (Job хранит снимок журнального MeetingId; memory-id читаем живьём).</param>
    /// <param name="reviewMode">Действующий режим проверки памяти («review» → кандидаты).</param>
    public AttachmentFactWorker(AttachmentStore attachments, AttachmentFactExtractor extractor,
        Func<long?> memMeetingId, Func<string?> reviewMode, LoggingService? log = null)
    {
        _attachments = attachments;
        _extractor = extractor;
        _memMeetingId = memMeetingId;
        _reviewMode = reviewMode;
        _log = log;
    }

    /// <summary>Запустить цикл (идемпотентно). Доезжает и очередь, оставшуюся с прошлого запуска.</summary>
    public void Start()
    {
        _loop ??= Task.Run(LoopAsync);
    }

    /// <summary>Разбудить цикл после постановки/перезапуска задания.</summary>
    public void Kick()
    {
        if (_disposed) return;
        Start();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>
    /// Отменить задание: Running → сигнал его linked-токену (экстрактор прерывается
    /// между частями/запросом и сам фиксирует Canceled); Queued/Interrupted →
    /// помечается Canceled в fact_jobs и пропускается при dequeue.
    /// true — задание найдено и отмена принята; false — неизвестный/уже терминальный id.
    /// Вызывать с любого потока; внутри — короткая синхронная работа + эмит события.
    /// </summary>
    public Task<bool> CancelAsync(long jobId)
    {
        if (_disposed) return Task.FromResult(false);

        // Уже выполняется — дёргаем его токен; запись Canceled сделает экстрактор.
        if (_running.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* финишировал между проверками */ }
            _log?.Info($"Attach: fact-job #{jobId}: отмена запрошена (running)", LogCategory);
            return Task.FromResult(true);
        }

        // Ещё в очереди / прерван до запуска — гасим в БД, dequeue его пропустит.
        try
        {
            var job = _attachments.ListFactJobs().FirstOrDefault(j => j.Id == jobId);
            if (job is { Status: FactJobStatus.Queued or FactJobStatus.Interrupted })
            {
                var canceled = job with { Status = FactJobStatus.Canceled };
                _attachments.UpdateFactJob(canceled);
                _log?.Info($"Attach: fact-job #{jobId}: отмена запрошена ({job.Status} → Canceled)", LogCategory);
                Emit(canceled);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _log?.Warn($"Attach: отмена fact-job #{jobId} упала: {ex.Message}", LogCategory);
            return Task.FromResult(false);
        }
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            FactJob? job = null;
            try
            {
                // Свежие сверху → берём самое старое. Queued + Interrupted
                // (Interrupted = процесс умер в работе; экстрактор доедет с RequestsDone).
                job = _attachments.ListFactJobs(FactJobStatus.Queued)
                    .Concat(_attachments.ListFactJobs(FactJobStatus.Interrupted))
                    .OrderBy(j => j.Id).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _log?.Error("Attach: чтение очереди fact_jobs упало", ex, LogCategory);
            }

            if (job == null)
            {
                try { await _wake.WaitAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Гонка отмены: задание могли пометить Canceled между выборкой и стартом.
            try
            {
                var fresh = _attachments.ListFactJobs().FirstOrDefault(j => j.Id == job.Id);
                if (fresh == null || fresh.Status is not (FactJobStatus.Queued or FactJobStatus.Interrupted))
                    continue;
                job = fresh;
            }
            catch (Exception ex) { _log?.Warn($"Attach: перепроверка fact-job #{job.Id} упала: {ex.Message}", LogCategory); }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _running[job.Id] = linked;
            try
            {
                // onJobUpdated → Emit после каждой части: живой % в чипе/тосте/карточке.
                var final = await _extractor.RunAsync(job, _attachments,
                        _memMeetingId(), _reviewMode(), linked.Token, Emit).ConfigureAwait(false);
                Emit(final);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                // Отмена задания без остановки воркера — подстраховка: экстрактор
                // сам фиксирует Canceled; сюда попадаем, если OCE прорвался мимо.
                try
                {
                    var canceled = job with { Status = FactJobStatus.Canceled };
                    _attachments.UpdateFactJob(canceled);
                    Emit(canceled);
                }
                catch (Exception ex2) { _log?.Error("Attach: запись Canceled job'а упала", ex2, LogCategory); }
            }
            catch (Exception ex)
            {
                _log?.Error($"Attach: fact-job #{job.Id} упал", ex, LogCategory);
                try
                {
                    var failed = job with { Status = FactJobStatus.Error, Error = ex.Message };
                    _attachments.UpdateFactJob(failed);
                    Emit(failed);
                }
                catch (Exception ex2) { _log?.Error("Attach: запись Error job'а упала", ex2, LogCategory); }
            }
            finally
            {
                _running.TryRemove(job.Id, out _);
            }
        }
    }

    private void Emit(FactJob job)
    {
        try { JobUpdated?.Invoke(job); }
        catch (Exception ex) { _log?.Warn($"Attach: подписчик JobUpdated упал: {ex.Message}", LogCategory); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _wake.Release(); } catch { /* уже закрыт */ }
        // _loop не ждём: фон-задача сама завершится по токену; Dispose вызывается при выходе.
        _wake.Dispose();
        _cts.Dispose();
    }
}
