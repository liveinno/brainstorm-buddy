using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Встроенный STT: GigaAM v3 e2e CTC (INT8 ONNX, BPE-словарь 257) через ONNX Runtime.
/// Отличия от v2 (NativeGigaamSttService): фронтенд n_fft=win=320 без center-паддинга
/// и BPE-декодер. Входная труба та же: WAV → 16кГц mono → лог-мел → ONNX → CTC → текст.
/// </summary>
public sealed class NativeGigaamV3SttService : IGigaamSttEngine
{
    private readonly MelFrontend _fe = new(nFft: 320, winLength: 320, hopLength: 160, nMels: 64, center: false);
    private readonly GigaamOnnxSession _session;
    private readonly GigaamV3CtcDecoder _decoder;
    private readonly int _chunkSec;
    private readonly int _overlapSec;

    public string Name => "native3";
    public string VersionTag => "v3";
    public string ActiveProvider => _session.ActiveProvider;

    public NativeGigaamV3SttService(string modelPath, string vocabPath, SttAccel accel = SttAccel.Cpu,
                                    int gpuDevice = 0, int chunkSec = 24, int overlapSec = 2,
                                    Services.LoggingService? logger = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("GigaAM v3 ONNX модель не найдена", modelPath);
        if (!File.Exists(vocabPath)) throw new FileNotFoundException("v3_e2e_ctc_vocab.txt не найден", vocabPath);
        _session = new GigaamOnnxSession(modelPath, accel, gpuDevice, logger);
        _decoder = GigaamV3CtcDecoder.FromVocabFile(vocabPath);
        _chunkSec = chunkSec;
        _overlapSec = overlapSec;
    }

    public Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
        => Task.Run<string?>(() =>
        {
            if (wavAudio == null || wavAudio.Length == 0) return null;
            float[] samples = WavPcm.WavTo16kMono(wavAudio);
            if (samples.Length < _fe.Nfft) return null;

            string text = TranscribeSamples(samples, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }, ct);

    public Task<TimedTranscript?> TranscribeTimedAsync(byte[] wavAudio, CancellationToken ct = default)
        => Task.Run<TimedTranscript?>(() =>
        {
            if (wavAudio == null || wavAudio.Length == 0) return null;
            float[] samples = WavPcm.WavTo16kMono(wavAudio);
            if (samples.Length < _fe.Nfft) return null;
            var t = TranscribeTimed(samples, ct);
            return string.IsNullOrWhiteSpace(t?.Text) ? null : t;
        }, ct);

    /// <summary>16кГц mono float → текст (чаккинг длинных записей, как у v2).</summary>
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
