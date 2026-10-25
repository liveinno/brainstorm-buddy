using Microsoft.Data.Sqlite;
using BrainstormBuddy.Services;

namespace BrainstormBuddy.Transcription;

/// <summary>
/// Локальный индекс журналов встреч (TASK §8): SQLite FTS5 поверх meetings/*.jsonl.
///
/// Гранулярность — документ на реплику: устойчивый адрес = (meeting_id, line_idx);
/// AI-саммари лежит строкой line_idx = -1. Метаданные в mtg_doc, текст — в
/// external-content FTS5 с sync-триггерами (тот же приём, что у mem_item_fts в
/// MemoryDbMigrator). Вектора E5 — отдельная таблица mtg_vec, тегированная
/// EmbeddingTag провайдера: смена модели не смешивает пространства.
///
/// Инкрементальный sync по отпечатку файла (mtime+size журнала, mtime sidecar'а):
/// изменённые встречи перечитываются и переиндексируются, удалённые выпадают.
/// Все документы встречи пересоздаются атомарно — частичных строк в выдаче нет.
/// Точный поиск (FTS) доступен сразу после sync; векторная обвязка достраивается
/// фоном (vec_tag в mtg_state отмечает готовность встречи для текущей модели).
///
/// Потокобезопасность: одно соединение + _gate (как SqliteMemoryStore); async
/// только на уровне чтения журналов — все SQL идут под lock синхронно.
/// </summary>
public sealed class MeetingSearchIndex : IDisposable
{
    public const int SchemaVersion = 1;
    /// <summary>Реплики короче этого порога векторов не получают: «ок»/«согласен»
    /// для смыслового поиска — шум. Саммари (line_idx=-1) векторизуется всегда.</summary>
    public const int MinVecTextLen = 24;

    private const string Category = "Memory";

    private readonly SqliteConnection _db;
    private readonly LoggingService? _log;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Один индексированный документ (реплика или саммари).</summary>
    public sealed class Doc
    {
        public long Id;
        public string MeetingId = "";
        public string JournalPath = "";
        public DateTime StartedAt;
        /// <summary>0-based индекс строки журнала; -1 — AI-саммари.</summary>
        public int LineIdx;
        public string Ts = "";
        public string Role = "";
        public string? Speaker;
        /// <summary>Стабильный mid сообщения журнала (null у старых файлов/summary) —
        /// адрес навигации к реплике надёжнее, чем время/порядок.</summary>
        public string? Mid;
        public string Text = "";
        /// <summary>bm25-ранг FTS-выдачи (отрицательный: меньше = лучше).</summary>
        public double Bm25;
    }

    public MeetingSearchIndex(string dbPath, LoggingService? log = null)
    {
        _log = log;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
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
        DbPath = dbPath;
        EnsureSchema();
    }

    public string DbPath { get; }
    /// <summary>FTS5 поднялся (probe при EnsureSchema). false → индекс бесполезен,
    /// сервис работает файловым сканом.</summary>
    public bool FtsAvailable { get; private set; }

    // ---------------- схема ----------------

