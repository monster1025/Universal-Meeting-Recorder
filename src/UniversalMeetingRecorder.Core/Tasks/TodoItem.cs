namespace UniversalMeetingRecorder.Core.Tasks;

/// <summary>Личная или рабочая — сквозное деление списка задач. В БД — 'personal' / 'work'.</summary>
public enum TaskScope
{
    Personal,
    Work,
}

public static class TaskScopeExtensions
{
    public static string ToDb(this TaskScope s) => s == TaskScope.Personal ? "personal" : "work";

    public static TaskScope ParseScope(string s) => s == "personal" ? TaskScope.Personal : TaskScope.Work;

    public static string Title(this TaskScope s) => s == TaskScope.Personal ? "Личная" : "Рабочая";
}

/// <summary>Задача (таблица task). Даты в БД — unix-секунды REAL, id — UUID-строка в верхнем регистре.</summary>
public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D").ToUpperInvariant();
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public TaskScope Scope { get; set; }
    /// <summary>null — без срока. Всегда начало локального дня.</summary>
    public DateTime? DueDate { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static TodoItem Create(string title, TaskScope scope, DateTime? dueDate = null, string? notes = null, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return new TodoItem
        {
            Title = title,
            Notes = notes,
            Scope = scope,
            DueDate = dueDate,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Срок раньше начала сегодняшнего дня: задача «на сегодня» ещё не просрочена.</summary>
    public bool IsOverdue(DateTime? nowLocal = null)
    {
        if (IsCompleted || DueDate is not { } due) return false;
        var today = (nowLocal ?? DateTime.Now).Date;
        return due.ToLocalTime() < today;
    }

    public TodoItem Clone() => (TodoItem)MemberwiseClone();
}

/// <summary>Связь задачи со встречей (N:M). Ключ — UUID встречи; ID записи Speakr — если выгружалась.</summary>
public sealed record TaskMeetingLink(
    string TaskId,
    int? SpeakrRecordingId,
    string? MeetingUuid,
    string? MeetingTitle,
    DateTime? MeetingDate)
{
    public Guid? MeetingId => Guid.TryParse(MeetingUuid, out var g) ? g : null;
}

/// <summary>Задача, подтверждённая пользователем: без id и дат — они появятся при вставке.</summary>
public sealed record TaskDraft(string Title, TaskScope Scope, DateTime? DueDate);
