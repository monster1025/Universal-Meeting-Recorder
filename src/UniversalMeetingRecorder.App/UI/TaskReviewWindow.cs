using System.Windows;
using System.Windows.Controls;
using UniversalMeetingRecorder.Core.Meetings;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Окно «Разбор задач» (порт TaskReviewWindow): открывается само, как только у встречи появились
/// предложения. Одна встреча за раз, остальные ждут в очереди; закрытие окна оставляет предложения
/// неразобранными (они остаются в карточке встречи) и показывает следующую.
/// </summary>
internal sealed class TaskReviewWindow : Window
{
    private readonly AppServices _s;
    private readonly Guid _meetingId;
    private string? _rendered;

    public TaskReviewWindow(AppServices services, Guid meetingId)
    {
        _s = services;
        _meetingId = meetingId;
        Title = "Разбор задач";
        Width = 720;
        Height = 480;
        MinWidth = 520;
        MinHeight = 320;
        Style = (Style)Application.Current.Resources["AppWindow"];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = WindowManager.AppIcon;
        _s.Meetings.Changed += OnChanged;
        Closed += (_, _) => _s.Meetings.Changed -= OnChanged;
        Render();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private void Render()
    {
        var meeting = _s.Meetings.Find(_meetingId);
        var key = meeting is null ? "" : MeetingJson.Serialize(meeting);
        if (key == _rendered) return;
        _rendered = key;

        var panel = new StackPanel { Margin = new Thickness(20) };
        if (meeting is null)
        {
            panel.Children.Add(Ui.Caption("Встреча не найдена"));
            Content = panel;
            return;
        }
        panel.Children.Add(Ui.Text(meeting.DisplayTitle, 15, FontWeights.SemiBold));
        var date = Ui.Caption(Ui.DateTimeShort(meeting.StartDate));
        date.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(date);

        if (meeting.ExtractionState == ExtractionState.Pending)
        {
            panel.Children.Add(Ui.Row(Ui.Spinner(), Ui.Text("Ищу задачи в транскрипте…")));
        }
        else if ((meeting.SuggestedTasks?.Count ?? 0) == 0)
        {
            panel.Children.Add(Ui.Text("Предложений нет — задачи для вас не найдены."));
            var close = Ui.Button("Закрыть", Close);
            close.Margin = new Thickness(0, 16, 0, 0);
            panel.Children.Add(close);
        }
        else
        {
            panel.Children.Add(new SuggestedTasksReviewView(meeting, _s.ActionItems, Close));
        }
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
