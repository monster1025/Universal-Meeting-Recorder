using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Tasks;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>
/// Виджет «Задачи» на рабочем столе — аналог WidgetKit-виджета на Mac (доска виджетов Windows 11
/// требует MSIX-упаковки, поэтому — обычное компактное окно). Открытые задачи в порядке приложения,
/// фильтр и лимит как в интенте виджета, «просрочено» красным, «сегодня»/«завтра», точка по сфере
/// (рабочая — синяя, личная — зелёная, как в виджете на Mac). Не поверх окон; перетаскивается,
/// позиция запоминается; правый клик — меню. Данные — напрямую из TaskStore (тот же процесс),
/// HTTP-фид для этого не нужен.
/// </summary>
internal sealed class TaskWidgetWindow : Window
{
    private readonly AppServices _s;
    private readonly StackPanel _body = new();

    public TaskWidgetWindow(AppServices services)
    {
        _s = services;
        Title = "Задачи — виджет";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 280;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        Content = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xFB, 0xFB, 0xFC)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            Child = _body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.2 },
        };
        if (_s.Settings.TaskWidgetOrigin is { } p)
        {
            Left = p.X;
            Top = p.Y;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Top + 24;
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            _s.Settings.TaskWidgetOrigin = new ScreenPoint(Left, Top);
        };
        MouseDoubleClick += (_, _) => _s.Windows.ShowTasks();
        ContextMenu = Menu();
        _s.Tasks.Changed += OnChanged;
        _s.Settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _s.Tasks.Changed -= OnChanged;
            _s.Settings.PropertyChanged -= OnSettingsChanged;
        };
        // Обновить «сегодня/завтра/просрочено» после полуночи.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        timer.Tick += (_, _) => Render();
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Render();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Render);

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.TaskWidgetFilter) or nameof(AppSettings.TaskWidgetLimit)) Dispatcher.BeginInvoke(Render);
    }

    private ContextMenu Menu()
    {
        var menu = new ContextMenu();
        foreach (var (key, title) in new[] { ("all", "Все задачи"), ("personal", "Личные"), ("work", "Рабочие") })
        {
            var item = new MenuItem { Header = title, IsCheckable = true };
            item.Click += (_, _) => _s.Settings.TaskWidgetFilter = key;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var open = new MenuItem { Header = "Открыть задачи…" };
        open.Click += (_, _) => _s.Windows.ShowTasks();
        menu.Items.Add(open);
        var hide = new MenuItem { Header = "Скрыть виджет" };
        hide.Click += (_, _) =>
        {
            _s.Settings.TaskWidgetEnabled = false;
            _s.Windows.SyncTaskWidget();
        };
        menu.Items.Add(hide);
        menu.Opened += (_, _) =>
        {
            var keys = new[] { "all", "personal", "work" };
            for (var i = 0; i < 3; i++) ((MenuItem)menu.Items[i]).IsChecked = _s.Settings.TaskWidgetFilter == keys[i];
        };
        return menu;
    }

    private void Render()
    {
        _body.Children.Clear();
        var filter = _s.Settings.TaskWidgetFilter;
        var open = _s.Tasks.Tasks.Where(t => !t.IsCompleted && (filter == "all" || t.Scope.ToDb() == filter)).ToList();
        var limit = _s.Settings.TaskWidgetLimit;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var count = Ui.Text(open.Count.ToString(), 13, FontWeights.SemiBold, Ui.Brush("Secondary"), wrap: false);
        DockPanel.SetDock(count, Dock.Right);
        header.Children.Add(count);
        header.Children.Add(Ui.Text("Задачи", 14, FontWeights.Bold, Brushes.Black, wrap: false));
        _body.Children.Add(header);

        if (open.Count == 0)
        {
            _body.Children.Add(Ui.Text("Нет открытых задач", 12, color: Ui.Brush("Secondary")));
            return;
        }
        var today = DateTime.Today;
        foreach (var t in open.Take(limit))
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = t.Scope == TaskScope.Work ? Ui.Brush("Blue") : Ui.Brush("Green"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            if (t.DueDate is { } due)
            {
                var local = due.ToLocalTime().Date;
                var (label, color) = local < today ? ("просрочено", Ui.Brush("Red"))
                    : local == today ? ("сегодня", Ui.Brush("Secondary"))
                    : local == today.AddDays(1) ? ("завтра", Ui.Brush("Secondary"))
                    : (local.ToString("d MMM", Ui.Ru), Ui.Brush("Secondary"));
                var dueText = Ui.Text(label, 11, color: color, wrap: false);
                dueText.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(dueText, Dock.Right);
                row.Children.Add(dueText);
            }
            row.Children.Add(Ui.Text(t.Title, 12, color: Brushes.Black, wrap: false));
            _body.Children.Add(row);
        }
        if (open.Count > limit)
        {
            var more = Ui.Text($"ещё {open.Count - limit}", 11, color: Ui.Brush("Secondary"));
            more.Margin = new Thickness(15, 4, 0, 0);
            _body.Children.Add(more);
        }
    }
}
