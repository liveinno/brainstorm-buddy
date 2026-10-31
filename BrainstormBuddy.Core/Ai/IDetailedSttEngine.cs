namespace BrainstormBuddy.Ai;

/// <summary>
/// Движок STT с детальным результатом: различает «речь распознана / речи нет /
/// ошибка / отмена», тогда как <see cref="ISttEngine.TranscribeAsync"/> сворачивает
/// всё в один null — на этом точность честного замера скорости теряется.
/// </summary>
public interface IDetailedSttEngine
{
    /// <summary>WAV (16 кГц mono) → детальный исход распознавания со статусом.</summary>
    Task<SttTranscribeResult> TranscribeDetailedAsync(byte[] wavAudio, CancellationToken ct = default);
}
