using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainstormBuddy.Core.Backup;

/// <summary>
/// Переносимый зашифрованный паролем архив профиля (создание / проверка /
/// атомарное восстановление). Шифрование — AES-256-GCM поверх PBKDF2
/// (см. <see cref="PortableArchiveCrypto"/>), привязки к DPAPI/учётной
/// записи Windows нет: перенос работает между профилями и машинами.
///
/// Незашифрованная часть файла — только сигнатура и параметры KDF.
/// Манифест (пути, размеры, SHA-256) лежит внутри шифрованного ZIP,
/// поэтому имена файлов и содержимое не утекают без пароля.
///
/// Восстановление всегда идёт в два этапа: полная расшифровка и проверка
/// во временный staging-каталог → только после успеха swap в destination
/// (старый профиль уезжает в резервную копию). Destination при любой
/// ошибке проверки остаётся нетронутым.
///
/// Модели (каталог models профиля: Whisper ggml, E5 ONNX, Nemotron) входят
/// в полный перенос по умолчанию и восстанавливаются вместе с профилем.
/// Пользователь может исключить их осознанно (UI: «Исключить загруженные
/// модели») — тогда на новом устройстве модели качаются заново.
/// </summary>
public static class PortableArchive
{
    /// <summary>Версия формата внутри зашифрованного манифеста.</summary>
    public const string ManifestFormatVersion = "bbuddy-profile/1";

    /// <summary>Имя зашифрованного манифеста внутри ZIP.</summary>
    private const string ManifestEntryName = "manifest.json";
    private const string DataEntryPrefix = "data/";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ManifestReadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ==================== Инвентаризация ====================

