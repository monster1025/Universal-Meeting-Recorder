using System.ComponentModel;
using System.Windows.Threading;
using UniversalMeetingRecorder.App.Notifications;
using UniversalMeetingRecorder.App.Recorder;
using UniversalMeetingRecorder.App.UI;
using UniversalMeetingRecorder.Core.Calendar;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Util;
using Microsoft.Win32;

namespace UniversalMeetingRecorder.App.Calendar;

/// <summary>
/// Напоминания «встреча идёт, а запись не включена» (порт MeetingReminderScheduler): тик 15 с,
/// решения — в <see cref="ReminderPolicy"/>. Держит красную обводку экрана в том же состоянии.
/// Тик — лишь зернистость проверки, дедлайны — по настенным часам; после сна — внеочередной тик.
/// </summary>
internal sealed class MeetingReminderScheduler : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly OutlookCalendarService _calendar;
    private readonly ToastNotifier _notifier;
    private readonly RecordingCoordinator _coordinator;
    private readonly ReminderBorderController _border;
    private readonly ReminderPolicy _policy = new();
    private readonly DispatcherTimer _timer;
    private bool _ticking;
    private OngoingMeeting? _ongoing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MeetingReminderScheduler(AppSettings settings, OutlookCalendarService calendar, ToastNotifier notifier, RecordingCoordinator coordinator, ReminderBorderController border)
    {
        _settings = settings;
        _calendar = calendar;
        _notifier = notifier;
        _coordinator = coordinator;
        _border = border;
        _timer = new DispatcherTimer { Interval = ReminderPolicy.TickInterval };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Идущая встреча — для меню («Открыть ссылку конференции») и настроек. Считается и при выключенных напоминаниях.</summary>
    public OngoingMeeting? Ongoing
    {
        get => _ongoing;
        private set
        {
            if (Equals(_ongoing, value)) return;
            _ongoing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ongoing)));
        }
    }

    public void Start(Dispatcher ui)
    {
        _notifier.ActionInvoked += action => ui.BeginInvoke(() => Handle(action));
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) ui.BeginInvoke(Tick);
        };
        _coordinator.PropertyChanged += (_, e) =>
        {
            // Запись началась — обводку и напоминание убираем сразу, не дожидаясь тика.
            if (e.PropertyName == nameof(RecordingCoordinator.IsRecording)) Tick();
        };
        _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _timer.Stop();
        _notifier.ClearReminder();
        _border.Update(false);
    }

    /// <summary>Идемпотентен: таймер, пробуждение, смена настроек.</summary>
    public async void Tick()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            var now = DateTime.UtcNow;
            List<OngoingMeeting> ongoing;
            try
            {
                ongoing = _settings.OutlookCalendarEnabled
                    ? await _calendar.OngoingMeetings(_settings.ReminderCalendarIds, now)
                    : new List<OngoingMeeting>();
            }
            catch (Exception ex)
            {
                Log.Error("Reminder: запрос календаря", ex);
                ongoing = new List<OngoingMeeting>();
            }
            Ongoing = ongoing.FirstOrDefault();
            var decision = _policy.Tick(now, _settings.MeetingReminderEnabled, _coordinator.IsRecording, Ongoing);
            if (decision.ClearNotification) _notifier.ClearReminder();
            _border.Update(decision.BorderVisible && _settings.ReminderBorderEnabled);
            if (decision.Notify is { } n) _notifier.PresentReminder(n.Title, n.Body);
        }
        finally
        {
            _ticking = false;
        }
    }

    private void Handle(ReminderAction action)
    {
        switch (action)
        {
            case ReminderAction.Snooze1: _policy.Snooze(DateTime.UtcNow, TimeSpan.FromSeconds(60)); break;
            case ReminderAction.Snooze5: _policy.Snooze(DateTime.UtcNow, TimeSpan.FromSeconds(300)); break;
            case ReminderAction.Disable: _policy.DisableForMeeting(); break;
        }
        _notifier.ClearReminder();
        // Подавление означает «не напоминать», а рамка — самое заметное напоминание.
        _border.Update(false);
    }
}
