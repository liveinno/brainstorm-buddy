using Microsoft.Data.Sqlite;

namespace BrainstormBuddy.Memory.Storage;

/// <summary>
/// Идемпотентные миграции memory.db. schema_version хранит номер применённой версии
/// (одна строка на версию, текущая = MAX(v)). v1 создаёт весь schema по контракту
/// (.work/memory-contracts.md §SQLite schema v1) + FTS5 external-content таблицу
/// и sync-триггеры. Если FTS5 недоступен в этой сборке SQLite — таблица/триггеры
/// пропускаются, возвращается false и SearchFts() работает через LIKE-fallback.
/// </summary>
public static class MemoryDbMigrator
{
    public const int LatestVersion = 2;
    public const string LogCategory = "Memory";

    /// <summary>
    /// Применить все отложенные миграции. Безопасно вызывать повторно.
    /// Возвращает true, если FTS5 доступен (probe = CREATE VIRTUAL TABLE … fts5).
    /// </summary>
    public static bool Migrate(SqliteConnection db, Action<string>? warn = null)
    {
        EnsureVersionTable(db);

        var version = GetVersion(db);
        if (version < 1)
        {
            using var tx = db.BeginTransaction();
            ApplyV1(db);
            SetVersion(db, 1);
            tx.Commit();
        }
        if (version < 2)
        {
            using var tx = db.BeginTransaction();
            ApplyV2(db);
            SetVersion(db, 2);
            tx.Commit();
        }

        // FTS5-probe на каждый Migrate: если движок без FTS5, CREATE VIRTUAL TABLE
        // бросит SqliteException → схема всё равно работает, FTS отключаем.
        var ftsExisted = TableExists(db, "mem_item_fts");
        if (TryCreateFtsTable(db, warn))
        {
            CreateFtsTriggers(db);
            if (!ftsExisted)
                RebuildFts(db); // FTS появился после того, как items уже писались — догоняем
            return true;
        }
        return false;
    }

