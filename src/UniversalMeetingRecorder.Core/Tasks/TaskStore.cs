using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Util;
using Microsoft.Data.Sqlite;

namespace UniversalMeetingRecorder.Core.Tasks;

/// <summary>
/// Список задач. Источник истины — SQLite. Вместо GRDB ValueObservation — перечитывание после
/// каждой записи и событие <see cref="Changed"/>: процесс — единственный писатель базы.
/// Ошибки — в <see cref="LastError"/> для показа в UI.
/// </summary>
public sealed class TaskStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection? _conn;
    private List<TodoItem> _tasks = new();
    private Dictionary<string, List<TaskMeetingLink>> _links = new();

    public string? LastError { get; private set; }

    /// <summary>Список или связи изменились (произвольный поток).</summary>
    public event Action? Changed;

    public TaskStore(string path)
    {
        try
        {
            _conn = TaskDatabase.Open(path);
            Reload();
        }
        catch (Exception ex)
        {
            LastError = $"Не удалось открыть базу задач: {ex.Message}";
            Log.Error("TaskStore: открытие базы", ex);
        }
    }

    /// <summary>Все задачи (открытые и закрытые) в порядке показа.</summary>
    public IReadOnlyList<TodoItem> Tasks
    {
        get
        {
            lock (_gate) return _tasks.Select(t => t.Clone()).ToList();
        }
    }

    public IReadOnlyList<TaskMeetingLink> LinksFor(string taskId)
    {
        lock (_gate) return _links.TryGetValue(taskId, out var l) ? l.ToList() : new List<TaskMeetingLink>();
    }

    /// <summary>
    /// Открытые: по сроку (без срока — в конец), затем по созданию. Закрытые — после открытых,
    /// по completedAt от новых к старым.
    /// </summary>
    public static List<TodoItem> Sorted(IEnumerable<TodoItem> items)
    {
        var list = items.ToList();
        var open = list.Where(t => !t.IsCompleted).ToList();
        open.Sort((a, b) =>
        {
            if (a.DueDate is { } da && b.DueDate is { } db && da != db) return da.CompareTo(db);
            if (a.DueDate is not null && b.DueDate is null) return -1;
            if (a.DueDate is null && b.DueDate is not null) return 1;
            return a.CreatedAt.CompareTo(b.CreatedAt);
        });
        var done = list.Where(t => t.IsCompleted)
            .OrderByDescending(t => t.CompletedAt ?? DateTime.MinValue)
            .ToList();
        return open.Concat(done).ToList();
    }

    public bool Add(string title, TaskScope scope, DateTime? dueDate = null, string? notes = null)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return false;
        var item = TodoItem.Create(trimmed, scope, dueDate, notes);
        return Write((conn, tx) => Insert(conn, tx, item));
    }

    /// <summary>Задачи из встречи вместе со связями — одной транзакцией.</summary>
    public bool AddTasks(IEnumerable<TaskDraft> drafts, Meeting meeting)
    {
        var cleaned = drafts
            .Select(d => d with { Title = d.Title.Trim() })
            .Where(d => d.Title.Length > 0)
            .ToList();
        if (cleaned.Count == 0) return true;
        var meetingTitle = meeting.DisplayTitle;
        return Write((conn, tx) =>
        {
            foreach (var draft in cleaned)
            {
                var item = TodoItem.Create(draft.Title, draft.Scope, draft.DueDate);
                Insert(conn, tx, item);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO task_meeting_link (task_id, speakr_recording_id, meeting_uuid, meeting_title, meeting_date)
                    VALUES ($task, $speakr, $uuid, $title, $date)
                    """;
                cmd.Parameters.AddWithValue("$task", item.Id);
                cmd.Parameters.AddWithValue("$speakr", (object?)meeting.SpeakrRecordingID ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$uuid", MeetingJson.Uuid(meeting.Id));
                cmd.Parameters.AddWithValue("$title", meetingTitle);
                cmd.Parameters.AddWithValue("$date", IsoDate.ToUnixSeconds(meeting.StartDate));
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>Заголовки задач, уже созданных из этой встречи, — для дедупликации предложений.</summary>
    public List<string> TitlesLinkedToMeeting(Guid meetingId)
    {
        var key = MeetingJson.Uuid(meetingId);
        lock (_gate)
        {
            return _tasks
                .Where(t => _links.TryGetValue(t.Id, out var l) && l.Any(x => string.Equals(x.MeetingUuid, key, StringComparison.OrdinalIgnoreCase)))
                .Select(t => t.Title)
                .ToList();
        }
    }

    public void Update(TodoItem item)
    {
        var copy = item.Clone();
        copy.UpdatedAt = DateTime.UtcNow;
        Write((conn, tx) => UpdateRow(conn, tx, copy));
    }

    public void ToggleComplete(TodoItem item)
    {
        var copy = item.Clone();
        var now = DateTime.UtcNow;
        copy.IsCompleted = !copy.IsCompleted;
        copy.CompletedAt = copy.IsCompleted ? now : null;
        copy.UpdatedAt = now;
        Write((conn, tx) => UpdateRow(conn, tx, copy));
    }

    public void Delete(TodoItem item)
    {
        Write((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM task WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Перечитать базу (на случай внешней правки файла).</summary>
    public void Reload()
    {
        if (_conn is null) return;
        try
        {
            List<TodoItem> items;
            List<TaskMeetingLink> links;
            lock (_gate)
            {
                items = ReadTasks(_conn);
                links = ReadLinks(_conn);
                _tasks = Sorted(items);
                _links = links.GroupBy(l => l.TaskId).ToDictionary(g => g.Key, g => g.ToList());
            }
        }
        catch (Exception ex)
        {
            LastError = $"Чтение задач: {ex.Message}";
            Log.Error("TaskStore: чтение", ex);
        }
        Changed?.Invoke();
    }

    private bool Write(Action<SqliteConnection, SqliteTransaction> body)
    {
        if (_conn is null)
        {
            LastError ??= "База задач недоступна";
            Changed?.Invoke();
            return false;
        }
        try
        {
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                body(_conn, tx);
                tx.Commit();
            }
            LastError = null;
            Reload();
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Запись задачи: {ex.Message}";
            Log.Error("TaskStore: запись", ex);
            Changed?.Invoke();
            return false;
        }
    }

    private static void Insert(SqliteConnection conn, SqliteTransaction tx, TodoItem t)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO task (id, title, notes, scope, due_date, is_completed, completed_at, created_at, updated_at)
            VALUES ($id, $title, $notes, $scope, $due, $done, $completedAt, $created, $updated)
            """;
        Bind(cmd, t);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateRow(SqliteConnection conn, SqliteTransaction tx, TodoItem t)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE task SET title = $title, notes = $notes, scope = $scope, due_date = $due,
                is_completed = $done, completed_at = $completedAt, created_at = $created, updated_at = $updated
            WHERE id = $id
            """;
        Bind(cmd, t);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, TodoItem t)
    {
        cmd.Parameters.AddWithValue("$id", t.Id);
        cmd.Parameters.AddWithValue("$title", t.Title);
        cmd.Parameters.AddWithValue("$notes", (object?)t.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scope", t.Scope.ToDb());
        cmd.Parameters.AddWithValue("$due", t.DueDate is { } d ? IsoDate.ToUnixSeconds(d) : DBNull.Value);
        cmd.Parameters.AddWithValue("$done", t.IsCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$completedAt", t.CompletedAt is { } c ? IsoDate.ToUnixSeconds(c) : DBNull.Value);
        cmd.Parameters.AddWithValue("$created", IsoDate.ToUnixSeconds(t.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", IsoDate.ToUnixSeconds(t.UpdatedAt));
    }

    private static List<TodoItem> ReadTasks(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, notes, scope, due_date, is_completed, completed_at, created_at, updated_at FROM task";
        using var r = cmd.ExecuteReader();
        var result = new List<TodoItem>();
        while (r.Read())
        {
            result.Add(new TodoItem
            {
                Id = r.GetString(0),
                Title = r.GetString(1),
                Notes = r.IsDBNull(2) ? null : r.GetString(2),
                Scope = TaskScopeExtensions.ParseScope(r.GetString(3)),
                DueDate = r.IsDBNull(4) ? null : IsoDate.FromUnixSeconds(r.GetDouble(4)),
                IsCompleted = r.GetInt64(5) != 0,
                CompletedAt = r.IsDBNull(6) ? null : IsoDate.FromUnixSeconds(r.GetDouble(6)),
                CreatedAt = IsoDate.FromUnixSeconds(r.GetDouble(7)),
                UpdatedAt = IsoDate.FromUnixSeconds(r.GetDouble(8)),
            });
        }
        return result;
    }

    private static List<TaskMeetingLink> ReadLinks(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT task_id, speakr_recording_id, meeting_uuid, meeting_title, meeting_date FROM task_meeting_link";
        using var r = cmd.ExecuteReader();
        var result = new List<TaskMeetingLink>();
        while (r.Read())
        {
            result.Add(new TaskMeetingLink(
                r.GetString(0),
                r.IsDBNull(1) ? null : (int)r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : IsoDate.FromUnixSeconds(r.GetDouble(4))));
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate) _conn?.Dispose();
    }
}
