namespace BrainstormBuddy.Core.Mcp.Jobs;

/// <summary>
/// Адаптер <see cref="McpJobStore"/> (jobs.jsonl) под контракт
/// <see cref="IMcpJobStore"/>, нужный <see cref="McpResearchJobRunner"/>:
/// очередь = задания в Queued (по CreatedUtc), сохранение — полный снимок.
/// Пробрасывает <see cref="IMcpJobCancelSource"/> — живая отмена Cancel(jobId).
/// </summary>
public sealed class McpJobStoreAdapter : IMcpJobStore, IMcpJobCancelSource
{
    private readonly McpJobStore _inner;

    public McpJobStoreAdapter(McpJobStore inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Обёрнутый стор (для Requeue/List/Cancel из фасада/UI).</summary>
    public McpJobStore Inner => _inner;

    /// <inheritdoc />
    public Task<(McpJob Job, bool Created)> EnqueueAsync(McpJob job, CancellationToken ct)
        => Task.FromResult(_inner.Enqueue(job));

    /// <inheritdoc />
    public Task<McpJob?> GetNextAsync(CancellationToken ct)
        => Task.FromResult(_inner.List()
            .Where(j => j.State == McpJobState.Queued)
            .OrderBy(j => j.CreatedUtc)
            .FirstOrDefault());

    /// <inheritdoc />
    public Task SaveAsync(McpJob job, CancellationToken ct)
    {
        _inner.Save(job);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>jobs.jsonl — единственный аудит: Save выше уже дописал снимок
    /// с новым состоянием; отдельная строка перехода не хранится.</remarks>
    public Task AppendTransitionAsync(McpJob job, McpJobState from, McpJobState to,
        string? note, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<McpJob?> FindByIdempotencyKeyAsync(string key, CancellationToken ct)
        => Task.FromResult(_inner.FindByIdempotencyKey(key));

    /// <inheritdoc />
    public CancellationToken CancelToken(string jobId) => _inner.CancelToken(jobId);
}
