using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>Единственные экземпляры окон приложения + очередь окна разбора задач.</summary>
internal sealed class WindowManager
{
    private readonly AppServices _s;
    private MeetingsWindow? _meetings;
    private TasksWindow? _tasks;
    private SettingsWindow? _settings;
    private TaskReviewWindow? _review;
    private Guid? _reviewing;
    private readonly Queue<Guid> _reviewQueue = new();
    private TaskWidgetWindow? _widget;

    public static ImageSource? AppIcon { get; } = LoadIcon();

    public WindowManager(AppServices services) => _s = services;

    private static ImageSource? LoadIcon()
    {
        try
        {
            return BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        }
        catch
        {
            return null;
        }
    }

    public void ShowMeetings(Guid? select = null)
    {
        if (select is { } id) _s.Meetings.RequestSelection(id);
        if (_meetings is null)
        {
            _meetings = new MeetingsWindow(_s);
            _meetings.Closed += (_, _) => _meetings = null;
        }
        else if (select is not null)
        {
            _meetings.Select(_s.Meetings.TakePendingSelection());
        }
        Ui.Activate(_meetings);
    }

    public void ShowTasks()
    {
        if (_tasks is null)
        {
            _tasks = new TasksWindow(_s);
            _tasks.Closed += (_, _) => _tasks = null;
        }
        Ui.Activate(_tasks);
    }

    public void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_s);
            _settings.Closed += (_, _) => _settings = null;
        }
        Ui.Activate(_settings);
    }

    /// <summary>Встреча с готовыми предложениями — показать сразу или поставить в очередь.</summary>
    public void EnqueueReview(Guid meetingId)
    {
        if (_reviewing == meetingId || _reviewQueue.Contains(meetingId)) return;
        if (_reviewing is null) PresentReview(meetingId);
        else _reviewQueue.Enqueue(meetingId);
    }

    private void PresentReview(Guid meetingId)
    {
        _reviewing = meetingId;
        _review = new TaskReviewWindow(_s, meetingId);
        _review.Closed += (_, _) =>
        {
            _review = null;
            _reviewing = null;
            if (_reviewQueue.Count > 0) PresentReview(_reviewQueue.Dequeue());
        };
        Ui.Activate(_review);
    }

    /// <summary>Показать или убрать виджет задач по настройке.</summary>
    public void SyncTaskWidget()
    {
        if (_s.Settings.TaskWidgetEnabled && _widget is null)
        {
            _widget = new TaskWidgetWindow(_s);
            _widget.Closed += (_, _) => _widget = null;
            _widget.Show();
        }
        else if (!_s.Settings.TaskWidgetEnabled && _widget is not null)
        {
            _widget.Close();
        }
    }
}