    private void EnsureSchema()
    {
        lock (_gate)
        {
            var version = ReadSchemaVersion();
            if (version != SchemaVersion)
                DropAll(); // несовместимая схема → пересоздаём (данные выводятся из журналов)

            Exec($"""
                CREATE TABLE IF NOT EXISTS mtg_meta(k TEXT PRIMARY KEY, v TEXT);
                INSERT OR REPLACE INTO mtg_meta(k,v) VALUES('schema','{SchemaVersion}');
                CREATE TABLE IF NOT EXISTS mtg_state(
                    meeting_id TEXT PRIMARY KEY,
                    journal_path TEXT NOT NULL,
                    started_utc TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,
                    vec_tag TEXT NULL);
                CREATE TABLE IF NOT EXISTS mtg_doc(
                    id INTEGER PRIMARY KEY,
                    meeting_id TEXT NOT NULL REFERENCES mtg_state(meeting_id) ON DELETE CASCADE,
                    line_idx INTEGER NOT NULL,
                    ts TEXT NOT NULL DEFAULT '',
                    role TEXT NOT NULL DEFAULT '',
                    speaker TEXT NULL,
                    mid TEXT NULL,
                    text TEXT NOT NULL DEFAULT '');
                CREATE INDEX IF NOT EXISTS mtg_doc_m ON mtg_doc(meeting_id);
                CREATE TABLE IF NOT EXISTS mtg_vec(
                    doc_id INTEGER NOT NULL REFERENCES mtg_doc(id) ON DELETE CASCADE,
                    model TEXT NOT NULL,
                    dim INTEGER NOT NULL,
                    vec BLOB NOT NULL,
                    PRIMARY KEY(doc_id, model));
                """);

            try
            {
                Exec("""
                    CREATE VIRTUAL TABLE IF NOT EXISTS mtg_fts USING fts5(
                        text, content='mtg_doc', content_rowid='id',
                        tokenize='unicode61 remove_diacritics 2');
                    """);
                Exec("""
                    CREATE TRIGGER IF NOT EXISTS mtg_doc_ai AFTER INSERT ON mtg_doc BEGIN
                        INSERT INTO mtg_fts(rowid, text) VALUES (new.id, new.text);
                    END;
                    CREATE TRIGGER IF NOT EXISTS mtg_doc_ad AFTER DELETE ON mtg_doc BEGIN
                        INSERT INTO mtg_fts(mtg_fts, rowid, text)
                            VALUES('delete', old.id, old.text);
                    END;
                    CREATE TRIGGER IF NOT EXISTS mtg_doc_au AFTER UPDATE OF text ON mtg_doc BEGIN
                        INSERT INTO mtg_fts(mtg_fts, rowid, text)
                            VALUES('delete', old.id, old.text);
                        INSERT INTO mtg_fts(rowid, text) VALUES (new.id, new.text);
                    END;
                    """);
                // FTS появился при уже заполненном mtg_doc (провайдер обновился) → догон.
                // На живом индексе rebuild не делаем: это полное перечитывание всех строк.
                using (var c = _db.CreateCommand())
                {
                    c.CommandText = """
                        SELECT (SELECT COUNT(*) FROM mtg_fts) = 0
                           AND (SELECT COUNT(*) FROM mtg_doc) > 0;
                        """;
                    if (Convert.ToInt64(c.ExecuteScalar() ?? 0) == 1)
                    {
                        c.CommandText = "INSERT INTO mtg_fts(mtg_fts) VALUES('rebuild');";
                        c.ExecuteNonQuery();
                    }
                }
                FtsAvailable = true;
            }
            catch (SqliteException ex)
            {
                FtsAvailable = false;
                _log?.Warn($"MeetingIndex: FTS5 недоступен: {ex.Message}", Category);
            }
        }
    }

