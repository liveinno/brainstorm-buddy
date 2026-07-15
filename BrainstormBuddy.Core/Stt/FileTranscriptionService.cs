namespace BrainstormBuddy.Stt;

/// <summary>Один отрезок транскрипта с тайм-кодами.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Результат транскрибации файла: сегменты с тайм-кодами + метаданные.</summary>
public sealed class FileTranscriptResult
{
    public List<TranscriptSegment> Segments { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public string ExtractMethod { get; init; } = "";

    /// <summary>Только текст, без меток — для саммари/копирования.</summary>
    public string PlainText => string.Join(" ", Segments.Select(s => s.Text)).Trim();

    /// <summary>Текст с тайм-кодами: «[mm:ss] ...» построчно.</summary>
    public string TimestampedText =>
        string.Join(Environment.NewLine, Segments.Select(s => $"[{Fmt(s.Start)}] {s.Text}"));

    public static string Fmt(TimeSpan t) =>
        (int)t.TotalHours > 0
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
}

/// <summary>
/// Транскрибирует длинную аудиодорожку встроенным GigaAM с ПОСЕГМЕНТНЫМИ тайм-кодами.
/// Режет 16кГц-моно поток на окна фикс. длины (по умолчанию 18с — меньше внутреннего
/// чанка движка 24с, чтобы окно шло одним проходом) и метит каждое временем начала.
/// Границу окна подвигает к тихому месту, чтобы реже резать слова.
/// </summary>
public sealed class FileTranscriptionService
{
    private readonly NativeGigaamSttService _stt;
    private readonly int _segmentSec;

    public FileTranscriptionService(NativeGigaamSttService stt, int segmentSec = 18)
    {
        _stt = stt;
        _segmentSec = Math.Clamp(segmentSec, 5, 23);
    }

    /// <param name="samples">16кГц mono float [-1..1].</param>
    /// <param name="progress">(доля 0..1, статус) — вызывается из фонового потока.</param>
    public FileTranscriptResult Transcribe(float[] samples, TimeSpan duration, string method,
        Action<double, string>? progress, CancellationToken ct)
    {
        int sr = MelFrontend.SampleRate;
        int seg = _segmentSec * sr;
        int total = samples.Length;
        var result = new FileTranscriptResult { Duration = duration, ExtractMethod = method };
        if (total == 0) return result;

        int start = 0;
        while (start < total)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + seg, total);
            if (end < total) end = SnapToQuiet(samples, end, (int)(1.2 * sr)); // подвинуть к тишине
            int len = end - start;
            var slice = new float[len];
            Array.Copy(samples, start, slice, 0, len);

            string text = (_stt.TranscribeSamples(slice, ct) ?? "").Trim();
            if (text.Length > 0)
                result.Segments.Add(new TranscriptSegment(
                    TimeSpan.FromSeconds((double)start / sr),
                    TimeSpan.FromSeconds((double)end / sr),
                    text));

            double frac = (double)end / total;
            progress?.Invoke(Math.Min(1.0, frac),
                $"{FileTranscriptResult.Fmt(TimeSpan.FromSeconds((double)end / sr))} / {FileTranscriptResult.Fmt(duration)}");
            start = end;
        }
        return result;
    }

    /// <summary>Находит локальный минимум энергии рядом с целевым индексом → режем в тишине.</summary>
    private static int SnapToQuiet(float[] s, int target, int window)
    {
        int lo = Math.Max(0, target - window);
        int hi = Math.Min(s.Length, target + window);
        const int frame = 320; // 20 мс
        double best = double.MaxValue;
        int bestIdx = target;
        for (int i = lo; i + frame < hi; i += frame)
        {
            double e = 0;
            for (int j = i; j < i + frame; j++) e += s[j] * s[j];
            if (e < best) { best = e; bestIdx = i + frame / 2; }
        }
        return bestIdx;
    }
}
