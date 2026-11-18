using System.Windows.Threading;
using UniversalMeetingRecorder.App.Calendar;
using UniversalMeetingRecorder.App.Notifications;
using UniversalMeetingRecorder.App.Platform;
using UniversalMeetingRecorder.App.Recorder;
using UniversalMeetingRecorder.App.Tray;
using UniversalMeetingRecorder.App.UI;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Tasks;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.App;

/// <summary>
/// Точка сборки всех долгоживущих объектов (аналог AppDelegate на Mac). Порядок старта повторяет
/// applicationDidFinishLaunching: список встреч → бэкфилл transcript.md → восстановление прерванных
/// записей (СТРОГО до запуска детектора) → загрузки → извлечение задач → детектор → календарь →
/// сводка → оверлей → фид задач.
/// </summary>
internal sealed class AppServices : IDisposable
{
    public Dispatcher Ui { get; }
    public AppSettings Settings { get; }
    public MeetingStore Meetings { get; }
    public UploadPipeline Upload { get; }
    public TaskStore Tasks { get; }
    public ActionItemService ActionItems { get; }
    public SystemMicMuteController Mute { get; }
    public RecordingCoordinator Coordinator { get; }
    public ToastNotifier Notifier { get; }
    public OutlookCalendarService Calendar { get; }
    public ReminderBorderController Border { get; }
    public MeetingReminderScheduler ReminderScheduler { get; }
    public TaskDigestScheduler Digest { get; }
    public MicMuteOverlay MuteOverlay { get; }
    public TaskFeedServer FeedServer { get; }
    public WindowManager Windows { get; }
    public TrayController Tray { get; }
    public IRecycleBin RecycleBin { get; } = new RecycleBin();
    private bool _shutDown;

    public AppServices(Dispatcher ui)
    {
        Ui = ui;
        Settings = new AppSettings(AppPaths.SettingsFile, new CredentialStore());
        Meetings = new MeetingStore(Settings.RecordingsRootPath);
        Upload = new UploadPipeline(Meetings, Settings);
        Tasks = new TaskStore(AppSettings.DefaultTasksDatabasePath);
        ActionItems = new ActionItemService(Meetings, Tasks, Settings);
        Upload.MeetingDone += id => ActionItems.ExtractIfEnabled(id);
        Mute = new SystemMicMuteController();
        Coordinator = new RecordingCoordinator(ui, Meetings, Settings, Upload, Mute);
        Notifier = new ToastNotifier();
        Calendar = new OutlookCalendarService();
        Border = new ReminderBorderController();
        ReminderScheduler = new MeetingReminderScheduler(Settings, Calendar, Notifier, Coordinator, Border);
        Digest = new TaskDigestScheduler(Settings, Tasks, Notifier);
        MuteOverlay = new MicMuteOverlay(Settings, Coordinator, Mute);
        FeedServer = new TaskFeedServer(() => TaskFeed.Encode(Tasks.Tasks, DateTime.UtcNow));
        Windows = new WindowManager(this);
        ActionItems.SuggestionsReady += id => ui.BeginInvoke(() => Windows.EnqueueReview(id));
        Tray = new TrayController(this);
    }

    public void Start()
    {
        Notifier.Initialize();
        Meetings.Refresh();
        Meetings.BackfillTranscriptMarkdown();
        Coordinator.RecoverUnfinished();
        Upload.ResumeAll();
        ActionItems.ResumePending();
        Coordinator.Start();
        ReminderScheduler.Start(Ui);
        Digest.Start(Ui);
        MuteOverlay.Start();
        FeedServer.Start();
        Tray.Show();
        Windows.SyncTaskWidget();
        Log.Info("Universal Meeting Recorder запущен");
    }

    /// <summary>
    /// Выход: сначала снять системный mute (безусловно), затем остановить всё и дождаться сведения
    /// текущей записи не дольше 5 с — недоделанное подхватит восстановление на следующем старте.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        try
        {
            Mute.SetMuted(false);
            FeedServer.Stop();
            ReminderScheduler.Stop();
            Coordinator.Stop();
            var conversion = Coordinator.StopActiveRecordingIfNeeded();
            conversion?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown", ex);
        }
    }

    public void Quit() => System.Windows.Application.Current.Shutdown();

    public void Dispose()
    {
        Tray.Dispose();
        Calendar.Dispose();
        Mute.Dispose();
        Tasks.Dispose();
    }
}
