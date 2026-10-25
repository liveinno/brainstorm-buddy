using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BrainstormBuddy.Core.Images;
using BrainstormBuddy.Services;
using Microsoft.Data.Sqlite;
namespace BrainstormBuddy.Core.Attachments;

/// <summary>
/// Хранилище чат-вложений: индекс attachments.db (SQLite, PRAGMA user_version=4)
/// + побайтовые копии в attachments\{documentId}\source{ext} и segments.json рядом.
/// Независимо от memory.db: выключенная память не отключает файлы.
/// Схема v2 (ChatImageStore.cs): изображения чата (chat_images, связи с
/// сообщениями, черновики) и кэш поддержки изображений (image_capability_cache).
///
/// Атомарность импорта: копия пишется в attachments\_tmp → File.Move в каталог
/// документа → запись в БД под _gate. Сбой между move и insert чинится при старте:
/// RecoverOnStartup() сносит сиротские doc-* каталоги без строки в documents и
/// img-* каталоги без строки в chat_images, остатки _tmp, а зависшие
/// fact_jobs (Queued/Running) → Interrupted.
///
/// Потокобезопасность — как у SqliteMemoryStore: одно соединение + lock.
/// DeleteDocumentAsync сталкивается с активным чтением/извлечением →
/// InvalidOperationException (аренда через AcquireDocument).
/// </summary>
public sealed partial class AttachmentStore : IDisposable, IChatImageStore, IImageCapabilityCache
{
    private const string LogCategory = "Attach";
    private const string SegmentsFile = "segments.json";
    private const string TempDirName = "_tmp";
    private const string DocIdPrefix = "doc-";

    private static readonly JsonSerializerOptions SegJsonOpts = new() { WriteIndented = false };

    private readonly string _root;
    private readonly string _tmp;
    private readonly SqliteConnection _db;
    private readonly LoggingService? _log;
    private readonly object _gate = new();

