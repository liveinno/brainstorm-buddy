using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BrainstormBuddy.Core.Backup;

/// <summary>
/// Инвентаризация профиля перед созданием архива. Профиль = каталог
/// данных приложения (по умолчанию %APPDATA%\BrainstormBuddy, переопределяется
/// BUDDY_DATA_DIR): config.json, memory.db, attachments.db, attachments/,
/// meetings/, images/, mcp/, models/ и прочие каталоги пользовательских данных.
///
/// Правила:
/// — SQLite-файлы корня (*.db) попадают в архив согласованным снимком
///   <c>VACUUM INTO</c> (не теряет WAL и читаемую работающую базу); sidecar
///   -wal/-shm/-journal в архив не нужны — снимок их уже учёл.
/// — logs/, tmp/, temp/ не входят по умолчанию (IncludeLogs для логов — опция).
/// — models/ (Whisper ggml, E5 ONNX, Nemotron) по умолчанию НЕ входят в
///   архив (IncludeModels = false): это гигабайты, которые на новом
///   устройстве качаются заново. Полный перенос — IncludeModels = true.
/// — Файл создаваемого архива исключается, если лежит внутри профиля.
/// — Внешние пути добавляются только явно через
///   <see cref="ArchiveInventoryOptions.ExternalPaths"/>.
/// </summary>
public static class PortableArchiveScanner
{
    /// <summary>
    /// Имя каталога моделей в профиле (Whisper ggml, E5 ONNX, Nemotron).
    /// По умолчанию каталог не входит в архив и включается только явным
    /// выбором пользователя.
    /// </summary>
    internal const string ModelsDirectoryName = "models";

