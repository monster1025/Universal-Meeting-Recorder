using System.Windows.Threading;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Tasks;
using Microsoft.Win32;

namespace UniversalMeetingRecorder.App.Notifications;

/// <summary>
/// Раз в день (по умолчанию в 10:30) — сводка по открытым задачам. Тик 30 с плюс после пробуждения.
/// «Отправлено сегодня» ставится, только если сводка действительно показана.
/// </summary>
internal sealed class TaskDigestScheduler
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly TaskStore _tasks;
    private readonly ToastNotifier _notifier;
    private readonly DispatcherTimer _timer;

    public TaskDigestScheduler(AppSettings settings, TaskStore tasks, ToastNotifier notifier)
    {
        _settings = settings;
        _tasks = tasks;
        _notifier = notifier;
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start(Dispatcher ui)
    {
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) ui.BeginInvoke(Tick);
        };
        _timer.Start();
        Tick();
    }

    public void Tick()
    {
        if (!_settings.TaskDigestEnabled) return;
        var now = DateTime.Now;
        if (!TaskDigest.IsDue(now, _settings.TaskDigestMinutes, _settings.TaskDigestLastSentDay)) return;
        if (Post(now)) _settings.TaskDigestLastSentDay = TaskDigest.DayKey(now);
    }

    /// <summary>Ручной показ из настроек — не трогает «уже слали сегодня». false — показывать нечего.</summary>
    public bool SendNow() => Post(DateTime.Now);

    private bool Post(DateTime now)
    {
        if (TaskDigest.Content(_tasks.Tasks, now) is not { } digest) return false;
        _notifier.PresentDigest(digest.Title, digest.Body);
        return true;
    }
}