    // documentId, сейчас занятые чтением/извлечением — DeleteDocument отклоняется.
    private readonly HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public AttachmentStore(string appDataDir, LoggingService? log = null)
    {
        _log = log;
        _root = Path.Combine(appDataDir, "attachments");
        _tmp = Path.Combine(_root, TempDirName);
        Directory.CreateDirectory(_tmp);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(appDataDir, "attachments.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        using (var pragma = _db.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }

        Migrate();
        RecoverOnStartup();
    }

    /// <summary>Корневой каталог копий (attachments\).</summary>
    public string RootDirectory => _root;

    // ---------------- схема ----------------

    /// <summary>
    /// Идемпотентная миграция по user_version: v1 — документы/связи/fact_jobs,
    /// v2 — таблицы изображений и кэша возможностей. user_version повышается
    /// только после успешного создания всех таблиц шага; повторный запуск на
    /// актуальной базе (>=2) ничего не делает, оборванная миграция повторяется.
    /// </summary>
    private void Migrate()
    {
        lock (_gate)
        {
            var v = GetUserVersion();
            if (v < 2)
            {
                // v1 (или чинка базы, где версия слетела при существующих таблицах —
                // CREATE IF NOT EXISTS идемпотентен и ничего не портит).
                using var cmd = _db.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS documents(
                        document_id  TEXT PRIMARY KEY,
                        display_name TEXT NOT NULL,
                        extension    TEXT NOT NULL,
                        kind         TEXT NOT NULL,
                        size_bytes   INTEGER NOT NULL,
                        sha256       TEXT NOT NULL UNIQUE,
                        created_utc  TEXT NOT NULL,
                        char_count   INTEGER NOT NULL DEFAULT 0,
                        page_count   INTEGER NOT NULL DEFAULT 0,
                        warnings     TEXT NOT NULL DEFAULT '[]',
                        page_stats   TEXT NULL,
                        deleted      INTEGER NOT NULL DEFAULT 0);

                    CREATE TABLE IF NOT EXISTS meeting_documents(
                        document_id TEXT NOT NULL REFERENCES documents(document_id),
                        meeting_id  TEXT NOT NULL,
                        state       TEXT NOT NULL DEFAULT 'Active',
                        added_utc   TEXT NOT NULL,
                        PRIMARY KEY(document_id, meeting_id));

                    CREATE TABLE IF NOT EXISTS fact_jobs(
                        id               INTEGER PRIMARY KEY,
                        document_id      TEXT NOT NULL,
                        meeting_id       TEXT NOT NULL,
                        status           TEXT NOT NULL,
                        chars_sent       INTEGER NOT NULL DEFAULT 0,
                        requests_planned INTEGER NOT NULL DEFAULT 0,
                        requests_done    INTEGER NOT NULL DEFAULT 0,
                        facts_saved      INTEGER NOT NULL DEFAULT 0,
                        facts_pending    INTEGER NOT NULL DEFAULT 0,
                        error            TEXT NULL,
                        provider_id      TEXT NOT NULL DEFAULT '',
                        model            TEXT NOT NULL DEFAULT '',
                        scope            TEXT NOT NULL DEFAULT '',
                        verify_mode      TEXT NOT NULL DEFAULT '',
                        created_utc      TEXT NOT NULL);

                    CREATE INDEX IF NOT EXISTS idx_documents_sha256
                        ON documents(sha256);
                    CREATE INDEX IF NOT EXISTS idx_meeting_documents_meeting
                        ON meeting_documents(meeting_id);
                    CREATE INDEX IF NOT EXISTS idx_fact_jobs_doc
                        ON fact_jobs(document_id, meeting_id, id);
                    """;
                cmd.ExecuteNonQuery();

                // v2 — изображения чата и кэш возможностей моделей.
                using var cmd2 = _db.CreateCommand();
                cmd2.CommandText = """
                    CREATE TABLE IF NOT EXISTS chat_images(
                        image_id     TEXT PRIMARY KEY,
                        file_name    TEXT NOT NULL,
                        mime         TEXT NOT NULL,
                        width        INTEGER NOT NULL,
                        height       INTEGER NOT NULL,
                        byte_size    INTEGER NOT NULL,
                        sha256       TEXT NOT NULL,
                        state        TEXT NOT NULL DEFAULT 'Ready',
                        original_path TEXT NOT NULL,
                        preview_path TEXT NULL,
                        send_path    TEXT NULL,
                        send_mime    TEXT NULL,
                        send_width   INTEGER NULL,
                        send_height  INTEGER NULL,
                        send_byte_size INTEGER NULL,
                        created_utc  TEXT NOT NULL);

                    CREATE TABLE IF NOT EXISTS message_image_links(
                        image_id   TEXT NOT NULL REFERENCES chat_images(image_id),
                        meeting_id TEXT NOT NULL,
                        message_id TEXT NOT NULL,
                        seq        INTEGER NOT NULL,
                        PRIMARY KEY(image_id, message_id));
                    CREATE INDEX IF NOT EXISTS idx_mil_message
                        ON message_image_links(meeting_id, message_id, seq);

                    CREATE TABLE IF NOT EXISTS draft_images(
                        meeting_id TEXT NOT NULL,
                        image_id   TEXT NOT NULL REFERENCES chat_images(image_id),
                        seq        INTEGER NOT NULL,
                        PRIMARY KEY(meeting_id, image_id));

                    CREATE TABLE IF NOT EXISTS image_capability_cache(
                        cache_key   TEXT PRIMARY KEY,
                        state       TEXT NOT NULL,
                        source      TEXT NOT NULL,
                        checked_utc TEXT NOT NULL,
                        note        TEXT NULL,
                        key_json    TEXT NULL);

                    CREATE TABLE IF NOT EXISTS catalog_refresh(
                        scope       TEXT PRIMARY KEY,
                        refreshed_utc TEXT NOT NULL);
                    """;
                cmd2.ExecuteNonQuery();
                SetUserVersion(2);
            }

            if (v < 3)
            {
                // v3 — метрика страниц документа (DocumentPageStats JSON):
                // успешный OCR — не ошибка, неполный прогон виден по номерам.
                if (!HasColumnLocked("documents", "page_stats"))
                {
                    using var cmd3 = _db.CreateCommand();
                    cmd3.CommandText = "ALTER TABLE documents ADD COLUMN page_stats TEXT NULL;";
                    cmd3.ExecuteNonQuery();
                }
                SetUserVersion(3);
            }

            if (v < 4)
            {
                // v4 — отпечаток условий извлечения (ExtractionFingerprint): по нему
                // повторный импорт того же содержимого переиспользует сегменты вместо
                // повторного OCR. У существующих строк NULL — прочитаются один раз.
                if (!HasColumnLocked("documents", "extract_key"))
                {
                    using var cmd4 = _db.CreateCommand();
                    cmd4.CommandText = "ALTER TABLE documents ADD COLUMN extract_key TEXT NULL;";
                    cmd4.ExecuteNonQuery();
                }
                SetUserVersion(4);
            }
        }
    }

    /// <summary>PRAGMA user_version (0 у свежей БД). Вызывается под _gate.</summary>
    private int GetUserVersion()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }


    /// <summary>Есть ли колонка у таблицы (под _gate; для идемпотентной миграции).</summary>
    private bool HasColumnLocked(string table, string column)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
    /// <summary>Выставить PRAGMA user_version после успешного шага миграции. Под _gate.</summary>
    private void SetUserVersion(int version)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Восстановление после некорректного завершения: остатки temp-каталога,
    /// сиротские doc-* каталоги без строки в documents и img-* каталоги без
    /// строки в chat_images (crash между move и INSERT), зависшие задания
    /// извлечения (Queued/Running → Interrupted). Чужие каталоги в attachments\
    /// и attachments\images не начинаются на img- — не трогаются.
    /// </summary>
    private void RecoverOnStartup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_tmp))
                TryDelete(f);
        }
        catch (Exception ex) { _log?.Warn($"recover: temp-каталог недоступен: {ex.Message}", LogCategory); }

        lock (_gate)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(_root, DocIdPrefix + "*"); }
            catch (Exception ex)
            {
                _log?.Warn($"recover: каталог вложений недоступен: {ex.Message}", LogCategory);
                dirs = [];
            }

            foreach (var dir in dirs)
            {
                var id = Path.GetFileName(dir);
                if (DocumentIdUsedLocked(id)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _log?.Info($"recover: удалён сиротский каталог {id} без строки в documents", LogCategory);
                }
                catch (Exception ex)
                {
                    _log?.Warn($"recover: не удалось удалить сиротский каталог {id}: {ex.Message}", LogCategory);
                }
            }

            // img-<guidN> каталоги без строки в chat_images — тот же сценарий
            // crash-между-move-и-INSERT, что и у doc-*.
            string[] imgDirs;
            try { imgDirs = Directory.GetDirectories(ImagesRootDir, ImageIdPrefix + "*"); }
            catch (DirectoryNotFoundException) { imgDirs = []; }
            catch (Exception ex)
            {
                _log?.Warn($"recover: каталог изображений недоступен: {ex.Message}", LogCategory);
                imgDirs = [];
            }

            foreach (var dir in imgDirs)
            {
                var id = Path.GetFileName(dir);
                if (ImageRowExistsLocked(id)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    _log?.Info($"recover: удалён сиротский каталог {id} без строки в chat_images", LogCategory);
                }
                catch (Exception ex)
                {
                    _log?.Warn($"recover: не удалось удалить сиротский каталог {id}: {ex.Message}", LogCategory);
                }
            }
        }

        MarkInterruptedJobs();
    }

    /// <summary>Зависшие задания извлечения при старте: Queued/Running → Interrupted.</summary>
    public void MarkInterruptedJobs()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE fact_jobs SET status='Interrupted'
                WHERE status IN ('Queued','Running');
                SELECT changes();
                """;
            var n = Convert.ToInt32(cmd.ExecuteScalar());
            if (n > 0)
                _log?.Warn($"recover: {n} fact_jobs помечено Interrupted (процесс умер в работе)", LogCategory);
        }
    }

    // ---------------- импорт / копии ----------------

    /// <summary>
    /// Импорт документа: потоковая копия source → temp (попутно sha256) → атомарный
    /// File.Move → запись в documents. Дедуп по sha256 содержимого: совпадение с
    /// живым документом возвращает существующий StoredDocument без повторного
    /// копирования. Удалённый/недоступный документ с тем же sha256 реимпортируется
    /// в тот же documentId (отметка стабильна). При ошибке temp и сиротский
    /// каталог чистятся.
    /// </summary>
    public async Task<StoredDocument> ImportAsync(
        string sourcePath, string displayName, DocumentKind kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        ThrowIfDisposed();

        var tmpPath = Path.Combine(_tmp, "imp-" + Guid.NewGuid().ToString("N") + ".part");
        string sha;
        long size;
        try
        {
            await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var dst = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buf = new byte[81920];
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                size = src.Length;
            }

            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(extension))
                extension = DefaultExtension(kind);

            string? docId = null;
            try
            {
                lock (_gate)
                {
                    var existing = FindByShaLocked(sha);
                    // Дедуп: живой документ с файлом на диске — возвращаем как есть.
                    if (existing is { Deleted: false } && File.Exists(SourcePathFor(existing)))
                        return existing;

                    docId = existing?.DocumentId ?? AllocateDocumentIdLocked(sha);
                    var dir = Path.Combine(_root, docId);
                    Directory.CreateDirectory(dir);
                    var dstPath = Path.Combine(dir, "source" + extension);
                    if (File.Exists(dstPath))
                        File.Delete(dstPath);
                    File.Move(tmpPath, dstPath);

                    // segments.json переживает реимпорт — сохраняем счётчики,
                    // иначе (байтов не было / файл был удалён) сбрасываем в 0.
                    var hasSegments = File.Exists(Path.Combine(dir, SegmentsFile));
                    if (existing != null)
                        UpdateAfterImportLocked(docId, displayName, extension, kind, size, hasSegments);
                    else
                        InsertDocumentLocked(docId, displayName, extension, kind, size, sha);

                    return GetDocumentLocked(docId)
                           ?? throw new InvalidOperationException($"documents: строка {docId} потеряна после записи");
                }
            }
            catch
            {
                if (docId != null)
                    TryDeleteOrphanDir(Path.Combine(_root, docId));
                throw;
            }
        }
        finally
        {
            TryDelete(tmpPath);
        }
    }

