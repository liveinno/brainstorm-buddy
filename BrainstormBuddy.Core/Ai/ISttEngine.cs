namespace BrainstormBuddy.Ai;

/// <summary>
/// Движок распознавания речи (STT). Абстракция, чтобы можно было выбирать между
/// внешним сервером (Docker / OpenAI-совместимый) и встроенным (GigaAM ONNX).
/// Контракт совпадает с прежним IApiClient.TranscribeAsync, чтобы вызывающий код не менялся.
/// </summary>
public interface ISttEngine
{
    /// <summary>Короткий идентификатор движка ("remote", "native").</summary>
    string Name { get; }

    /// <summary>WAV (16 кГц mono) → распознанный текст, либо null (пусто/ошибка).</summary>
    Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default);

    /// <summary>Доступен ли движок (сервер поднят / модель загружена).</summary>
    Task<bool> HealthAsync(CancellationToken ct = default);
}
