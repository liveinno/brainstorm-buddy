using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BrainstormBuddy.Media;

/// <summary>
/// Извлекает моно-аудио 16 кГц (float [-1..1]) из локальных медиафайлов для подачи
/// во встроенный STT. Приоритет — Windows Media Foundation (mp4/m4a/mp3/wav/wma, без
/// внешних зависимостей). Для webm/opus/mkv, где у Media Foundation обычно нет кодеков,
/// используется ffmpeg, ЕСЛИ он есть в PATH. Иначе — понятная ошибка.
/// </summary>
public sealed class MediaAudioExtractor
{
    public const int TargetSampleRate = 16000;

    public sealed record Result(float[] Samples, TimeSpan Duration, string Method);

    /// <summary>Форматы, которые заведомо просим у ffmpeg (Media Foundation их обычно не тянет).</summary>
    private static readonly HashSet<string> FfmpegFirst = new(StringComparer.OrdinalIgnoreCase)
    { ".webm", ".ogg", ".opus", ".mkv", ".flac", ".oga" };

    public Result Extract(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден", path);
        var ext = Path.GetExtension(path);
        bool ffmpegAvailable = TryFindFfmpeg(out var ffmpeg);

        if (FfmpegFirst.Contains(ext))
        {
            if (ffmpegAvailable) return ExtractViaFfmpeg(ffmpeg, path, ct);
            // Попытка через Media Foundation как запасной вариант (вдруг стоят Web Media Extensions).
            try { return ExtractViaMediaFoundation(path, ct); }
            catch
            {
                throw new NotSupportedException(
                    $"Формат {ext} не удалось декодировать системными кодеками. " +
                    "Установите «Web Media Extensions» (Microsoft Store) или ffmpeg, " +
                    "либо конвертируйте файл в mp4.");
            }
        }

        // mp4/m4a/mp3/wav/wma/aac → Media Foundation. При неудаче — ffmpeg, если есть.
        try { return ExtractViaMediaFoundation(path, ct); }
        catch when (ffmpegAvailable) { return ExtractViaFfmpeg(ffmpeg, path, ct); }
    }

    private static Result ExtractViaMediaFoundation(string path, CancellationToken ct)
    {
        using var reader = new MediaFoundationReader(path);
        var duration = reader.TotalTime;
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
            sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sp.WaveFormat.SampleRate != TargetSampleRate)
            sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);
        var samples = ReadAll(sp, ct);
        if (duration <= TimeSpan.Zero)
            duration = TimeSpan.FromSeconds((double)samples.Length / TargetSampleRate);
        return new Result(samples, duration, "Media Foundation");
    }

    private static Result ExtractViaFfmpeg(string ffmpeg, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -vn: без видео; f32le: сырые 32-бит float сэмплы прямо в float[]
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", path, "-vn",
                                  "-ac", "1", "-ar", TargetSampleRate.ToString(), "-f", "f32le", "-" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg");
        // stderr читаем асинхронно, чтобы не поймать взаимоблокировку при заполнении буфера
        var errTask = proc.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        var outStream = proc.StandardOutput.BaseStream;
        var buf = new byte[1 << 16];
        int read;
        while ((read = outStream.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            ms.Write(buf, 0, read);
        }
        proc.WaitForExit();
        var err = errTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0 && ms.Length == 0)
            throw new InvalidOperationException($"ffmpeg не смог декодировать файл: {err}".Trim());

        var bytes = ms.ToArray();
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        var duration = TimeSpan.FromSeconds((double)samples.Length / TargetSampleRate);
        return new Result(samples, duration, "ffmpeg");
    }

    private static float[] ReadAll(ISampleProvider sp, CancellationToken ct)
    {
        var outp = new List<float>(1 << 20);
        var buf = new float[TargetSampleRate]; // ~1с за чтение
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++) outp.Add(buf[i]);
        }
        return outp.ToArray();
    }

    /// <summary>Каталог для докачанного в приложении ffmpeg (%APPDATA%\BrainstormBuddy\ffmpeg).</summary>
    public static string DownloadedFfmpegDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrainstormBuddy", "ffmpeg");

    /// <summary>
    /// Ищет ffmpeg.exe по приоритету: рядом с exe (бандл из инсталлятора) →
    /// %APPDATA%\BrainstormBuddy\ffmpeg (докачан в приложении) → PATH.
    /// </summary>
    public static bool TryFindFfmpeg(out string path)
    {
        // 1) бандл из инсталлятора ({app}\ffmpeg\ffmpeg.exe)
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) { path = bundled; return true; }

        // 2) докачанный в приложении
        var downloaded = Path.Combine(DownloadedFfmpegDir, "ffmpeg.exe");
        if (File.Exists(downloaded)) { path = downloaded; return true; }

        // 3) системный PATH
        var envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in envPath.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) { path = candidate; return true; }
            }
            catch { /* некорректный элемент PATH — пропускаем */ }
        }
        path = "";
        return false;
    }
}
