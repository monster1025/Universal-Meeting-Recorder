using Microsoft.Data.Sqlite;

namespace UniversalMeetingRecorder.Core.Tasks;

/// <summary>
/// SQLite-файл со списком задач — та же схема, что у macOS-версии (GRDB). WAL, внешние ключи.
/// Версии схемы хранятся в таблице <c>grdb_migrations</c> так же, как это делает GRDB: файл
/// базы переносим между Mac и Windows в обе стороны, и ни одна сторона не попытается
/// повторно применить уже применённую миграцию.
/// </summary>
public static class TaskDatabase
{
    private const string V1 = """
        CREATE TABLE task (
            id           TEXT PRIMARY KEY,
            title        TEXT NOT NULL,
            notes        TEXT,
            scope        TEXT NOT NULL,
            due_date     REAL,
            is_completed INTEGER NOT NULL DEFAULT 0,
            completed_at REAL,
            created_at   REAL NOT NULL,
            updated_at   REAL NOT NULL
        );
        CREATE INDEX idx_task_active ON task(is_completed, due_date);

        CREATE TABLE task_meeting_link (
            task_id             TEXT NOT NULL REFERENCES task(id) ON DELETE CASCADE,
            speakr_recording_id INTEGER,
            meeting_uuid        TEXT,
            meeting_title       TEXT,
            meeting_date        REAL,
            PRIMARY KEY (task_id, speakr_recording_id, meeting_uuid),
            CHECK (speakr_recording_id IS NOT NULL OR meeting_uuid IS NOT NULL)
        );
        """;

    private static readonly (string Id, string Sql)[] Migrations = { ("v1", V1) };

    public static SqliteConnection Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA journal_mode = WAL;");
        Exec(conn, "PRAGMA foreign_keys = ON;");
        Exec(conn, "PRAGMA busy_timeout = 5000;");
        Migrate(conn);
        return conn;
    }

    private static void Migrate(SqliteConnection conn)
    {
        // Ровно та же таблица, что создаёт GRDB DatabaseMigrator.
        Exec(conn, "CREATE TABLE IF NOT EXISTS grdb_migrations (identifier TEXT NOT NULL PRIMARY KEY);");
        var applied = new HashSet<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT identifier FROM grdb_migrations";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) applied.Add(reader.GetString(0));
        }
        foreach (var (id, sql) in Migrations)
        {
            if (applied.Contains(id)) continue;
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO grdb_migrations (identifier) VALUES ($id)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