    /// <summary>
    /// Сканирует профиль и возвращает состав архива для показа пользователю
    /// (пути, размеры, хэши, предупреждения). Обёртка над
    /// <see cref="PortableArchiveScanner.BuildAsync"/>.
    /// </summary>
    public static Task<ArchiveInventory> BuildInventoryAsync(
        ArchiveInventoryOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
        => PortableArchiveScanner.BuildAsync(options, progress, ct);

    // ==================== Создание ====================

    /// <summary>
    /// Создаёт архив: инвентаризация → ZIP с data/N и manifest.json →
    /// поточное шифрование → атомарная замена <paramref name="archivePath"/>.
    /// Незавершённый файл не считается архивом: результат пишется через
    /// временный файл в том же каталоге.
    /// Каталог models по умолчанию не архивируется; включить его можно
    /// явно (<see cref="ArchiveInventoryOptions.IncludeModels"/>).
    /// </summary>
    /// <param name="options">Профиль и состав. ArchivePath здесь задаётся
    /// параметром <paramref name="archivePath"/> (опция тоже учитывается,
    /// чтобы архив не попал в сам себя).</param>
    /// <param name="iterations">Итерации PBKDF2; 0 → безопасный дефолт.</param>
    public static async Task<ArchiveCreateResult> CreateAsync(
        ArchiveInventoryOptions options,
        string archivePath,
        string password,
        int iterations = 0,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Пароль архива не может быть пустым.", nameof(password));
        if (iterations <= 0) iterations = PortableArchiveCrypto.DefaultIterations;
        if (iterations < PortableArchiveCrypto.MinIterations ||
            iterations > PortableArchiveCrypto.MaxIterations)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        var profile = Path.GetFullPath(options.ProfileDirectory);
        var archiveFull = Path.GetFullPath(archivePath);
        var opts = new ArchiveInventoryOptions
        {
            ProfileDirectory = profile,
            ArchivePath = archiveFull,
            ExternalPaths = options.ExternalPaths,
            IncludeModels = options.IncludeModels,
            IncludeLogs = options.IncludeLogs,
        };

        var inventory = await BuildInventoryAsync(opts, progress, ct).ConfigureAwait(false);
        if (inventory.Entries.Count == 0)
            throw new PortableArchiveException(
                "Профиль пуст — архивировать нечего.", ArchiveFailureKind.MissingRequiredSource);
        // DPAPI-секреты: расшифровываем текущим пользователем и кладём
        // внутрь шифрованного payload отдельной записью — после restore
        // их перепривяжет Rebind. Секреты не попадают в манифест/логи.
        var exported = PortableArchiveSecrets.Export(profile);
        // Заметки считаем ПОСЛЕ экспорта: перенесённые токены — Remapped,
        // невошедшие гранты — конкретные RequiresLogin (см. DetectComponentNotes).
        var componentNotes = PortableArchiveScanner.DetectComponentNotes(
            profile,
            exported.Select(r => r.ConnectionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        var dir = Path.GetDirectoryName(archiveFull);
        if (dir != null) Directory.CreateDirectory(dir);
        var tmpZip = Path.Combine(dir ?? Path.GetTempPath(), $".{Path.GetFileName(archiveFull)}.ziptmp");
        var tmpOut = Path.Combine(dir ?? Path.GetTempPath(), $".{Path.GetFileName(archiveFull)}.tmp");
        var snapshots = new List<string>();
        try
        {
            // --- ZIP-пayload: data/{index} + manifest.json (всё уходит под шифр). ---
            await using (var zipFs = new FileStream(
                tmpZip, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var zip = new ZipArchive(zipFs, ZipArchiveMode.Create, leaveOpen: true);
                var manifest = new ArchiveManifest
                {
                    FormatVersion = ManifestFormatVersion,
                    CreatedUtc = DateTime.UtcNow,
                    ProfileDirectory = profile,
                    ComponentNotes = componentNotes,
                };
                // Корни внешних источников — для точного маппинга при restore:
                // ссылку «D:\models\e5\sub» надо вести в external/<ap>/sub,
                // а не в файл с совпадающим именем. Источником может быть и
                // одиночный файл (stdio exe MCP) — тогда пара точная.
                foreach (var ext in options.ExternalPaths ?? Array.Empty<ArchiveExternalPath>())
                {
                    try
                    {
                        var src = Path.GetFullPath(ext.SourcePath);
                        var ap = PortableArchiveScanner.NormalizeRelative(ext.ArchivePath);
                        if (ap.StartsWith("external/", StringComparison.Ordinal) &&
                            (Directory.Exists(src) || File.Exists(src)))
                            manifest.ExternalSources.Add(
                                new ArchiveManifestExternalSource
                                { ArchivePath = ap, SourcePath = src });
                    }
                    catch { /* битый путь отловит инвентаризация — тут только заметка */ }
                }
                var index = 0;
                long done = 0;
                foreach (var entry in inventory.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = entry.SourcePath;
                    if (entry.Kind == ArchiveEntryKind.SqliteSnapshot)
                    {
                        source = await PortableArchiveScanner
                            .SnapshotSqliteAsync(entry.SourcePath, ct).ConfigureAwait(false);
                        snapshots.Add(source);
                    }

                    var zipEntry = zip.CreateEntry(DataEntryPrefix + index, CompressionLevel.Optimal);
                    string sha;
                    long len;
                    FileStream srcFile;
                    try
                    {
                        srcFile = new FileStream(
                            source, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 1 << 20,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new PortableArchiveException(
                            $"Не удалось прочитать {source}: {ex.Message}",
                            ArchiveFailureKind.Io, ex);
                    }

                    await using (var dst = zipEntry.Open())
                    await using (var src = srcFile)
                    {
                        using var h = System.Security.Cryptography.SHA256.Create();
                        var buf = new byte[1 << 20];
                        len = 0;
                        int n;
                        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                        {
                            h.TransformBlock(buf, 0, n, null, 0);
                            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                            len += n;
                            done += n;
                            progress?.Report(new ArchiveProgress(
                                "create", entry.ArchivePath, done,
                                Math.Max(done, inventory.TotalBytes)));
                        }
                        h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        sha = Convert.ToHexString(h.Hash!);
                    }

                    manifest.Entries.Add(new ArchiveManifestEntry
                    {
                        Index = index,
                        ArchivePath = entry.ArchivePath,
                        OriginalPath = entry.SourcePath,
                        Length = len,
                        Sha256 = sha,
                        Kind = entry.Kind,
                        Required = entry.Required,
                    });
                    index++;
                }

                var manEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using (var ms = manEntry.Open())
                    await JsonSerializer.SerializeAsync(ms, manifest, ManifestJson, ct)
                        .ConfigureAwait(false);
                if (exported.Count > 0)
                {
                    var secEntry = zip.CreateEntry(
                        PortableArchiveSecrets.SecretsEntryName, CompressionLevel.Optimal);
                    await using var ss = secEntry.Open();
                    await JsonSerializer.SerializeAsync(ss,
                        PortableArchiveSecrets.Pack(exported), ManifestJson, ct)
                        .ConfigureAwait(false);
                }
            }

            // --- Шифрование payload → временный файл → атомарный Move. ---
            await using (var src = new FileStream(
                tmpZip, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(
                tmpOut, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var total = src.Length;
                long encDone = 0;
                await PortableArchiveCrypto.EncryptStreamAsync(
                    src, dst, password, iterations,
                    new Progress<long>(n =>
                    {
                        encDone += n;
                        progress?.Report(new ArchiveProgress(
                            "encrypt", null, Math.Min(encDone, total), total));
                    }),
                    ct).ConfigureAwait(false);
            }

            File.Move(tmpOut, archiveFull, overwrite: true);
            return new ArchiveCreateResult(archiveFull, inventory,
                BuildComponents(inventory, componentNotes));
        }
        finally
        {
            TryDelete(tmpZip);
            TryDelete(tmpOut);
            foreach (var s in snapshots) TryDelete(s);
        }
    }

    // ==================== Проверка ====================

    /// <summary>
    /// Полная проверка архива без восстановления: формат, пароль, ZIP,
    /// манифест и SHA-256 всех записей. Для UI «показать состав до замены».
    /// Записи каталога models проверяются и переносятся вместе с профилем.
    /// </summary>
    public static async Task<ArchiveValidationResult> ValidateAsync(
        string archivePath, string password,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            long decDone = 0;
            var decProgress = progress is null ? null : new Progress<long>(n =>
            {
                decDone += n;
                progress.Report(new ArchiveProgress("decrypt", null, decDone, decDone));
            });
            await using var payload = await DecryptToTempZipAsync(
                archivePath, password, decProgress, ct).ConfigureAwait(false);
            using var zip = OpenZip(payload);
            var manifest = await ReadManifestAsync(zip, ct).ConfigureAwait(false);
            var total = manifest.Entries.Sum(e => Math.Max(0, e.Length));
            long done = 0;
            var verifyProgress = progress is null ? null : new Progress<long>(n =>
            {
                done += n;
                progress.Report(new ArchiveProgress(
                    "verify", null, Math.Min(done, total), total));
            });
            var effective = new Dictionary<string, string>(StringComparer.Ordinal);
            await VerifyEntriesAsync(zip, manifest, stagingRoot: null,
                options: null, remaps: null, effective,
                verifyProgress, ct).ConfigureAwait(false);
            var components = BuildComponents(manifest, effective);
            // Конкретика «чего нет на этом устройстве»: абсолютные пути из
            // конфига вне профиля, которых не существует (чужие exe, диски).
            var cfgEntry = zip.GetEntry(DataEntryPrefix +
                (manifest.Entries.FirstOrDefault(
                    e => e.ArchivePath == "config.json")?.Index ?? -1));
            if (cfgEntry != null)
            {
                // config.json может содержать API-ключ — разбираем в памяти,
                // без открытой копии в %TEMP%. Битый json не валит проверку.
                try
                {
                    await using var s = cfgEntry.Open();
                    using var cfgDoc = await JsonDocument
                        .ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
                    foreach (var c in PortableArchiveRelocator
                        .MissingExternalComponents(cfgDoc.RootElement, manifest.ProfileDirectory, manifest))
                        components.Add(c);
                }
                catch (JsonException) { }
            }
            return new ArchiveValidationResult(
                true, ArchiveFailureKind.None, null, manifest, components);
        }
        catch (ArchiveFormatException ex)
        {
            return new ArchiveValidationResult(false, ex.Kind, ex.Message, null);
        }
        catch (InvalidDataException ex)
        {
            // Битая deflate-запись ZIP — данные архива повреждены, не ввод-вывод диска.
            return new ArchiveValidationResult(
                false, ArchiveFailureKind.Corrupt, ex.Message, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ArchiveValidationResult(
                false, ArchiveFailureKind.Io, ex.Message, null);
        }
    }

    // ==================== Восстановление ====================

    /// <summary>
    /// Восстанавливает профиль из архива в <paramref name="destinationProfile"/>.
    /// Этапы: расшифровка → проверка манифеста → распаковка во временный
    /// staging-каталог (рядом с destination, тот же том) с верификацией
    /// SHA-256 каждой записи → swap: destination → *.backup-…, staging →
    /// destination. Любой сбой до swap не трогает destination.
    /// Записи каталога models распаковываются вместе с профилем.
    /// </summary>
    public static async Task<ArchiveRestoreResult> RestoreAsync(
        string archivePath,
        string password,
        string destinationProfile,
        ArchiveRestoreOptions? options = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new ArchiveRestoreOptions();
        var dest = Path.GetFullPath(destinationProfile);
        var parent = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(parent))
            return new ArchiveRestoreResult(
                false, ArchiveFailureKind.InvalidFormat,
                "Некорректный каталог назначения: " + destinationProfile,
                dest, null, null);
        var staging = Path.Combine(parent, $".{Path.GetFileName(dest)}.restore-{Guid.NewGuid():N}");
        string? backup = null;
        ArchiveManifest? manifest = null;
        var remaps = new List<ArchivePathRemap>();

        try
        {
            long decDone = 0;
            var decProgress = progress is null ? null : new Progress<long>(n =>
            {
                decDone += n;
                progress.Report(new ArchiveProgress("decrypt", null, decDone, decDone));
            });
            await using var payload = await DecryptToTempZipAsync(
                archivePath, password, decProgress, ct).ConfigureAwait(false);
            using var zip = OpenZip(payload);
            var man = await ReadManifestAsync(zip, ct).ConfigureAwait(false);
            manifest = man;

            var totalBytes = RestorableBytes(man);

            long done = 0;
            var restoreProgress = progress is null ? null : new Progress<long>(n =>
            {
                done += n;
                progress.Report(new ArchiveProgress(
                    "extract", null, Math.Min(done, totalBytes), totalBytes));
            });

            // Полная распаковка и верификация во временный каталог.
            var effective = new Dictionary<string, string>(StringComparer.Ordinal);
            await VerifyEntriesAsync(zip, man, staging,
                options, remaps, effective, restoreProgress, ct)
                .ConfigureAwait(false);

            // Перенос ссылок: абсолютные пути старого профиля/external-источников
            // сразу получают конечный корень destination. В staging лежит ещё
            // временное имя каталога, поэтому записывать его в config нельзя:
            // после Directory.Move(staging, dest) ссылка стала бы битой.
            var warnings = new List<string>();
            PortableArchiveRelocator.Relocate(staging, dest, man, effective, warnings);
            // Секреты перепривязываются DPAPI этой учётной записи в staging.
            var secEntry = zip.GetEntry(PortableArchiveSecrets.SecretsEntryName);
            if (secEntry != null && options.RebindSecrets)
            {
                List<PortableArchiveSecrets.SecretRecord>? records;
                await using (var ss = secEntry.Open())
                    records = PortableArchiveSecrets.Unpack(ss, ct);
                if (records != null && records.Count > 0)
                    PortableArchiveSecrets.Rebind(staging, records, warnings, effective);
            }

            // Гранты, чей блоб так и не расшифровывается этой учётной записью,
            // — конкретные RequiresLogin (архив старой версии / нет секрета).
            var extra = new List<ArchiveComponentStatus>();
            foreach (var id in PortableArchiveSecrets.StaleConnectionIds(staging, effective))
                extra.Add(new ArchiveComponentStatus("mcp.tokens",
                    ArchiveComponentState.RequiresLogin,
                    $"Подключение {id}: токен не перенесён — перевыпустите на этом устройстве."));
            // config.json мог быть переназначен PathRemapper'ом — ищем его
            // фактическое расположение, а не фиксированное имя в staging.
            var cfgRel = effective.TryGetValue("config.json", out var cr) ? cr : "config.json";
            var cfgStaged = PortableArchiveScanner.ResolveUnderRoot(staging, cfgRel)
                ?? Path.Combine(staging, "config.json");
            extra.AddRange(PortableArchiveRelocator.MissingExternalComponents(
                cfgStaged, dest, man));
            var components = BuildComponents(man, effective, extra);

            ct.ThrowIfCancellationRequested(); // отмена до swap — destination нетронут
            // staging может не существовать (все записи optional и отсутствуют
            // в payload): иначе Directory.Move(staging, dest) упал бы уже
            // после отвода destination в backup — легальный «пустой профиль»
            // обязан восстанавливаться.
            Directory.CreateDirectory(staging);
            if (!options.StoresClosed)
            {
                return new ArchiveRestoreResult(
                    false, ArchiveFailureKind.LifecycleNotClosed,
                    "Перед заменой профиля закройте хранилища (memory.db, attachments.db, writers).",
                    dest, null, man, components, remaps);
            }

            // Swap: обе стороны на одном томе (staging — соседний каталог).
            Directory.CreateDirectory(parent);
            if (Directory.Exists(dest))
            {
                backup = dest + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var suffix = 0;
                while (Directory.Exists(backup)) backup += "-" + (++suffix);
                Directory.Move(dest, backup);
            }
            try
            {
                Directory.Move(staging, dest);
            }
            catch
            {
                // Откат: вернуть старый профиль на место.
                if (backup != null && Directory.Exists(backup) && !Directory.Exists(dest))
                    try { Directory.Move(backup, dest); backup = null; } catch { }
                throw;
            }

            if (!options.KeepBackup && backup != null)
            {
                try { Directory.Delete(backup, recursive: true); } catch { }
                backup = null;
            }

            return new ArchiveRestoreResult(
                true, ArchiveFailureKind.None, null, dest, backup, man,
                components, remaps, warnings);
        }
        catch (ArchiveFormatException ex)
        {
            return new ArchiveRestoreResult(
                false, ex.Kind, ex.Message, dest, null, manifest, null, remaps);
        }
        catch (InvalidDataException ex)
        {
            // Битая deflate-запись ZIP — данные архива повреждены, не ввод-вывод диска.
            return new ArchiveRestoreResult(
                false, ArchiveFailureKind.Corrupt, ex.Message, dest, null, manifest, null, remaps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ArchiveRestoreResult(
                false, ArchiveFailureKind.Io, ex.Message, dest, backup, manifest, null, remaps);
        }
        finally
        {
            // Частичное состояние не оставляет следов: staging всегда убираем.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
        }
    }

    // ==================== Внутреннее ====================

    /// <summary>Расшифровывает файл архива во временный ZIP (DeleteOnClose).</summary>
    internal static async Task<FileStream> DecryptToTempZipAsync(
        string archivePath, string password,
        IProgress<long>? bytesProgress, CancellationToken ct)
    {
        if (!File.Exists(archivePath))
            throw new ArchiveFormatException(
                ArchiveFailureKind.InvalidFormat, "Файл архива не найден.");
        if (string.IsNullOrWhiteSpace(password))
            throw new ArchiveFormatException(
                ArchiveFailureKind.WrongPassword, "Пароль не задан.");

        var tmp = Path.Combine(Path.GetTempPath(), $"bsb-arch-dec-{Guid.NewGuid():N}.zip");
        FileStream payload;
        try
        {
            payload = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1 << 20,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new ArchiveFormatException(ArchiveFailureKind.Io, ex.Message, ex);
        }

        try
        {
            await using var src = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await PortableArchiveCrypto.DecryptStreamAsync(
                src, payload, password, bytesProgress, ct).ConfigureAwait(false);
            payload.Position = 0;
            return payload;
        }
        catch
        {
            payload.Dispose();
            TryDelete(tmp);
            throw;
        }
    }

    private static ZipArchive OpenZip(FileStream payload)
    {
        try
        {
            return new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new ArchiveFormatException(
                ArchiveFailureKind.Corrupt, "Внутренний контейнер архива повреждён.", ex);
        }
    }

    private static async Task<ArchiveManifest> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry(ManifestEntryName);
        if (entry == null)
            throw new ArchiveFormatException(
                ArchiveFailureKind.Corrupt, "В архиве отсутствует манифест.");

        ArchiveManifest? manifest;
        try
        {
            await using var s = entry.Open();
            manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(
                s, ManifestReadJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ArchiveFormatException(
                ArchiveFailureKind.Corrupt, "Манифест архива повреждён.", ex);
        }

        if (manifest == null ||
            !string.Equals(manifest.FormatVersion, ManifestFormatVersion, StringComparison.Ordinal))
        {
            throw new ArchiveFormatException(
                ArchiveFailureKind.InvalidFormat, "Неподдерживаемая версия архива.");
        }
        if (manifest.Entries == null || manifest.Entries.Count == 0)
            throw new ArchiveFormatException(
                ArchiveFailureKind.Corrupt, "Манифест архива пуст.");

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenIndex = new HashSet<int>();
        foreach (var e in manifest.Entries)
        {
            if (PortableArchiveScanner.IsUnsafeRelative(e.ArchivePath))
                throw new ArchiveFormatException(
                    ArchiveFailureKind.Traversal,
                    $"Манифест содержит путь за пределами каталога: {e.ArchivePath}");
            if (!seenPaths.Add(e.ArchivePath) || e.Index < 0 || !seenIndex.Add(e.Index))
                throw new ArchiveFormatException(
                    ArchiveFailureKind.Corrupt, "Манифест содержит дублирующиеся записи.");
            if (e.Length < 0)
                throw new ArchiveFormatException(
                    ArchiveFailureKind.Corrupt, "Манифест содержит отрицательный размер.");
        }
        foreach (var es in manifest.ExternalSources ?? new List<ArchiveManifestExternalSource>())
        {
            // Префикс назначения участвует в перезаписи ссылок: он обязан
            // указывать внутрь каталога восстановления и начинаться с
            // external/ — иначе ссылки уйдут в произвольное место.
            if (string.IsNullOrWhiteSpace(es.ArchivePath) ||
                !es.ArchivePath.StartsWith("external/", StringComparison.Ordinal) ||
                PortableArchiveScanner.IsUnsafeRelative(es.ArchivePath))
                throw new ArchiveFormatException(
                    ArchiveFailureKind.Traversal,
                    $"Манифест содержит внешний источник за пределами каталога: {es.ArchivePath}");
            if (string.IsNullOrWhiteSpace(es.SourcePath))
                throw new ArchiveFormatException(
                    ArchiveFailureKind.Corrupt, "Манифест содержит внешний источник без пути.");
        }
        return manifest;
    }

    /// <summary>
    /// Проверяет data/N против манифеста и либо считает хэши (stagingRoot=null),
    /// либо распаковывает в stagingRoot с перепроверкой. Заполняет
    /// <paramref name="effective"/> парами (archivePath → effectivePath).
    /// </summary>
    private static async Task VerifyEntriesAsync(
        ZipArchive zip, ArchiveManifest manifest, string? stagingRoot,
        ArchiveRestoreOptions? options, List<ArchivePathRemap>? remaps,
        Dictionary<string, string> effective,
        IProgress<long>? progress, CancellationToken ct)
    {
        // Защита от незадекларированных полезных данных: data/-записей,
        // которых нет в манифесте, в штатном архиве быть не может.
        var declared = new HashSet<int>(manifest.Entries.Select(e => e.Index));
        foreach (var z in zip.Entries)
        {
            var name = z.FullName;
            if (!name.StartsWith(DataEntryPrefix, StringComparison.Ordinal)) continue;
            if (!int.TryParse(name[DataEntryPrefix.Length..], out var idx) ||
                !declared.Contains(idx))
                throw new ArchiveFormatException(
                    ArchiveFailureKind.Corrupt,
                    $"В архиве данные без записи в манифесте: {name}");
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var dataEntry = zip.GetEntry(DataEntryPrefix + e.Index);
            if (dataEntry == null)
            {
                if (e.Required)
                    throw new ArchiveFormatException(
                        ArchiveFailureKind.Corrupt,
                        $"В архиве отсутствуют данные обязательной записи {e.ArchivePath}.");
                continue; // необязательная запись может отсутствовать — компонент станет Missing
            }

            // Переназначение пути — только внутрь staging-корня.
            var rel = e.ArchivePath;
            if (options?.PathRemapper is { } remapper)
            {
                var alt = remapper.Remap(e.ArchivePath, e.OriginalPath);
                if (!string.IsNullOrEmpty(alt))
                {
                    alt = PortableArchiveScanner.NormalizeRelative(alt);
                    if (PortableArchiveScanner.IsUnsafeRelative(alt))
                        throw new ArchiveFormatException(
                            ArchiveFailureKind.Traversal,
                            $"Переназначение вывело путь за пределы каталога: {alt}");
                    rel = alt;
                }
            }

            string? target = null;
            if (stagingRoot != null)
            {
                target = PortableArchiveScanner.ResolveUnderRoot(stagingRoot, rel);
                if (target == null)
                    throw new ArchiveFormatException(
                        ArchiveFailureKind.Traversal,
                        $"Путь записи выходит за пределы каталога восстановления: {rel}");
                if (!used.Add(target))
                    throw new ArchiveFormatException(
                        ArchiveFailureKind.Corrupt,
                        $"Две записи указывают на один файл: {rel}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            }
            effective[e.ArchivePath] = rel;
            if (rel != e.ArchivePath)
                remaps?.Add(new ArchivePathRemap(e.ArchivePath, rel, e.OriginalPath));

            using var h = System.Security.Cryptography.SHA256.Create();
            long len = 0;
            await using (var src = dataEntry.Open())
            {
                FileStream? dst = null;
                if (target != null)
                {
                    dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 1 << 20, FileOptions.Asynchronous);
                }
                try
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        h.TransformBlock(buf, 0, n, null, 0);
                        if (dst != null)
                            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        len += n;
                        progress?.Report(n);
                    }
                    h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                }
                finally
                {
                    if (dst != null) await dst.DisposeAsync().ConfigureAwait(false);
                }
            }

            var sha = Convert.ToHexString(h.Hash!);
            if (!string.Equals(sha, e.Sha256, StringComparison.OrdinalIgnoreCase) ||
                len != e.Length)
            {
                throw new ArchiveFormatException(
                    ArchiveFailureKind.Corrupt,
                    $"Запись {e.ArchivePath} повреждена: хэш/размер не совпадает с манифестом.");
            }
        }
    }

    /// <summary>Статусы компонентов по составу инвентаризации (создание).</summary>
    internal static IReadOnlyList<ArchiveComponentStatus> BuildComponents(
        ArchiveInventory inv, IReadOnlyList<ArchiveComponentStatus>? notes = null)
    {
        var names = new List<string>();
        var list = new List<ArchiveComponentStatus>();
        foreach (var e in inv.Entries)
        {
            var comp = ComponentOf(e.ArchivePath);
            if (!names.Contains(comp, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(comp);
                list.Add(new(comp, ArchiveComponentState.Included, e.ArchivePath));
            }
        }
        AppendNotes(list, names, notes);
        return list;
    }

    /// <summary>Статусы компонентов по манифесту (validate/restore).</summary>
    internal static List<ArchiveComponentStatus> BuildComponents(
        ArchiveManifest manifest, IReadOnlyDictionary<string, string>? effective = null,
        IReadOnlyList<ArchiveComponentStatus>? extra = null)
    {
        var names = new List<string>();
        var list = new List<ArchiveComponentStatus>();
        foreach (var e in manifest.Entries)
        {
            var comp = ComponentOf(e.ArchivePath);
            if (names.Contains(comp, StringComparer.OrdinalIgnoreCase)) continue;
            names.Add(comp);
            string? e2 = null;
            var hasEff = effective != null &&
                effective.TryGetValue(e.ArchivePath, out e2);
            var eff = e2 ?? e.ArchivePath;
            var state = effective != null && !hasEff
                ? ArchiveComponentState.Missing
                : string.Equals(eff, e.ArchivePath, StringComparison.Ordinal)
                    ? ArchiveComponentState.Included
                    : ArchiveComponentState.Remapped;
            list.Add(new(comp, state, e.ArchivePath));
        }
        var notes = new List<ArchiveComponentStatus>();
        // Конкретные заметки (per-connection RequiresLogin) впереди общих
        // манифестных: при конфликте компонента у общей заметки конкретная
        // детализация информативнее.
        if (extra != null) notes.AddRange(extra);
        if (manifest.ComponentNotes != null) notes.AddRange(manifest.ComponentNotes);
        AppendNotes(list, names, notes);
        return list;
    }

    /// <summary>
    /// Добавляет заметки (RequiresLogin и др.). Дедуп: по компоненту у
    /// записей-компонентов и по (компонент, текст) у самих заметок — две
    /// разные заметки на один компонент (несколько подключений MCP)
    /// обе должны дойти до пользователя.
    /// </summary>
    private static void AppendNotes(
        List<ArchiveComponentStatus> list, List<string> names,
        IReadOnlyList<ArchiveComponentStatus>? notes)
    {
        if (notes == null) return;
        var seenNotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in notes)
        {
            if (names.Contains(n.Component, StringComparer.OrdinalIgnoreCase)) continue;
            if (!seenNotes.Add(n.Component + "|" + n.Detail)) continue;
            list.Add(n);
        }
    }

    /// <summary>Компонент = верхний сегмент пути; *.db в корне → имя файла.</summary>
    private static string ComponentOf(string archivePath)
    {
        var slash = archivePath.IndexOf('/');
        if (slash >= 0)
            return archivePath[..slash];
        return archivePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(archivePath)
            : archivePath;
    }

    /// <summary>
    /// Суммарный размер записей манифеста — столько данных реально переносится
    /// при восстановлении. Каталог models переносится тоже, если он есть в
    /// архиве: исключение моделей действует на создание новых архивов.
    /// </summary>
    private static long RestorableBytes(ArchiveManifest manifest)
        => manifest.Entries.Sum(e => Math.Max(0, e.Length));

    /// <summary>Человекочитаемый размер для предупреждений (Б/КБ/МБ/ГБ).</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        double v = bytes;
        string[] units = { "КБ", "МБ", "ГБ", "ТБ" };
        var i = -1;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:F1} {units[i]}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
