using System.Globalization;
using Microsoft.Data.Sqlite;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Memory.Storage;

/// <summary>
/// <see cref="IMemoryStore"/> поверх одной <see cref="SqliteConnection"/> + lock
/// (по контракту: простота важнее пула). DateTime храним как ISO-8601 UTC TEXT
/// (sortable, roundtrip), enum'ы — именами, embedding-вектора — little-endian BLOB.
/// FTS5-индекс синхронизируется триггерами; если FTS недоступен — LIKE-fallback.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private const string Category = MemoryDbMigrator.LogCategory;

    private static readonly char[] WordSeparators =
        [' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}',
         '"', '\'', '«', '»', '—', '–', '/', '\\', '|', '*', '+', '=', '<', '>', '@', '#',
         '$', '%', '^', '&', '~', '`'];

    // Покрытие запроса вместо голого bm25: смысловым считается слово от 2 букв
    // (короткие термины «QA», «AI», «ML», «ID» — тема, а не шум); служебные
    // слова отсекает QueryText (точное совпадение + опечатки по стему).
    // Стем fallback не короче 4 букв и не чаще ~1/8 корпуса.
    private const int MinContentWordLen = 2;
    private const int StemMinLen = 4;
    private const int MinTermDf = 25;
    private const int MaxTermDfDivisor = 8;

    /// <summary>Мета-слова памяти: в вопросе «есть доступ к памяти? поищи sensor
    /// supply» они описывают способ поиска, а не тему. Убираются до MATCH,
    /// чтобы не приносить FAQ-факты о самой памяти в топ. Запрос только из
    /// таких слов возвращается через fallback QueryText (поведение не ломается).</summary>
    private static readonly HashSet<string> MemoryScaffolding = new(StringComparer.Ordinal)
    {
        "память", "памяти", "памятью", "памятях",
        "доступ", "доступа", "доступе", "доступом", "доступен", "доступна",
        "доступно", "доступны", "доступность",
        "запомни", "запомнил", "запомнила", "запомнить", "помнишь", "помню",
        "помнит", "помнят", "вспомни", "вспомнить", "вспоминать",
        "знаешь", "знаем", "знают", "знание", "знания", "знаний",
        "факт", "факты", "фактов", "фактах", "фактам",
        "информация", "информации", "информацию", "сведения", "сведений",
        "memory", "memories",
    };

    private readonly SqliteConnection _db;
    private readonly LoggingService? _log;
    private readonly object _gate = new();
    private bool _disposed;

    // Инкрементится на каждой мутации mem_item/mem_embedding — MemoryRecallService
    // кэширует embeddings+items по этой ревизии (бенч: LoadEmbeddings ~170мс на 50k).
    private long _dataRevision;

    public long DataRevision => Interlocked.Read(ref _dataRevision);
    private void BumpRevision() => Interlocked.Increment(ref _dataRevision);

    public SqliteMemoryStore(string dbPath, LoggingService? log = null)
    {
        DbPath = dbPath;
        _log = log;

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // одно соединение на стор: пул только держит handle на файл
        }.ToString());
        _db.Open();
        using (var pragma = _db.CreateCommand())
        {
            pragma.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
    }

    public string DbPath { get; }

    /// <summary>FTS5 probe заполняется в <see cref="Migrate"/>. false → LIKE-fallback.</summary>
    public bool FtsAvailable { get; private set; }

    public void Migrate()
    {
        lock (_gate)
        {
            FtsAvailable = MemoryDbMigrator.Migrate(_db, msg => _log?.Warn(msg, Category));
            if (!FtsAvailable)
                _log?.Warn("FTS5 недоступен: SearchFts работает через LIKE-fallback", Category);
            EnsureAttachmentProvenanceColumns();
        }
    }

    /// <summary>
    /// +4 nullable TEXT-колонки provenance чат-вложений (фича «файлы в контексте»).
    /// Идемпотентно: SQLite не умеет ADD COLUMN IF NOT EXISTS — проверяем
    /// PRAGMA table_info. Старые БД (v1/v2) добирают колонки здесь, свежая
    /// v1-схема создаёт их сразу — обе дороги сходятся.
    /// </summary>
    private void EnsureAttachmentProvenanceColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = _db.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(mem_item);";
            using var r = info.ExecuteReader();
            while (r.Read())
                existing.Add(r.GetString(1));
        }

        foreach (var col in new[] { "source_doc_id", "source_doc_name",
                                    "source_doc_sha256", "source_anchor" })
        {
            if (existing.Contains(col)) continue;
            using var alter = _db.CreateCommand();
            alter.CommandText = $"ALTER TABLE mem_item ADD COLUMN {col} TEXT NULL;";
            alter.ExecuteNonQuery();
            _log?.Info($"migrate: mem_item + колонка {col} (provenance вложений)", Category);
        }
    }

    // ---------------- meetings / segments ----------------

    public long UpsertMeeting(MemoryMeeting m)
    {
        lock (_gate)
        {
            if (m.Id > 0)
            {
                using var upd = _db.CreateCommand();
                upd.CommandText = """
                    UPDATE mem_meeting SET title=$title, project_id=$pid, started_utc=$start,
                        ended_utc=$end, memory_mode=$mode, use_memory=$use, write_memory=$write,
                        summary=$summary, journal_path=$journal, agenda=$agenda
                    WHERE id=$id;
                    """;
                BindMeeting(upd, m);
                upd.Parameters.AddWithValue("$id", m.Id);
                if (upd.ExecuteNonQuery() > 0)
                    return m.Id;
            }

            using var ins = _db.CreateCommand();
            ins.CommandText = """
                INSERT INTO mem_meeting(title, project_id, started_utc, ended_utc, memory_mode,
                    use_memory, write_memory, summary, journal_path, agenda)
                VALUES($title, $pid, $start, $end, $mode, $use, $write, $summary, $journal, $agenda);
                SELECT last_insert_rowid();
                """;
            BindMeeting(ins, m);
            m.Id = (long)ins.ExecuteScalar()!;
            return m.Id;
        }
    }

    private static void BindMeeting(SqliteCommand cmd, MemoryMeeting m)
    {
        cmd.Parameters.AddWithValue("$title", m.Title);
        cmd.Parameters.AddWithValue("$pid", (object?)m.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", Iso(m.StartedAtUtc));
        cmd.Parameters.AddWithValue("$end", (object?)IsoOrNull(m.EndedAtUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", m.MemoryMode);
        cmd.Parameters.AddWithValue("$use", m.UseMemory ? 1 : 0);
        cmd.Parameters.AddWithValue("$write", m.WriteMemory ? 1 : 0);
        cmd.Parameters.AddWithValue("$summary", m.Summary);
        cmd.Parameters.AddWithValue("$journal", m.JournalPath);
        cmd.Parameters.AddWithValue("$agenda", m.Agenda);
    }

    public MemoryMeeting? GetMeeting(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, project_id, started_utc, ended_utc, memory_mode,
                       use_memory, write_memory, summary, journal_path, agenda
                FROM mem_meeting WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadMeeting(r) : null;
        }
    }

    public List<MemoryMeeting> ListMeetings(int limit = 200)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, project_id, started_utc, ended_utc, memory_mode,
                       use_memory, write_memory, summary, journal_path, agenda
                FROM mem_meeting ORDER BY started_utc DESC, id DESC LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<MemoryMeeting>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadMeeting(r));
            return list;
        }
    }

    private static MemoryMeeting ReadMeeting(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Title = r.GetString(1),
        ProjectId = r.IsDBNull(2) ? null : r.GetInt64(2),
        StartedAtUtc = ParseIso(r.GetString(3)),
        EndedAtUtc = r.IsDBNull(4) ? null : ParseIso(r.GetString(4)),
        MemoryMode = r.GetString(5),
        UseMemory = r.GetInt32(6) != 0,
        WriteMemory = r.GetInt32(7) != 0,
        Summary = r.GetString(8),
        JournalPath = r.GetString(9),
        Agenda = r.GetString(10),
    };

    public long AppendSegment(MemorySegment s)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            // seq = max(seq)+1 внутри этой встречи; lock делает автоинкремент атомарным.
            cmd.CommandText = """
                INSERT INTO mem_segment(meeting_id, seq, role, text, speaker, start_ms, end_ms, ts_utc)
                VALUES($mid, COALESCE((SELECT MAX(seq) FROM mem_segment WHERE meeting_id=$mid), 0) + 1,
                       $role, $text, $speaker, $start, $end, $ts);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$mid", s.MeetingId);
            cmd.Parameters.AddWithValue("$role", s.Role);
            cmd.Parameters.AddWithValue("$text", s.Text);
            cmd.Parameters.AddWithValue("$speaker", (object?)s.Speaker ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start", (object?)s.StartMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$end", (object?)s.EndMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", Iso(s.TsUtc));
            s.Id = (long)cmd.ExecuteScalar()!;
            return s.Id;
        }
    }

    public List<MemorySegment> GetSegments(long meetingId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, meeting_id, seq, role, text, speaker, start_ms, end_ms, ts_utc
                FROM mem_segment WHERE meeting_id=$mid ORDER BY seq;
                """;
            cmd.Parameters.AddWithValue("$mid", meetingId);
            var list = new List<MemorySegment>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new MemorySegment
                {
                    Id = r.GetInt64(0),
                    MeetingId = r.GetInt64(1),
                    Seq = r.GetInt64(2),
                    Role = r.GetString(3),
                    Text = r.GetString(4),
                    Speaker = r.IsDBNull(5) ? null : r.GetString(5),
                    StartMs = r.IsDBNull(6) ? null : r.GetInt64(6),
                    EndMs = r.IsDBNull(7) ? null : r.GetInt64(7),
                    TsUtc = ParseIso(r.GetString(8)),
                });
            }
            return list;
        }
    }

    // ---------------- items ----------------

    // Новые колонки — строго в конец (позиционное чтение в ReadItem + score-ordinal
    // в SearchFtsTable/SearchLikeFallback завязаны на этот порядок).
    private const string ItemColumns = """
        id, scope_type, scope_id, type, content, importance, confidence, pinned, status,
        valid_from_utc, valid_until_utc, superseded_by, source_meeting_id, source_segment_id,
        source_start_ms, source_end_ms, speaker, source, created_utc, updated_utc,
        last_confirmed_utc, source_doc_id, source_doc_name, source_doc_sha256, source_anchor
        """;


    private const string ItemInsertColumns = """
        scope_type, scope_id, type, content, importance, confidence, pinned, status,
        valid_from_utc, valid_until_utc, superseded_by, source_meeting_id, source_segment_id,
        source_start_ms, source_end_ms, speaker, source, created_utc, updated_utc,
        last_confirmed_utc, source_doc_id, source_doc_name, source_doc_sha256, source_anchor
        """;

    private const string ItemInsertParams = """
        $scope, $scopeId, $type, $content, $imp, $conf, $pin, $status,
        $vfrom, $vuntil, $supBy, $srcM, $srcS, $srcStart, $srcEnd, $speaker, $src,
        $created, $updated, $confirmed, $srcDocId, $srcDocName, $srcDocSha, $srcAnchor
        """;

    public long InsertItem(MemoryItem it)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (it.CreatedAtUtc == default) it.CreatedAtUtc = now;
            it.UpdatedAtUtc = now;
            if (it.ValidFromUtc == default) it.ValidFromUtc = now;

            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"INSERT INTO mem_item({ItemInsertColumns}) VALUES({ItemInsertParams}); SELECT last_insert_rowid();";
            BindItem(cmd, it, withId: false);
            it.Id = (long)cmd.ExecuteScalar()!;
            BumpRevision();
            return it.Id;
        }
    }

    public void UpdateItem(MemoryItem it)
    {
        lock (_gate)
        {
            it.UpdatedAtUtc = DateTime.UtcNow;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                UPDATE mem_item SET scope_type=$scope, scope_id=$scopeId, type=$type,
                    content=$content, importance=$imp, confidence=$conf, pinned=$pin,
                    status=$status, valid_from_utc=$vfrom, valid_until_utc=$vuntil,
                    superseded_by=$supBy, source_meeting_id=$srcM, source_segment_id=$srcS,
                    source_start_ms=$srcStart, source_end_ms=$srcEnd, speaker=$speaker,
                    source=$src, created_utc=$created, updated_utc=$updated,
                    last_confirmed_utc=$confirmed, source_doc_id=$srcDocId,
                    source_doc_name=$srcDocName, source_doc_sha256=$srcDocSha,
                    source_anchor=$srcAnchor
                WHERE id=$id;
                """;
            BindItem(cmd, it, withId: true);
            cmd.ExecuteNonQuery();
            BumpRevision();
        }
    }

    private static void BindItem(SqliteCommand cmd, MemoryItem it, bool withId)
    {
        if (withId) cmd.Parameters.AddWithValue("$id", it.Id);
        cmd.Parameters.AddWithValue("$scope", it.ScopeType.ToString());
        cmd.Parameters.AddWithValue("$scopeId", (object?)it.ScopeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", it.Type.ToString());
        cmd.Parameters.AddWithValue("$content", it.Content);
        cmd.Parameters.AddWithValue("$imp", it.Importance);
        cmd.Parameters.AddWithValue("$conf", it.Confidence);
        cmd.Parameters.AddWithValue("$pin", it.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", it.Status.ToString());
        cmd.Parameters.AddWithValue("$vfrom", Iso(it.ValidFromUtc));
        cmd.Parameters.AddWithValue("$vuntil", (object?)IsoOrNull(it.ValidUntilUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$supBy", (object?)it.SupersededById ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcM", (object?)it.SourceMeetingId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcS", (object?)it.SourceSegmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcStart", (object?)it.SourceStartMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcEnd", (object?)it.SourceEndMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$speaker", (object?)it.Speaker ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src", it.Source);
        cmd.Parameters.AddWithValue("$created", Iso(it.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$updated", Iso(it.UpdatedAtUtc));
        cmd.Parameters.AddWithValue("$confirmed", (object?)IsoOrNull(it.LastConfirmedAtUtc) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcDocId", (object?)it.SourceDocumentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcDocName", (object?)it.SourceDocName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcDocSha", (object?)it.SourceDocSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$srcAnchor", (object?)it.SourceAnchor ?? DBNull.Value);
    }

    private static MemoryItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        ScopeType = Enum.Parse<MemoryScopeType>(r.GetString(1)),
        ScopeId = r.IsDBNull(2) ? null : r.GetInt64(2),
        Type = Enum.Parse<MemoryItemType>(r.GetString(3)),
        Content = r.GetString(4),
        Importance = r.GetDouble(5),
        Confidence = r.GetDouble(6),
        Pinned = r.GetInt32(7) != 0,
        Status = Enum.Parse<MemoryItemStatus>(r.GetString(8)),
        ValidFromUtc = ParseIso(r.GetString(9)),
        ValidUntilUtc = r.IsDBNull(10) ? null : ParseIso(r.GetString(10)),
        SupersededById = r.IsDBNull(11) ? null : r.GetInt64(11),
        SourceMeetingId = r.IsDBNull(12) ? null : r.GetInt64(12),
        SourceSegmentId = r.IsDBNull(13) ? null : r.GetInt64(13),
        SourceStartMs = r.IsDBNull(14) ? null : r.GetInt64(14),
        SourceEndMs = r.IsDBNull(15) ? null : r.GetInt64(15),
        Speaker = r.IsDBNull(16) ? null : r.GetString(16),
        Source = r.GetString(17),
        CreatedAtUtc = ParseIso(r.GetString(18)),
        UpdatedAtUtc = ParseIso(r.GetString(19)),
        LastConfirmedAtUtc = r.IsDBNull(20) ? null : ParseIso(r.GetString(20)),
        SourceDocumentId = r.IsDBNull(21) ? null : r.GetString(21),
        SourceDocName = r.IsDBNull(22) ? null : r.GetString(22),
        SourceDocSha256 = r.IsDBNull(23) ? null : r.GetString(23),
        SourceAnchor = r.IsDBNull(24) ? null : r.GetString(24),
    };

    public MemoryItem? GetItem(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {ItemColumns} FROM mem_item WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadItem(r) : null;
        }
    }

    public List<MemoryItem> ListItems(MemoryScopeType? scope = null, long? scopeId = null,
        MemoryItemStatus? status = null, int limit = 500)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            var sql = $"SELECT {ItemColumns} FROM mem_item WHERE 1=1";
            if (scope.HasValue)
            {
                sql += " AND scope_type=$scope";
                cmd.Parameters.AddWithValue("$scope", scope.Value.ToString());
            }
            if (scopeId.HasValue)
            {
                sql += " AND scope_id=$scopeId";
                cmd.Parameters.AddWithValue("$scopeId", scopeId.Value);
            }
            if (status.HasValue)
            {
                sql += " AND status=$status";
                cmd.Parameters.AddWithValue("$status", status.Value.ToString());
            }
            // limit <= 0 = «без ограничения» (режим «Все», §6.4): SQLite
            // LIMIT 0 вернул бы ноль строк, а не «все».
            sql += " ORDER BY updated_utc DESC, id DESC";
            if (limit > 0)
            {
                sql += " LIMIT $lim";
                cmd.Parameters.AddWithValue("$lim", limit);
            }
            cmd.CommandText = sql;

            var list = new List<MemoryItem>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadItem(r));
            return list;
        }
    }

    public void SetStatus(long id, MemoryItemStatus st, long? supersededBy = null)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            using var cmd = _db.CreateCommand();
            // Superseded-факты не удаляем: проставляем valid_until, если ещё не задан.
            cmd.CommandText = """
                UPDATE mem_item SET status=$st, updated_utc=$now,
                    superseded_by = CASE WHEN $supBy IS NOT NULL THEN $supBy ELSE superseded_by END,
                    valid_until_utc = CASE WHEN $st='Superseded' AND valid_until_utc IS NULL
                                           THEN $now ELSE valid_until_utc END,
                    last_confirmed_utc = CASE WHEN $st='Active'
                                              THEN $now ELSE last_confirmed_utc END
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$st", st.ToString());
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$supBy", (object?)supersededBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            BumpRevision();
        }
    }

    public void HardDeleteItem(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            // tags/embeddings сносятся ON DELETE CASCADE; FTS — триггером mem_item_ad.
            cmd.CommandText = "DELETE FROM mem_item WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            BumpRevision();
        }
    }

    // ---------------- projects / people / tags ----------------

    public long UpsertProject(MemoryProject p)
    {
        lock (_gate)
        {
            if (p.Id > 0)
            {
                using var upd = _db.CreateCommand();
                upd.CommandText = """
                    UPDATE mem_project SET name=$name, description=$desc,
                        created_utc=$created, archived=$arch WHERE id=$id;
                    """;
                BindProject(upd, p);
                upd.Parameters.AddWithValue("$id", p.Id);
                if (upd.ExecuteNonQuery() > 0)
                    return p.Id;
            }

            using var ins = _db.CreateCommand();
            // name UNIQUE: повторный upsert по имени возвращает существующую строку.
            ins.CommandText = """
                INSERT INTO mem_project(name, description, created_utc, archived)
                VALUES($name, $desc, $created, $arch)
                ON CONFLICT(name) DO UPDATE SET
                    description=excluded.description, archived=excluded.archived
                RETURNING id;
                """;
            BindProject(ins, p);
            p.Id = (long)ins.ExecuteScalar()!;
            return p.Id;
        }
    }

    private static void BindProject(SqliteCommand cmd, MemoryProject p)
    {
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$desc", p.Description);
        cmd.Parameters.AddWithValue("$created", Iso(p.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$arch", p.Archived ? 1 : 0);
    }

    public List<MemoryProject> ListProjects(bool includeArchived = false)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, description, created_utc, archived FROM mem_project
                WHERE ($all = 1 OR archived = 0) ORDER BY name;
                """;
            cmd.Parameters.AddWithValue("$all", includeArchived ? 1 : 0);
            var list = new List<MemoryProject>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new MemoryProject
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    Description = r.GetString(2),
                    CreatedAtUtc = ParseIso(r.GetString(3)),
                    Archived = r.GetInt32(4) != 0,
                });
            }
            return list;
        }
    }

    public long UpsertPerson(MemoryPerson p)
    {
        lock (_gate)
        {
            if (p.Id > 0)
            {
                using var upd = _db.CreateCommand();
                upd.CommandText =
                    "UPDATE mem_person SET name=$name, notes=$notes, created_utc=$created WHERE id=$id;";
                BindPerson(upd, p);
                upd.Parameters.AddWithValue("$id", p.Id);
                if (upd.ExecuteNonQuery() > 0)
                    return p.Id;
            }

            using var ins = _db.CreateCommand();
            ins.CommandText = """
                INSERT INTO mem_person(name, notes, created_utc)
                VALUES($name, $notes, $created)
                ON CONFLICT(name) DO UPDATE SET notes=excluded.notes
                RETURNING id;
                """;
            BindPerson(ins, p);
            p.Id = (long)ins.ExecuteScalar()!;
            return p.Id;
        }
    }

    private static void BindPerson(SqliteCommand cmd, MemoryPerson p)
    {
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$notes", p.Notes);
        cmd.Parameters.AddWithValue("$created", Iso(p.CreatedAtUtc));
    }

    public List<MemoryPerson> ListPeople()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, name, notes, created_utc FROM mem_person ORDER BY name;";
            var list = new List<MemoryPerson>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new MemoryPerson
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    Notes = r.GetString(2),
                    CreatedAtUtc = ParseIso(r.GetString(3)),
                });
            }
            return list;
        }
    }

    public void AddTag(long itemId, string tag)
    {
        // §6.3 (аудит 2026-10-15): ключ тега нормализован (trim, без '#', lower)
        // — поиск/дедуп регистронезависимы; исходное написание храним в tag_disp.
        var disp = tag.Trim().TrimStart('#').Trim();
        tag = NormalizeTag(tag);
        if (tag.Length == 0) return;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO mem_tag(item_id, tag, tag_disp) VALUES($id, $tag, $disp)
                ON CONFLICT(item_id, tag) DO UPDATE SET tag_disp = excluded.tag_disp;
                """;
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$tag", tag);
            cmd.Parameters.AddWithValue("$disp", disp);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Единая нормализация тега: trim, снятие ведущих '#', lower.
    /// Применяется и при записи (AddTag), и при разборе запроса (SearchFts).</summary>
    internal static string NormalizeTag(string tag) =>
        tag.Trim().TrimStart('#').Trim().ToLowerInvariant();

    public List<string> GetTags(long itemId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(tag_disp, tag) FROM mem_tag WHERE item_id=$id ORDER BY COALESCE(tag_disp, tag);";
            cmd.Parameters.AddWithValue("$id", itemId);
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(r.GetString(0));
            return list;
        }
    }

    // ---------------- FTS + embeddings ----------------
    public List<(MemoryItem Item, double FtsScore)> SearchFts(string query, int limit = 50)
    {
        // [secret]/[screen] — метки канала, не содержание запроса.
        query = QueryText.StripServiceMarkers(query);
        // '#' не входит в QuerySeparators → «#файл» приходит одним словом-тегом.
        var words = QueryText.FilterScaffolding(SplitQueryWords(query), MemoryScaffolding);
        var tags = words.Where(w => w.StartsWith('#'))
                        .Select(NormalizeTag)
                        .Where(t => t.Length > 0)
                        .Distinct()
                        .ToList();
        var contentWords = words.Where(w => !w.StartsWith('#')).ToList();
        if ((contentWords.Count == 0 && tags.Count == 0) || limit <= 0)
            return [];

        lock (_gate)
        {
            // §6.3: теги живут в mem_tag — FTS5 по контенту их не видит
            // ('#' вообще не токенизируется). Тег-кандидаты получают полное
            // покрытие (score 1.0): тег — явный маркер пользователя.
            var tagged = tags.Count > 0
                ? SearchTags(tags, limit)
                : new List<(MemoryItem Item, double FtsScore)>();
            if (contentWords.Count == 0)
                return tagged;

            var content = FtsAvailable
                ? SearchFtsTable(contentWords, limit)
                : SearchLikeFallback(contentWords, limit);
            if (tags.Count == 0)
                return content;

            // Смешанный запрос «#тег слова»: тег — фильтр, текст ранжирует
            // внутри него; тег-кандидаты без текстового матча — в хвост (0.5).
            var tagIds = new HashSet<long>(tagged.Select(t => t.Item.Id));
            var merged = content.Where(c => tagIds.Contains(c.Item.Id)).ToList();
            var matched = new HashSet<long>(merged.Select(m => m.Item.Id));
            merged.AddRange(tagged.Where(t => !matched.Contains(t.Item.Id))
                                  .Select(t => (t.Item, 0.5)));
            return merged.Take(limit).ToList();
        }
    }

    /// <summary>Поиск по тегам: item с ЛЮБЫМ из нормализованных тегов (OR),
    /// новые сверху. Score 1.0 — тег-матч считается полным покрытием запроса.</summary>
    private List<(MemoryItem Item, double FtsScore)> SearchTags(List<string> tags, int limit)
    {
        using var cmd = _db.CreateCommand();
        var inList = string.Join(", ", tags.Select((_, i) => $"$t{i}"));
        cmd.CommandText = $"""
            SELECT DISTINCT {ItemColumns}, 1.0 AS score
            FROM mem_item JOIN mem_tag ON mem_tag.item_id = mem_item.id
            WHERE mem_tag.tag IN ({inList})
            ORDER BY mem_item.id DESC LIMIT $lim;
            """;
        for (var i = 0; i < tags.Count; i++)
            cmd.Parameters.AddWithValue($"$t{i}", tags[i]);
        cmd.Parameters.AddWithValue("$lim", limit);

        var list = new List<(MemoryItem, double)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                list.Add((ReadItem(r), 1.0));
        return list;
    }
    /// <summary>Разбить запрос на слова: обрезать все FTS5-спецсимволы/кавычки.
    /// '#' НЕ разделитель — «#файл» остаётся целым токеном-тегом (SearchFts).</summary>
    private static readonly char[] QuerySeparators =
        WordSeparators.Where(c => c != '#').ToArray();

    private static List<string> SplitQueryWords(string query)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
            return words;
        foreach (var w in query.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = w.Trim();
            if (word.Length > 0 && word != "-" && word != "+" && word != "#")
                words.Add(word);
        }
        return words;
    }

    /// <summary>
    /// FTS5 MATCH: слова через OR с prefix-звёздочкой («token*»). OR+prefix нужен
    /// для recall — естественный вопрос содержит стоп-слова и флексии русского,
    /// AND по точным словам падал в ноль. Порядок задаёт НЕ голый bm25:
    /// -bm25 по любому корню зашумляет top — одно совпавшее слово («Alpha»,
    /// «ли*») давало шумному кандидату ftsNorm≈1 после max-нормализации ранкера.
    /// Поэтому FtsScore = IDF-взвешенная доля покрытых слов запроса (0..1,
    /// шкала сравнима с косинусом E5-ветки), bm25 идёт только тай-брейком.
    /// Для слова без prefix-матчей ищем укороченный терм (формы русского слова:
    /// «мерджить*»→«мердж*»…), но не короче StemMinLen и не чаще MaxTermDf
    /// корпуса — иначе короткий стем заливает выдачу шумом.
    /// </summary>
    private List<(MemoryItem Item, double FtsScore)> SearchFtsTable(List<string> words, int limit)
    {
        // Слова ≥MinContentWordLen — «смысловые»: только они считаются в покрытии.
        // Короткие («в», «ли», «за») в MATCH остаются для recall-кандидатов,
        // но их однокорневые prefix-матчи не дают очков покрытия.
        var orderedContent = words
            .Where(w => w.Length >= MinContentWordLen)
            .Select(w => w.ToLowerInvariant())
            .ToList();
        var contentWords = orderedContent.Distinct().ToList();
        var phraseNgrams = QueryText.BuildPhraseNgrams(orderedContent);

        // df (document frequency) терма по FTS-индексу + выбор рабочего терма
        // с prefix-fallback. df=0 у смыслового слова — исключаем из знаменателя
        // покрытия: такое слово всё равно ничего не различает по токенам.
        // Слово, которого нет в корпусе вовсе, но чей стем — служебный
        // («поисктаь» ← «поискать»), тоже не терм: опечатка служебного слова
        // не должна приносить редкий стем «поиск» и перевешивать тему запроса.
        var terms = new List<(string Word, string Term, long Df)>();
        long rowCount = CountRows("mem_item");
        foreach (var w in contentWords)
        {
            var (term, df, dfFull) = ResolveTerm(w, rowCount);
            if (dfFull == 0 && QueryText.IsNearStopWord(w, MemoryScaffolding))
                continue;
            terms.Add((w, term, df));
        }

        var match = string.Join(" OR ",
            words.Select(w => "\"" + w + "\"*")
                 .Concat(terms.Where(t => t.Df > 0 && t.Term != t.Word)
                              .Select(t => "\"" + t.Term + "\"*")));

        using var cmd = _db.CreateCommand();
        // bm25() возвращает отрицательные значения (меньше = лучше) — возвращаем -bm25
        // как сырой сигнал тай-брейка, сортируем по убыванию.
        cmd.CommandText = $"""
            SELECT mem_item.*, -bm25(mem_item_fts) AS score
            FROM mem_item_fts JOIN mem_item ON mem_item.id = mem_item_fts.rowid
            WHERE mem_item_fts MATCH $q
            ORDER BY score DESC LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$lim", limit);

        var rows = new List<(MemoryItem Item, double Raw)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add((ReadItem(r), r.GetDouble(r.GetOrdinal("score"))));

        return ScoreByCoverage(rows, terms, rowCount, phraseNgrams);
    }

    /// <summary>
    /// Итоговый FtsScore кандидата: IDF-взвешенная доля покрытых смысловых
    /// слов запроса (0..1 — шкала сравнима с косинусом E5-ветки ранкера).
    /// Слово «покрыто», если токен контента делит с ним общий стем
    /// (<see cref="SharesStem"/>). Нормированный raw-score движка даёт ±5%
    /// тай-брейк внутри одного уровня покрытия; при пустом списке слов
    /// (знаменатель нулевой) остаётся старый порядок по raw. Слово, которого
    /// нет в корпусе вовсе (df=0), всё равно входит в знаменатель с весом
    /// самого редкого терма: иначе вопрос о теме, отсутствующей в памяти
    /// («мобильное приложение»), получал бы 1.0 за одно служебное слово
    /// «отвечает» и выглядел как полное совпадение. Точная пара/тройка
    /// соседних слов запроса («sensor supply») — бонус к покрытию: это тема
    /// вопроса, а не набор служебных слов вокруг неё.
    /// </summary>
    private static List<(MemoryItem, double)> ScoreByCoverage(
        List<(MemoryItem Item, double Raw)> rows,
        List<(string Word, string Term, long Df)> terms,
        long rowCount,
        HashSet<string>? phraseNgrams = null)
    {
        double maxRaw = rows.Count > 0 ? rows.Max(x => x.Raw) : 0.0;
        // df=0 — слово отсутствует в корпусе: весит как редчайший терм, но
        // не покрывается ни одним кандидатом (см. doc-комментарий выше).
        double idfDen = terms.Sum(t => Idf(t.Df > 0 ? t.Df : 1, rowCount));
        var list = new List<(MemoryItem, double)>(rows.Count);
        foreach (var (item, raw) in rows)
        {
            double num = 0.0;
            if (idfDen > 0)
            {
                var toks = item.Content.Split(WordSeparators,
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var (word, _, df) in terms)
                {
                    if (df <= 0) continue;
                    if (toks.Any(t => SharesStem(word, t)))
                        num += Idf(df, rowCount);
                }
            }
            double cov = idfDen > 0 ? num / idfDen : 0.0;
            double score = cov * (1.0 + 0.05 * (maxRaw > 0 ? raw / maxRaw : 0.0));
            if (phraseNgrams is { Count: > 0 } && cov > 0)
            {
                var normalized = QueryText.NormalizeForPhrase(item.Content);
                var matches = QueryText.CountPhraseMatches(normalized, phraseNgrams);
                // Один точный биграмм темы весит больше, чем разница покрытия
                // от служебного слова; кап — чтобы длинная фраза не давала
                // неограниченный бонус за счёт повторов.
                if (matches > 0) score += Math.Min(0.6, 0.5 * matches);
            }
            if (cov <= 0 && idfDen <= 0)
                score = maxRaw > 0 ? raw / maxRaw : 0.0;
            list.Add((item, score));
        }
        list.Sort((a, b) => b.Item2 != a.Item2
            ? b.Item2.CompareTo(a.Item2) : a.Item1.Id.CompareTo(b.Item1.Id));
        return list;
    }

    /// <summary>
    /// Рабочий терм для слова, его df и df самого слова (без стем-фолбэка):
    /// само слово, если у него есть prefix-матчи в корпусе; иначе укорачиваем
    /// (мин. StemMinLen) до первого терма с df>0 и умеренной частотой — так
    /// «Ивану»/«мерджить» находят формы «Иван»/«мердж*» без стеммера. Полное
    /// слово с частотой за капом не сокращаем: оно само по себе легальный
    /// частотный терм. dfFull=0 — слова нет в корпусе (опечатка/незнакомое).
    /// </summary>
    private (string Term, long Df, long DfFull) ResolveTerm(string word, long rowCount)
    {
        long maxDf = Math.Max(MinTermDf, rowCount / MaxTermDfDivisor);
        long dfFull = MatchCount(word);
        if (dfFull > 0) return (word, dfFull, dfFull);
        for (int len = word.Length - 1; len >= Math.Min(StemMinLen, word.Length); len--)
        {
            long df = MatchCount(word[..len]);
            // df за капом (стем слишком частотный) — короче только шумнее.
            if (df > 0 && df <= maxDf) return (word[..len], df, 0);
            if (df > maxDf) break;
        }
        return (word, 0, 0);
    }

    /// <summary>См. <see cref="QueryText.SharesStem"/> — стем общий, если
    /// префикс «достаточен» для длины слова (2/3/4 буквы).</summary>
    internal static bool SharesStem(string a, string b) => QueryText.SharesStem(a, b);


    private static double Idf(long df, long n) =>
        Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

    /// <summary>Число строк FTS-индекса, где есть токен с префиксом term.</summary>
    private long MatchCount(string term)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM mem_item_fts WHERE mem_item_fts MATCH $m";
        cmd.Parameters.AddWithValue("$m", "\"" + term + "\"*");
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private long CountRows(string table)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Fallback без FTS5: слова через OR LIKE %word% (зеркалит
    /// OR-семантику MATCH; медленно, только при IsFts=false). Порядок — тот же
    /// coverage-скоринг, df считается LIKE-подстрокой (грубее токенов, редкий путь).</summary>
    private List<(MemoryItem Item, double FtsScore)> SearchLikeFallback(List<string> words, int limit)
    {
        var orderedContent = words
            .Where(w => w.Length >= MinContentWordLen)
            .Select(w => w.ToLowerInvariant())
            .ToList();
        var contentWords = orderedContent.Distinct().ToList();
        var phraseNgrams = QueryText.BuildPhraseNgrams(orderedContent);
        var terms = new List<(string Word, string Term, long Df)>();
        long rowCount = CountRows("mem_item");
        foreach (var w in contentWords)
        {
            var df = LikeCount(w);
            // Та же защита, что в FTS-ветке: неизвестное слово со служебным
            // стемом — опечатка служебного слова, а не тема.
            if (df == 0 && QueryText.IsNearStopWord(w, MemoryScaffolding))
                continue;
            terms.Add((w, w, df));
        }

        using var cmd = _db.CreateCommand();
        var sql = $"SELECT {ItemColumns}, 1.0 AS score FROM mem_item WHERE ";
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) sql += " OR ";
            sql += $"content LIKE $w{i} ESCAPE '\\'";
            cmd.Parameters.AddWithValue($"$w{i}",
                "%" + EscapeLike(words[i]) + "%");
        }
        sql += " LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.CommandText = sql;

        var rows = new List<(MemoryItem Item, double Raw)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add((ReadItem(r), 1.0));
        return ScoreByCoverage(rows, terms, rowCount, phraseNgrams);
    }

    /// <summary>Число строк, где content содержит подстроку word (LIKE).</summary>
    private long LikeCount(string word)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM mem_item WHERE content LIKE $w ESCAPE '\\'";
        cmd.Parameters.AddWithValue("$w", "%" + EscapeLike(word) + "%");
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static string EscapeLike(string word) =>
        word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public void SaveEmbedding(long itemId, float[] vec, string model)
    {
        lock (_gate)
        {
            var blob = FloatsToBytes(vec);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO mem_embedding(item_id, model, dim, vec) VALUES($id, $model, $dim, $vec)
                ON CONFLICT(item_id) DO UPDATE SET model=$model, dim=$dim, vec=$vec;
                """;
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$dim", vec.Length);
            cmd.Parameters.AddWithValue("$vec", blob);
            cmd.ExecuteNonQuery();
            BumpRevision();
        }
    }

    public List<(long ItemId, float[] Vec, string Model)> LoadEmbeddings()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT item_id, model, dim, vec FROM mem_embedding;";
            var list = new List<(long, float[], string)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var blob = (byte[])r.GetValue(3);
                list.Add((r.GetInt64(0), BytesToFloats(blob, r.GetInt32(2)), r.GetString(1)));
            }
            return list;
        }
    }

    /// <summary>float[] → little-endian BLOB (BitConverter на x86/x64 уже little-endian).</summary>
    private static byte[] FloatsToBytes(float[] vec)
    {
        var bytes = new byte[vec.Length * sizeof(float)];
        if (BitConverter.IsLittleEndian)
        {
            Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        }
        else
        {
            for (var i = 0; i < vec.Length; i++)
            {
                var b = BitConverter.GetBytes(vec[i]);
                Array.Reverse(b);
                Buffer.BlockCopy(b, 0, bytes, i * 4, 4);
            }
        }
        return bytes;
    }

    private static float[] BytesToFloats(byte[] blob, int dim)
    {
        var vec = new float[dim];
        if (BitConverter.IsLittleEndian)
        {
            Buffer.BlockCopy(blob, 0, vec, 0, Math.Min(blob.Length, dim * sizeof(float)));
        }
        else
        {
            for (var i = 0; i < dim && i * 4 + 4 <= blob.Length; i++)
            {
                var b = new byte[4];
                Buffer.BlockCopy(blob, i * 4, b, 0, 4);
                Array.Reverse(b);
                vec[i] = BitConverter.ToSingle(b, 0);
            }
        }
        return vec;
    }

    // ---------------- jobs ----------------

    private const string JobColumns =
        "id, kind, payload_json, status, attempts, run_after_utc, created_utc, started_utc, finished_utc, error";

    public long EnqueueJob(string kind, string payloadJson = "")
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO mem_job(kind, payload_json, status, attempts, created_utc)
                VALUES($kind, $payload, 'pending', 0, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$payload", payloadJson);
            cmd.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public List<MemoryJob> ListJobs(string? status = null, int limit = 100)
    {
        lock (_gate)
        {
            WriteBackClaimedJobs();
            using var cmd = _db.CreateCommand();
            var sql = $"SELECT {JobColumns} FROM mem_job";
            if (status != null)
            {
                sql += " WHERE status=$st";
                cmd.Parameters.AddWithValue("$st", status);
            }
            sql += " ORDER BY id LIMIT $lim";
            cmd.Parameters.AddWithValue("$lim", limit);
            cmd.CommandText = sql;

            var list = new List<MemoryJob>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadJob(r));
            return list;
        }
    }

    private static MemoryJob ReadJob(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Kind = r.GetString(1),
        PayloadJson = r.GetString(2),
        Status = r.GetString(3),
        Attempts = r.GetInt32(4),
        RunAfterUtc = r.IsDBNull(5) ? null : ParseIso(r.GetString(5)),
        CreatedAtUtc = ParseIso(r.GetString(6)),
        StartedAtUtc = r.IsDBNull(7) ? null : ParseIso(r.GetString(7)),
        FinishedAtUtc = r.IsDBNull(8) ? null : ParseIso(r.GetString(8)),
        Error = r.IsDBNull(9) ? null : r.GetString(9),
    };

    /// <summary>
    /// Выданные ClaimJob джобы — identity-map. MemoryJobWorker делает retry
    /// мутацией полей объекта (Status="pending", RunAfterUtc, Error) без вызова
    /// FinishJob — перед следующим pick'ом сливаем мутации в БД, иначе retry
    /// потеряется (см. TODO-contract в MemoryJobWorker).
    /// </summary>
    private readonly Dictionary<long, MemoryJob> _claimedJobs = new();

    /// <summary>
    /// Атомарно (одна connection + lock) забрать первую pending-джобу либо
    /// реклеймить зависшую running (started_utc старше staleAfter) → status=running,
    /// attempts++, started_utc=now. Порядок: pending с наименьшим id приоритетнее.
    /// </summary>
    public MemoryJob? ClaimJob(TimeSpan staleAfter)
    {
        lock (_gate)
        {
            WriteBackClaimedJobs();

            var now = DateTime.UtcNow;
            var staleBefore = Iso(now - staleAfter);
            var nowIso = Iso(now);

            using var pick = _db.CreateCommand();
            pick.CommandText = """
                SELECT id FROM mem_job
                WHERE (status='pending' AND (run_after_utc IS NULL OR run_after_utc <= $now))
                   OR (status='running' AND started_utc IS NOT NULL AND started_utc < $stale)
                ORDER BY CASE WHEN status='pending' THEN 0 ELSE 1 END, id
                LIMIT 1;
                """;
            pick.Parameters.AddWithValue("$now", nowIso);
            pick.Parameters.AddWithValue("$stale", staleBefore);
            var idObj = pick.ExecuteScalar();
            if (idObj == null)
                return null;

            var id = (long)idObj;
            using var upd = _db.CreateCommand();
            upd.CommandText = """
                UPDATE mem_job SET status='running', attempts=attempts+1,
                    started_utc=$now, finished_utc=NULL, error=NULL
                WHERE id=$id;
                """;
            upd.Parameters.AddWithValue("$now", nowIso);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();

            // identity-map: если объект этой джобы уже отдавали — возвращаем его же,
            // обновив поля из БД (как in-memory стор по ссылке).
            if (!_claimedJobs.TryGetValue(id, out var job))
            {
                using var get = _db.CreateCommand();
                get.CommandText = $"SELECT {JobColumns} FROM mem_job WHERE id=$id;";
                get.Parameters.AddWithValue("$id", id);
                using var r = get.ExecuteReader();
                job = r.Read() ? ReadJob(r) : null;
            }
            else
            {
                job.Status = "running";
                job.Attempts++;
                job.StartedAtUtc = now;
                job.FinishedAtUtc = null;
                job.Error = null;
            }
            if (job != null)
                _claimedJobs[id] = job;
            return job;
        }
    }

    /// <summary>
    /// Слить мутации выданных джоб в БД. Пишем только если объект перестал быть
    /// 'running' (worker перевёл в 'pending' для retry): владелец объекта — worker,
    /// и пока он running, БД уже отражает актуальное состояние. Записи НЕ снимаются
    /// с учёта — объект остаётся в identity-map до FinishJob.
    /// </summary>
    private void WriteBackClaimedJobs()
    {
        foreach (var job in _claimedJobs.Values)
        {
            if (job.Status == "running")
                continue;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE mem_job SET status=$st, run_after_utc=$after, error=$err,
                    started_utc=$started, finished_utc=$finished
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$st", job.Status);
            cmd.Parameters.AddWithValue("$after", (object?)IsoOrNull(job.RunAfterUtc) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err", (object?)job.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$started", (object?)IsoOrNull(job.StartedAtUtc) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$finished", (object?)IsoOrNull(job.FinishedAtUtc) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", job.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public void FinishJob(long id, string? error = null)
    {
        lock (_gate)
        {
            var nowIso = Iso(DateTime.UtcNow);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE mem_job SET status=$st, finished_utc=$now, error=$err WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$st", error == null ? "completed" : "failed");
            cmd.Parameters.AddWithValue("$now", nowIso);
            cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();

            // Держим identity-map-объект в синхроне и снимаем с учёта.
            if (_claimedJobs.TryGetValue(id, out var job))
            {
                job.Status = error == null ? "completed" : "failed";
                job.FinishedAtUtc = ParseIso(nowIso);
                job.Error = error;
            }
            _claimedJobs.Remove(id);
        }
    }

    /// <summary>Вернуть зависшие running-джобы в pending (retryable по JobStaleSec).</summary>
    public void RequeueStale(TimeSpan staleAfter)
    {
        lock (_gate)
        {
            WriteBackClaimedJobs();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE mem_job SET status='pending', started_utc=NULL
                WHERE status='running' AND started_utc IS NOT NULL AND started_utc < $stale;
                """;
            cmd.Parameters.AddWithValue("$stale", Iso(DateTime.UtcNow - staleAfter));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Ручной retry из UI: failed/running-джоба → pending с чистой историей попыток.</summary>
    public void RetryJob(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE mem_job SET status='pending', attempts=0, run_after_utc=NULL,
                    started_utc=NULL, finished_utc=NULL, error=NULL
                WHERE id=$id AND status IN ('failed','running','pending');
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();

            // identity-map: если объект уже выдан ClaimJob — синхронизируем поля.
            if (_claimedJobs.TryGetValue(id, out var job))
            {
                job.Status = "pending";
                job.Attempts = 0;
                job.RunAfterUtc = null;
                job.StartedAtUtc = null;
                job.FinishedAtUtc = null;
                job.Error = null;
            }
        }
    }

    /// <summary>Массово удалить джобы по статусу (например 'failed'), вернуть число удалённых.</summary>
    public int DeleteJobsByStatus(string status)
    {
        lock (_gate)
        {
            WriteBackClaimedJobs();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM mem_job WHERE status=$st;";
            cmd.Parameters.AddWithValue("$st", status);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Удалить проект: его scope-items сносятся (FTS-триггером), у встреч
    /// обнуляется project_id — orphan-ссылок не остаётся.
    /// </summary>
    public void DeleteProject(long id)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var di = _db.CreateCommand();
            di.CommandText = "DELETE FROM mem_item WHERE scope_type='Project' AND scope_id=$id;";
            di.Parameters.AddWithValue("$id", id);
            di.ExecuteNonQuery();

            using var dm = _db.CreateCommand();
            dm.CommandText = "UPDATE mem_meeting SET project_id=NULL WHERE project_id=$id;";
            dm.Parameters.AddWithValue("$id", id);
            dm.ExecuteNonQuery();

            using var dp = _db.CreateCommand();
            dp.CommandText = "DELETE FROM mem_project WHERE id=$id;";
            dp.Parameters.AddWithValue("$id", id);
            dp.ExecuteNonQuery();
            tx.Commit();
            BumpRevision();
        }
    }

    /// <summary>Удалить персону и её scope-items (FTS-триггером).</summary>
    public void DeletePerson(long id)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var di = _db.CreateCommand();
            di.CommandText = "DELETE FROM mem_item WHERE scope_type='Person' AND scope_id=$id;";
            di.Parameters.AddWithValue("$id", id);
            di.ExecuteNonQuery();

            using var dp = _db.CreateCommand();
            dp.CommandText = "DELETE FROM mem_person WHERE id=$id;";
            dp.Parameters.AddWithValue("$id", id);
            dp.ExecuteNonQuery();
            tx.Commit();
            BumpRevision();
        }
    }

    // ---------------- maintenance ----------------

    public void DeleteMeeting(long id, bool deleteDerivedItems, bool deleteSegments)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            if (deleteDerivedItems)
            {
                using var di = _db.CreateCommand();
                di.CommandText = "DELETE FROM mem_item WHERE source_meeting_id=$id;";
                di.Parameters.AddWithValue("$id", id);
                di.ExecuteNonQuery();
            }
            if (deleteSegments)
            {
                using var ds = _db.CreateCommand();
                ds.CommandText = "DELETE FROM mem_segment WHERE meeting_id=$id;";
                ds.Parameters.AddWithValue("$id", id);
                ds.ExecuteNonQuery();
            }
            using var dm = _db.CreateCommand();
            dm.CommandText = "DELETE FROM mem_meeting WHERE id=$id;";
            dm.Parameters.AddWithValue("$id", id);
            dm.ExecuteNonQuery();
            tx.Commit();
            BumpRevision();
        }
    }

    /// <summary>Retention: удалить сегменты старше beforeUtc, вернуть число удалённых.</summary>
    public int PurgeSegmentsOlderThan(DateTime beforeUtc)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM mem_segment WHERE ts_utc < $before;";
            cmd.Parameters.AddWithValue("$before", Iso(beforeUtc));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Retention: удалить не-закреплённые items старше beforeUtc (по updated_utc),
    /// любой статус — retention это пользовательская политика. Tags/embeddings
    /// сносятся ON DELETE CASCADE, FTS-строки — триггером mem_item_ad.
    /// </summary>
    public int PurgeItemsOlderThan(DateTime beforeUtc)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM mem_item WHERE updated_utc < $before AND pinned = 0;";
            cmd.Parameters.AddWithValue("$before", Iso(beforeUtc));
            var n = cmd.ExecuteNonQuery();
            if (n > 0)
            {
                _log?.Info($"retention: purged {n} memory items older than {Iso(beforeUtc)}", Category);
                BumpRevision();
            }
            return n;
        }
    }

    public MemoryStats GetStats()
    {
        lock (_gate)
        {
            WriteBackClaimedJobs();
            long meetings = ScalarLong("SELECT COUNT(*) FROM mem_meeting");
            long items = ScalarLong("SELECT COUNT(*) FROM mem_item");
            long active = ScalarLong("SELECT COUNT(*) FROM mem_item WHERE status='Active'");
            long superseded = ScalarLong("SELECT COUNT(*) FROM mem_item WHERE status='Superseded'");
            long pendingJobs = ScalarLong("SELECT COUNT(*) FROM mem_job WHERE status='pending'");
            long dbBytes = 0;
            try
            {
                if (File.Exists(DbPath))
                    dbBytes = new FileInfo(DbPath).Length;
            }
            catch (IOException) { /* файл заблокирован другим читателем — вернём 0 */ }

            return new MemoryStats(meetings, items, active, superseded, pendingJobs,
                dbBytes, FtsAvailable);
        }
    }

    private long ScalarLong(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---------------- helpers ----------------

    private static string Iso(DateTime dt)
        => dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string? IsoOrNull(DateTime? dt)
        => dt.HasValue ? Iso(dt.Value) : null;

    private static DateTime ParseIso(string s)
        => DateTime.Parse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces);

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