    private int ReadSchemaVersion()
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT v FROM mtg_meta WHERE k='schema'";
            var v = cmd.ExecuteScalar() as string;
            return int.TryParse(v, out var n) ? n : 0;
        }
        catch { return 0; } // таблицы ещё нет
    }

    private void DropAll()
    {
        Exec("""
            DROP TRIGGER IF EXISTS mtg_doc_ai;
            DROP TRIGGER IF EXISTS mtg_doc_au;
            DROP TRIGGER IF EXISTS mtg_doc_ad;
            DROP TABLE IF EXISTS mtg_fts;
            DROP TABLE IF EXISTS mtg_vec;
            DROP TABLE IF EXISTS mtg_doc;
            DROP TABLE IF EXISTS mtg_state;
            DROP TABLE IF EXISTS mtg_meta;
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------- отпечаток и sync ----------------

    /// <summary>Отпечаток состояния встречи: изменение журнала или sidecar'а
    /// (summary/lineCount) меняет fp → встреча переиндексируется.</summary>
    public static string Fingerprint(MeetingInfo m)
    {
        long jmtime = 0, jsize = -1, mmtime = 0;
        try
        {
            var fi = new FileInfo(m.JournalPath);
            if (fi.Exists) { jmtime = fi.LastWriteTimeUtc.Ticks; jsize = fi.Length; }
        }
        catch { }
        try
        {
            var meta = Path.Combine(Path.GetDirectoryName(m.JournalPath) ?? "", m.Id + ".meta.json");
            if (File.Exists(meta)) mmtime = File.GetLastWriteTimeUtc(meta).Ticks;
        }
        catch { }
        return $"{jmtime:x}:{jsize:x}:{mmtime:x}";
    }

    private sealed class StateRow
    {
        public string Fingerprint = "";
        public string JournalPath = "";
    }

    private Dictionary<string, StateRow> LoadState()
    {
        var map = new Dictionary<string, StateRow>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT meeting_id, fingerprint, journal_path FROM mtg_state";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = new StateRow { Fingerprint = r.GetString(1), JournalPath = r.GetString(2) };
        return map;
    }

    /// <summary>
    /// Инкрементальный sync: перечитывает только встречи с новым отпечатком,
    /// удаляет документы исчезнувших файлов. Чтение журналов — вне блокировки
    /// (async IO), запись — одна транзакция. Возвращает число переиндексированных.
    /// Отмена бросает OCE ДО транзакции — индекс остаётся согласованным.
    /// </summary>
    public async Task<int> SyncAsync(IReadOnlyList<MeetingInfo> meetings,
        Func<MeetingInfo, CancellationToken, Task<List<MeetingJournalLine>>> readLines,
        CancellationToken ct)
    {
        Dictionary<string, StateRow> states;
        lock (_gate) states = LoadState();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<(MeetingInfo M, string Fp, List<MeetingJournalLine> Lines)>();
        foreach (var m in meetings)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(m.Id);
            var fp = Fingerprint(m);
            if (states.TryGetValue(m.Id, out var st) && st.Fingerprint == fp)
                continue; // отпечаток совпал — в индексе актуальная версия
            List<MeetingJournalLine> lines;
            try { lines = await readLines(m, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { continue; } // битый журнал — пропускаем до следующего sync
            // Сохраняем отпечаток ДО чтения: append после снимка должен
            // вызвать повторное чтение, а не пометить новую реплику прочитанной.
            changed.Add((m, fp, lines));
        }

        var deleted = states.Keys.Where(id => !seen.Contains(id)).ToList();
        if (changed.Count == 0 && deleted.Count == 0) return 0;

        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in deleted)
                DeleteMeetingLocked(id);
            foreach (var (m, fp, lines) in changed)
            {
                ReplaceDocsLocked(m, fp, lines);
            }
            tx.Commit();
        }
        return changed.Count;
    }

    /// <summary>Полный rebuild: чистит индекс; следующий SyncAsync зальёт всё заново.</summary>
    public void Rebuild()
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            Exec("DELETE FROM mtg_vec; DELETE FROM mtg_doc; DELETE FROM mtg_state;");
            tx.Commit();
        }
    }

    private void ReplaceDocsLocked(MeetingInfo m, string fp, List<MeetingJournalLine> lines)
    {
        // Внешний ключ mtg_doc -> mtg_state требует, чтобы состояние встречи
        // существовало до вставки реплик. Раньше upsert стоял после INSERT,
        // из-за чего весь sync откатывался при включённых foreign_keys.
        using (var up = _db.CreateCommand())
        {
            up.CommandText = """
                INSERT INTO mtg_state(meeting_id, journal_path, started_utc, fingerprint, vec_tag)
                VALUES($id, $p, $s, $f, NULL)
                ON CONFLICT(meeting_id) DO UPDATE SET journal_path=$p, started_utc=$s,
                    fingerprint=$f, vec_tag=NULL;
                """;
            up.Parameters.AddWithValue("$id", m.Id);
            up.Parameters.AddWithValue("$p", m.JournalPath ?? "");
            up.Parameters.AddWithValue("$s", m.StartedAt.ToString("o"));
            up.Parameters.AddWithValue("$f", fp);
            up.ExecuteNonQuery();
        }

        using (var del = _db.CreateCommand())
        {
            del.CommandText = "DELETE FROM mtg_doc WHERE meeting_id=$id";
            del.Parameters.AddWithValue("$id", m.Id);
            del.ExecuteNonQuery(); // триггер сносит FTS-строки; vec — каскадом FK
        }

        using (var ins = _db.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO mtg_doc(meeting_id, line_idx, ts, role, speaker, mid, text)
                VALUES($m, $i, $ts, $r, $s, $mid, $t);
                """;
            var pm = ins.Parameters.AddWithValue("$m", m.Id);
            var pi = ins.Parameters.Add("$i", SqliteType.Integer);
            var pts = ins.Parameters.Add("$ts", SqliteType.Text);
            var pr = ins.Parameters.Add("$r", SqliteType.Text);
            var ps = ins.Parameters.Add("$s", SqliteType.Text);
            var pmid = ins.Parameters.Add("$mid", SqliteType.Text);
            var pt = ins.Parameters.Add("$t", SqliteType.Text);
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                pi.Value = i;
                pts.Value = l.Ts ?? "";
                pr.Value = l.Role ?? "";
                ps.Value = (object?)l.Speaker ?? DBNull.Value;
                pmid.Value = (object?)l.MessageId ?? DBNull.Value;
                pt.Value = l.Text ?? "";
                ins.ExecuteNonQuery();
            }
            if (!string.IsNullOrEmpty(m.Summary))
            {
                pi.Value = -1;
                pts.Value = "";
                pr.Value = "summary";
                pmid.Value = DBNull.Value;
                pt.Value = m.Summary;
                ins.ExecuteNonQuery();
            }
        }
    }

    private void DeleteMeetingLocked(string meetingId)
    {
        using var c1 = _db.CreateCommand();
        c1.CommandText = "DELETE FROM mtg_doc WHERE meeting_id=$id";
        c1.Parameters.AddWithValue("$id", meetingId);
        c1.ExecuteNonQuery();
        using var c2 = _db.CreateCommand();
        c2.CommandText = "DELETE FROM mtg_state WHERE meeting_id=$id";
        c2.Parameters.AddWithValue("$id", meetingId);
        c2.ExecuteNonQuery();
    }

    /// <summary>Точечное удаление (например, если вызывающий знает об удалении раньше sync).</summary>
    public void DeleteMeeting(string meetingId)
    {
        lock (_gate) DeleteMeetingLocked(meetingId);
    }

    // ---------------- поиск ----------------

    /// <summary>FTS5-кандидаты по MATCH-выражению; сортировка bm25, лимит сверху.</summary>
    public List<Doc> SearchFts(string matchQuery, int limit, string? excludeMeetingId)
    {
        var list = new List<Doc>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT d.id, d.meeting_id, d.line_idx, d.ts, d.role, d.speaker, d.mid, d.text,
                       s.journal_path, s.started_utc, bm25(mtg_fts)
                FROM mtg_fts
                JOIN mtg_doc d ON d.id = mtg_fts.rowid
                JOIN mtg_state s ON s.meeting_id = d.meeting_id
                WHERE mtg_fts MATCH $q
                {(excludeMeetingId != null ? "AND d.meeting_id <> $ex" : "")}
                ORDER BY bm25(mtg_fts) LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$q", matchQuery);
            cmd.Parameters.AddWithValue("$lim", limit);
            if (excludeMeetingId != null) cmd.Parameters.AddWithValue("$ex", excludeMeetingId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadDoc(r));
        }
        return list;
    }

    /// <summary>Документы с векторами модели (для косинусной ветки поиска).</summary>
    public List<(Doc D, float[] Vec)> LoadVecs(string modelTag)
    {
        var list = new List<(Doc, float[])>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT d.id, d.meeting_id, d.line_idx, d.ts, d.role, d.speaker, d.mid, d.text,
                       s.journal_path, s.started_utc, 0.0, v.vec
                FROM mtg_vec v
                JOIN mtg_doc d ON d.id = v.doc_id
                JOIN mtg_state s ON s.meeting_id = d.meeting_id
                WHERE v.model = $tag;
                """;
            cmd.Parameters.AddWithValue("$tag", modelTag);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((ReadDoc(r), Memory.Embeddings.VectorMath.FromBytes((byte[])r[11])));
        }
        return list;
    }

    /// <summary>Штамп векторного слоя для кэша (count+max id по модели).</summary>
    public (long Count, long MaxId) VecStamp(string modelTag)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*), COALESCE(MAX(doc_id),0) FROM mtg_vec WHERE model=$tag";
            cmd.Parameters.AddWithValue("$tag", modelTag);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
        }
    }

    /// <summary>Документы, достойные вектора, но ещё без него (для фонового job'а).</summary>
    public List<Doc> DocsNeedingVec(string modelTag, int limit)
    {
        var list = new List<Doc>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT d.id, d.meeting_id, d.line_idx, d.ts, d.role, d.speaker, d.mid, d.text,
                       s.journal_path, s.started_utc, 0.0
                FROM mtg_doc d
                JOIN mtg_state s ON s.meeting_id = d.meeting_id
                WHERE (d.line_idx = -1 OR length(d.text) >= $min)
                  AND NOT EXISTS (SELECT 1 FROM mtg_vec v WHERE v.doc_id = d.id AND v.model = $tag)
                ORDER BY d.id LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$min", MinVecTextLen);
            cmd.Parameters.AddWithValue("$tag", modelTag);
            cmd.Parameters.AddWithValue("$lim", limit);
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(ReadDoc(r));
            if (list.Count == 0)
                CompleteEmbeddedMeetingsLocked(modelTag);
        }
        return list;
    }

    /// <summary>Сохранить пачку векторов; после записи домечивает встречи,
    /// у которых все достойные документы покрыты (vec_tag = tag).</summary>
    public void SaveVecs(IReadOnlyList<(long DocId, float[] Vec)> vecs, string modelTag)
    {
        if (vecs.Count == 0) return;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO mtg_vec(doc_id, model, dim, vec) VALUES($id, $m, $d, $v)
                    ON CONFLICT(doc_id, model) DO UPDATE SET dim=$d, vec=$v;
                    """;
                var pid = cmd.Parameters.Add("$id", SqliteType.Integer);
                var pm = cmd.Parameters.AddWithValue("$m", modelTag);
                var pd = cmd.Parameters.Add("$d", SqliteType.Integer);
                var pv = cmd.Parameters.Add("$v", SqliteType.Blob);
                foreach (var (docId, vec) in vecs)
                {
                    pid.Value = docId;
                    pd.Value = vec.Length;
                    pv.Value = Memory.Embeddings.VectorMath.ToBytes(vec);
                    cmd.ExecuteNonQuery();
                }
            }
            CompleteEmbeddedMeetingsLocked(modelTag);
            tx.Commit();
        }
    }

    /// <summary>vec_tag = tag встречам без непокрытых достойных документов.</summary>
    private void CompleteEmbeddedMeetingsLocked(string modelTag)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE mtg_state SET vec_tag=$tag
            WHERE (vec_tag IS NULL OR vec_tag <> $tag)
              AND NOT EXISTS (
                SELECT 1 FROM mtg_doc d
                WHERE d.meeting_id = mtg_state.meeting_id
                  AND (d.line_idx = -1 OR length(d.text) >= $min)
                  AND NOT EXISTS (SELECT 1 FROM mtg_vec v
                                  WHERE v.doc_id = d.id AND v.model = $tag));
            """;
        cmd.Parameters.AddWithValue("$tag", modelTag);
        cmd.Parameters.AddWithValue("$min", MinVecTextLen);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Встречи, где векторная обвязка для модели ещё не завершена.</summary>
    public int CountPendingVecMeetings(string modelTag)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM mtg_state WHERE vec_tag IS NULL OR vec_tag <> $tag";
            cmd.Parameters.AddWithValue("$tag", modelTag);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>Соседние реплики для контекста hit'а (до/после по line_idx).</summary>
    public (string Before, string After) GetContext(string meetingId, int lineIdx)
    {
        var before = "";
        var after = "";
        if (lineIdx < 0) return (before, after); // саммари — соседей нет
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT line_idx, text FROM mtg_doc
                WHERE meeting_id=$m AND line_idx IN ($a, $b) ORDER BY line_idx;
                """;
            cmd.Parameters.AddWithValue("$m", meetingId);
            cmd.Parameters.AddWithValue("$a", lineIdx - 1);
            cmd.Parameters.AddWithValue("$b", lineIdx + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.GetInt32(0) == lineIdx - 1) before = r.GetString(1);
                else after = r.GetString(1);
            }
        }
        return (before, after);
    }

    /// <summary>Статистика индекса (встреч/документов).</summary>
    public (int Meetings, int Docs) Stats()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT (SELECT COUNT(*) FROM mtg_state), (SELECT COUNT(*) FROM mtg_doc)";
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
        }
    }

    private static Doc ReadDoc(SqliteDataReader r)
    {
        var d = new Doc
        {
            Id = r.GetInt64(0),
            MeetingId = r.GetString(1),
            LineIdx = r.GetInt32(2),
            Ts = r.IsDBNull(3) ? "" : r.GetString(3),
            Role = r.IsDBNull(4) ? "" : r.GetString(4),
            Speaker = r.IsDBNull(5) ? null : r.GetString(5),
            Mid = r.IsDBNull(6) ? null : r.GetString(6),
            Text = r.IsDBNull(7) ? "" : r.GetString(7),
            JournalPath = r.IsDBNull(8) ? "" : r.GetString(8),
            Bm25 = r.IsDBNull(10) ? 0 : r.GetDouble(10),
        };
        if (!r.IsDBNull(9) && DateTime.TryParse(r.GetString(9), out var st))
            d.StartedAt = st;
        return d;
    }

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
