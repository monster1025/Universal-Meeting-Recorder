using System.Globalization;

namespace UniversalMeetingRecorder.Core.Tasks;

/// <summary>
/// Чистые функции ежедневной сводки по задачам: когда её пора слать и что написать.
/// Все времена — локальные (сводка «в 10:30» — по настенным часам пользователя).
/// </summary>
public static class TaskDigest
{
    /// <summary>Пропущенную (сон, выключенное приложение) сводку догоняем, но не дольше 6 часов.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(6);

    public static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool IsDue(DateTime nowLocal, int minutesFromMidnight, string? lastSentDay)
    {
        if (lastSentDay == DayKey(nowLocal)) return false;
        var scheduled = nowLocal.Date.AddMinutes(minutesFromMidnight);
        return nowLocal >= scheduled && nowLocal < scheduled + CatchUpWindow;
    }

    /// <summary>Текст сводки; null — открытых задач нет (пустое уведомление — спам).</summary>
    public static (string Title, string Body)? Content(IEnumerable<TodoItem> tasks, DateTime nowLocal)
    {
        var open = tasks.Where(t => !t.IsCompleted).ToList();
        if (open.Count == 0) return null;

        var startOfToday = nowLocal.Date;
        var overdue = open.Where(t => t.DueDate is { } d && d.ToLocalTime() < startOfToday).ToList();
        var today = open.Where(t => t.DueDate is { } d && d.ToLocalTime().Date == startOfToday).ToList();

        var lines = new List<string>();
        if (overdue.Count > 0) lines.Add($"Просрочено ({overdue.Count}): " + Summary(overdue));
        if (today.Count > 0) lines.Add($"Сегодня ({today.Count}): " + Summary(today));
        if (lines.Count == 0) lines.Add("Срочных нет — задачи без срока или на потом.");
        return ($"Открытых задач: {open.Count}", string.Join("\n", lines));
    }

    /// <summary>До трёх заголовков, остальное — счётчиком.</summary>
    private static string Summary(List<TodoItem> tasks)
    {
        var shown = string.Join("; ", tasks.Take(3).Select(t => t.Title));
        var rest = tasks.Count - 3;
        return rest > 0 ? $"{shown} и ещё {rest}" : shown;
    }
}
