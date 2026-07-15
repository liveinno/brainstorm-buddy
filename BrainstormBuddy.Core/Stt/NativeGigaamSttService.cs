using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using BrainstormBuddy.Ai;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Встроенный STT: GigaAM v2 CTC через ONNX Runtime, без Docker/Python.
/// WAV (любой sr/каналы) → 16кГц mono → лог-мел → ONNX → CTC → текст.
/// CPU по умолчанию (везде), DirectML — опция.
/// </summary>
public sealed class NativeGigaamSttService : ISttEngine, IDisposable
{
    private readonly MelFrontend _fe = new();
    private readonly GigaamOnnxSession _session;
    private readonly GigaamCtcDecoder _decoder;
    private readonly int _chunkSec;
    private readonly int _overlapSec;

    public string Name => "native";
    public string ActiveProvider => _session.ActiveProvider;

    public NativeGigaamSttService(string modelPath, string labelsPath, SttAccel accel = SttAccel.Cpu,
                                  int gpuDevice = 0, int chunkSec = 24, int overlapSec = 2)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("GigaAM ONNX модель не найдена", modelPath);
        if (!File.Exists(labelsPath)) throw new FileNotFoundException("labels.json не найден", labelsPath);
        _session = new GigaamOnnxSession(modelPath, accel, gpuDevice);
        _decoder = GigaamCtcDecoder.FromLabelsFile(labelsPath);
        _chunkSec = chunkSec;
        _overlapSec = overlapSec;
    }

    public Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
        => Task.Run<string?>(() =>
        {
            if (wavAudio == null || wavAudio.Length == 0) return null;
            float[] samples = WavTo16kMono(wavAudio);
            if (samples.Length < MelFrontend.NFft) return null;

            string text = TranscribeSamples(samples, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
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

    public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true); // модель загружена в конструкторе

    // WAV (любой формат) → 16кГц mono float [-1..1]
    private static float[] WavTo16kMono(byte[] wav)
    {
        using var ms = new MemoryStream(wav);
        using var reader = new WaveFileReader(ms);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != MelFrontend.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, MelFrontend.SampleRate);

        var outp = new List<float>();
        var buf = new float[16000];
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) outp.Add(buf[i]);
        return outp.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
