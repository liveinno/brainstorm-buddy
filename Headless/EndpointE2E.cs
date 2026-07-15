using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Audio;
using BrainstormBuddy.Media;
using BrainstormBuddy.Stt;

namespace BrainstormBuddy.Headless;

/// <summary>
/// СКВОЗНОЙ бенчмарк эндпойнтинга: нарезаем аудио разными режимами (fixed vs adaptive),
/// РЕАЛЬНО распознаём каждый чанк движком (GigaAM и/или Whisper), сшиваем чанки с устранением
/// overlap-дублей и считаем WER против эталона. Отвечает на вопрос: улучшает ли адаптивная
/// нарезка сам транскрипт end-to-end, а не только расположение границ.
///
/// Overlap-сшивка обязательна: AudioBuffer оставляет ~1с перекрытия между чанками, поэтому без
/// неё режим с бо́льшим числом чанков штрафовался бы за дубли (нечестно). Стичим по совпадающему
/// хвосту/началу — как правильный конкатенатор транскрипта.
/// </summary>
public static class EndpointE2E
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Set(double sec) => _now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(sec);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    public static int Run(string[] rest, string appDataDir)
    {
        string video = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        if (!File.Exists(video)) { Console.WriteLine($"Файл не найден: {video}"); return 2; }

        int maxSec = ArgInt(rest, "--maxsec", 360);
        string engineArg = (ArgStr(rest, "--engine", "gigaam")).ToLowerInvariant();
        var modeArg = ArgStr(rest, "--modes", "fixed:1.8,adaptive");
        string onnx = ArgStr(rest, "--onnx", "artifacts/gigaam/v2_ctc.onnx");
        string labels = ArgStr(rest, "--labels", "tools/gigaam_export/labels.json");
        string whisperModel = ArgStr(rest, "--whisper-model",
            Path.Combine(appDataDir, "models", "ggml-large-v3-turbo-q5_0.bin"));
        string whisperAccel = ArgStr(rest, "--whisper-accel", "gpu");   // gpu (Vulkan) | cpu
        int vk = ArgInt(rest, "--vk", 1);                                 // Vulkan-устройство: 1 = дискретка

        // Боевые параметры чанкинга.
        int sr = MediaAudioExtractor.TargetSampleRate;
        int chunkMax = ArgInt(rest, "--chunkmax", 15);
        double rms = ArgDouble(rest, "--rms", 0.01);
        int vad = ArgInt(rest, "--vad", 0);
        int preRoll = 400, postRoll = 500, overlap = 1000, minSpeech = 1000;

        Console.WriteLine($"[e2e] video={Path.GetFileName(video)} maxsec={maxSec} engine={engineArg} modes={modeArg}");
        var audio = new MediaAudioExtractor().Extract(video);
        float[] samples = audio.Samples;
        if (maxSec > 0 && samples.Length > maxSec * sr)
        {
            var cut = new float[maxSec * sr]; Array.Copy(samples, cut, cut.Length); samples = cut;
        }
        double windowSec = samples.Length / (double)sr;
        Console.WriteLine($"[e2e] окно {TimeSpan.FromSeconds(windowSec):hh\\:mm\\:ss} ({samples.Length} сэмплов)");

        // Эталон в пределах окна.
        var refWords = TranscriptCompare.NormalizeWords(LoadReferenceText(video, windowSec));
        Console.WriteLine($"[e2e] эталон: {refWords.Length} слов в окне");
        if (refWords.Length == 0) { Console.WriteLine("Пустой эталон — нечего сравнивать"); return 2; }

        var modes = ParseModes(modeArg);
        var engines = engineArg == "both" ? new[] { "gigaam", "whisper" } : new[] { engineArg };

        var report = new StringBuilder();
        report.AppendLine($"# E2E эндпойнтинг × STT — {Path.GetFileNameWithoutExtension(video)}");
        report.AppendLine($"окно {windowSec:F0}с, эталон {refWords.Length} слов, chunkMax={chunkMax}s, vad={vad}");
        report.AppendLine();
        report.AppendLine("| движок | режим | чанков | слов | WER | точность | STT-время | RTF |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var eng in engines)
        {
            ISttEngine engine;
            try
            {
                Console.WriteLine($"\n[e2e] гружу движок {eng}{(eng == "whisper" ? $" (accel={whisperAccel}, vk={vk})" : "")}…");
                engine = eng == "whisper"
                    ? new WhisperSttEngine(whisperModel, "auto", whisperAccel, vk)
                    : new NativeGigaamSttService(onnx, labels, SttAccel.Cpu);
            }
            catch (Exception ex) { Console.WriteLine($"[e2e] движок {eng} не поднялся: {ex.Message}"); continue; }

            foreach (var mode in modes)
            {
                var chunks = EmitChunks(samples, sr, mode, chunkMax, rms, vad, preRoll, postRoll, overlap, minSpeech,
                    out double finalT);
                string label = mode.Adaptive ? $"adaptive→{finalT:F1}с" : mode.Name;

                var acc = new List<string>();
                var swStt = Stopwatch.StartNew();
                int done = 0;
                foreach (var wav in chunks)
                {
                    string? txt;
                    try { txt = engine.TranscribeAsync(wav).GetAwaiter().GetResult(); }
                    catch { txt = null; }
                    done++;
                    Console.Write($"\r[e2e] {eng}/{label}: {done}/{chunks.Count} чанков…    ");
                    if (string.IsNullOrWhiteSpace(txt)) continue;
                    StitchAppend(acc, TranscriptCompare.NormalizeWords(txt));
                }
                swStt.Stop();
                Console.WriteLine();

                var hyp = acc.ToArray();
                int dist = TranscriptCompare.WordLevenshtein(refWords, hyp);
                double wer = (double)dist / refWords.Length;
                double acc1 = Math.Max(0, 1 - wer);
                double rtf = swStt.Elapsed.TotalSeconds / windowSec;

                report.AppendLine($"| {eng} | {label} | {chunks.Count} | {hyp.Length} | {wer * 100:F1}% | {acc1 * 100:F1}% | {swStt.Elapsed.TotalSeconds:F0}с | {rtf:F2} |");
                Console.WriteLine($"[e2e] {eng} {label,-14} чанков={chunks.Count,3} слов={hyp.Length,5} WER={wer * 100:F1}% точность={acc1 * 100:F1}% RTF={rtf:F2}");
            }

            (engine as IDisposable)?.Dispose();
        }

        var evalDir = Path.Combine(appDataDir, "eval");
        Directory.CreateDirectory(evalDir);
        var outPath = Path.Combine(evalDir, $"endpoint_e2e_{Path.GetFileNameWithoutExtension(video)}.md");
        File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"\n[e2e] отчёт: {outPath}");
        Console.WriteLine(report.ToString());
        return 0;
    }

    private sealed record ModeSpec(string Name, double SilenceSec, bool Adaptive);

    private static List<ModeSpec> ParseModes(string spec)
    {
        var list = new List<ModeSpec>();
        foreach (var p in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = p.Trim();
            if (t == "adaptive") list.Add(new("adaptive", 1.2, true));
            else if (t.StartsWith("fixed:") && double.TryParse(t.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                list.Add(new(t, s, false));
        }
        return list.Count > 0 ? list : new() { new("fixed:1.8", 1.8, false), new("adaptive", 1.2, true) };
    }

    private static List<byte[]> EmitChunks(float[] samples, int sr, ModeSpec mode, int chunkMax, double rms,
        int vad, int preRoll, int postRoll, int overlap, int minSpeech, out double finalT)
    {
        var clock = new ManualClock();
        var buf = new AudioBuffer(sr, chunkMax, mode.SilenceSec, rms, vad, preRoll, postRoll, overlap, minSpeech,
            logger: null, timeProvider: clock);
        if (mode.Adaptive)
            buf.EnableAdaptiveEndpointing(new PauseAdaptiveController(new AdaptiveEndpointConfig(), mode.SilenceSec, clock));

        var outp = new List<byte[]>();
        int block = sr / 10;
        long fed = 0;
        for (int i = 0; i < samples.Length; i += block)
        {
            int n = Math.Min(block, samples.Length - i);
            clock.Set(fed / (double)sr);
            var seg = new float[n]; Array.Copy(samples, i, seg, 0, n);
            buf.AddSamples(seg);
            fed += n;
            while (buf.TryGetReadyChunk(out var wav)) outp.Add(wav);
        }
        clock.Set(fed / (double)sr);
        if (buf.Flush(out var tail)) outp.Add(tail);
        finalT = buf.CurrentSilenceSeconds;
        return outp;
    }

    // Сшивка с устранением overlap-дублей: находим макс. совпадение хвоста acc и начала next.
    private static void StitchAppend(List<string> acc, string[] next)
    {
        if (next.Length == 0) return;
        int maxOv = Math.Min(12, Math.Min(acc.Count, next.Length));
        int best = 0;
        for (int k = maxOv; k >= 2; k--)
        {
            bool match = true;
            for (int i = 0; i < k; i++)
                if (!acc[acc.Count - k + i].Equals(next[i], StringComparison.Ordinal)) { match = false; break; }
            if (match) { best = k; break; }
        }
        for (int i = best; i < next.Length; i++) acc.Add(next[i]);
    }

    private static string LoadReferenceText(string mediaPath, double maxSec)
    {
        var dir = Path.GetDirectoryName(mediaPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(mediaPath);
        var refFile = new[] { Path.Combine(dir, baseName + "_timestamped.txt"), Path.Combine(dir, baseName + ".txt") }
            .FirstOrDefault(File.Exists);
        if (refFile == null) return "";
        var rx = new Regex(@"^\[?(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\]?\s+(.*)$");
        var sb = new StringBuilder();
        foreach (var raw in File.ReadAllLines(refFile))
        {
            var m = rx.Match(raw.Trim());
            if (!m.Success) continue;
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            double t = h * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
            if (maxSec > 0 && t > maxSec) break;
            sb.Append(m.Groups[4].Value.Trim()).Append(' ');
        }
        return sb.ToString();
    }

    private static string ArgStr(string[] a, string key, string def)
    { var v = a.SkipWhile(x => x != key).Skip(1).FirstOrDefault(); return string.IsNullOrEmpty(v) || v.StartsWith("--") ? def : v; }
    private static int ArgInt(string[] a, string key, int def)
    { var v = a.SkipWhile(x => x != key).Skip(1).FirstOrDefault(); return int.TryParse(v, out var r) ? r : def; }
    private static double ArgDouble(string[] a, string key, double def)
    { var v = a.SkipWhile(x => x != key).Skip(1).FirstOrDefault(); return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def; }
}