    /// <summary>Каталоги верхнего уровня, не являющиеся пользовательскими данными.</summary>
    private static readonly HashSet<string> ExcludedTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "tmp", "temp", "dumps",
    };
    /// <summary>Временные каталоги на любой глубине (attachments/tmp — scratch импорта).</summary>
    private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmp", "temp", "_tmp",
    };
    private static readonly string[] SqliteSidecarSuffixes = { "-wal", "-shm", "-journal" };

    /// <summary>Сигнатура заголовка SQLite3 (первые 16 байт файла).</summary>
    private static readonly byte[] SqliteMagic =
        "SQLite format 3\0"u8.ToArray();


    /// <summary>Собирает инвентаризацию: список файлов с относительными путями, размерами и SHA-256.</summary>
    public static async Task<ArchiveInventory> BuildAsync(
        ArchiveInventoryOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var profile = Path.GetFullPath(options.ProfileDirectory);
        if (!Directory.Exists(profile))
            throw new PortableArchiveException(
                $"Каталог профиля не существует: {profile}", ArchiveFailureKind.MissingRequiredSource);

        var warnings = new List<string>();
        var entries = new List<ArchiveInventoryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Пасс 1: перечисление и размеры — нужен TotalBytes для честного прогресса.
        var candidates = new List<(string ArchivePath, string SourcePath, ArchiveEntryKind Kind, bool Required, long Length)>();

        var archiveFullPath = options.ArchivePath is { Length: > 0 } p ? Path.GetFullPath(p) : null;

        foreach (var file in EnumerateProfileFiles(profile, options.IncludeModels, options.IncludeLogs))
        {
            if (archiveFullPath != null &&
                string.Equals(Path.GetFullPath(file), archiveFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue; // архив не включает сам себя
            }

            var rel = NormalizeRelative(Path.GetRelativePath(profile, file));
            var kind = IsSqliteCandidate(rel, file) ? ArchiveEntryKind.SqliteSnapshot : ArchiveEntryKind.File;
            long len;
            try { len = new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PortableArchiveException(
                    $"Файл исчез или недоступен при сканировании: {file}", ArchiveFailureKind.Io, ex);
            }
            candidates.Add((rel, file, kind, Required: true, len));
        }

        foreach (var ext in options.ExternalPaths ?? Array.Empty<ArchiveExternalPath>())
        {
            string source;
            string archivePath;
            try
            {
                source = Path.GetFullPath(ext.SourcePath);
                archivePath = NormalizeRelative(ext.ArchivePath ?? "");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new PortableArchiveException(
                    $"Недопустимый путь внешнего источника: {ext.SourcePath} → {ext.ArchivePath}",
                    ArchiveFailureKind.InvalidFormat, ex);
            }
            if (IsUnsafeRelative(archivePath))
                throw new PortableArchiveException(
                    $"Недопустимый путь внутри архива: {ext.ArchivePath}", ArchiveFailureKind.Traversal);
            // Ссылки на внешние данные переписываются relocator'ом только на
            // external/-префикс (BuildPairs + валидация манифеста). Источник
            // без префикса молча создавал бы битые ссылки на новом устройстве.
            if (!archivePath.StartsWith("external/", StringComparison.Ordinal))
                throw new PortableArchiveException(
                    $"Внешний источник должен архивироваться под префиксом external/: {ext.ArchivePath}",
                    ArchiveFailureKind.InvalidFormat);
            if (IsUnderDirectory(source, profile))
            {
                warnings.Add($"Внешний путь {ext.SourcePath} уже внутри профиля — пропущен.");
                continue;
            }

            if (File.Exists(source))
            {
                // Корень источника-файла обязан иметь имя: «external/foo/»
                // сопоставился бы каталогу, а точная пара для relocator'а
                // требует пути без слэша.
                if (archivePath.EndsWith("/", StringComparison.Ordinal))
                    throw new PortableArchiveException(
                        $"Путь внутри архива для файлового источника не должен кончаться слэшем: {ext.ArchivePath}",
                        ArchiveFailureKind.InvalidFormat);
                long fileLen;
                try { fileLen = new FileInfo(source).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (ext.Required)
                        throw new PortableArchiveException(
                            $"Файл исчез или недоступен при сканировании: {source}", ArchiveFailureKind.Io, ex);
                    warnings.Add($"Внешний источник недоступен: {ext.SourcePath}");
                    continue;
                }
                candidates.Add((archivePath, source,
                    IsSqliteCandidate(archivePath, source) ? ArchiveEntryKind.SqliteSnapshot : ArchiveEntryKind.File,
                    ext.Required, fileLen));
            }
            else if (Directory.Exists(source))
            {
                // Внешний источник копируется как есть: scratch-исключения
                // профиля (tmp/temp/models) к чужому каталогу не применяем —
                // иначе часть данных пропадёт из архива молча.
                var files = EnumerateProfileFiles(
                    source, true, true, applyProfileExclusions: false).ToList();
                if (files.Count == 0)
                {
                    if (ext.Required)
                        throw MissingRequired(ext.SourcePath);
                    warnings.Add($"Внешний каталог пуст: {ext.SourcePath}");
                }
                foreach (var f in files)
                {
                    var sub = archivePath + "/" + NormalizeRelative(Path.GetRelativePath(source, f));
                    long extLen;
                    try { extLen = new FileInfo(f).Length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new PortableArchiveException(
                            $"Файл исчез или недоступен при сканировании: {f}", ArchiveFailureKind.Io, ex);
                    }
                    candidates.Add((sub, f,
                        IsSqliteCandidate(sub, f) ? ArchiveEntryKind.SqliteSnapshot : ArchiveEntryKind.File,
                        ext.Required, extLen));
                }
            }
            else
            {
                if (ext.Required)
                    throw MissingRequired(ext.SourcePath);
                warnings.Add($"Внешний источник отсутствует: {ext.SourcePath}");
            }
        }

        // Пасс 2: хэши. SQLite-файлы хэшируем как файлы (при записи архива для них
        // берётся согласованный снимок; хэш в манифесте всё равно пересчитывается
        // по фактически записанным байтам).
        var total = Math.Max(0L, candidates.Sum(c => c.Length));
        long done = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string sha;
            try
            {
                sha = c.Kind == ArchiveEntryKind.SqliteSnapshot
                    ? "" : await HashFileAsync(c.SourcePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PortableArchiveException(
                    $"Не удалось прочитать {c.SourcePath}: {ex.Message}", ArchiveFailureKind.Io, ex);
            }

            if (!seen.Add(c.ArchivePath))
            {
                throw new PortableArchiveException(
                    $"Несколько источников используют путь архива: {c.ArchivePath}", ArchiveFailureKind.InvalidFormat);
            }
            entries.Add(new ArchiveInventoryEntry(
                c.ArchivePath, c.SourcePath, c.Length, sha, c.Kind, c.Required));
            done += c.Length;
            progress?.Report(new ArchiveProgress("inventory", c.ArchivePath, done, total));
        }

        entries.Sort((a, b) => string.Compare(a.ArchivePath, b.ArchivePath, StringComparison.Ordinal));
        return new ArchiveInventory(profile, entries, entries.Sum(e => e.Length), warnings);
    }

    /// <summary>
    /// Согласованный снимок SQLite-базы через <c>VACUUM INTO</c> во временный файл.
    /// База должна существовать и быть читаемой; занятая писателем база —
    /// исключение с понятным текстом (копировать живую базу «как файл» нельзя:
    /// потеряется содержимое WAL).
    /// </summary>
    /// <returns>Путь к временному снимку. Caller удаляет файл после записи.</returns>
    internal static async Task<string> SnapshotSqliteAsync(string dbPath, CancellationToken ct)
    {
        var snapPath = Path.Combine(
            Path.GetDirectoryName(dbPath)!, $".bsb-arch-snap-{Guid.NewGuid():N}.db");
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            SqliteConnection? conn = null;
            try
            {
                conn = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
                await conn.OpenAsync(ct).ConfigureAwait(false);

                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=10000;";
                    await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                using var cmd = conn.CreateCommand();
                // VACUUM INTO не принимает параметры в целевом выражении — литерал.
                cmd.CommandText = $"VACUUM INTO '{snapPath.Replace("'", "''")}';";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return snapPath;
            }
            catch (SqliteException ex) when (
                (ex.SqliteErrorCode & 0xFF) == 5 /* SQLITE_BUSY* */ && attempt < attempts)
            {
                try { File.Delete(snapPath); } catch { }
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                try { File.Delete(snapPath); } catch { }
                throw new PortableArchiveException(
                    $"Не удалось сделать снимок {dbPath}: база занята или недоступна. " +
                    $"Закройте приложение перед созданием архива. ({ex.Message})",
                    ArchiveFailureKind.Io, ex);
            }
            catch
            {
                try { File.Delete(snapPath); } catch { }
                throw;
            }
            finally
            {
                conn?.Dispose();
            }
        }
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>Путь внутри архива: прямой слэш, без ведущих разделителей.</summary>
    internal static string NormalizeRelative(string path)
        => path.Replace('\\', '/');

    /// <summary>Проверка на выход за пределы корня: .. / абсолютные пути / UNC / диски.</summary>
    internal static bool IsUnsafeRelative(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return true;
        if (normalized.StartsWith('/') || normalized.StartsWith('\\')) return true;
        if (normalized.Contains('\\')) return true;
        if (normalized.Length >= 2 && normalized[1] == ':') return true; // C:/...
        foreach (var seg in normalized.Split('/'))
        {
            if (seg is "" or "." or "..") return true;
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return true;
            var device = seg.Split('.')[0];
            if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (device.Length == 4 && device[3] is >= '1' and <= '9' &&
                 (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))) return true;
        }
        return false;
    }

    internal static bool IsUnderDirectory(string filePath, string dir)
    {
        var dirFull = Path.GetFullPath(dir);
        if (!dirFull.EndsWith(Path.DirectorySeparatorChar))
            dirFull += Path.DirectorySeparatorChar;
        var fileFull = Path.GetFullPath(filePath);
        return fileFull.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Полный путь назначения внутри корня или null при выходе за границу.</summary>
    internal static string? ResolveUnderRoot(string root, string normalizedRel)
    {
        if (IsUnsafeRelative(normalizedRel)) return null;
        var full = Path.GetFullPath(Path.Combine(root, normalizedRel.Replace('/', Path.DirectorySeparatorChar)));
        return IsUnderDirectory(full, root) || string.Equals(
            full, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// Файл-кандидат на согласованный снимок: имя в архиве оканчивается на
    /// .db/.sqlite И содержимое реально начинается с заголовка SQLite3.
    /// Произвольный файл с расширением .db (кэш, бинарь) архивируется
    /// обычной копией — VACUUM INTO на нём упал бы и валил весь backup.
    /// </summary>
    private static bool IsSqliteCandidate(string archivePath, string sourcePath)
    {
        if (!archivePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase) &&
            !archivePath.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var head = new byte[SqliteMagic.Length];
            using var fs = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return fs.Read(head, 0, head.Length) == head.Length &&
                head.AsSpan().SequenceEqual(SqliteMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // недоступный файл — обычная запись; её хэш/чтение дадут понятную ошибку
        }
    }

    private static bool IsSqliteSidecar(string path)
    {
        foreach (var suffix in SqliteSidecarSuffixes)
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path[..^suffix.Length])) return true;
        return false;
    }

    private static IEnumerable<string> EnumerateProfileFiles(
        string profile, bool includeModels, bool includeLogs,
        bool applyProfileExclusions = true)
    {
        var stack = new Stack<string>();
        stack.Push(profile);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PortableArchiveException(
                    $"Нет доступа к каталогу профиля: {dir}", ArchiveFailureKind.Io, ex);
            }

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                // Транзиентные файлы профиля: *.tmp / *.tmp-<pid> (chat images,
                // tool-results), остатки аварийно прерванного CreateAsync
                // (*.ziptmp) и собственные снапшоты/темпы архива. Во внешних
                // каталогах это пользовательские данные — не выкидываем.
                if (applyProfileExclusions &&
                    (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".ziptmp", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains(".tmp-", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith(".bsb-arch-", StringComparison.Ordinal)))
                    continue;
                if (applyProfileExclusions && IsSqliteSidecar(f))
                    continue;
                if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)
                    throw new PortableArchiveException(
                        $"Ссылка на внешний файл требует явного включения: {f}", ArchiveFailureKind.MissingRequiredSource);
                yield return f;
            }
            foreach (var d in dirs)
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                    throw new PortableArchiveException(
                        $"Ссылка на внешний каталог требует явного включения: {d}", ArchiveFailureKind.MissingRequiredSource);
                var name = Path.GetFileName(d);
                if (applyProfileExclusions)
                {
                    if (ExcludedDirNames.Contains(name))
                        continue; // scratch-каталоги (attachments/tmp и пр.) — не данные
                    var top = string.Equals(
                        Path.GetDirectoryName(Path.GetFullPath(d)),
                        Path.GetFullPath(profile), StringComparison.OrdinalIgnoreCase);
                    if (top)
                    {
                        if (!includeModels &&
                            name.Equals(ModelsDirectoryName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (name.Equals("logs", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!includeLogs) continue;
                        }
                        else if (ExcludedTopDirs.Contains(name))
                            continue;
                    }
                }
                stack.Push(d);
            }
        }
    }

    // =============== Заметки о переносимости компонентов ===============

    /// <summary>
    /// Смотрит на содержимое профиля (не только имена файлов) и возвращает
    /// постоянные заметки о переносимости: DPAPI-блобы в mcp/permissions.json
    /// и SecretRef-подключения в config.json — RequiresLogin, если секрет не
    /// попал в архив (<paramref name="exportedSecretIds"/> содержит
    /// connectionId перенесённых грантов; значения SecretRef — те же id).
    /// Перенесённые секреты отмечаются Remapped. Сами секреты не читаются и
    /// не логируются — только факт наличия блобов.
    /// </summary>
    /// null → архив старой версии: любой блоб = RequiresLogin.
    internal static List<ArchiveComponentStatus> DetectComponentNotes(
        string profile, HashSet<string>? exportedSecretIds = null)
    {
        var notes = new List<ArchiveComponentStatus>();
        var permPath = Path.Combine(profile, "mcp", "permissions.json");
        if (File.Exists(permPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(permPath));
                if (doc.RootElement.TryGetProperty("grants", out var grants) &&
                    grants.ValueKind == JsonValueKind.Array)
                {
                    var n = 0;
                    var moved = 0;
                    foreach (var g in grants.EnumerateArray())
                    {
                        if (!g.TryGetProperty("protectedToken", out var pt) ||
                            pt.ValueKind != JsonValueKind.String ||
                            string.IsNullOrEmpty(pt.GetString()))
                            continue;
                        n++;
                        // Перенесённым считается только грант, чей connectionId
                        // реально попал в выгруженные секреты (aId). Если Export
                        // сломался и вернул пустой список — moved=0 и все блобы
                        // честно уходят в RequiresLogin, а не в ложный Remapped.
                        if (exportedSecretIds != null &&
                            g.TryGetProperty("connectionId", out var aId) &&
                            aId.ValueKind == JsonValueKind.String &&
                            exportedSecretIds.Contains(aId.GetString() ?? ""))
                            moved++;
                    }
                    if (n > 0)
                    {
                        // Число грантов, чьи секреты НЕ уехали в архив.
                        var stale = exportedSecretIds == null ? n : n - moved;
                        if (stale > 0)
                            notes.Add(new ArchiveComponentStatus(
                                "mcp.tokens", ArchiveComponentState.RequiresLogin,
                                $"{stale} токен(ов) MCP-подключений зашифрованы DPAPI CurrentUser: " +
                                "на новой учётной записи их потребуется перевыпустить."));
                        else
                            notes.Add(new ArchiveComponentStatus(
                                "mcp.tokens", ArchiveComponentState.Remapped,
                                $"{n} токен(ов) MCP-подключений перенесены в архив и " +
                                "перепривяжутся к учётной записи при восстановлении."));
                    }
                }
            }
            catch { /* файл ещё пишется или битый — пометки пропускаем, данные всё равно уедут */ }
        }

        var cfgPath = Path.Combine(profile, "config.json");
        if (File.Exists(cfgPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                var secretRefs = 0;
                var movedRefs = 0;
                var names = new List<string>();
                if (TryProp(doc.RootElement, "Mcp", out var mcp))
                {
                    foreach (var listName in new[] { "Connections", "OutgoingServers" })
                    {
                        if (!TryProp(mcp, listName, out var arr) ||
                            arr.ValueKind != JsonValueKind.Array) continue;
                        foreach (var it in arr.EnumerateArray())
                        {
                            if (!TryProp(it, "SecretRef", out var sr) ||
                                sr.ValueKind != JsonValueKind.String ||
                                string.IsNullOrEmpty(sr.GetString()))
                                continue;
                            // SecretRef — connectionId гранта в permissions.json;
                            // перенесённый секрет не требует переввода.
                            if (exportedSecretIds != null &&
                                exportedSecretIds.Contains(sr.GetString()!))
                            {
                                movedRefs++;
                                continue;
                            }
                            secretRefs++;
                            if (TryProp(it, "Name", out var nm) &&
                                nm.ValueKind == JsonValueKind.String &&
                                names.Count < 5)
                                names.Add(nm.GetString() ?? "");
                        }
                    }
                }
                if (secretRefs > 0)
                    notes.Add(new ArchiveComponentStatus(
                        "mcp.secrets", ArchiveComponentState.RequiresLogin,
                        $"{secretRefs} подключение(й) с DPAPI-секретом" +
                        (names.Count > 0 ? $" ({string.Join(", ", names)})" : "") +
                        " — после переноса на другую учётную запись требуется переввод ключа/токена."));
                else if (movedRefs > 0)
                    notes.Add(new ArchiveComponentStatus(
                        "mcp.secrets", ArchiveComponentState.Remapped,
                        $"{movedRefs} подключение(й) с DPAPI-секретом перенесены в архив " +
                        "и перепривяжутся к учётной записи при восстановлении."));
            }
            catch { }
        }
        return notes;
    }

    /// <summary>
    /// Абсолютные пути ОС из JSON-элемента (рекурсивно, только строковые значения,
    /// похожие на «диск:\» или «\\»). Для поиска внешних exe/моделей в конфиге —
    /// компоненты, которых может не быть на другом устройстве.
    /// </summary>
    internal static IEnumerable<string> CollectAbsolutePaths(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s) && LooksLikeWindowsAbsPath(s))
                    yield return s!;
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    foreach (var p in CollectAbsolutePaths(item)) yield return p;
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    foreach (var p in CollectAbsolutePaths(prop.Value)) yield return p;
                break;
        }
    }

    internal static bool LooksLikeWindowsAbsPath(string s)
    {
        var t = s.Trim();
        if (t.Length >= 3 && char.IsLetter(t[0]) && t[1] == ':' &&
            (t[2] == '\\' || t[2] == '/')) return true;
        return t.StartsWith("\\\\", StringComparison.Ordinal);
    }

    /// <summary>Свойство объекта без учёта регистра (конфиг читается case-insensitive).</summary>
    internal static bool TryProp(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            { value = p.Value; return true; }
        return false;
    }

    private static PortableArchiveException MissingRequired(string path)
        => new($"Обязательный источник не найден: {path}", ArchiveFailureKind.MissingRequiredSource);
}