    /// <summary>
    /// Сохранить извлечённые сегменты (segments.json, атомарно через temp→move)
    /// и обновить счётчики документа. warnings != null → перезаписать список
    /// предупреждений извлечения (скрытые листы, OCR и т.п.).
    /// <paramref name="extractKey"/> — отпечаток доведённого до конца прогона
    /// (<see cref="ExtractionFingerprint"/>): по нему повторный импорт решает,
    /// можно ли переиспользовать сегменты. null (частичный прогон/несовпадение)
    /// сбрасывает отпечаток — такие сегменты не переиспользуются.
    /// </summary>
    public async Task SaveSegmentsAsync(string documentId, IReadOnlyList<DocSegment> segs,
        int charCount, int pageCount, CancellationToken ct, IReadOnlyList<string>? warnings = null,
        DocumentPageStats? pageStats = null, string? extractKey = null)
    {
        ArgumentNullException.ThrowIfNull(segs);
        ThrowIfDisposed();

        var tmpPath = Path.Combine(_tmp, "seg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var rows = segs.Select(s => new SegmentRow(s.Text, s.Anchor.Kind, s.Anchor.Label, s.Order)).ToList();
            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(rows, SegJsonOpts), ct)
                .ConfigureAwait(false);

            lock (_gate)
            {
                var doc = GetDocumentLocked(documentId)
                          ?? throw new InvalidOperationException($"документ {documentId} не найден в хранилище");
                var dir = Path.Combine(_root, documentId);
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, SegmentsFile);
                if (File.Exists(dst))
                    File.Delete(dst);
                File.Move(tmpPath, dst);

                using var cmd = _db.CreateCommand();
                cmd.CommandText = warnings == null
                    ? "UPDATE documents SET char_count=$cc, page_count=$pc, page_stats=$ps, extract_key=$ek WHERE document_id=$id;"
                    : "UPDATE documents SET char_count=$cc, page_count=$pc, warnings=$w, page_stats=$ps, extract_key=$ek WHERE document_id=$id;";
                cmd.Parameters.AddWithValue("$cc", charCount);
                cmd.Parameters.AddWithValue("$pc", pageCount);
                cmd.Parameters.AddWithValue("$ps",
                    pageStats is { HasData: true } ? JsonSerializer.Serialize(pageStats) : DBNull.Value);
                cmd.Parameters.AddWithValue("$ek", (object?)extractKey ?? DBNull.Value);
                if (warnings != null)
                    cmd.Parameters.AddWithValue("$w", JsonSerializer.Serialize(warnings));
                cmd.Parameters.AddWithValue("$id", doc.DocumentId);
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            TryDelete(tmpPath);
        }
    }

    /// <summary>Сегменты документа из segments.json; файла нет/битый → пустой список.</summary>
    public async Task<IReadOnlyList<DocSegment>> LoadSegmentsAsync(string documentId, CancellationToken ct)
    {
        ThrowIfDisposed();
        var path = Path.Combine(_root, documentId, SegmentsFile);
        if (!File.Exists(path))
            return [];
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var rows = JsonSerializer.Deserialize<List<SegmentRow>>(json, SegJsonOpts);
            if (rows == null) return [];
            return rows.Select(r => new DocSegment(r.Text, new TextAnchor(r.Kind, r.Label), r.Order))
                       .OrderBy(s => s.Order)
                       .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Warn($"segments.json документа {documentId} не читается: {ex.Message}", LogCategory);
            return [];
        }
    }

    // ---- План извлечения фактов (адаптивная нарезка; переживает Interrupted) ----

    /// <summary>Элемент плана fact-задания: текст части и флаг окончательного провала
    /// (часть не извлеклась даже на минимальном размере — не повторять бесконечно).</summary>
    public sealed record FactPlanItem(
        [property: System.Text.Json.Serialization.JsonPropertyName("t")] string Text,
        [property: System.Text.Json.Serialization.JsonPropertyName("f")] bool Failed = false,
        // Границы части в склеенном тексте сегментов [Start,End) — «Источник» факта
        // ведёт в место документа, а не в номер части. 0/0 — диапазон не восстановлен
        // (план записан старой версией или часть не нашлась в тексте).
        [property: System.Text.Json.Serialization.JsonPropertyName("s")] int Start = 0,
        [property: System.Text.Json.Serialization.JsonPropertyName("e")] int End = 0);

    private static string FactPlanPath(string root, long jobId, string documentId) =>
        Path.Combine(root, documentId, $"factjob-{jobId}.plan.json");

    /// <summary>План частей fact-задания (factjob-{id}.plan.json рядом с segments.json;
    /// атомарно через temp→move). Null-план удаляет файл — задание завершено.</summary>
    public async Task SaveFactJobPlanAsync(long jobId, string documentId,
        IReadOnlyList<FactPlanItem>? plan, CancellationToken ct)
    {
        ThrowIfDisposed();
        var dst = FactPlanPath(_root, jobId, documentId);
        if (plan == null)
        {
            TryDelete(dst);
            return;
        }
        var tmpPath = Path.Combine(_tmp, "plan-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(plan.ToList(), SegJsonOpts), ct)
                .ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
                File.Delete(dst);
            File.Move(tmpPath, dst);
        }
        finally
        {
            TryDelete(tmpPath);
        }
    }

    /// <summary>План частей fact-задания; файла нет/битый → null (нарезка заново).</summary>
    public async Task<IReadOnlyList<FactPlanItem>?> LoadFactJobPlanAsync(
        long jobId, string documentId, CancellationToken ct)
    {
        ThrowIfDisposed();
        var path = FactPlanPath(_root, jobId, documentId);
        if (!File.Exists(path))
            return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<FactPlanItem>>(json, SegJsonOpts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Warn($"план fact-job {jobId} не читается: {ex.Message}", LogCategory);
            return null;
        }
    }

    /// <summary>Карточка документа по id (включая Deleted=true).</summary>
    public StoredDocument? Get(string documentId)
    {
        lock (_gate)
            return GetDocumentLocked(documentId);
    }

    /// <summary>Путь локальной побайтовой копии (может отсутствовать — см. ReadSourceBytes).</summary>
    public string GetSourcePath(StoredDocument doc) => SourcePathFor(doc);

    /// <summary>
    /// Исходные байты копии («Открыть», «Сохранить как…»). Копии нет на диске →
    /// FileNotFoundException — вызывающий показывает «Файл недоступен».
    /// </summary>
    public byte[] ReadSourceBytes(StoredDocument doc)
    {
        var path = SourcePathFor(doc);
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FileNotFoundException("локальная копия документа отсутствует", path, ex);
        }
    }

    // ---------------- связи с встречами ----------------

    /// <summary>Привязать документ к встрече журнала (идемпотентно: дубликата не будет).</summary>
    public Task LinkToMeetingAsync(string documentId, string meetingId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _ = GetDocumentLocked(documentId)
                ?? throw new InvalidOperationException($"документ {documentId} не найден в хранилище");
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO meeting_documents(document_id, meeting_id, state, added_utc)
                VALUES($d, $m, 'Active', $t);
                """;
            cmd.Parameters.AddWithValue("$d", documentId);
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.Parameters.AddWithValue("$t", Iso(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <summary>Убрать документ из контекста встречи. Копия и прочие связи остаются.</summary>
    public Task UnlinkFromMeetingAsync(string documentId, string meetingId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM meeting_documents WHERE document_id=$d AND meeting_id=$m;";
            cmd.Parameters.AddWithValue("$d", documentId);
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <summary>Активные документы встречи журнала (по времени добавления).</summary>
    public IReadOnlyList<MeetingAttachment> ListMeetingDocuments(string meetingId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT {DocColumns("d")}, m.meeting_id, m.added_utc
                FROM meeting_documents m JOIN documents d ON d.document_id = m.document_id
                WHERE m.meeting_id = $m
                ORDER BY m.added_utc, m.rowid;
                """;
            cmd.Parameters.AddWithValue("$m", meetingId);
            var list = new List<MeetingAttachment>();
            using var r = cmd.ExecuteReader();
            // m.meeting_id/m.added_utc идут сразу ПОСЛЕ колонок документа —
            // ordinals DocCols.Length/+1. Жёсткие 11/12 ломались, когда DocCols
            // вырос на page_stats («NULL at ordinal 11»): meeting_id читался
            // из page_stats.
            var meetingCol = DocCols.Length;
            while (r.Read())
                list.Add(new MeetingAttachment(ReadDocument(r),
                    r.GetString(meetingCol), ParseIso(r.GetString(meetingCol + 1))));
            return list;
        }
    }

    /// <summary>Число документов у одной встречи.</summary>
    public int CountMeetingDocuments(string meetingId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM meeting_documents WHERE meeting_id=$m;";
            cmd.Parameters.AddWithValue("$m", meetingId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Батч-счётчики для списка встреч: meetingId → число файлов (одна выборка).</summary>
    public IReadOnlyDictionary<string, int> CountByMeeting()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT meeting_id, COUNT(*) FROM meeting_documents GROUP BY meeting_id;";
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                map[r.GetString(0)] = r.GetInt32(1);
            return map;
        }
    }

    /// <summary>Все встречи, к которым привязан документ (для DeletePreview/истории).</summary>
    public IReadOnlyList<string> ListMeetingIdsFor(string documentId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT meeting_id FROM meeting_documents WHERE document_id=$d ORDER BY added_utc;";
            cmd.Parameters.AddWithValue("$d", documentId);
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>Документ уже привязан к указанной встрече (runner: пропуск повторной линковки/извлечения).</summary>
    public bool IsLinkedToMeeting(string documentId, string meetingId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM meeting_documents WHERE document_id=$d AND meeting_id=$m;";
            cmd.Parameters.AddWithValue("$d", documentId);
            cmd.Parameters.AddWithValue("$m", meetingId);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    // ---------------- удаление ----------------

    /// <summary>
    /// Предпросмотр удаления: число связанных встреч и оценка уже извлечённых
    /// фактов (сумма по fact_jobs — attachments.db не читает memory.db).
    /// </summary>
    public Task<DeletePreview> PreviewDeleteAsync(string documentId)
    {
        lock (_gate)
        {
            int meetings, facts;
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM meeting_documents WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                meetings = Convert.ToInt32(cmd.ExecuteScalar());
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COALESCE(SUM(facts_saved + facts_pending), 0) FROM fact_jobs WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                facts = Convert.ToInt32(cmd.ExecuteScalar());
            }
            return Task.FromResult(new DeletePreview(meetings, facts));
        }
    }

    /// <summary>
    /// Постоянное удаление: связи стираются, строка documents остаётся с
    /// Deleted=true (отметка «Файл удалён» у старых ответов/фактов), каталог копии
    /// сносится с диска. Активное чтение/извлечение (AcquireDocument) →
    /// InvalidOperationException.
    /// </summary>
    public Task DeleteDocumentAsync(string documentId)
    {
        lock (_gate)
        {
            if (_inUse.Contains(documentId))
                throw new InvalidOperationException(
                    $"документ {documentId} сейчас используется — удаление отклонено");

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM meeting_documents WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "UPDATE documents SET deleted=1, extract_key=NULL WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                cmd.ExecuteNonQuery();
            }
            TryDeleteOrphanDir(Path.Combine(_root, documentId));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Удалить документ ТОЛЬКО если он ни к чему не привязан (0 связей со
    /// встречами и 0 фактов) — «временная копия этой операции» пикера:
    /// отмена/убрать-до-Commit не должны оставлять постоянную запись.
    /// Проверка и удаление атомарны под _gate. true — удалён, false — привязан
    /// (запись сохранена) или не существует.
    /// </summary>
    public Task<bool> DeleteIfOrphanedAsync(string documentId)
    {
        lock (_gate)
        {
            if (_inUse.Contains(documentId)) return Task.FromResult(false);
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM meeting_documents WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return Task.FromResult(false);
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT COALESCE(SUM(facts_saved + facts_pending), 0) FROM fact_jobs WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return Task.FromResult(false);
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM meeting_documents WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "UPDATE documents SET deleted=1, extract_key=NULL WHERE document_id=$d;";
                cmd.Parameters.AddWithValue("$d", documentId);
                cmd.ExecuteNonQuery();
            }
            TryDeleteOrphanDir(Path.Combine(_root, documentId));
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Аренда документа против гонки с DeleteDocument (длинное чтение/извлечение).
    /// Вызывающий обязан Dispose; повторная аренда того же id → InvalidOperationException.
    /// </summary>
    public IDisposable AcquireDocument(string documentId)
    {
        lock (_gate)
        {
            if (!_inUse.Add(documentId))
                throw new InvalidOperationException($"документ {documentId} уже используется");
            return new DocumentLease(this, documentId);
        }
    }

    private void ReleaseDocument(string documentId)
    {
        lock (_gate)
            _inUse.Remove(documentId);
    }

    private sealed class DocumentLease(AttachmentStore store, string documentId) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                store.ReleaseDocument(documentId);
        }
    }

    // ---------------- задания извлечения фактов ----------------

    /// <summary>Поставить задание извлечения в очередь. Возвращает id новой строки.</summary>
    public Task<long> EnqueueFactJobAsync(FactJob job)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO fact_jobs(document_id, meeting_id, status, chars_sent,
                    requests_planned, requests_done, facts_saved, facts_pending,
                    error, provider_id, model, scope, verify_mode, created_utc)
                VALUES($d, $m, $st, $cs, $rp, $rd, $fs, $fp, $err,
                    $prov, $model, $scope, $vm, $created);
                SELECT last_insert_rowid();
                """;
            BindJob(cmd, job);
            return Task.FromResult((long)cmd.ExecuteScalar()!);
        }
    }

    /// <summary>Перезаписать задание целиком (прогресс/статус/ошибка).</summary>
    public void UpdateFactJob(FactJob job)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE fact_jobs SET status=$st, chars_sent=$cs, requests_planned=$rp,
                    requests_done=$rd, facts_saved=$fs, facts_pending=$fp, error=$err,
                    provider_id=$prov, model=$model, scope=$scope, verify_mode=$vm
                WHERE id=$id;
                """;
            BindJob(cmd, job);
            cmd.Parameters.AddWithValue("$id", job.Id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Последнее задание по паре документ×встреча (для статуса в строке файла).</summary>
    public FactJob? LatestJobFor(string documentId, string meetingId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT {JobColumns} FROM fact_jobs
                WHERE document_id=$d AND meeting_id=$m
                ORDER BY id DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$d", documentId);
            cmd.Parameters.AddWithValue("$m", meetingId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadJob(r) : null;
        }
    }

    /// <summary>Задания по фильтру статуса (null → все), свежие сверху.</summary>
    public IReadOnlyList<FactJob> ListFactJobs(FactJobStatus? status = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = status == null
                ? $"SELECT {JobColumns} FROM fact_jobs ORDER BY id DESC;"
                : $"SELECT {JobColumns} FROM fact_jobs WHERE status=$st ORDER BY id DESC;";
            if (status != null)
                cmd.Parameters.AddWithValue("$st", status.Value.ToString());
            var list = new List<FactJob>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadJob(r));
            return list;
        }
    }

    // ---------------- internals ----------------

    private const string JobColumns = """
        id, document_id, meeting_id, status, chars_sent, requests_planned,
        requests_done, facts_saved, facts_pending, error, provider_id, model,
        scope, verify_mode, created_utc
        """;

    private static readonly string[] DocCols =
        ["document_id", "display_name", "extension", "kind", "size_bytes", "sha256",
         "created_utc", "char_count", "page_count", "warnings", "deleted", "page_stats",
         "extract_key"];

    private static string DocColumns(string alias) =>
        string.Join(", ", DocCols.Select(c =>
            string.IsNullOrEmpty(alias) ? c : alias + "." + c));

    private StoredDocument? GetDocumentLocked(string documentId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText =
            $"SELECT {DocColumns("")} FROM documents WHERE document_id=$id;";
        cmd.Parameters.AddWithValue("$id", documentId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDocument(r) : null;
    }

    private StoredDocument? FindByShaLocked(string sha256)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT {DocColumns("")} FROM documents WHERE sha256=$sha LIMIT 1;";
        cmd.Parameters.AddWithValue("$sha", sha256);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDocument(r) : null;
    }

    private bool DocumentIdUsedLocked(string documentId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents WHERE document_id=$id;";
        cmd.Parameters.AddWithValue("$id", documentId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// doc-&lt;8 hex sha256&gt;; при коллизии префикса (другой контент, тот же
    /// префикс) расширяем до 12/16/… hex — id стабилен для sha256.
    /// </summary>
    private string AllocateDocumentIdLocked(string sha256)
    {
        for (var len = 8; len <= sha256.Length; len += 4)
        {
            var cand = DocIdPrefix + sha256[..len];
            if (!DocumentIdUsedLocked(cand) && !Directory.Exists(Path.Combine(_root, cand)))
                return cand;
        }
        throw new InvalidOperationException("sha256 исчерпан для document_id");
    }

    private void InsertDocumentLocked(string docId, string displayName, string extension,
        DocumentKind kind, long size, string sha)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents(document_id, display_name, extension, kind,
                size_bytes, sha256, created_utc)
            VALUES($id, $name, $ext, $kind, $size, $sha, $created);
            """;
        cmd.Parameters.AddWithValue("$id", docId);
        cmd.Parameters.AddWithValue("$name", displayName);
        cmd.Parameters.AddWithValue("$ext", extension);
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.Parameters.AddWithValue("$created", Iso(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    private void UpdateAfterImportLocked(string docId, string displayName, string extension,
        DocumentKind kind, long size, bool hasSegments)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE documents SET display_name=$name, extension=$ext, kind=$kind,
                size_bytes=$size, deleted=0,
                char_count = CASE WHEN $hasSeg = 0 THEN 0 ELSE char_count END,
                page_count = CASE WHEN $hasSeg = 0 THEN 0 ELSE page_count END,
                warnings   = CASE WHEN $hasSeg = 0 THEN '[]' ELSE warnings END,
                extract_key = CASE WHEN $hasSeg = 0 THEN NULL ELSE extract_key END
            WHERE document_id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", docId);
        cmd.Parameters.AddWithValue("$name", displayName);
        cmd.Parameters.AddWithValue("$ext", extension);
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$hasSeg", hasSegments ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private void BindJob(SqliteCommand cmd, FactJob job)
    {
        cmd.Parameters.AddWithValue("$d", job.DocumentId);
        cmd.Parameters.AddWithValue("$m", job.MeetingId);
        cmd.Parameters.AddWithValue("$st", job.Status.ToString());
        cmd.Parameters.AddWithValue("$cs", job.CharsSent);
        cmd.Parameters.AddWithValue("$rp", job.RequestsPlanned);
        cmd.Parameters.AddWithValue("$rd", job.RequestsDone);
        cmd.Parameters.AddWithValue("$fs", job.FactsSaved);
        cmd.Parameters.AddWithValue("$fp", job.FactsPending);
        cmd.Parameters.AddWithValue("$err", (object?)job.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prov", job.ProviderId);
        cmd.Parameters.AddWithValue("$model", job.Model);
        cmd.Parameters.AddWithValue("$scope", job.Scope);
        cmd.Parameters.AddWithValue("$vm", job.VerifyMode);
        cmd.Parameters.AddWithValue("$created", Iso(job.CreatedUtc));
    }

    private static StoredDocument ReadDocument(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        Enum.Parse<DocumentKind>(r.GetString(3)),
        r.GetInt64(4), r.GetString(5), ParseIso(r.GetString(6)),
        r.GetInt32(7), r.GetInt32(8), ParseWarnings(r.GetString(9)),
        r.GetInt32(10) != 0,
        r.IsDBNull(11) ? null : ParsePageStats(r.GetString(11)),
        r.IsDBNull(12) ? null : r.GetString(12));

    private static DocumentPageStats? ParsePageStats(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<DocumentPageStats>(json);
            return s is { HasData: true } ? s : null;
        }
        catch (JsonException) { return null; }
    }

    private static FactJob ReadJob(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2),
        Enum.Parse<FactJobStatus>(r.GetString(3)),
        r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
        r.GetInt32(7), r.GetInt32(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13),
        ParseIso(r.GetString(14)));

    private static IReadOnlyList<string> ParseWarnings(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private string SourcePathFor(StoredDocument doc) =>
        Path.Combine(_root, doc.DocumentId, "source" + doc.Extension);

    private static string DefaultExtension(DocumentKind kind) => kind switch
    {
        DocumentKind.Txt => ".txt",
        DocumentKind.Markdown => ".md",
        DocumentKind.Docx => ".docx",
        DocumentKind.Xlsx => ".xlsx",
        DocumentKind.Pptx => ".pptx",
        DocumentKind.Pdf => ".pdf",
        _ => ".bin"
    };

    private void TryDeleteOrphanDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _log?.Warn($"не удалось удалить каталог {dir}: {ex.Message}", LogCategory); }
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log?.Warn($"не удалось удалить temp {path}: {ex.Message}", LogCategory); }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AttachmentStore));
    }

    private static string Iso(DateTime dt)
        => dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string s)
        => DateTime.Parse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces);

    // DTO сегмента в segments.json (поля сокращены: файл — внутренний формат).
    private sealed record SegmentRow(
        [property: System.Text.Json.Serialization.JsonPropertyName("t")] string Text,
        [property: System.Text.Json.Serialization.JsonPropertyName("k")] string Kind,
        [property: System.Text.Json.Serialization.JsonPropertyName("l")] string Label,
        [property: System.Text.Json.Serialization.JsonPropertyName("o")] int Order);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }
}
