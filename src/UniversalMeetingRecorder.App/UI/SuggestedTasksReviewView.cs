using System.Windows;
using System.Windows.Controls;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Tasks;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Разбор предложенных задач (порт SuggestedTasksReview): галочка (по умолчанию — только подтверждённые
/// транскриптом), редактируемый заголовок, цитата с таймкодом или пометкой «не подтверждено»,
/// личная/рабочая (по умолчанию рабочая), срок. Используется в карточке встречи и в окне «Разбор задач».
/// </summary>
internal sealed class SuggestedTasksReviewView : StackPanel
{
    private sealed class Draft
    {
        public required SuggestedTask Suggestion;
        public required CheckBox Selected;
        public required TextBox Title;
        public required RadioButton Work;
        public required CheckBox HasDue;
        public required DatePicker Due;
    }

    private readonly Meeting _meeting;
    private readonly ActionItemService _service;
    private readonly Action _onFinish;
    private readonly List<Draft> _drafts = new();
    private readonly Button _create;

    public SuggestedTasksReviewView(Meeting meeting, ActionItemService service, Action? onFinish = null)
    {
        _meeting = meeting;
        _service = service;
        _onFinish = onFinish ?? (() => { });

        foreach (var s in meeting.SuggestedTasks ?? new List<SuggestedTask>()) Children.Add(Row(s));

        Children.Add(new TextBlock
        {
            Text = "Невыбранные предложения будут отклонены и больше не появятся.",
            Style = (Style)Application.Current.Resources["Caption"],
            Margin = new Thickness(0, 6, 0, 8),
        });
        _create = Ui.Button("", Create);
        var bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(_create);
        bar.Children.Add(Ui.Button("Отклонить все", () =>
        {
            _service.DismissAll(_meeting.Id);
            _onFinish();
        }));
        var recheck = Ui.Button("Проверить заново", () => _ = _service.Extract(_meeting.Id));
        DockPanel.SetDock(recheck, Dock.Right);
        bar.Children.Add(recheck);
        Children.Add(bar);
        UpdateCreateButton();
    }

    private UIElement Row(SuggestedTask s)
    {
        var selected = new CheckBox { IsChecked = s.Confirmed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        selected.Checked += (_, _) => UpdateCreateButton();
        selected.Unchecked += (_, _) => UpdateCreateButton();
        var title = new TextBox { Text = s.Title, Padding = new Thickness(3) };
        var head = new DockPanel();
        DockPanel.SetDock(selected, Dock.Left);
        head.Children.Add(selected);
        head.Children.Add(title);

        var quote = new WrapPanel { Margin = new Thickness(24, 4, 0, 0) };
        if (!s.Confirmed)
        {
            quote.Children.Add(Ui.Text("не подтверждено транскриптом  ", 12, color: Ui.Brush("Orange"), wrap: false));
        }
        else if (s.Timestamp is { } t)
        {
            quote.Children.Add(Ui.Text(Ui.Timecode(t) + "  ", 12, color: Ui.Brush("Secondary"), wrap: false));
        }
        quote.Children.Add(new TextBox
        {
            Text = $"«{s.Quote}»",
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 60,
            Foreground = Ui.Brush("Secondary"),
            FontSize = 12,
        });

        var group = "scope-" + s.Id;
        var work = new RadioButton { Content = "Рабочая", GroupName = group, IsChecked = true, Margin = new Thickness(0, 0, 10, 0) };
        var personal = new RadioButton { Content = "Личная", GroupName = group, Margin = new Thickness(0, 0, 16, 0) };
        var hasDue = new CheckBox { Content = "Срок", IsChecked = s.DueDate is not null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var due = new DatePicker { SelectedDate = (s.DueDate ?? DateTime.UtcNow).ToLocalTime().Date, Visibility = s.DueDate is null ? Visibility.Collapsed : Visibility.Visible, Width = 130 };
        hasDue.Checked += (_, _) => due.Visibility = Visibility.Visible;
        hasDue.Unchecked += (_, _) => due.Visibility = Visibility.Collapsed;
        var options = Ui.Row(work, personal, hasDue, due);
        options.Margin = new Thickness(24, 6, 0, 0);

        _drafts.Add(new Draft { Suggestion = s, Selected = selected, Title = title, Work = work, HasDue = hasDue, Due = due });
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(head);
        panel.Children.Add(quote);
        panel.Children.Add(options);
        return panel;
    }

    private void UpdateCreateButton()
    {
        var count = _drafts.Count(d => d.Selected.IsChecked == true);
        _create.Content = $"Создать выбранные ({count})";
        _create.IsEnabled = count > 0;
    }

    private void Create()
    {
        var accepted = _drafts
            .Where(d => d.Selected.IsChecked == true)
            .Select(d => new TaskDraft(
                d.Title.Text,
                d.Work.IsChecked == true ? TaskScope.Work : TaskScope.Personal,
                d.HasDue.IsChecked == true && d.Due.SelectedDate is { } date ? DateTime.SpecifyKind(date.Date, DateTimeKind.Local).ToUniversalTime() : null))
            .ToList();
        if (_service.Resolve(_meeting.Id, accepted)) _onFinish();
    }
}
