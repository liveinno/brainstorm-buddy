using System.Net.Http;
using System.Security.Cryptography;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Embeddings;

/// <summary>
/// Скачивание multilingual-e5-small по запросу пользователя (спека memory2 §21,
/// сценарий Lite §22): стриминг в *.partial, проверка размера/SHA256, атомарный
/// rename → готовый каталог. Битый файл не считается установленной моделью:
/// несовпадение хэша → partial удаляется, IsAvailable остаётся false.
/// FTS работает всё это время — E5 строго опционален.
/// </summary>
public sealed class E5ModelDownloader
{
    private const string LogCat = "Memory";
    private const string RepoBase =
        "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/";

    /// <summary>Продакшн-набор файлов: модель FP32 + SentencePiece.
    /// tokenizer.json не нужен (C# читает sp-модель напрямую); config/
    /// tokenizer_config — метаданные, не обязательны.</summary>
    public sealed record ModelFile(
        string RemoteName,   // путь внутри репо
        string LocalName,    // имя файла в каталоге модели
        long SizeBytes,
        string Sha256);

    /// <summary>FP32-набор — универсальный reference (спека §4/§5).</summary>
    public static readonly ModelFile[] RequiredFiles =
    {
        new("onnx/model.onnx", "model.onnx",
            470268510L, "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665"),
        new("sentencepiece.bpe.model", "sentencepiece.bpe.model",
            5069051L, "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
        // Метаданные — маленькие, качаем для отладки/совместимости.
        new("config.json", "config.json", 0, ""),
        new("tokenizer_config.json", "tokenizer_config.json", 0, ""),
    };

    /// <summary>Официальный AVX512-VNNI INT8 (113 МБ): качество на E2E-наборе
    /// не ниже FP32 (§5 gate: R@3=1.00, R@1 −0.02), RAM −330 МБ. Но это
    /// ускорение под AVX512 — качаем его только на VNNI-совместимом железе,
    /// на остальных ПК остаётся FP32 (спека §3: не универсальный CPU artifact).</summary>
    private static readonly ModelFile[] VnniFiles =
    {
        new("onnx/model_qint8_avx512_vnni.onnx", "model.onnx",
            118346824L, "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88"),
        new("sentencepiece.bpe.model", "sentencepiece.bpe.model",
            5069051L, "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
        new("config.json", "config.json", 0, ""),
        new("tokenizer_config.json", "tokenizer_config.json", 0, ""),
    };

    /// <summary>Железо машины поддерживает AVX512 (консервативный прокси для
    /// VNNI; ORT сам выберет совместимые QLinear-ядра в любом случае).</summary>
    internal static bool PrefersQuantized
        => OperatingSystem.IsWindows() && System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated;

    /// <summary>Файлы, которые DownloadAsync ставит на эту машину.</summary>
    public static ModelFile[] PreferredFiles => PrefersQuantized ? VnniFiles : RequiredFiles;

    /// <summary>Суммарный размер загрузки (для UI-подписи «Размер загрузки»).</summary>
    public static long TotalDownloadBytes => PreferredFiles.Sum(f => f.SizeBytes);

    private readonly HttpClient _http;
    private readonly LoggingService? _log;

    public E5ModelDownloader(HttpClient? http = null, LoggingService? log = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(30);   // 470 МБ на слабом канале
        _log = log;
    }

    /// <summary>Известные хэши model.onnx → имя варианта (для диагностики в логе).</summary>
    private static readonly Dictionary<string, string> KnownModelHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        [RequiredFiles[0].Sha256] = "fp32",
        [VnniFiles[0].Sha256] = "qint8_avx512_vnni",
    };

    /// <summary>
    /// model.onnx + sentencepiece.bpe.model на месте? Обязательные файлы должны
    /// существовать и быть непустыми. Хэш сверяем только для диагностики: файл с
    /// неизвестным хэшем (юзер положил сам / бандл иной ревизии) — Debug и принимаем,
    /// иначе Full-бандл FP32 на VNNI-машине вечно «не установлен». Битая модель
    /// всплывёт на load в LocalE5EmbeddingProvider — не считаем это установкой.
    /// </summary>
    public bool IsInstalled(string modelDir) => StaticIsInstalled(modelDir, _log);

    /// <summary>То же, что <see cref="IsInstalled"/>, но без инстанса (EmbeddingFactory, настройки).</summary>
    public static bool StaticIsInstalled(string modelDir, LoggingService? log = null)
    {
        var p = Path.Combine(modelDir, "model.onnx");
        var s = Path.Combine(modelDir, "sentencepiece.bpe.model");
        long modelLen, spLen;
        try
        {
            modelLen = new FileInfo(p).Length;
            spLen = new FileInfo(s).Length;
        }
        catch { return false; }
        if (modelLen <= 0 || spLen <= 0) return false;
        // Хэш считаем только когда размер похож на известный артефакт —
        // не гонять 470 МБ через SHA256 ради диагностики.
        if (modelLen == RequiredFiles[0].SizeBytes || modelLen == VnniFiles[0].SizeBytes)
        {
            try
            {
                using var fs = File.OpenRead(p);
                var hex = Convert.ToHexString(SHA256.HashData(fs));
                log?.Debug(KnownModelHashes.TryGetValue(hex, out var variant)
                    ? $"E5: model.onnx = known artifact '{variant}'"
                    : $"E5: model.onnx sha256={hex[..16]}… not in manifests — accepting local file", LogCat);
            }
            catch (Exception ex)
            {
                log?.Debug($"E5: hash check skipped: {ex.Message}", LogCat);
            }
        }
        return true;
    }

    /// <summary>
    /// Скачать недостающие файлы в modelDir. progress: 0..1 по байтам.
    /// Кидает Exception на сетевых/хэш-ошибках — UI покажет, retry штатен.
    /// </summary>
    public async Task DownloadAsync(string modelDir, IProgress<double>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(modelDir);
        var total = Math.Max(1, TotalDownloadBytes);
        long done = 0;

        foreach (var f in PreferredFiles)
        {
            var dest = Path.Combine(modelDir, f.LocalName);
            ct.ThrowIfCancellationRequested();

            // Уже есть и проверен — пропускаем (повторный запуск после обрыва).
            if (File.Exists(dest) && f.Sha256.Length > 0 && await VerifyAsync(dest, f, ct))
            {
                done += f.SizeBytes;
                progress?.Report((double)done / total);
                continue;
            }

            var partial = dest + ".partial";
            _log?.Info($"E5 download: {f.RemoteName} → {dest}", LogCat);
            await DownloadOneAsync(f, partial, ct);
            await VerifyOrThrowAsync(partial, f, ct);
            File.Move(partial, dest, overwrite: true);   // атомарный rename
            _log?.Info($"E5 download: {f.LocalName} ok", LogCat);
            done += f.SizeBytes;
            progress?.Report((double)done / total);
        }
    }

    private async Task DownloadOneAsync(ModelFile f, string partial, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(RepoBase + f.RemoteName,
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Перезаписываем: старый partial мог быть от обрыва другой ревизии.
        await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private async Task VerifyOrThrowAsync(string partial, ModelFile f, CancellationToken ct)
    {
        if (!await VerifyAsync(partial, f, ct))
        {
            try { File.Delete(partial); } catch { /* битый partial не держим */ }
            throw new InvalidDataException(
                $"E5 download corrupted: {f.LocalName} size/hash mismatch");
        }
    }

    private static async Task<bool> VerifyAsync(string path, ModelFile f, CancellationToken ct)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return false;
        if (f.SizeBytes > 0 && fi.Length != f.SizeBytes) return false;
        if (f.Sha256.Length == 0) return true;             // метаданные без эталона
        await using var s = fi.OpenRead();
        var hash = await SHA256.HashDataAsync(s, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
