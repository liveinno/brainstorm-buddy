using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BrainstormBuddy.Core.Backup;

/// <summary>
/// Переназначение абсолютных путей внутри распакованного профиля. Архив
/// хранит manifest.ProfileDirectory (исходный %APPDATA%\…); при восстановлении
/// в другой каталог все ссылки на старый корень переписываются на новый.
///
/// Сам файл profile перемещается целиком — пути внутри данных тоже должны
/// переехать: meta.json встреч (JournalPath), SQLite-базы
/// (chat_images.original/preview/send_path, mem_meeting.journal_path,
/// mtg_state.journal_path), config.json (все строковые значения). Плюс
/// external/-записи: если источник архивировался из каталога вне профиля,
/// ссылки на него переписываются на копию внутри восстановленного профиля.
///
/// Выполняется ДО swap в staging-каталоге: ошибки отсюда не трогают
/// destination. Пишет только безопасные обновления; всё неудачное уходит в
/// warnings — это заметки, не отказ восстановления.
/// </summary>
internal static class PortableArchiveRelocator
{
    /// <summary>Пара (исходный абсолютный префикс → архивный префикс назначения).</summary>
    private sealed record MapPair(string SourcePrefix, string ArchivePrefix);

    /// <summary>
    /// Строит карту переназначения: external/-записи по длине убывают раньше,
    /// корень старого профиля — последним ("" архивный префикс = новый корень).
    /// </summary>
    private static List<MapPair> BuildPairs(
        ArchiveManifest manifest, IReadOnlyDictionary<string, string> effective)
    {
        var pairs = new List<MapPair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Точные корни внешних каталогов, записанные при создании архива:
        // «D:\models\e5\sub\f» → external/<ap>/sub/f независимо от того,
        // существует ли источник на машине восстановления.
        var rootAps = new List<string>();
        foreach (var es in manifest.ExternalSources ?? new List<ArchiveManifestExternalSource>())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(es.SourcePath) ||
                    string.IsNullOrWhiteSpace(es.ArchivePath) ||
                    !es.ArchivePath.StartsWith("external/", StringComparison.Ordinal))
                    continue;
                var key = "R" + Path.GetFullPath(es.SourcePath).TrimEnd('\\', '/');
                if (seen.Add(key))
                {
                    pairs.Add(new MapPair(key[1..], es.ArchivePath.TrimEnd('/')));
                    rootAps.Add(es.ArchivePath.TrimEnd('/') + "/");
                }
            }
            catch { }
        }

        foreach (var e in manifest.Entries)
        {
            var ap = effective.TryGetValue(e.ArchivePath, out var eff) ? eff : e.ArchivePath;
            if (!ap.StartsWith("external/", StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(e.OriginalPath)) continue;

            string? srcPrefix = null;
            string? archPrefix = null;
            try
            {
                var segs = ap.Split('/');
                // Запись-файл, сам являющаяся корнем источника (точное
                // совпадение), и запись под корнем-каталогом покрыты парой
                // из ExternalSources — оценка по глубине для них не нужна.
                var covered = rootAps.Any(r => ap.StartsWith(r, StringComparison.Ordinal) ||
                    string.Equals(ap, r.TrimEnd('/'), StringComparison.Ordinal));
                if (!covered && segs.Length > 2)
                {
                    // Файл внутри каталога-источника из архива старой версии
                    // (без ExternalSources): корень = external/<первый сегмент>,
                    // предок = OriginalPath минус столько каталогов, сколько
                    // сегментов в относительном пути. Для записей под известным
                    // корнем этой оценки нет — она может быть длиннее корня
                    // и ошибочно победить в сортировке по длине.
                    var dir = Path.GetFullPath(e.OriginalPath);
                    for (var up = segs.Length - 2; up > 0; up--)
                        dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? dir;
                    srcPrefix = dir;
                    archPrefix = "external/" + segs[1];
                }
                else if (!covered)
                {
                    // Файл: точное совпадение плюс родительский каталог как префикс.
                    var srcDir = Path.GetDirectoryName(Path.GetFullPath(e.OriginalPath));
                    var archDir = ap.Contains('/') ? ap[..ap.LastIndexOf('/')] : "";
                    if (!string.IsNullOrEmpty(srcDir))
                    {
                        var dkey = "D" + srcDir.TrimEnd('\\', '/');
                        if (seen.Add(dkey))
                            pairs.Add(new MapPair(dkey[1..], archDir));
                    }
                    srcPrefix = Path.GetFullPath(e.OriginalPath);
                    archPrefix = ap;
                }
                // covered: запись под корнем из ExternalSources — корневая
                // пара уже даёт точный маппинг, дубли не нужны.
            }
            catch { continue; }
            if (srcPrefix is null || archPrefix is null)
                continue;
            var key = "F" + srcPrefix.TrimEnd('\\', '/');
            if (seen.Add(key))
                pairs.Add(new MapPair(key[1..], archPrefix));
        }

        var oldRoot = "";
        try { oldRoot = Path.GetFullPath(manifest.ProfileDirectory).TrimEnd('\\', '/'); }
        catch { }
        if (!string.IsNullOrEmpty(oldRoot) && seen.Add("P" + oldRoot))
            pairs.Add(new MapPair(oldRoot, ""));
        // Длинные префиксы сначала — чтобы external/* выигрывал у корня профиля.
        pairs.Sort((a, b) => b.SourcePrefix.Length.CompareTo(a.SourcePrefix.Length));
        return pairs;
    }

    /// <summary>
    /// Если <paramref name="value"/> начинается с одного из исходных префиксов
    /// (без учёта регистра, / и \ эквивалентны), возвращает путь под новым корнем.
    /// </summary>
    private static string? RelocateString(
        string value, List<MapPair> pairs, string newRoot)
    {
        var t = value.Trim();
        if (!PortableArchiveScanner.LooksLikeWindowsAbsPath(t)) return null;
        var normValue = t.Replace('/', '\\');
        foreach (var p in pairs)
        {
            var src = p.SourcePrefix.TrimEnd('\\', '/');
            if (!normValue.Equals(src, StringComparison.OrdinalIgnoreCase) &&
                !normValue.StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))
                continue;
            var tail = normValue[src.Length..].TrimStart('\\', '/');
            var joined = string.IsNullOrEmpty(p.ArchivePrefix)
                ? tail
                : p.ArchivePrefix.TrimEnd('/') + (tail.Length > 0 ? "/" + tail : "");
            var abs = joined.Replace('/', Path.DirectorySeparatorChar);
            string full;
            try
            {
                // Хвост собирается из произвольной строки конфига: «name:»
                // и прочие комбинации Path.GetFullPath отклоняет
                // NotSupportedException — мимо catch в Relocate полетел бы
                // сырой отказ restore. Такое значение просто не переписываем.
                full = Path.GetFullPath(Path.Combine(newRoot, abs));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
            // Манифест проверяется на входе, но результат всё равно обязан
            // остаться внутри корня восстановления.
            if (!PortableArchiveScanner.IsUnderDirectory(full, newRoot) &&
                !string.Equals(full, newRoot, StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
        return null;
    }

    /// Переписывает все ссылки на старый профиль/external-источники в файлах
    /// staging-каталога. Значения получают конечный корень назначения, который
    /// останется корректным после атомарного перемещения staging в destination.
    /// Возвращает число переписанных значений; каждая ошибка добавляет предупреждение.
    /// </summary>
    internal static int Relocate(
        string stagingRoot, string destinationRoot, ArchiveManifest manifest,
        IReadOnlyDictionary<string, string> effective, List<string> warnings)
    {
        var newRoot = Path.GetFullPath(destinationRoot).TrimEnd('\\', '/');
        var pairs = BuildPairs(manifest, effective);
        try
        {
            var oldRoot = Path.GetFullPath(manifest.ProfileDirectory).TrimEnd('\\', '/');
            if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Восстановление в тот же каталог: корневая пара — identity,
                // а вот external/-источники переназначать всё равно нужно
                // (их копии лежат внутри профиля, а исходный каталог может
                // отсутствовать) — убираем только бесполезную корневую пару.
                pairs.RemoveAll(p => p.ArchivePrefix.Length == 0);
            }
        }
        catch { } // битый ProfileDirectory: external-пары всё равно применимы
        if (pairs.Count == 0) return 0;

        var changed = 0;

        // 1) JSON-файлы со служебными ссылками: config.json, meetings/*.meta.json,
        // transcriptions/*.json (SourcePath), mcp/*.json и пр. Каталог
        // attachments/ исключён: source/segments.json — контент документов и
        // изображений, его байты не трогаем.
        var jsonFiles = new List<string>();
        var attachRoot = Path.Combine(stagingRoot, "attachments");
        try
        {
            foreach (var jf in Directory.EnumerateFiles(
                stagingRoot, "*.json", SearchOption.AllDirectories))
            {
                if (PortableArchiveScanner.IsUnderDirectory(jf, attachRoot)) continue;
                jsonFiles.Add(jf);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Не удалось прочитать список json-файлов: {ex.Message}");
        }

        foreach (var jf in jsonFiles)
        {
            try
            {
                var text = File.ReadAllText(jf);
                using var doc = JsonDocument.Parse(text);
                var edited = RewriteElement(doc.RootElement, pairs, newRoot, ref changed);
                if (edited != null)
                {
                    var tmp = jf + ".reloc";
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
                        edited.Value.WriteTo(w);
                    File.Move(tmp, jf, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                warnings.Add($"Не удалось переписать пути в {Path.GetFileName(jf)}: {ex.Message}");
            }
        }

        // 2) SQLite-базы: фиксированные путь-колонки.
        changed += RelocateSqlite(Path.Combine(stagingRoot, "attachments.db"),
            ("chat_images", "image_id", new[] { "original_path", "preview_path", "send_path" }),
            pairs, newRoot, warnings);
        changed += RelocateSqlite(Path.Combine(stagingRoot, "memory.db"),
            ("mem_meeting", "id", new[] { "journal_path" }),
            pairs, newRoot, warnings);
        changed += RelocateSqlite(Path.Combine(stagingRoot, "meetings-index.db"),
            ("mtg_state", "meeting_id", new[] { "journal_path" }),
            pairs, newRoot, warnings);
        return changed;
    }

    /// <summary>
    /// Рекурсивная перезапись JSON-значений: возвращает изменённый корень или
    /// null, если ничего не менялось.
    /// </summary>
    private static JsonElement? RewriteElement(
        JsonElement el, List<MapPair> pairs, string newRoot, ref int changed)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var rel = RelocateString(el.GetString() ?? "", pairs, newRoot);
                if (rel == null) return null;
                changed++;
                return JsonDocument.Parse(JsonSerializer.Serialize(rel)).RootElement.Clone();
            case JsonValueKind.Array:
                {
                    List<JsonElement>? items = null;
                    var i = 0;
                    foreach (var item in el.EnumerateArray())
                    {
                        var sub = RewriteElement(item, pairs, newRoot, ref changed);
                        if (sub != null)
                        {
                            items ??= el.EnumerateArray().Select(x => x.Clone()).ToList();
                            items[i] = sub.Value;
                        }
                        i++;
                    }
                    if (items == null) return null;
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms))
                    {
                        w.WriteStartArray();
                        foreach (var it in items) it.WriteTo(w);
                        w.WriteEndArray();
                    }
                    ms.Position = 0;
                    return JsonDocument.Parse(ms).RootElement.Clone();
                }
            case JsonValueKind.Object:
                {
                    // Пары, а не словарь: JsonDocument допускает дубли имён
                    // свойств (ручные правки конфига) — ToDictionary на них
                    // бросает ArgumentException мимо catch → сырой отказ restore.
                    // Порядок и дубли сохраняем как в источнике.
                    List<KeyValuePair<string, JsonElement>>? props = null;
                    var i = 0;
                    foreach (var p in el.EnumerateObject())
                    {
                        var sub = RewriteElement(p.Value, pairs, newRoot, ref changed);
                        if (sub != null)
                        {
                            props ??= el.EnumerateObject()
                                .Select(x => KeyValuePair.Create(x.Name, x.Value.Clone()))
                                .ToList();
                            props[i] = KeyValuePair.Create(p.Name, sub.Value);
                        }
                        i++;
                    }
                    if (props == null) return null;
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms))
                    {
                        w.WriteStartObject();
                        foreach (var kv in props)
                        {
                            w.WritePropertyName(kv.Key);
                            kv.Value.WriteTo(w);
                        }
                        w.WriteEndObject();
                    }
                    ms.Position = 0;
                    return JsonDocument.Parse(ms).RootElement.Clone();
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// UPDATE по строкам: REPLACE в SQL регистрозависим, поэтому читаем
    /// колонки и пишем обратно только реально изменившиеся строки.
    /// </summary>
    private static int RelocateSqlite(
        string dbPath,
        (string Table, string Key, string[] PathCols) target,
        List<MapPair> pairs, string newRoot, List<string> warnings)
    {
        if (!File.Exists(dbPath)) return 0;
        var changed = 0;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Pooling = false,
            }.ToString());
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }

            // Таблицы может не быть (старая схема) — тогда база просто молча
            // обходится: SELECT упадёт, поймаем и предупредим один раз.
            var sel = conn.CreateCommand();
            sel.CommandText = $"SELECT {target.Key}, " +
                string.Join(", ", target.PathCols) + $" FROM {target.Table}";
            var rows = new List<(string Key, string?[] Cols)>();
            using (var r = sel.ExecuteReader())
            {
                while (r.Read())
                {
                    var cols = new string?[target.PathCols.Length];
                    for (var i = 0; i < cols.Length; i++)
                        cols[i] = r.IsDBNull(i + 1) ? null : r.GetString(i + 1);
                    rows.Add((r.GetValue(0)?.ToString() ?? "", cols));
                }
            }
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Cols.Length; i++)
                {
                    var v = row.Cols[i];
                    if (string.IsNullOrEmpty(v)) continue;
                    var nv = RelocateString(v, pairs, newRoot);
                    if (nv == null || string.Equals(nv, v, StringComparison.Ordinal)) continue;
                    var upd = conn.CreateCommand();
                    upd.CommandText = $"UPDATE {target.Table} SET {target.PathCols[i]}=$v " +
                                      $"WHERE {target.Key}=$k";
                    upd.Parameters.AddWithValue("$v", nv);
                    upd.Parameters.AddWithValue("$k", row.Key);
                    upd.ExecuteNonQuery();
                    changed++;
                }
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            warnings.Add($"Не удалось переписать пути в {Path.GetFileName(dbPath)}: {ex.Message}");
        }
        return changed;
    }

    /// <summary>
    /// Внешние абсолютные пути из staged config.json, которых нет на этом
    /// устройстве (чужой exe для MCP, каталог моделей, папка экспорта).
    /// Компоненты «Missing» — конкретный список, а не «что-то не подключилось».
    /// <paramref name="profileDir"/> — КОНЕЧНЫЙ каталог восстановления:
    /// Relocate уже переписал внутренние ссылки на него.
    /// </summary>
    internal static List<ArchiveComponentStatus> MissingExternalComponents(
        string configPath, string profileDir, ArchiveManifest manifest)
    {
        if (!File.Exists(configPath))
            return new List<ArchiveComponentStatus>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            return MissingExternalComponents(doc.RootElement, profileDir, manifest);
        }
        catch { return new List<ArchiveComponentStatus>(); }
    }

    /// <summary>То же для уже распарсенного корня config.json — проверка архива без распаковки на диск.</summary>
    internal static List<ArchiveComponentStatus> MissingExternalComponents(
        JsonElement configRoot, string profileDir, ArchiveManifest? manifest)
    {
        var list = new List<ArchiveComponentStatus>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Корень для отсева внутренних путей: битый/пустой profileDir в
        // манифесте не должен ронять проверку — просто нет что отсекать.
        string? root = null;
        try { if (!string.IsNullOrWhiteSpace(profileDir)) root = Path.GetFullPath(profileDir); }
        catch { }
        foreach (var p in PortableArchiveScanner.CollectAbsolutePaths(configRoot))
        {
            var t = p.Trim();
            try
            {
                // Пути внутри профиля — уже переписаны/внутри архива, не считаем.
                if (root != null && PortableArchiveScanner.IsUnderDirectory(t, root)) continue;
                // Ссылка покрыта самим архивом: external/-записи восстановят
                // копию внутри профиля — это не «нет на устройстве».
                if (manifest != null && CoveredByArchivedSource(t, manifest)) continue;
            }
            catch { continue; } // «абсолютный» путь из чужого конфига может не парситься у нас
            if (File.Exists(t) || Directory.Exists(t)) continue;
            // Только реальные сценарии: exe/каталоги моделей/пути сохранения.
            missing.Add(t);
            if (missing.Count >= 10) break;
        }
        foreach (var m in missing)
            list.Add(new ArchiveComponentStatus(
                "external.paths", ArchiveComponentState.Missing,
                $"Нет на этом устройстве: {m}"));
        return list;
    }

    /// <summary>
    /// Ссылка на исходный внешний каталог/файл, включённый в архив как
    /// external/-записи: при восстановлении она будет переписана на копию
    /// внутри профиля, так что отсутствие оригинала на этом устройстве
    /// не делает компонент потерянным.
    /// </summary>
    private static bool CoveredByArchivedSource(string absPath, ArchiveManifest manifest)
    {
        foreach (var es in manifest.ExternalSources ?? new List<ArchiveManifestExternalSource>())
        {
            if (string.IsNullOrWhiteSpace(es.SourcePath)) continue;
            try
            {
                var src = Path.GetFullPath(es.SourcePath).TrimEnd('\\', '/');
                if (PortableArchiveScanner.IsUnderDirectory(absPath, src) ||
                    string.Equals(Path.GetFullPath(absPath), src,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        foreach (var e in manifest.Entries ?? new List<ArchiveManifestEntry>())
        {
            if (!e.ArchivePath.StartsWith("external/", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(e.OriginalPath)) continue;
            try
            {
                if (string.Equals(Path.GetFullPath(absPath),
                        Path.GetFullPath(e.OriginalPath), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }
}
