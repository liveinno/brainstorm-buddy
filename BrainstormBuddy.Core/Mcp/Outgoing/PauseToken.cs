namespace BrainstormBuddy.Core.Mcp.Outgoing;

/// <summary>
/// Кооперативная пауза для tool-loop: ResumeTokenSource.Pause() → цикл останавливается
/// перед следующим шагом (IsPauseRequested → Cancelled) либо ждёт Resume() через
/// WaitWhilePausedAsync для синхронного чата.
/// </summary>
public sealed class PauseTokenSource
{
    private TaskCompletionSource<bool>? _paused;

    public PauseToken Token => new(this);

    public bool IsPaused => _paused != null;

    public void Pause()
    {
        lock (this) _paused ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? tcs;
        lock (this)
        {
            tcs = _paused;
            _paused = null;
        }
        tcs?.TrySetResult(true);
    }

    internal Task WaitWhilePausedAsync()
    {
        lock (this) return _paused?.Task ?? Task.CompletedTask;
    }
}

/// <summary>Немутабельный токен паузы, передаваемый в loop-итерацию.</summary>
public readonly struct PauseToken
{
    private readonly PauseTokenSource? _source;

    internal PauseToken(PauseTokenSource? source) => _source = source;

    public static PauseToken None => new(null);

    /// <summary>Новая явная пауза запрошена — следующий шаг должен отмениться.</summary>
    public bool IsPauseRequested => _source?.IsPaused == true;

    /// <summary>Ждать снятия паузы (для чата: вопрос «подвисает», пока LLM на паузе).</summary>
    public Task WaitWhilePausedAsync() => _source?.WaitWhilePausedAsync() ?? Task.CompletedTask;
}