    private static void EnsureVersionTable(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS schema_version(v INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    private static int GetVersion(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(v),0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetVersion(SqliteConnection db, int v)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO schema_version(v) VALUES($v);";
        cmd.Parameters.AddWithValue("$v", v);
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection db, string name)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool TryCreateFtsTable(SqliteConnection db, Action<string>? warn)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS mem_item_fts USING fts5(
                    content, content='mem_item', content_rowid='id',
                    tokenize='unicode61 remove_diacritics 2');
                """;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            warn?.Invoke($"FTS5 недоступен, SearchFts будет использовать LIKE-fallback: {ex.Message}");
            return false;
        }
    }

    /// <summary>Триггеры держат external-content FTS в синхроне с mem_item.</summary>
    private static void CreateFtsTriggers(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TRIGGER IF NOT EXISTS mem_item_ai AFTER INSERT ON mem_item BEGIN
                INSERT INTO mem_item_fts(rowid, content) VALUES (new.id, new.content);
            END;
            CREATE TRIGGER IF NOT EXISTS mem_item_ad AFTER DELETE ON mem_item BEGIN
                INSERT INTO mem_item_fts(mem_item_fts, rowid, content)
                    VALUES('delete', old.id, old.content);
            END;
            CREATE TRIGGER IF NOT EXISTS mem_item_au AFTER UPDATE OF content ON mem_item BEGIN
                INSERT INTO mem_item_fts(mem_item_fts, rowid, content)
                    VALUES('delete', old.id, old.content);
                INSERT INTO mem_item_fts(rowid, content) VALUES (new.id, new.content);
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    private static void RebuildFts(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        // Штатный external-content rebuild: переливает mem_item в индекс целиком.
        cmd.CommandText = "INSERT INTO mem_item_fts(mem_item_fts) VALUES('rebuild');";
        cmd.ExecuteNonQuery();
    }

    private static void ApplyV1(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS mem_project(
                id          INTEGER PRIMARY KEY,
                name        TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                archived    INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS mem_person(
                id          INTEGER PRIMARY KEY,
                name        TEXT NOT NULL UNIQUE,
                notes       TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS mem_meeting(
                id          INTEGER PRIMARY KEY,
                title       TEXT NOT NULL DEFAULT '',
                project_id  INTEGER NULL REFERENCES mem_project(id),
                started_utc TEXT NOT NULL,
                ended_utc   TEXT NULL,
                memory_mode TEXT NOT NULL DEFAULT 'smart',
                use_memory  INTEGER NOT NULL DEFAULT 0,
                write_memory INTEGER NOT NULL DEFAULT 0,
                summary     TEXT NOT NULL DEFAULT '',
                journal_path TEXT NOT NULL DEFAULT '');

            CREATE TABLE IF NOT EXISTS mem_segment(
                id         INTEGER PRIMARY KEY,
                meeting_id INTEGER NOT NULL,
                seq        INTEGER NOT NULL,
                role       TEXT NOT NULL DEFAULT '',
                text       TEXT NOT NULL DEFAULT '',
                speaker    TEXT NULL,
                start_ms   INTEGER NULL,
                end_ms     INTEGER NULL,
                ts_utc     TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS mem_item(
                id                 INTEGER PRIMARY KEY,
                scope_type         TEXT NOT NULL,
                scope_id           INTEGER NULL,
                type               TEXT NOT NULL,
                content            TEXT NOT NULL,
                importance         REAL NOT NULL DEFAULT 0.5,
                confidence         REAL NOT NULL DEFAULT 0.5,
                pinned             INTEGER NOT NULL DEFAULT 0,
                status             TEXT NOT NULL DEFAULT 'Active',
                valid_from_utc     TEXT NOT NULL,
                valid_until_utc    TEXT NULL,
                superseded_by      INTEGER NULL,
                source_meeting_id  INTEGER NULL,
                source_segment_id  INTEGER NULL,
                source_start_ms    INTEGER NULL,
                source_end_ms      INTEGER NULL,
                speaker            TEXT NULL,
                source             TEXT NOT NULL DEFAULT 'extraction',
                created_utc        TEXT NOT NULL,
                updated_utc        TEXT NOT NULL,
                last_confirmed_utc TEXT NULL);

            CREATE TABLE IF NOT EXISTS mem_embedding(
                item_id INTEGER PRIMARY KEY REFERENCES mem_item(id) ON DELETE CASCADE,
                model   TEXT NOT NULL,
                dim     INTEGER NOT NULL,
                vec     BLOB NOT NULL);

            CREATE TABLE IF NOT EXISTS mem_tag(
                item_id INTEGER NOT NULL REFERENCES mem_item(id) ON DELETE CASCADE,
                tag     TEXT NOT NULL,
                PRIMARY KEY(item_id, tag));

            CREATE TABLE IF NOT EXISTS mem_job(
                id            INTEGER PRIMARY KEY,
                kind          TEXT NOT NULL,
                payload_json  TEXT NOT NULL DEFAULT '',
                status        TEXT NOT NULL DEFAULT 'pending',
                attempts      INTEGER NOT NULL DEFAULT 0,
                run_after_utc TEXT NULL,
                created_utc   TEXT NOT NULL,
                started_utc   TEXT NULL,
                finished_utc  TEXT NULL,
                error         TEXT NULL);

            CREATE INDEX IF NOT EXISTS idx_mem_item_scope      ON mem_item(scope_type, scope_id);
            CREATE INDEX IF NOT EXISTS idx_mem_item_status     ON mem_item(status);
            CREATE INDEX IF NOT EXISTS idx_mem_item_type       ON mem_item(type);
            CREATE INDEX IF NOT EXISTS idx_mem_segment_meeting ON mem_segment(meeting_id, seq);
            CREATE INDEX IF NOT EXISTS idx_mem_job_status      ON mem_job(status, run_after_utc);
            CREATE INDEX IF NOT EXISTS idx_mem_meeting_started ON mem_meeting(started_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>v2: повестка встречи из Pre-call Brief (PreCallBriefWindow → memory_context).</summary>
    private static void ApplyV2(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "ALTER TABLE mem_meeting ADD COLUMN agenda TEXT NOT NULL DEFAULT '';";
        cmd.ExecuteNonQuery();
    }
}
