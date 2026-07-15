namespace BrainstormBuddy.Stt;

/// <summary>
/// Транскрибатор файла с посегментными тайм-кодами. Реализуется двумя движками:
/// GigaAM (тайм-коды по окнам) и Whisper (нативные сегменты + пунктуация).
/// </summary>
public interface IFileTranscriber : IDisposable
{
    /// <summary>Короткое имя движка для UI («GigaAM · CPU», «Whisper turbo»).</summary>
    string EngineName { get; }

    /// <param name="samples16k">16 кГц mono float [-1..1].</param>
    /// <param name="progress">(доля 0..1, статус) — из фонового потока.</param>
    FileTranscriptResult Transcribe(float[] samples16k, TimeSpan duration, string method,
        Action<double, string>? progress, CancellationToken ct);
}

/// <summary>Адаптер GigaAM под IFileTranscriber (поверх FileTranscriptionService).</summary>
public sealed class GigaamFileTranscriber : IFileTranscriber
{
    private readonly NativeGigaamSttService _stt;
    private readonly bool _ownsStt;
    private readonly FileTranscriptionService _svc;

    public GigaamFileTranscriber(NativeGigaamSttService stt, bool ownsStt)
    {
        _stt = stt;
        _ownsStt = ownsStt;
        _svc = new FileTranscriptionService(stt);
    }

    public string EngineName => $"GigaAM · {_stt.ActiveProvider}";

    public FileTranscriptResult Transcribe(float[] samples16k, TimeSpan duration, string method,
        Action<double, string>? progress, CancellationToken ct)
        => _svc.Transcribe(samples16k, duration, method, progress, ct);

    public void Dispose()
    {
        if (_ownsStt) _stt.Dispose();
    }
}

/// <summary>Обёртка «не владею движком» — когда движок закэширован в App и живёт дольше окна.</summary>
public sealed class NonOwningFileTranscriber : IFileTranscriber
{
    private readonly IFileTranscriber _inner;
    public NonOwningFileTranscriber(IFileTranscriber inner) => _inner = inner;
    public string EngineName => _inner.EngineName;
    public FileTranscriptResult Transcribe(float[] samples16k, TimeSpan duration, string method,
        Action<double, string>? progress, CancellationToken ct)
        => _inner.Transcribe(samples16k, duration, method, progress, ct);
    public void Dispose() { /* владелец — App, не диспозим */ }
}
