using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Встроенный STT: GigaAM v2 CTC через ONNX Runtime, без Docker/Python.
/// WAV (любой sr/каналы) → 16кГц mono → лог-мел → ONNX → CTC → текст.
/// CPU по умолчанию (везде), DirectML — опция. Версия v3 — NativeGigaamV3SttService.
/// </summary>
public sealed class NativeGigaamSttService : IGigaamSttEngine
{
    private readonly MelFrontend _fe = new();
    private readonly GigaamOnnxSession _session;
    private readonly GigaamCtcDecoder _decoder;
    private readonly int _chunkSec;
    private readonly int _overlapSec;

    public string Name => "native";
    public string VersionTag => "v2";
    public string ActiveProvider => _session.ActiveProvider;

    public NativeGigaamSttService(string modelPath, string labelsPath, SttAccel accel = SttAccel.Cpu,
                                  int gpuDevice = 0, int chunkSec = 24, int overlapSec = 2,
                                  Services.LoggingService? logger = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("GigaAM ONNX модель не найдена", modelPath);
        if (!File.Exists(labelsPath)) throw new FileNotFoundException("labels.json не найден", labelsPath);
        _session = new GigaamOnnxSession(modelPath, accel, gpuDevice, logger);
        _decoder = GigaamCtcDecoder.FromLabelsFile(labelsPath);
        _chunkSec = chunkSec;
        _overlapSec = overlapSec;
    }

    public Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
        => Task.Run<string?>(() =>
        {
            if (wavAudio == null || wavAudio.Length == 0) return null;
            float[] samples = WavPcm.WavTo16kMono(wavAudio);
            if (samples.Length < MelFrontend.NFft) return null;

            string text = TranscribeSamples(samples, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }, ct);

    public Task<TimedTranscript?> TranscribeTimedAsync(byte[] wavAudio, CancellationToken ct = default)
        => Task.Run<TimedTranscript?>(() =>
        {
            if (wavAudio == null || wavAudio.Length == 0) return null;
            float[] samples = WavPcm.WavTo16kMono(wavAudio);
            if (samples.Length < MelFrontend.NFft) return null;
            var t = TranscribeTimed(samples, ct);
            return string.IsNullOrWhiteSpace(t?.Text) ? null : t;
        }, ct);

    /// <summary>16кГц mono float → текст (с чанкингом длинных записей, как в LocalSttBridge).</summary>
    public string TranscribeSamples(float[] samples, CancellationToken ct = default)
    {
        int sr = MelFrontend.SampleRate;
        int maxLen = _chunkSec * sr;
        if (samples.Length <= maxLen)
            return _decoder.Decode(_session.Run(_fe.Compute(samples)));

        int overlap = _overlapSec * sr;
        var parts = new List<string>();
        int start = 0;
        while (start < samples.Length)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + maxLen, samples.Length);
            var slice = new float[end - start];
            Array.Copy(samples, start, slice, 0, slice.Length);
            var t = _decoder.Decode(_session.Run(_fe.Compute(slice)));
            if (!string.IsNullOrWhiteSpace(t)) parts.Add(t);
            if (end >= samples.Length) break;
            start = end - overlap;
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Текст + пословные таймкоды. Без чанкинга движок = один прогон: слова идут
    /// от начала samples (offset=0). С чанкингом слова каждого среза сдвигаются
    /// на его начало; overlap-дубликаты осмысленно остаются — внутричанковой
    /// разметке безразличен лишний повторный спан той же позиции.
    /// </summary>
    public TimedTranscript? TranscribeTimed(float[] samples, CancellationToken ct = default)
    {
        int sr = MelFrontend.SampleRate;
        int maxLen = _chunkSec * sr;
        if (samples.Length <= maxLen)
        {
            var logp = _session.Run(_fe.Compute(samples));
            var spf = logp.Length > 0 ? samples.Length / (double)sr / logp.Length : 0;
            var words = _decoder.DecodeWords(logp, spf);
            var text = string.Join(' ', words.Select(w => w.Text));
            return new TimedTranscript(text, words);
        }

        int overlap = _overlapSec * sr;
        var all = new List<SttWordSpan>();
        int start = 0;
        while (start < samples.Length)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + maxLen, samples.Length);
            var slice = new float[end - start];
            Array.Copy(samples, start, slice, 0, slice.Length);
            var logp = _session.Run(_fe.Compute(slice));
            var spf = logp.Length > 0 ? slice.Length / (double)sr / logp.Length : 0;
            var offset = start / (double)sr;
            foreach (var w in _decoder.DecodeWords(logp, spf))
                all.Add(new SttWordSpan(w.Text, w.StartSec + offset, w.EndSec + offset));
            if (end >= samples.Length) break;
            start = end - overlap;
        }
        var fullText = string.Join(' ', all.Select(w => w.Text));
        return new TimedTranscript(fullText, all);
    }

    public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true); // модель загружена в конструкторе

    public void Dispose() => _session.Dispose();
}
