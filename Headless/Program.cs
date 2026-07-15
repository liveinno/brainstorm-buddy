using System.Diagnostics;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Audio;
using BrainstormBuddy.Config;
using BrainstormBuddy.Headless;
using BrainstormBuddy.Services;
using BrainstormBuddy.Stt;
using NAudio.Wave;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected/CI */ }

var appDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "BrainstormBuddy");
Directory.CreateDirectory(appDataDir);

var logger = new LoggingService(appDataDir);
var transcriptPath = Path.Combine(appDataDir, "transcript-utf8.txt");
// FileShare.ReadWrite — чтобы несколько Headless-процессов могли работать параллельно
// (бенчмарки/replay в фоне), а не падать на эксклюзивной блокировке файла.
using var transcript = new StreamWriter(
    new FileStream(transcriptPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
    new System.Text.UTF8Encoding(true));
transcript.WriteLine($"\n=== Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
Console.WriteLine($"[Headless] Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine($"[Headless] Logs: {logger.LogFilePath}");
Console.WriteLine($"[Headless] Transcript: {transcriptPath}");
logger.Info("=== Headless host starting ===", "App");

// Бенчмарк эндпойнтинга: сравнить режимы нарезки речи (fixed/adaptive) на видео.
// --endpoint-bench <mp4|папка> [--modes fixed:0.6,fixed:1.8] [--vad 0] [--chunkmax 15]
if (args.Contains("--endpoint-bench"))
{
    var rest = args.SkipWhile(a => a != "--endpoint-bench").Skip(1).ToArray();
    return BrainstormBuddy.Headless.EndpointBench.Run(rest);
}

// СКВОЗНОЙ бенчмарк: нарезка (fixed/adaptive) → реальный STT каждого чанка → WER vs эталон.
// --endpoint-e2e <mp4> --engine gigaam|whisper|both [--maxsec 360] [--modes fixed:1.8,adaptive]
if (args.Contains("--endpoint-e2e"))
{
    var rest = args.SkipWhile(a => a != "--endpoint-e2e").Skip(1).ToArray();
    return BrainstormBuddy.Headless.EndpointE2E.Run(rest, appDataDir);
}

// Прогон реплик через реальный AgentOrchestrator (qwen 3b) → JSON для оценки суд-агентами.
// --agent-replay <transcript|folder> [--maxlines 40] [--questions-only]
if (args.Contains("--agent-replay"))
{
    var rest = args.SkipWhile(a => a != "--agent-replay").Skip(1).ToArray();
    return BrainstormBuddy.Headless.AgentReplay.Run(rest, appDataDir);
}

// Проверка фичи «Транскрибация файла»: извлечение аудио (mp4/webm/…) + посегментные тайм-коды.
// --transcribe-file <media> [onnx] [labels]
if (args.Contains("--transcribe-file"))
{
    var rest = args.SkipWhile(a => a != "--transcribe-file").Skip(1).ToArray();
    string media = rest.ElementAtOrDefault(0) ?? "";
    string onnx = rest.ElementAtOrDefault(1) ?? "artifacts/gigaam/v2_ctc.onnx";
    string labels = rest.ElementAtOrDefault(2) ?? "tools/gigaam_export/labels.json";
    Console.WriteLine($"[transcribe-file] media={media}");
    Console.WriteLine($"[transcribe-file] onnx={onnx}  labels={labels}");
    var sw = Stopwatch.StartNew();
    var extractor = new BrainstormBuddy.Media.MediaAudioExtractor();
    var audio = extractor.Extract(media);
    Console.WriteLine($"[transcribe-file] extracted {audio.Samples.Length} samples ({audio.Duration:mm\\:ss}) via {audio.Method} in {sw.ElapsedMilliseconds}ms");
    using var fileEngine = new NativeGigaamSttService(onnx, labels, SttAccel.Cpu);
    var svc = new BrainstormBuddy.Stt.FileTranscriptionService(fileEngine);
    var res = svc.Transcribe(audio.Samples, audio.Duration, audio.Method,
        (frac, status) => Console.Write($"\r[transcribe-file] {frac * 100:F0}%  {status}        "),
        default);
    Console.WriteLine();
    Console.WriteLine($"[transcribe-file] segments={res.Segments.Count}  totalMs={sw.ElapsedMilliseconds}");
    Console.WriteLine("----- TIMESTAMPED -----");
    Console.WriteLine(res.TimestampedText);
    Console.WriteLine("-----------------------");
    return 0;
}

// Проверка Whisper-движка: транскрибация файла с пунктуацией + нативными тайм-кодами.
// --whisper-file <media> [ggml-model]
if (args.Contains("--whisper-file"))
{
    var rest = args.SkipWhile(a => a != "--whisper-file").Skip(1).ToArray();
    string media = rest.ElementAtOrDefault(0) ?? "";
    string model = rest.ElementAtOrDefault(1) ?? Path.Combine(appDataDir, "models", "ggml-large-v3-turbo-q5_0.bin");
    string accel = rest.ElementAtOrDefault(2) ?? "cpu";
    int vk = int.TryParse(rest.ElementAtOrDefault(3), out var d) ? d : -1;
    Console.WriteLine($"[whisper-file] media={media}");
    Console.WriteLine($"[whisper-file] model={model}  accel={accel}  vkDevice={vk}");
    var sw = Stopwatch.StartNew();
    var audio = new BrainstormBuddy.Media.MediaAudioExtractor().Extract(media);
    Console.WriteLine($"[whisper-file] audio {audio.Duration:hh\\:mm\\:ss} via {audio.Method} ({sw.ElapsedMilliseconds}ms), гружу Whisper…");
    using var w = new BrainstormBuddy.Stt.WhisperSttEngine(model, "auto", accel, vk);
    var res = ((BrainstormBuddy.Stt.IFileTranscriber)w).Transcribe(audio.Samples, audio.Duration, audio.Method,
        (f, s) => Console.Write($"\r[whisper-file] {f * 100:F0}% {s}        "), default);
    Console.WriteLine();
    Console.WriteLine($"[whisper-file] segments={res.Segments.Count} totalMs={sw.ElapsedMilliseconds}");
    Console.WriteLine("----- WHISPER TIMESTAMPED -----");
    Console.WriteLine(res.TimestampedText);
    Console.WriteLine("-------------------------------");
    return 0;
}

// Транскрибация + сравнение с эталонным .txt: WER / соответствие / кириллица-only.
// --transcribe-compare <media> <ref.txt> [onnx] [labels] [out.txt]
if (args.Contains("--transcribe-compare"))
{
    var rest = args.SkipWhile(a => a != "--transcribe-compare").Skip(1).ToArray();
    string media = rest.ElementAtOrDefault(0) ?? "";
    string refTxt = rest.ElementAtOrDefault(1) ?? "";
    string onnx = rest.ElementAtOrDefault(2) ?? "artifacts/gigaam/v2_ctc.onnx";
    string labels = rest.ElementAtOrDefault(3) ?? "tools/gigaam_export/labels.json";
    string outPath = rest.ElementAtOrDefault(4) ?? media + "_mine.txt";
    Console.WriteLine($"[compare] media={media}");
    Console.WriteLine($"[compare] ref  ={refTxt}");
    var sw = Stopwatch.StartNew();
    var audio = new BrainstormBuddy.Media.MediaAudioExtractor().Extract(media);
    Console.WriteLine($"[compare] audio {audio.Duration:hh\\:mm\\:ss} via {audio.Method} ({sw.ElapsedMilliseconds}ms), распознаю…");
    using var eng = new NativeGigaamSttService(onnx, labels, SttAccel.Cpu);
    var res = new BrainstormBuddy.Stt.FileTranscriptionService(eng).Transcribe(audio.Samples, audio.Duration, audio.Method,
        (frac, st) => Console.Write($"\r[compare] {frac * 100:F0}% {st}          "), default);
    Console.WriteLine();
    File.WriteAllText(outPath, res.TimestampedText, new System.Text.UTF8Encoding(true));
    Console.WriteLine($"[compare] мой транскрипт → {outPath}  ({res.Segments.Count} сегм., {sw.ElapsedMilliseconds}ms)");

    string refText = File.ReadAllText(refTxt).TrimStart('﻿');
    var refW = TranscriptCompare.NormalizeWords(refText);
    var myW = TranscriptCompare.NormalizeWords(res.PlainText);
    double wer = refW.Length == 0 ? 1 : (double)TranscriptCompare.WordLevenshtein(refW, myW) / refW.Length;
    double acc = Math.Max(0, 1 - wer);
    double jac = TranscriptCompare.Jaccard(refW, myW);
    var refRu = refW.Where(TranscriptCompare.IsCyr).ToArray();
    var myRu = myW.Where(TranscriptCompare.IsCyr).ToArray();
    double accRu = refRu.Length == 0 ? 0 : Math.Max(0, 1 - (double)TranscriptCompare.WordLevenshtein(refRu, myRu) / refRu.Length);
    Console.WriteLine("========= СРАВНЕНИЕ С ЭТАЛОНОМ =========");
    Console.WriteLine($"слов: эталон={refW.Length}  моё={myW.Length}  (кириллица в эталоне={refRu.Length})");
    Console.WriteLine($"Соответствие (1-WER):         {acc * 100:F1}%");
    Console.WriteLine($"Jaccard (пересечение словаря): {jac * 100:F1}%");
    Console.WriteLine($"Кириллица-only соответствие:   {accRu * 100:F1}%");
    Console.WriteLine("=======================================");
    return 0;
}

// Проверка авто-установки ffmpeg: скачивание + распаковка + запуск.
if (args.Contains("--install-ffmpeg"))
{
    var dir = args.SkipWhile(a => a != "--install-ffmpeg").Skip(1).FirstOrDefault()
              ?? BrainstormBuddy.Media.MediaAudioExtractor.DownloadedFfmpegDir;
    Console.WriteLine($"[ffmpeg] качаю в {dir} …");
    var prog = new Progress<double>(f => Console.Write($"\r[ffmpeg] {f * 100:F0}%     "));
    var exe = await new BrainstormBuddy.Media.FfmpegInstaller().InstallAsync(dir, prog, default);
    var fi = new FileInfo(exe);
    Console.WriteLine($"\n[ffmpeg] установлен: {exe}  ({fi.Length / 1048576.0:F1} МБ)");
    var psi = new ProcessStartInfo(exe, "-version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
    using var vp = Process.Start(psi)!;
    var ver = vp.StandardOutput.ReadLine();
    vp.WaitForExit();
    Console.WriteLine($"[ffmpeg] {ver}  (exit {vp.ExitCode})");
    return 0;
}

if (args.Contains("--validate-mel"))
{
    var dir = args.SkipWhile(a => a != "--validate-mel").Skip(1).FirstOrDefault()
              ?? "tools/gigaam_export";
    var wavPath = Path.Combine(dir, "test.wav");
    var refPath = Path.Combine(dir, "ref_features.npy");
    Console.WriteLine($"[validate-mel] wav={wavPath}");
    Console.WriteLine($"[validate-mel] ref={refPath}");

    // читаем WAV как mono float [-1..1]
    var samples = new List<float>();
    using (var rdr = new AudioFileReader(wavPath))
    {
        var buf = new float[16000];
        int read;
        int ch = rdr.WaveFormat.Channels;
        while ((read = rdr.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i += ch) samples.Add(buf[i]);
        Console.WriteLine($"[validate-mel] samples={samples.Count} sr={rdr.WaveFormat.SampleRate} ch={ch}");
    }

    var fe = new MelFrontend();
    var cs = fe.Compute(samples.ToArray()); // [64][frames]
    int mels = cs.Length, frames = cs[0].Length;

    var (refData, shape) = NpyIO.LoadFloat32(refPath); // [1,64,T]
    int refMels = shape[^2], refFrames = shape[^1];
    Console.WriteLine($"[validate-mel] C#: [{mels}x{frames}]  ref: [{refMels}x{refFrames}]");

    int cmpFrames = Math.Min(frames, refFrames);
    double maxAbs = 0, sumAbs = 0; long cnt = 0;
    for (int m = 0; m < Math.Min(mels, refMels); m++)
        for (int t = 0; t < cmpFrames; t++)
        {
            double a = cs[m][t];
            double b = refData[m * refFrames + t];
            double d = Math.Abs(a - b);
            if (d > maxAbs) maxAbs = d;
            sumAbs += d; cnt++;
        }
    Console.WriteLine($"[validate-mel] max_abs_diff={maxAbs:F5}  mean_abs_diff={(sumAbs/cnt):F6}");
    Console.WriteLine(maxAbs < 0.05 ? "[validate-mel] ✅ PASS (совпадает с эталоном)"
                                    : "[validate-mel] ❌ FAIL (фронтенд не совпал)");
    return 0;
}

if (args.Contains("--validate-onnx"))
{
    var rest = args.SkipWhile(a => a != "--validate-onnx").Skip(1).ToArray();
    var dir = rest.ElementAtOrDefault(0) ?? "tools/gigaam_export";
    var onnxPath = rest.ElementAtOrDefault(1) ?? "artifacts/gigaam/v2_ctc.onnx";
    Console.WriteLine($"[validate-onnx] dir={dir}\n[validate-onnx] onnx={onnxPath}");

    var samples = new List<float>();
    using (var rdr = new AudioFileReader(Path.Combine(dir, "test.wav")))
    {
        var buf = new float[16000]; int read; int ch = rdr.WaveFormat.Channels;
        while ((read = rdr.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i += ch) samples.Add(buf[i]);
    }

    var feats = new MelFrontend().Compute(samples.ToArray());
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var sess = new GigaamOnnxSession(onnxPath, SttAccel.Cpu);
    var logp = sess.Run(feats); // [T'][34]
    sw.Stop();
    Console.WriteLine($"[validate-onnx] provider={sess.ActiveProvider}  frames_out={logp.Length}x{logp[0].Length}  onnx_ms={sw.ElapsedMilliseconds}");

    // сверка логитов с эталоном
    var (refData, shape) = NpyIO.LoadFloat32(Path.Combine(dir, "ref_logprobs.npy")); // [1,T',34]
    int rt = shape[^2], rv = shape[^1];
    int cmp = Math.Min(logp.Length, rt);
    double maxAbs = 0, sumAbs = 0; long cnt = 0;
    for (int t = 0; t < cmp; t++)
        for (int v = 0; v < rv; v++)
        { double d = Math.Abs(logp[t][v] - refData[t * rv + v]); if (d > maxAbs) maxAbs = d; sumAbs += d; cnt++; }
    Console.WriteLine($"[validate-onnx] logits max_abs_diff={maxAbs:F5} mean={(sumAbs/cnt):F6}");

    var decoder = GigaamCtcDecoder.FromLabelsFile(Path.Combine(dir, "labels.json"));
    Console.WriteLine($"[validate-onnx] decoded C#: '{decoder.Decode(logp)}'");
    var refText = File.Exists(Path.Combine(dir, "ref_text.txt")) ? File.ReadAllText(Path.Combine(dir, "ref_text.txt")) : "?";
    Console.WriteLine($"[validate-onnx] decoded ref: '{refText}'");
    Console.WriteLine(maxAbs < 0.5 ? "[validate-onnx] ✅ PASS" : "[validate-onnx] ❌ FAIL");
    return 0;
}

if (args.Contains("--bench"))
{
    var rest = args.SkipWhile(a => a != "--bench").Skip(1).ToArray();
    var wavPath = rest.ElementAtOrDefault(0) ?? "artifacts/gigaam/bench20.wav";
    var onnxPath = rest.ElementAtOrDefault(1) ?? "artifacts/gigaam/v2_ctc.onnx";
    var labelsPath = rest.ElementAtOrDefault(2) ?? "tools/gigaam_export/labels.json";
    var dockerUrl = rest.ElementAtOrDefault(3) ?? "http://localhost:8765/v1";

    double durSec;
    using (var r = new AudioFileReader(wavPath)) durSec = r.TotalTime.TotalSeconds;
    var wavBytes = File.ReadAllBytes(wavPath);
    Console.WriteLine($"[bench] wav={wavPath} dur={durSec:F1}s bytes={wavBytes.Length}");
    var results = new List<(string name, double ms, int chars, string extra)>();

    // 1) Docker (remote GigaAM)
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // прогрев
        await PostDocker(http, dockerUrl, wavBytes);
        var times = new List<double>(); int chars = 0;
        for (int i = 0; i < 2; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            chars = await PostDocker(http, dockerUrl, wavBytes);
            sw.Stop(); times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        results.Add(("Docker (remote)", times[times.Count / 2], chars, dockerUrl));
    }
    catch (Exception ex) { results.Add(("Docker (remote)", -1, 0, "ошибка: " + ex.Message)); }

    // 2) Native CPU
    await BenchNative("Native CPU", BrainstormBuddy.Stt.SttAccel.Cpu, 0);
    // 3) Native GPU (DirectML) — обе видеокарты
    await BenchNative("Native GPU:0 (DirectML)", BrainstormBuddy.Stt.SttAccel.DirectML, 0);
    await BenchNative("Native GPU:1 (DirectML)", BrainstormBuddy.Stt.SttAccel.DirectML, 1);

    async Task BenchNative(string name, BrainstormBuddy.Stt.SttAccel accel, int gpu)
    {
        try
        {
            using var svc = new NativeGigaamSttService(onnxPath, labelsPath, accel, gpu);
            var wb = File.ReadAllBytes(wavPath);
            await svc.TranscribeAsync(wb); // прогрев
            var times = new List<double>(); int chars = 0;
            for (int i = 0; i < 2; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var t = await svc.TranscribeAsync(wb);
                sw.Stop(); times.Add(sw.Elapsed.TotalMilliseconds); chars = (t ?? "").Length;
            }
            times.Sort();
            results.Add((name, times[times.Count / 2], chars, "provider=" + svc.ActiveProvider));
        }
        catch (Exception ex) { results.Add((name, -1, 0, "ошибка: " + ex.Message)); }
    }

    static async Task<int> PostDocker(HttpClient http, string baseUrl, byte[] wav)
    {
        using var content = new MultipartFormDataContent();
        var ac = new ByteArrayContent(wav);
        ac.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Add(ac, "file", "audio.wav");
        content.Add(new StringContent("gigaam-v2"), "model");
        content.Add(new StringContent("json"), "response_format");
        var resp = await http.PostAsync($"{baseUrl.TrimEnd('/')}/audio/transcriptions", content);
        var body = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Length : 0;
    }

    Console.WriteLine($"\n[bench] === РЕЗУЛЬТАТЫ (аудио {durSec:F1}s) ===");
    Console.WriteLine($"[bench] {"движок",-24} {"мс",8} {"RTF",8}  символов");
    foreach (var (name, ms, chars, extra) in results)
    {
        if (ms < 0) { Console.WriteLine($"[bench] {name,-24} {"—",8} {"—",8}  {extra}"); continue; }
        Console.WriteLine($"[bench] {name,-24} {ms,8:F0} {ms / (durSec * 1000),8:F3}  {chars}  ({extra})");
    }
    return 0;
}

if (args.Contains("--transcribe-native"))
{
    var rest = args.SkipWhile(a => a != "--transcribe-native").Skip(1).ToArray();
    var wavPath = rest.ElementAtOrDefault(0) ?? "real.wav";
    var onnxPath = rest.ElementAtOrDefault(1) ?? "artifacts/gigaam/v2_ctc.onnx";
    var labelsPath = rest.ElementAtOrDefault(2) ?? "tools/gigaam_export/labels.json";
    var accel = args.Contains("--dml") ? BrainstormBuddy.Stt.SttAccel.DirectML : BrainstormBuddy.Stt.SttAccel.Cpu;

    var wavBytes = File.ReadAllBytes(wavPath);
    using var svc = new NativeGigaamSttService(onnxPath, labelsPath, accel);
    Console.WriteLine($"[native] provider={svc.ActiveProvider} wav={wavPath} bytes={wavBytes.Length}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var text = await svc.TranscribeAsync(wavBytes);
    sw.Stop();
    Console.WriteLine($"[native] time={sw.ElapsedMilliseconds}ms");
    Console.WriteLine($"[native] text: {text}");
    return 0;
}

if (args.Contains("--record"))
{
    var path = args.SkipWhile(a => a != "--record").Skip(1).FirstOrDefault() ?? "loopback.wav";
    var secs = double.TryParse(args.SkipWhile(a => a != "--duration").Skip(1).FirstOrDefault(), out var s) ? s : 8.0;
    await RecordLoopbackAsync(path, secs, logger);
return 0;

static async Task RecordLoopbackAsync(string path, double seconds, LoggingService logger)
{
    logger.Info($"Recording {seconds}s of loopback → {path}", "Audio");
    using var capture = new WasapiLoopbackCapture();
    var fmt = capture.WaveFormat;
    var mono = new List<float>();
    var done = new TaskCompletionSource();
    capture.DataAvailable += (s, e) =>
    {
        var ch = Math.Max(1, fmt.Channels);
        if (fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            for (int i = 0; i < e.BytesRecorded / 4; i += ch)
            {
                float sum = 0f;
                for (int c = 0; c < ch && (i + c) * 4 + 3 < e.BytesRecorded; c++)
                    sum += BitConverter.ToSingle(e.Buffer, (i + c) * 4);
                mono.Add(sum / ch);
            }
        }
    };
    capture.RecordingStopped += (s, e) => done.TrySetResult();
    capture.StartRecording();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    capture.StopRecording();
    await done.Task;
    var wav = WavEncoder.EncodeWav(mono.ToArray(), fmt.SampleRate);
    File.WriteAllBytes(path, wav);
    logger.Info($"Wrote {wav.Length} bytes to {path}", "Audio");
}
}

var configPath = Path.Combine(appDataDir, "config.json");
var loader = new ConfigLoader(configPath, logger);
var config = loader.Load();
logger.Info("Config loaded", "Config");

logger.Info($"API: {config.Api.BaseUrl}, Chat: {config.Api.ChatModel}", "Config");
logger.Info($"STT: {config.Api.SttBaseUrl ?? config.Api.BaseUrl}, SttModel: {config.Api.SttModel}", "Config");
logger.Info($"Audio: SampleRate={config.Audio.SampleRate}, ChunkMax={config.Audio.ChunkMaxSeconds}s, Rms={config.Audio.RmsThreshold}", "Config");

if (string.IsNullOrWhiteSpace(config.Api.ApiKey))
{
    logger.Error("ApiKey is empty — aborting. Set it in %AppData%\\BrainstormBuddy\\config.json", null, "App");
    return 2;
}

var api = new OpenAiClient(config.Api, logger);

// Диагностика LLM: реальная проверка (ключ/URL + чат-пинг моделью) на конфиге приложения.
// Ключ читается из config.json самим клиентом — тот же путь, что в бою.
if (args.Contains("--check-llm"))
{
    Console.WriteLine($"[check-llm] BaseUrl={config.Api.BaseUrl}  Model={config.Api.ChatModel}");
    var (ok, detail) = await api.CheckLlmConnectionAsync();
    Console.WriteLine($"[check-llm] ping: ok={ok}  detail={detail}");
    // Полноценный запрос как «секрет» (системный промпт + токены) — точная копия боевого пути.
    var full = await api.AskAsync("Привет, ответь одним словом.", "Ты полезный ассистент.",
        config.Advanced.MaxResponseTokens, new System.Collections.Generic.List<BrainstormBuddy.Ai.ChatMessage>());
    Console.WriteLine($"[check-llm] AskAsync: '{(full.Content ?? "<null/пусто>")}' (tokens: prompt={full.PromptTokens}, completion={full.CompletionTokens})");
    return ok ? 0 : 1;
}

var sttConfig = new ApiConfig
{
    BaseUrl = config.Api.SttBaseUrl ?? config.Api.BaseUrl,
    ApiKey = string.Empty,
    ChatModel = config.Api.ChatModel,
    SttBaseUrl = string.Empty,
    SttModel = config.Api.SttModel,
    SttLanguage = config.Api.SttLanguage,
    RequestTimeoutSeconds = config.Api.RequestTimeoutSeconds,
    MaxRetries = config.Api.MaxRetries
};
var sttApi = new OpenAiClient(sttConfig, logger);
logger.Info("Two clients created: LLM uses Bearer key, STT is anonymous", "Ai");

var buffer = new AudioBuffer(
    config.Audio.SampleRate,
    config.Audio.ChunkMaxSeconds,
    config.Audio.SilenceSeconds,
    config.Audio.RmsThreshold,
    config.Audio.VadMode,
    config.Audio.PreRollMs,
    config.Audio.PostRollMs,
    config.Audio.OverlapMs,
    config.Audio.MinSpeechMs,
    logger);
// Настройки вне конструктора (авто-калибровка порога) — как в GUI-приложении, из конфига.
buffer.UpdateParameters(config.Audio.RmsThreshold, config.Audio.SilenceSeconds,
    config.Audio.PreRollMs, config.Audio.PostRollMs, config.Audio.MinSpeechMs,
    config.Audio.AutoCalibrateThreshold);

using var loopback = new LoopbackAudioSource(buffer, config.Audio.SampleRate, logger);
loopback.Start();
logger.Info("Loopback capture started", "Audio");

var history = new List<ChatMessage>();
var swTotal = Stopwatch.StartNew();
var chunkIdx = 0;
var lastLlmCall = DateTime.MinValue;
var minLlmGap = TimeSpan.FromSeconds(15);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); logger.Info("Ctrl+C pressed, shutting down", "App"); };

logger.Info("Main loop started", "Loop");
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        if (buffer.HasCompleteUtterance())
        {
            chunkIdx++;
            var wav = buffer.GetChunkForTranscription();
            buffer.Reset();
            logger.Info($"Chunk #{chunkIdx}: {wav.Length} bytes", "Loop");

            var sttSw = Stopwatch.StartNew();
            var text = await sttApi.TranscribeAsync(wav, cts.Token);
            sttSw.Stop();
            logger.Info($"STT → {(string.IsNullOrWhiteSpace(text) ? "<empty>" : text)} ({sttSw.ElapsedMilliseconds}ms)", "Loop");
            if (!string.IsNullOrWhiteSpace(text))
            {
                transcript.WriteLine($"[{DateTime.Now:HH:mm:ss}] [STT] {text}");
                transcript.Flush();
            }

            if (string.IsNullOrWhiteSpace(text)) continue;

            var sinceLastLlm = DateTime.Now - lastLlmCall;
            if (lastLlmCall != DateTime.MinValue && sinceLastLlm < minLlmGap)
            {
                logger.Info($"LLM rate-limit: skipping (last call {sinceLastLlm.TotalSeconds:F0}s ago, need {minLlmGap.TotalSeconds:F0}s)", "Loop");
                Console.WriteLine($"[STT] {text}");
                Console.WriteLine($"[LLM] (suppressed — rate limit)");
                continue;
            }
            else if (lastLlmCall != DateTime.MinValue)
            {
                logger.Info($"LLM gap {sinceLastLlm.TotalSeconds:F0}s, allowing call", "Loop");
            }

            var askSw = Stopwatch.StartNew();
            var result = await api.AskAsync(text, config.Advanced.SystemPrompt, config.Advanced.MaxResponseTokens, history, cts.Token);
            askSw.Stop();
            lastLlmCall = DateTime.Now;
            var answer = result.Content;

            if (string.IsNullOrWhiteSpace(answer))
            {
                logger.Warn("LLM returned empty", "Loop");
                continue;
            }

            history.Add(ChatMessage.User(text));
            history.Add(ChatMessage.Assistant(answer));
            while (history.Count > config.Advanced.HistorySize) history.RemoveAt(0);

            logger.Info($"LLM → {answer} ({askSw.ElapsedMilliseconds}ms)", "Loop");
            transcript.WriteLine($"[{DateTime.Now:HH:mm:ss}] [LLM] {answer}  ({askSw.ElapsedMilliseconds}ms)");
            transcript.Flush();
            Console.WriteLine();
            Console.WriteLine($"════════════════════════════════════════");
            Console.WriteLine($"[STT] {text}");
            Console.WriteLine($"[LLM] {answer}  ({askSw.ElapsedMilliseconds}ms)");
            Console.WriteLine($"════════════════════════════════════════");
        }
        else
        {
            await Task.Delay(150, cts.Token);
        }
    }
}
catch (OperationCanceledException)
{
    logger.Info("Main loop cancelled", "Loop");
}
catch (Exception ex)
{
    logger.Error("Main loop crashed", ex, "Loop");
    return 1;
}
finally
{
    logger.Info($"=== Headless exiting. Uptime: {swTotal.Elapsed:hh\\:mm\\:ss}, chunks={chunkIdx} ===", "App");
}

return 0;

// Утилита сравнения транскриптов (нормализация + пословный WER).
static class TranscriptCompare
{
    // Убирает тайм-коды ([mm:ss] и mm:ss), приводит к нижнему регистру, ё→е,
    // оставляет только буквы/цифры → массив слов.
    public static string[] NormalizeWords(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d{1,2}:\d{2}(:\d{2})?\b", " ");
        s = s.ToLowerInvariant().Replace('ё', 'е');
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool IsCyr(string w) => w.Length > 0 && w[0] >= 'Ѐ' && w[0] <= 'ӿ';

    // Пословное расстояние Левенштейна (rolling arrays).
    public static int WordLevenshtein(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[m];
    }

    public static double Jaccard(string[] a, string[] b)
    {
        var sa = new HashSet<string>(a);
        var sb = new HashSet<string>(b);
        int inter = sa.Count(sb.Contains);
        var uni = new HashSet<string>(a);
        uni.UnionWith(b);
        return uni.Count == 0 ? 0 : (double)inter / uni.Count;
    }
}
