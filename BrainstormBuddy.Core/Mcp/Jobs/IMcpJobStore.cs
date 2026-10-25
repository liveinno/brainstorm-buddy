namespace BrainstormBuddy.Core.Mcp.Jobs;

/// <summary>
/// Минимальный контракт стора фоновых MCP-заданий. Реализация (SQLite/файл) —
/// на стороне входящего контура; раннер исходящих исследований зависит только
/// от этого интерфейса.
/// </summary>
public interface IMcpJobStore
{
    /// <summary>Идемпотентная вставка: при совпадении IdempotencyKey — существующая запись, created=false.</summary>
    Task<(McpJob Job, bool Created)> EnqueueAsync(McpJob job, CancellationToken ct);

    /// <summary>Следующая незавершённая задача (Queued или ожидающая продолжения), null — очередь пуста.</summary>
    Task<McpJob?> GetNextAsync(CancellationToken ct);

    /// <summary>Сохранить обновлённую запись (State/Stage/Progress/Result/Error/ResumeToken/UpdatedUtc).</summary>
    Task SaveAsync(McpJob job, CancellationToken ct);

    /// <summary>Хвост аудита переходов состояний (jsonl-строка на выбор реализации).</summary>
    Task AppendTransitionAsync(McpJob job, McpJobState from, McpJobState to, string? note, CancellationToken ct);

    /// <summary>Поиск по idempotency-ключу (для повторного enqueue после рестарта UI).</summary>
    Task<McpJob?> FindByIdempotencyKeyAsync(string key, CancellationToken ct);
}

/// <summary>
/// Опциональный канал живой отмены задания (§6.5 «пользователь может остановить
/// работу»): стор, реализующий его, выдаёт токен, срабатывающий при Cancel(jobId).
/// Runner использует его, если стор поддерживает; иначе — опрос job.State.
/// </summary>
public interface IMcpJobCancelSource
{
    /// <summary>Токен отмены job'а (создаётся при первом обращении; срабатывает
    /// при Cancel). Переqueue-состояние не отменяет ранее выданный токен.</summary>
    CancellationToken CancelToken(string jobId);
}
