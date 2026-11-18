using System.Globalization;

namespace UniversalMeetingRecorder.Core.Calendar;

/// <summary>Что делать после тика планировщика напоминаний.</summary>
public sealed record ReminderDecision(bool BorderVisible, bool ClearNotification, (string Title, string Body)? Notify);

/// <summary>
/// Мозг напоминаний «встреча идёт, а запись не включена» без платформы (порт MeetingReminderScheduler.tick):
/// первое напоминание сразу на старте встречи, дальше раз в 5 минут; «отложить» на 1/5 минут
/// и «отключить до конца встречи» действуют на вхождение (ключ включает время старта).
/// Подавление живёт в памяти — горизонт минуты. Сменилась встреча — всё с чистого листа.
/// </summary>
public sealed class ReminderPolicy
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ReminderInterval = TimeSpan.FromSeconds(300);
    public const string NotificationTitle = "Встреча идёт, а запись не включена";

    private readonly Dictionary<string, DateTime> _suppressedUntil = new();
    private OngoingMeeting? _active;
    private DateTime? _nextFire;

    public OngoingMeeting? ActiveMeeting => _active;

    /// <param name="ongoing">Первая идущая встреча (null — нет или нет доступа к календарю).</param>
    public ReminderDecision Tick(DateTime nowUtc, bool enabled, bool isRecording, OngoingMeeting? ongoing)
    {
        foreach (var key in _suppressedUntil.Where(p => p.Value <= nowUtc).Select(p => p.Key).ToList()) _suppressedUntil.Remove(key);

        if (!enabled || isRecording || ongoing is null)
        {
            ClearActive();
            return new ReminderDecision(false, true, null);
        }
        if (_active?.Id != ongoing.Id)
        {
            ClearActive();
            _active = ongoing;
            _nextFire = nowUtc;
        }
        if (_suppressedUntil.TryGetValue(ongoing.Id, out var until) && until > nowUtc)
        {
            return new ReminderDecision(false, true, null);
        }
        if (nowUtc < (_nextFire ?? nowUtc)) return new ReminderDecision(true, false, null);

        _nextFire = nowUtc + ReminderInterval;
        var started = ongoing.StartUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        return new ReminderDecision(true, false, (NotificationTitle, $"«{ongoing.Title}» началась в {started}."));
    }

    /// <summary>Отложить напоминания по текущей встрече.</summary>
    public void Snooze(DateTime nowUtc, TimeSpan interval)
    {
        if (_active is { } a) _suppressedUntil[a.Id] = nowUtc + interval;
    }

    /// <summary>Молчать до конца этого вхождения (глобальный выключатель — в настройках).</summary>
    public void DisableForMeeting()
    {
        if (_active is { } a) _suppressedUntil[a.Id] = a.EndUtc;
    }

    private void ClearActive()
    {
        _active = null;
        _nextFire = null;
    }
}
