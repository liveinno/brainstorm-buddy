using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BrainstormBuddy.Audio;
using BrainstormBuddy.Media;

namespace BrainstormBuddy.Headless;

/// <summary>
/// Офлайн-бенчмарк эндпойнтинга: mp4 → MediaAudioExtractor → настоящая VAD-стейт-машина
/// AudioBuffer (детерминированно, идентично проду) → чанки с границами → метрики против
/// эталонного timestamped-транскрипта. Без микрофона и без реального времени.
///
/// Приватность: в отчёт не попадает текст видео — только границы/длительности/счётчики.
/// Отчёты пишутся в %APPDATA%\BrainstormBuddy\eval\ (вне репозитория).
/// </summary>
public static class EndpointBench
{
    // Ручной провайдер времени: продвигаем «часы» по аудио-времени поданных сэмплов.
    // Без него AudioBuffer посчитал бы speechDuration по стенным часам (≈0 в тесном цикле)
    // и задавил бы ВСЕ реплики по MinSpeechMs. Это ключ детерминизма (см. критику харнесса).
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTimeOffset start) => _now = start;
        public void Set(DateTimeOffset t) => _now = t;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed record RefLine(double StartSec, string Text);

    private sealed record ModeSpec(string Name, double SilenceSec, int VadMode, bool Adaptive = false);

    // Конфиг адаптива для прогона — можно свипать флагами --pctl/--mult/--margin/--maxgap.
    private static AdaptiveEndpointConfig AdaptiveCfg = new();

    private sealed record Metrics(
        string Mode, int Chunks, int Forced, int Micro, int Suppressed,
        double MedianDurSec, double P90DurSec, double MedianLatSec,
        double ForcedPct, double MicroPct, double Unused, double CoveragePct);

    public static int Run(string[] rest)
    {
        string target = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.WriteLine("Использование: --endpoint-bench <mp4|папка> [--modes fixed:0.6,fixed:1.8,fixed:4.0] [--vad 0]");
            return 2;
        }

        int vad = ArgInt(rest, "--vad", 0);
        AdaptiveCfg = new AdaptiveEndpointConfig
        {
            Percentile = ArgDouble(rest, "--pctl", new AdaptiveEndpointConfig().Percentile),
            Multiplier = ArgDouble(rest, "--mult", new AdaptiveEndpointConfig().Multiplier),
            MarginMs = ArgInt(rest, "--margin", new AdaptiveEndpointConfig().MarginMs),
            MaxGapMs = ArgInt(rest, "--maxgap", new AdaptiveEndpointConfig().MaxGapMs),
        };
        var modes = ParseModes(rest, vad);

        // Параметры чанкинга — как в боевом конфиге (см. %APPDATA%\...\config.json).
        int chunkMaxSec = ArgInt(rest, "--chunkmax", 15);
        double rms = ArgDouble(rest, "--rms", 0.01);
        int preRoll = ArgInt(rest, "--preroll", 400);
        int postRoll = ArgInt(rest, "--postroll", 500);
        int overlap = ArgInt(rest, "--overlap", 1000);
        int minSpeech = ArgInt(rest, "--minspeech", 1000);

        var files = new List<string>();
        if (Directory.Exists(target))
            files.AddRange(Directory.GetFiles(target, "*.mp4").Concat(Directory.GetFiles(target, "*.webm")).Concat(Directory.GetFiles(target, "*.mkv")));
        else if (File.Exists(target))
            files.Add(target);
        if (files.Count == 0) { Console.WriteLine($"Нет медиафайлов в {target}"); return 2; }

        var evalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrainstormBuddy", "eval");
        Directory.CreateDirectory(evalDir);

        var md = new StringBuilder();
        md.AppendLine("# Бенчмарк эндпойнтинга");
        md.AppendLine($"chunkMax={chunkMaxSec}s rms={rms} preRoll={preRoll} postRoll={postRoll} overlap={overlap} minSpeech={minSpeech}");
        md.AppendLine();
        var csv = new StringBuilder();
        csv.AppendLine("file;mode;chunks;forced;forced_pct;micro;micro_pct;suppressed;median_dur_s;p90_dur_s;median_lat_s;coverage_pct");

        var extractor = new MediaAudioExtractor();
        int sr = MediaAudioExtractor.TargetSampleRate;

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            Console.WriteLine($"\n=== {name} ===");
            float[] samples;
            try
            {
                var audio = extractor.Extract(file);
                samples = audio.Samples;
                int maxSec = ArgInt(rest, "--maxsec", 0);
                if (maxSec > 0 && samples.Length > maxSec * sr)
                {
                    var cut = new float[maxSec * sr];
                    Array.Copy(samples, cut, cut.Length);
                    samples = cut;
                }
                Console.WriteLine($"  аудио {TimeSpan.FromSeconds(samples.Length / (double)sr):hh\\:mm\\:ss} ({samples.Length} сэмплов) via {audio.Method}");
            }
            catch (Exception ex) { Console.WriteLine($"  извлечение аудио не удалось: {ex.Message}"); continue; }

            var refLines = LoadReference(file);
            Console.WriteLine($"  эталонных реплик: {refLines.Count}");
            var refBounds = refLines.Select(r => r.StartSec).OrderBy(x => x).ToList();

            md.AppendLine($"## {name}  ({samples.Length / (double)sr / 60:F1} мин, эталонных реплик: {refLines.Count})");
            md.AppendLine();
            md.AppendLine("| режим | чанков | forced | micro(<1с) | supp | медиана | p90 | лат | покрытие пауз |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|");

            foreach (var mode in modes)
            {
                var (chunks, suppressed, finalT) = Simulate(samples, sr, mode.SilenceSec, chunkMaxSec, rms,
                    mode.VadMode, preRoll, postRoll, overlap, minSpeech, mode.Adaptive);
                string label = mode.Adaptive ? $"{mode.Name}→{finalT:F1}с" : mode.Name;

                if (rest.Contains("--dump"))
                {
                    Console.WriteLine($"  [dump {mode.Name}] первые эталонные границы: {string.Join(", ", refBounds.Take(8).Select(x => x.ToString("F0")))}");
                    Console.WriteLine($"  [dump {mode.Name}] первые чанки onset→end: {string.Join(", ", chunks.Take(8).Select(c => $"{c.OnsetSec:F0}→{c.SpeechEndSec:F0}"))}");
                }
                var m = ComputeMetrics(label, chunks, suppressed, refBounds);
                md.AppendLine($"| {label} | {m.Chunks} | {m.Forced} ({m.ForcedPct:F0}%) | {m.Micro} ({m.MicroPct:F0}%) | {m.Suppressed} | {m.MedianDurSec:F1}с | {m.P90DurSec:F1}с | {m.MedianLatSec:F2}с | {m.CoveragePct:F0}% |");
                csv.AppendLine(string.Join(';', new[] {
                    name, m.Mode, m.Chunks.ToString(), m.Forced.ToString(), F(m.ForcedPct), m.Micro.ToString(),
                    F(m.MicroPct), m.Suppressed.ToString(), F(m.MedianDurSec), F(m.P90DurSec), F(m.MedianLatSec),
                    F(m.CoveragePct) }));
                Console.WriteLine($"  {label,-16} чанков={m.Chunks,4} forced={m.ForcedPct,3:F0}% micro={m.MicroPct,3:F0}% медиана={m.MedianDurSec,4:F1}с лат={m.MedianLatSec:F2}с покрытие={m.CoveragePct,3:F0}%");
            }

            // Семантическая склейка (подход 3) на уровне текста: прогоняем эталонные фрагменты
            // через TurnAggregator и смотрим, сколько оборванных мыслей склеивается.
            if (refLines.Count > 0)
            {
                var agg = new BrainstormBuddy.Audio.TurnAggregator();
                int emitted = 0, held = 0;
                foreach (var r in refLines)
                {
                    if (agg.Push(r.Text) != null) emitted++; else held++;
                }
                if (agg.HasPending) emitted++;
                double reduction = 100.0 * (refLines.Count - emitted) / refLines.Count;
                Console.WriteLine($"  [склейка текста] {refLines.Count} фрагментов → {emitted} реплик (−{reduction:F0}%, склеено {held})");
                md.AppendLine($"- Текстовая склейка (подход 3): {refLines.Count} фрагментов → {emitted} реплик (−{reduction:F0}%).");
            }
            md.AppendLine();
        }

        var mdPath = Path.Combine(evalDir, "endpoint_bench.md");
        var csvPath = Path.Combine(evalDir, "endpoint_bench.csv");
        File.WriteAllText(mdPath, md.ToString(), new UTF8Encoding(true));
        File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"\nОтчёт: {mdPath}");
        Console.WriteLine($"CSV:   {csvPath}");
        return 0;
    }

    private static (List<ChunkInfo> chunks, int suppressed, double finalT) Simulate(
        float[] samples, int sr, double silenceSec, int chunkMaxSec, double rms,
        int vadMode, int preRoll, int postRoll, int overlap, int minSpeech, bool adaptive)
    {
        var epoch = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(epoch);
        var buf = new AudioBuffer(sr, chunkMaxSec, silenceSec, rms, vadMode, preRoll, postRoll,
            overlap, minSpeech, logger: null, timeProvider: clock);
        if (adaptive)
        {
            var ctrl = new PauseAdaptiveController(AdaptiveCfg, silenceSec, clock);
            buf.EnableAdaptiveEndpointing(ctrl);
        }
        var chunks = new List<ChunkInfo>();
        int block = sr / 10; // 100 мс
        long fed = 0;
        for (int i = 0; i < samples.Length; i += block)
        {
            int n = Math.Min(block, samples.Length - i);
            // Продвигаем часы к аудио-позиции ДО подачи блока (эмит/dwell внутри увидят это время).
            clock.Set(epoch.AddSeconds(fed / (double)sr));
            var seg = new float[n];
            Array.Copy(samples, i, seg, 0, n);
            buf.AddSamples(seg);
            fed += n;
            while (buf.TryGetReadyChunk(out _, out var info)) chunks.Add(info);
        }
        clock.Set(epoch.AddSeconds(fed / (double)sr));
        if (buf.Flush(out _, out var tail)) chunks.Add(tail);
        return (chunks, buf.ChunksSuppressed, buf.CurrentSilenceSeconds);
    }

    private static Metrics ComputeMetrics(string mode, List<ChunkInfo> chunks, int suppressed, List<double> refBounds)
    {
        int n = chunks.Count;
        if (n == 0)
            return new Metrics(mode, 0, 0, 0, suppressed, 0, 0, 0, 0, 0, 0, 0);

        int forced = chunks.Count(c => c.Reason == "forced");
        var durs = chunks.Select(c => c.SpeechDurationSec).OrderBy(x => x).ToList();
        int micro = durs.Count(d => d < 1.0);
        var lats = chunks.Select(c => c.EndpointLatencySec).OrderBy(x => x).ToList();

        // Покрытие: доля эталонных границ (реальных пауз), рядом с которыми (в пределах tol)
        // реально встал рез чанка. Считаем только по границам ВНУТРИ покрытого чанками диапазона
        // (эталон не транскрибирует интро/тишину — вне диапазона границы штрафовать нечестно).
        const double tol = 0.6;
        double coverage = 0;
        if (refBounds.Count > 0 && n > 0)
        {
            var ends = chunks.Select(c => c.SpeechEndSec).OrderBy(x => x).ToList();
            double lo = ends.First(), hi = ends.Last();
            var inRange = refBounds.Where(b => b >= lo - tol && b <= hi + tol).ToList();
            if (inRange.Count > 0)
                coverage = 100.0 * inRange.Count(b => NearestDist(ends, b) <= tol) / inRange.Count;
        }

        return new Metrics(mode, n, forced, micro, suppressed,
            Median(durs), Percentile(durs, 0.90), Median(lats),
            100.0 * forced / n, 100.0 * micro / n, 0, coverage);
    }

    private static double NearestDist(List<double> sorted, double x)
    {
        // sorted по возрастанию; линейный поиск ближайшего (списки маленькие/средние — ок).
        double best = double.MaxValue;
        foreach (var v in sorted) { double d = Math.Abs(v - x); if (d < best) best = d; else if (v > x && d > best) break; }
        return best;
    }

    private static double Median(List<double> s) => s.Count == 0 ? 0 : Percentile(s, 0.5);
    private static double Percentile(List<double> s, double q)
    {
        if (s.Count == 0) return 0;
        int idx = Math.Clamp((int)Math.Ceiling(q * s.Count) - 1, 0, s.Count - 1);
        return s[idx];
    }

    private static List<RefLine> LoadReference(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(mediaPath);
        var candidates = new[]
        {
            Path.Combine(dir, baseName + "_timestamped.txt"),
            Path.Combine(dir, baseName + ".txt"),
        };
        var refFile = candidates.FirstOrDefault(File.Exists);
        if (refFile == null) return new();

        var lines = new List<RefLine>();
        // Форматы: "mm:ss  текст" (реальный) | "[mm:ss] текст" | "[h:mm:ss] текст" | "hh:mm:ss текст"
        var rx = new Regex(@"^\[?(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\]?\s+(.*)$");
        foreach (var raw in File.ReadAllLines(refFile, DetectEncoding(refFile)))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = rx.Match(line);
            if (!m.Success) continue;
            int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
            int mm = int.Parse(m.Groups[2].Value);
            int ss = int.Parse(m.Groups[3].Value);
            double t = h * 3600 + mm * 60 + ss;
            lines.Add(new RefLine(t, m.Groups[4].Value.Trim()));
        }
        return lines;
    }

    private static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        return new UTF8Encoding(false);
    }

    private static List<ModeSpec> ParseModes(string[] rest, int defaultVad)
    {
        var spec = rest.SkipWhile(a => a != "--modes").Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(spec) || spec.StartsWith("--"))
            return new()
            {
                new("fixed:4.0", 4.0, defaultVad),
                new("fixed:1.8", 1.8, defaultVad),
                new("fixed:0.8", 0.8, defaultVad),
                new("fixed:0.6", 0.6, defaultVad),
            };
        var list = new List<ModeSpec>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.StartsWith("fixed:") && double.TryParse(p.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                list.Add(new(p, s, defaultVad));
            else if (p == "adaptive")
                list.Add(new("adaptive", 1.2, defaultVad, Adaptive: true)); // cold-start 1.2с
            // semantic подключается на следующем этапе
        }
        return list.Count > 0 ? list : new() { new("fixed:1.8", 1.8, defaultVad) };
    }

    private static string F(double d) => d.ToString("F2", CultureInfo.InvariantCulture);
    private static int ArgInt(string[] a, string key, int def)
    {
        var v = a.SkipWhile(x => x != key).Skip(1).FirstOrDefault();
        return int.TryParse(v, out var r) ? r : def;
    }
    private static double ArgDouble(string[] a, string key, double def)
    {
        var v = a.SkipWhile(x => x != key).Skip(1).FirstOrDefault();
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;
    }
}
