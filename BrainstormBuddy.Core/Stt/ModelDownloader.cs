using System.Security.Cryptography;

namespace BrainstormBuddy.Stt;

/// <summary>
/// Докачка ONNX-модели GigaAM: из GitLab Release/Package (URL из конфига) в %APPDATA%\models.
/// Вариант C (запасной): модель, положенная рядом с exe инсталлятором — качать не нужно.
/// Приватный репозиторий → скачивание требует токен (заголовок из конфига, НЕ хардкод в коде).
/// </summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;

    public ModelDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <param name="progress">0..1 доля скачанного (может быть -1, если размер неизвестен).</param>
    public async Task DownloadAsync(string url, string destPath, string? authHeaderName, string? authHeaderValue,
                                    long expectedBytes, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var tmp = destPath + ".part";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
            req.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? expectedBytes;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 20]; // 1 МБ
            long done = 0; int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
                else progress?.Report(-1);
            }
        }

        if (expectedBytes > 0)
        {
            var got = new FileInfo(tmp).Length;
            if (got != expectedBytes)
            {
                File.Delete(tmp);
                throw new InvalidDataException($"Размер модели не совпал: ожидалось {expectedBytes}, скачано {got}");
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);
    }

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
