using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UniversalMeetingRecorder.Core.Tasks;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Окно «Задачи» (порт TasksView): фильтр Все/Личные/Рабочие (запоминается), «Показать закрытые»,
/// быстрое добавление по Enter со сроком, строки с кружком выполнения, связью со встречей, сроком,
/// редактором по двойному клику / ⓘ и контекстным меню.
/// </summary>
internal sealed class TasksWindow : Window
{
    private readonly AppServices _s;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6), Visibility = Visibility.Collapsed };
    private readonly CheckBox _showCompleted = new() { Content = "Показать закрытые", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _newTitle = new() { Padding = new Thickness(4), BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _placeholder = new() { IsHitTestVisible = false, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _dueChip = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 4, 1), Margin = new Thickness(4, 0, 4, 0), Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, RadioButton> _filters = new();
    private DateTime? _newDue;

    public TasksWindow(AppServices services)
    {
        _s = services;
        Title = "Задачи";
        Width = 560;
        Height = 620;
        MinWidth = 420;
        MinHeight = 360;
        Style = (Style)Application.Current.Resources["AppWindow"];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = WindowManager.AppIcon;

        var top = new DockPanel { Margin = new Thickness(12, 12, 12, 8) };
        var filterRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, title) in new[] { ("all", "Все"), ("personal", "Личные"), ("work", "Рабочие") })
        {
            var rb = new RadioButton { Content = title, GroupName = "filter", Style = TryFindResource(typeof(ToggleButton)) as Style, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 2, 0) };
            rb.Checked += (_, _) =>
            {
                _s.Settings.TasksScopeFilter = key;
                UpdatePlaceholder();
                Render();
            };
            _filters[key] = rb;
            filterRow.Children.Add(rb);
        }
        DockPanel.SetDock(_showCompleted, Dock.Right);
        top.Children.Add(_showCompleted);
        top.Children.Add(filterRow);
        _showCompleted.Checked += (_, _) => Render();
        _showCompleted.Unchecked += (_, _) => Render();

        // Быстрое добавление.
        var calendarButton = new Button { Content = "📅", ToolTip = "Срок задачи", Padding = new Thickness(6, 0, 6, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        var popup = DuePopup(calendarButton);
        calendarButton.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        _newTitle.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (_s.Tasks.Add(_newTitle.Text, NewTaskScope, _newDue))
            {
                _newTitle.Text = "";
                SetDue(null);
            }
        };
        _newTitle.TextChanged += (_, _) => _placeholder.Visibility = _newTitle.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Foreground = Ui.Brush("Secondary");
        var inputGrid = new Grid();
        inputGrid.Children.Add(_newTitle);
        inputGrid.Children.Add(_placeholder);
        var add = new DockPanel();
        DockPanel.SetDock(calendarButton, Dock.Right);
        DockPanel.SetDock(_dueChip, Dock.Right);
        add.Children.Add(calendarButton);
        add.Children.Add(_dueChip);
        add.Children.Add(inputGrid);
        var addBorder = new Border { Child = add, BorderBrush = SystemColors.ActiveBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(12, 0, 12, 8), Padding = new Thickness(2), Background = SystemColors.WindowBrush };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(addBorder, Dock.Top);
        DockPanel.SetDock(_error, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(addBorder);
        root.Children.Add(_error);
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 0, 6, 6) });
        Content = root;

        (_filters.GetValueOrDefault(_s.Settings.TasksScopeFilter) ?? _filters["all"]).IsChecked = true;
        UpdatePlaceholder();
        _s.Tasks.Changed += OnChanged;
        _s.Meetings.Changed += OnChanged;
        Closed += (_, _) =>
        {
            _s.Tasks.Changed -= OnChanged;
            _s.Meetings.Changed -= OnChanged;
        };
        Render();
        Loaded += (_, _) => _newTitle.Focus();
    }

    private string Filter => _s.Settings.TasksScopeFilter;
    private TaskScope NewTaskScope => Filter == "personal" ? TaskScope.Personal : TaskScope.Work;

    private void UpdatePlaceholder() => _placeholder.Text = $"Новая задача — Enter ({NewTaskScope.Title().ToLowerInvariant()})";

    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private Popup DuePopup(UIElement target)
    {
        var calendar = new System.Windows.Controls.Calendar { DisplayDate = DateTime.Today };
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var syncing = false;
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (!syncing && calendar.SelectedDate is { } d)
            {
                SetDue(d);
                popup.IsOpen = false;
            }
        };
        var buttons = Ui.Row(
            Ui.Button("Сегодня", () => { SetDue(DateTime.Today); popup.IsOpen = false; }),
            Ui.Button("Завтра", () => { SetDue(DateTime.Today.AddDays(1)); popup.IsOpen = false; }),
            Ui.Button("Без срока", () => { SetDue(null); popup.IsOpen = false; }));
        buttons.Margin = new Thickness(6);
        var panel = new StackPanel { Children = { calendar, buttons } };
        popup.Child = new Border { Child = panel, Background = SystemColors.WindowBrush, BorderBrush = SystemColors.ActiveBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4) };
        popup.Opened += (_, _) =>
        {
            // Предвыбор текущего срока не должен сам себя «выбирать» и закрывать поповер.
            syncing = true;
            calendar.SelectedDate = _newDue?.ToLocalTime().Date;
            calendar.DisplayDate = _newDue?.ToLocalTime().Date ?? DateTime.Today;
            syncing = false;
        };
        return popup;
    }

    private void SetDue(DateTime? localDate)
    {
        _newDue = localDate is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Local).ToUniversalTime() : null;
        if (_newDue is { } due)
        {
            var remove = new Button { Content = "✕", ToolTip = "Убрать срок", BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(4, 0, 0, 0), FontSize = 10 };
            remove.Click += (_, _) => SetDue(null);
            _dueChip.Child = Ui.Row(Ui.Text(Ui.DayMonth(due), 12, wrap: false), remove);
            _dueChip.Background = Ui.Tint("Gray", 0.18);
            _dueChip.Visibility = Visibility.Visible;
        }
        else
        {
            _dueChip.Visibility = Visibility.Collapsed;
        }
    }

    private void Render()
    {
        _error.Text = _s.Tasks.LastError ?? "";
        _error.Visibility = _s.Tasks.LastError is null ? Visibility.Collapsed : Visibility.Visible;
        _rows.Children.Clear();
        var showCompleted = _showCompleted.IsChecked == true;
        var visible = _s.Tasks.Tasks.Where(t => (showCompleted || !t.IsCompleted) && (Filter == "all" || t.Scope.ToDb() == Filter)).ToList();
        if (visible.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = showCompleted ? "Задач нет" : "Нет открытых задач", Foreground = Ui.Brush("Secondary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) });
            return;
        }
        var meetingIds = _s.Meetings.Meetings.Select(m => m.Id).ToHashSet();
        foreach (var t in visible) _rows.Children.Add(Row(t, meetingIds));
    }

    private UIElement Row(TodoItem task, HashSet<Guid> meetingIds)
    {
        var link = _s.Tasks.LinksFor(task.Id).FirstOrDefault();
        var scopeColor = task.Scope == TaskScope.Personal ? "Pink" : "Green";

        var toggle = new Button
        {
            Content = task.IsCompleted ? "●" : "○",
            Foreground = Ui.Brush(scopeColor),
            FontSize = 16,
            Padding = new Thickness(2, -2, 6, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = task.IsCompleted ? "Открыть снова" : "Выполнено",
        };
        toggle.Click += (_, _) => _s.Tasks.ToggleComplete(task);

        var title = new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (task.IsCompleted) title.TextDecorations = TextDecorations.Strikethrough;
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Edit(task);
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(task.Notes)) right.Children.Add(new TextBlock { Text = "≡", ToolTip = task.Notes, Margin = new Thickness(6, 0, 0, 0), Foreground = Ui.Brush("Secondary") });
        if (link?.MeetingTitle is { } meetingTitle)
        {
            var label = Ui.Text("👥 " + meetingTitle, 11, color: Ui.Brush("Secondary"), wrap: false);
            label.MaxWidth = 160;
            label.ToolTip = $"Из встречи: {meetingTitle}";
            label.Margin = new Thickness(6, 0, 0, 0);
            right.Children.Add(label);
        }
        if (task.DueDate is { } due)
        {
            var dueText = Ui.Text(Ui.DayMonth(due), 12, color: task.IsOverdue() ? Ui.Brush("Red") : Ui.Brush("Secondary"), wrap: false);
            dueText.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(dueText);
        }
        if (Filter == "all") right.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(6, 0, 0, 0), Background = Ui.Tint(scopeColor, 0.15), Child = Ui.Text(task.Scope.Title(), 11, wrap: false) });
        var info = new Button { Content = "ⓘ", ToolTip = "Изменить задачу", BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(6, 0, 0, 0), Foreground = Ui.Brush("Secondary") };
        info.Click += (_, _) => Edit(task);
        right.Children.Add(info);

        var row = new DockPanel { Margin = new Thickness(6, 4, 6, 4), Opacity = task.IsCompleted ? 0.6 : 1, Background = Brushes.Transparent };
        DockPanel.SetDock(toggle, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(right);
        row.Children.Add(title);

        var menu = new ContextMenu();
        if (link?.MeetingId is { } mid && meetingIds.Contains(mid))
        {
            var open = new MenuItem { Header = "Открыть встречу…" };
            open.Click += (_, _) => _s.Windows.ShowMeetings(mid);
            menu.Items.Add(open);
        }
        var delete = new MenuItem { Header = "Удалить" };
        delete.Click += (_, _) => _s.Tasks.Delete(task);
        menu.Items.Add(delete);
        row.ContextMenu = menu;

        return new Border { Child = row, BorderBrush = Ui.Tint("Gray", 0.2), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private void Edit(TodoItem task)
    {
        var editor = new TaskEditorWindow(task, _s.Tasks.LinksFor(task.Id).FirstOrDefault()?.MeetingTitle) { Owner = this };
        if (editor.ShowDialog() == true) _s.Tasks.Update(editor.Result!);
    }
}

/// <summary>Редактор задачи (на Mac — поповер): название, описание, личная/рабочая, срок.</summary>
internal sealed class TaskEditorWindow : Window
{
    public TodoItem? Result { get; private set; }

    public TaskEditorWindow(TodoItem task, string? meetingTitle)
    {
        Title = "Задача";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Style = (Style)Application.Current.Resources["AppWindow"];

        var title = new TextBox { Text = task.Title, Padding = new Thickness(4) };
        var notes = new TextBox { Text = task.Notes ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4) };
        var work = new RadioButton { Content = "Рабочая", GroupName = "scope", IsChecked = task.Scope == TaskScope.Work, Margin = new Thickness(0, 0, 12, 0) };
        var personal = new RadioButton { Content = "Личная", GroupName = "scope", IsChecked = task.Scope == TaskScope.Personal };
        var hasDue = new CheckBox { Content = "Срок", IsChecked = task.DueDate is not null, Margin = new Thickness(0, 10, 0, 4) };
        var calendar = new System.Windows.Controls.Calendar { SelectedDate = (task.DueDate ?? DateTime.UtcNow).ToLocalTime().Date, DisplayDate = (task.DueDate ?? DateTime.UtcNow).ToLocalTime().Date, Visibility = task.DueDate is null ? Visibility.Collapsed : Visibility.Visible, HorizontalAlignment = HorizontalAlignment.Left };
        hasDue.Checked += (_, _) => calendar.Visibility = Visibility.Visible;
        hasDue.Unchecked += (_, _) => calendar.Visibility = Visibility.Collapsed;

        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Сохранить", IsDefault = true, Padding = new Thickness(12, 3, 12, 3) };
        title.TextChanged += (_, _) => save.IsEnabled = title.Text.Trim().Length > 0;
        save.Click += (_, _) =>
        {
            var updated = task.Clone();
            updated.Title = title.Text.Trim();
            var trimmedNotes = notes.Text.Trim();
            updated.Notes = trimmedNotes.Length == 0 ? null : trimmedNotes;
            updated.Scope = work.IsChecked == true ? TaskScope.Work : TaskScope.Personal;
            updated.DueDate = hasDue.IsChecked == true && calendar.SelectedDate is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Local).ToUniversalTime() : null;
            Result = updated;
            DialogResult = true;
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Ui.Caption("Название"));
        panel.Children.Add(title);
        var notesCaption = Ui.Caption("Описание");
        notesCaption.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(notesCaption);
        panel.Children.Add(notes);
        var scope = Ui.Row(work, personal);
        scope.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(scope);
        panel.Children.Add(hasDue);
        panel.Children.Add(calendar);
        if (meetingTitle is not null)
        {
            var from = Ui.Caption("👥 Из встречи: " + meetingTitle);
            from.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(from);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, save } };
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            title.Focus();
            title.SelectAll();
        };
    }
}
