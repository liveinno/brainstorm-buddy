using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Общий контракт встроенных движков GigaAM (v2 char-CTC и v3 BPE-CTC):
/// нужен FileTranscriptionService/GigaamFileTranscriber и резолверам в App,
/// чтобы переиспользовать живой движок независимо от версии модели.
/// </summary>
public interface IGigaamSttEngine : ISttEngine, IDisposable
{
    /// <summary>Версия модели: "v2" | "v3" (для UI и логов).</summary>
    string VersionTag { get; }

    /// <summary>ONNX-провайдер исполнения: "CPU" | "DirectML:N".</summary>
    string ActiveProvider { get; }

    /// <summary>16 кГц mono float [-1..1] → распознанный текст (чанкинг внутри).</summary>
    string TranscribeSamples(float[] samples, CancellationToken ct = default);

    /// <summary>
    /// Текст + пословные границы (секунды от начала samples), источник — CTC-
    /// фреймы GigaAM. null — пустой результат. Нужен внутричанковой разметке
    /// спикеров; вызывающий конвертирует слова → сэмплы потока по wav-базе.
    /// </summary>
    TimedTranscript? TranscribeTimed(float[] samples, CancellationToken ct = default);

    /// <summary>Асинхронная версия TranscribeTimed (файл/WAV-байты).</summary>
    Task<TimedTranscript?> TranscribeTimedAsync(byte[] wavAudio, CancellationToken ct = default);
}
