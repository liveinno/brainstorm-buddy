using System.Buffers.Binary;
using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Файловая транскрибация через внешний (облачный) STT: тот же контракт
/// <see cref="IFileTranscriber"/>, что у локальных GigaAM/Whisper, но распознавание
/// выполняет <see cref="IDetailedSttEngine"/> (в приложении — уже существующий
/// <c>OpenAiClient</c>; второго HTTP-стека здесь нет).
///
/// Порезка повторяет локальную (<see cref="FileTranscriptionService"/>): окна по
/// 18 с, граница подвигается к тишине через
/// <see cref="FileTranscriptionService.SnapToQuiet"/>. Границы вычисляются ДО
/// первого запроса — число частей и их тайм-коды известны заранее. Запросы идут
/// строго последовательно; каждый — PCM16 mono 16 кГц WAV меньше 1 МиБ.
///
/// Исход честный: <see cref="FileTranscriptResult.IsComplete"/> = true только
/// когда распознаны все части. Отмена/сбой/«речи нет» останавливают работу на
/// проблемной части, уже принятые сегменты остаются в результате, причина — в
/// <see cref="FileTranscriptResult.FailureReason"/>; локального fallback нет.
/// Если токен отменён уже после последней принятой части — результат остаётся
/// полным.
/// </summary>
public sealed class CloudFileTranscriber : IFileTranscriber
{
    /// <summary>Длина окна: 18 с — как у локального <see cref="FileTranscriptionService"/>.</summary>
    internal const int SegmentSeconds = 18;

    /// <summary>Окно поиска тишины вокруг расчётной границы, секунды.</summary>
    internal const double QuietWindowSeconds = 1.2;

    /// <summary>Потолок одного WAV-запроса: 1 МиБ (18 с + сдвиг к тишине ≈ 614 КБ).</summary>
    internal const int MaxChunkBytes = 1024 * 1024;

    /// <summary>Канонический PCM-WAV заголовок (RIFF/fmt/data).</summary>
    internal const int WavHeaderBytes = 44;

    private readonly IDetailedSttEngine _engine;
    private readonly string _engineName;

    public CloudFileTranscriber(IDetailedSttEngine engine, string engineName)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engineName = engineName ?? "";
    }

    public string EngineName => _engineName;

    /// <param name="samples16k">16 кГц mono float [-1..1].</param>
    /// <param name="progress">(доля готовых частей 0..1, «N/M · mm:ss / mm:ss») — из фонового потока.</param>
    public FileTranscriptResult Transcribe(float[] samples16k, TimeSpan duration, string method,
        Action<double, string>? progress, CancellationToken ct)
    {
        var result = new FileTranscriptResult { Duration = duration, ExtractMethod = method };
        if (samples16k is null || samples16k.Length == 0) return result;

        int sr = MelFrontend.SampleRate;
        var parts = PlanParts(samples16k, sr);
        result.TotalParts = parts.Count;

        for (int i = 0; i < parts.Count; i++)
        {
            (int start, int end) = parts[i];
            SttTranscribeResult response;
            try
            {
                ct.ThrowIfCancellationRequested();
                byte[] wav = EncodeWav16kMono(samples16k.AsSpan(start, end - start));
                response = _engine.TranscribeDetailedAsync(wav, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Incomplete(result, canceled: true, "транскрибация отменена");
            }
            catch (OperationCanceledException ex)
            {
                // Не отмена вызывающим: истёк внутренний таймаут запроса — это сбой.
                return Incomplete(result, canceled: false, "запрос распознавания прерван: " + ex.Message);
            }
            catch (Exception ex)
            {
                return Incomplete(result, canceled: false, "ошибка распознавания: " + ex.Message);
            }

            string? text = response.Text?.Trim();
            switch (response.Status)
            {
                case SttTranscribeStatus.Text when !string.IsNullOrEmpty(text):
                    result.Segments.Add(new TranscriptSegment(
                        TimeSpan.FromSeconds((double)start / sr),
                        TimeSpan.FromSeconds((double)end / sr),
                        text));
                    result.CompletedParts++;
                    progress?.Invoke(
                        (double)result.CompletedParts / parts.Count,
                        $"{result.CompletedParts}/{parts.Count} · " +
                        $"{FileTranscriptResult.Fmt(TimeSpan.FromSeconds((double)end / sr))} / " +
                        FileTranscriptResult.Fmt(duration));
                    break;

                case SttTranscribeStatus.Text:
                    // Статус «распознано», но текста нет — часть не принята.
                    return Incomplete(result, canceled: false, "пустой ответ распознавания");

                case SttTranscribeStatus.NoSpeech:
                    return Incomplete(result, canceled: false, response.Error ?? "речь не распознана");

                case SttTranscribeStatus.Canceled:
                    return Incomplete(result, canceled: true, response.Error ?? "запрос отменён");

                default: // SttTranscribeStatus.Failed: HTTP-код/таймаут/нет поля text.
                    return Incomplete(result, canceled: false, response.Error ?? "ошибка распознавания");
            }
        }

        // Последняя часть принята: даже если токен отменили сразу после неё, результат полный.
        return result;
    }

    /// <summary>Движок принадлежит App и переживает окно — здесь ничего не освобождаем.</summary>
    public void Dispose() { }

    private static FileTranscriptResult Incomplete(FileTranscriptResult result, bool canceled, string reason)
    {
        result.IsComplete = false;
        result.IsCanceled = canceled;
        result.FailureReason = reason;
        return result;
    }

    /// <summary>
    /// Заранее нарезает поток так же, как локальный сервис: окна по 18 с, граница —
    /// ближайшая тишина (±1.2 с). Возвращает пары [start,end) в сэмплах; конец
    /// следующей части всегда равен началу предыдущей.
    /// </summary>
    internal static List<(int Start, int End)> PlanParts(float[] samples, int sampleRate)
    {
        int seg = SegmentSeconds * sampleRate;
        int quietWindow = (int)(QuietWindowSeconds * sampleRate);
        int maxSamples = (MaxChunkBytes - WavHeaderBytes) / 2;
        var parts = new List<(int Start, int End)>();

        int start = 0;
        while (start < samples.Length)
        {
            int end = Math.Min(start + seg, samples.Length);
            if (end < samples.Length)
                end = FileTranscriptionService.SnapToQuiet(samples, end, quietWindow);
            if (end - start > maxSamples) end = start + maxSamples; // страховка от >1 МиБ
            parts.Add((start, end));
            start = end;
        }
        return parts;
    }

    /// <summary>
    /// 16 кГц mono float [-1..1] → канонический PCM16 WAV (44-байтный заголовок).
    /// Пишет прямо из диапазона исходного массива: float-срез не копируется.
    /// </summary>
    internal static byte[] EncodeWav16kMono(ReadOnlySpan<float> samples)
    {
        const int sampleRate = MelFrontend.SampleRate;
        const short channels = 1;
        const short bitsPerSample = 16;

        int dataSize = samples.Length * 2;
        var wav = new byte[WavHeaderBytes + dataSize];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16); // размер fmt-блока
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * (bitsPerSample / 8)));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);

        var pcm = span[WavHeaderBytes..];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            BinaryPrimitives.WriteInt16LittleEndian(pcm[(i * 2)..], (short)(v * short.MaxValue));
        }
        return wav;
    }
}