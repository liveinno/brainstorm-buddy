using System.IO.Compression;

namespace BrainstormBuddy.Media;

/// <summary>
/// Автоматическая установка ffmpeg (нужен для webm/mkv/opus). Скачивает официальную
/// Windows-сборку (gyan.dev «essentials», рекомендована на ffmpeg.org) и распаковывает
/// ffmpeg.exe/ffprobe.exe в %APPDATA%\BrainstormBuddy\ffmpeg. Только по кнопке пользователя.
/// </summary>
public sealed class FfmpegInstaller
{
    // Официальная сборка под Windows (ffmpeg.org → gyan.dev). ~80 МБ zip.
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private readonly HttpClient _http;

    public FfmpegInstaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    /// <summary>
    /// Скачивает и распаковывает ffmpeg.exe в <paramref name="destDir"/>. Возвращает путь к ffmpeg.exe.
    /// </summary>
    /// <param name="progress">Доля скачивания 0..1.</param>
    public async Task<string> InstallAsync(string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(destDir);
        var zipPath = Path.Combine(destDir, "_ffmpeg_dl.zip");

        // --- скачивание (стримингом, с прогрессом) ---
        using (var resp = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                if (total is > 0) progress?.Report(Math.Min(1.0, (double)got / total.Value));
            }
        }

        // --- распаковка ffmpeg.exe (+ ffprobe.exe, best-effort) ---
        string outExe = Path.Combine(destDir, "ffmpeg.exe");
        using (var za = ZipFile.OpenRead(zipPath))
        {
            var entry = za.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("ffmpeg.exe не найден в скачанном архиве.");
            entry.ExtractToFile(outExe, overwrite: true);

            var probe = za.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase));
            if (probe != null)
            {
                try { probe.ExtractToFile(Path.Combine(destDir, "ffprobe.exe"), overwrite: true); }
                catch { /* ffprobe не критичен */ }
            }
        }

        try { File.Delete(zipPath); } catch { /* временный zip */ }
        return outExe;
    }
}
