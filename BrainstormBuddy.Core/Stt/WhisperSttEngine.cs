using BrainstormBuddy.Ai;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Встроенный Whisper (large-v3-turbo) через Whisper.net (whisper.cpp), без Docker/Python.
/// Даёт пунктуацию, заглавные, английский и НАТИВНЫЕ тайм-коды сегментов. Тяжелее GigaAM —
/// для транскрибации файлов (качество важнее задержки). Модель ggml качается отдельно.
/// </summary>
public sealed class WhisperSttEngine : ISttEngine, IFileTranscriber
{
    private readonly WhisperFactory _factory;
    private readonly string _language;
    private readonly string _accel;

    public string Name => "whisper";
    public string EngineName => _accel == "gpu" ? "Whisper turbo · GPU" : "Whisper turbo · CPU";

    /// <param name="language">"auto" (детект, поддержка смешанной речи) или код ("ru").</param>
    /// <param name="accel">"gpu" (Vulkan) или "cpu". (App разрешает "auto" заранее.)</param>
    /// <param name="gpuDevice">Индекс Vulkan-устройства для GPU (на ноутбуках 0 — встройка).</param>
    public WhisperSttEngine(string modelPath, string language = "auto", string accel = "cpu", int gpuDevice = -1)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper ggml-модель не найдена", modelPath);
        _accel = string.Equals(accel, "gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "cpu";
        ConfigureRuntime(_accel, gpuDevice);
        _factory = WhisperFactory.FromPath(modelPath);
        _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    // Порядок нативных рантаймов (глобально, ДО первой загрузки либы). CPU-режим — без GPU.
    // Vulkan работает на NVIDIA/AMD/Intel через драйвер; при отсутствии устройства — откат на CPU.
    private static void ConfigureRuntime(string accel, int gpuDevice)
    {
        try
        {
            bool gpu = accel == "gpu";
            RuntimeOptions.RuntimeLibraryOrder = gpu
                ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
            // На ноутбуках Vulkan-устройство 0 — встройка (медленно). Целимся в дискретку.
            if (gpu && gpuDevice >= 0)
                Environment.SetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES", gpuDevice.ToString());
        }
        catch { /* дрейф API — оставляем дефолтный порядок */ }
    }

    private WhisperProcessor Build() =>
        _factory.CreateBuilder()
            .WithLanguage(_language)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8)) // ~физические ядра
            .Build();

    // ---- ISttEngine: WAV → текст (для общего пути/оверлея) ----
    // ПРИМЕЧАНИЕ по скорости: доминирующая цена Whisper на живых коротких чанках — сам whisper_full,
    // который кодирует ФИКСИРОВАННОЕ окно 30с на каждый вызов (~12с на Vulkan-GPU для turbo),
    // независимо от длины реплики. Переиспользование процессора это НЕ уменьшает (проверено замером:
    // те же ~12.5с/чанк). Поэтому для живого режима дефолт — GigaAM (CTC, ~0.5с/чанк), а Whisper —
    // для транскрибации файлов (длинное непрерывное аудио, мало границ окна).
    public async Task<string?> TranscribeAsync(byte[] wavAudio, CancellationToken ct = default)
    {
        if (wavAudio == null || wavAudio.Length == 0) return null;
        var samples = WavTo16kMono(wavAudio);
        if (samples.Length < 1600) return null;

        var sb = new System.Text.StringBuilder();
        // await using — при отмене процессор закрывается через DisposeAsync,
        // иначе Whisper.net кидает «Cannot dispose while processing».
        await using var proc = Build();
        await foreach (var seg in proc.ProcessAsync(samples, ct))
            sb.Append(seg.Text);
        var text = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true);

    // ---- IFileTranscriber: сегменты с нативными тайм-кодами Whisper ----
    public FileTranscriptResult Transcribe(float[] samples16k, TimeSpan duration, string method,
        Action<double, string>? progress, CancellationToken ct)
    {
        var result = new FileTranscriptResult { Duration = duration, ExtractMethod = method };
        if (samples16k.Length == 0) return result;

        double total = duration.TotalSeconds > 0 ? duration.TotalSeconds : (double)samples16k.Length / 16000;
        // WhisperProcessor — IAsyncDisposable. При ОТМЕНЕ его нельзя закрывать синхронным
        // Dispose() («Cannot dispose while processing») — только DisposeAsync (в finally ниже).
        var proc = Build();
        try
        {
            // Асинхронный поток сегментов потребляем синхронно (мы уже в фоновом потоке).
            var e = proc.ProcessAsync(samples16k, ct).GetAsyncEnumerator(ct);
            try
            {
                while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    ct.ThrowIfCancellationRequested();
                    var seg = e.Current;
                    var text = seg.Text?.Trim() ?? "";
                    if (text.Length > 0)
                        result.Segments.Add(new TranscriptSegment(seg.Start, seg.End, text));
                    double frac = total > 0 ? Math.Min(1.0, seg.End.TotalSeconds / total) : 0;
                    progress?.Invoke(frac, $"{FileTranscriptResult.Fmt(seg.End)} / {FileTranscriptResult.Fmt(duration)}");
                }
            }
            finally { e.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally { proc.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return result;
    }

    // WAV (любой формат) → 16кГц mono float [-1..1]
    private static float[] WavTo16kMono(byte[] wav)
    {
        using var ms = new MemoryStream(wav);
        using var reader = new WaveFileReader(ms);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != 16000)
            sp = new WdlResamplingSampleProvider(sp, 16000);

        var outp = new List<float>();
        var buf = new float[16000];
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) outp.Add(buf[i]);
        return outp.ToArray();
    }

    public void Dispose() => _factory.Dispose();
}
