using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalMeetingRecorder.App.Platform;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Speakr;
using UniversalMeetingRecorder.Core.Tasks;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>Окно «Встречи»: список слева, карточка встречи справа (порт MeetingsListView + MeetingDetailView).</summary>
internal sealed class MeetingsWindow : Window
{
    private readonly AppServices _s;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer _detailScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20) };
    private Guid? _selected;
    private string? _renderedJson;
    private bool _updatingList;

    public MeetingsWindow(AppServices services)
    {
        _s = services;
        Title = "Встречи";
        Width = 1000;
        Height = 680;
        MinWidth = 720;
        MinHeight = 480;
        Style = (Style)Application.Current.Resources["AppWindow"];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = WindowManager.AppIcon;

        var refresh = Ui.Button("⟳ Обновить", () => _s.Meetings.Refresh());
        refresh.Margin = new Thickness(8);
        var toolbar = new DockPanel();
        toolbar.Children.Add(refresh);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_list, 0);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_detailScroll, 2);
        grid.Children.Add(_list);
        grid.Children.Add(splitter);
        grid.Children.Add(_detailScroll);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(grid);
        Content = root;

        _list.SelectionChanged += (_, _) =>
        {
            if (_updatingList) return;
            _selected = (_list.SelectedItem as ListBoxItem)?.Tag as Guid?;
            RenderDetail(force: true);
        };
        _s.Meetings.Changed += OnStoreChanged;
        _s.Settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _s.Meetings.Changed -= OnStoreChanged;
            _s.Settings.PropertyChanged -= OnSettingsChanged;
        };

        _s.Meetings.Refresh();
        Select(_s.Meetings.TakePendingSelection());
    }

    public void Select(Guid? id)
    {
        if (id is null) return;
        _selected = id;
        RenderList();
        RenderDetail(force: true);
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Core.Settings.AppSettings.DeleteAfterUploadEnabled) or nameof(Core.Settings.AppSettings.ExtractTasksEnabled))
        {
            Dispatcher.BeginInvoke(() => RenderDetail(force: true));
        }
    }

    private void OnStoreChanged() => Dispatcher.BeginInvoke(() =>
    {
        RenderList();
        RenderDetail(force: false);
    });

    // ----- Список -----

    private void RenderList()
    {
        _updatingList = true;
        try
        {
            _list.Items.Clear();
            foreach (var m in _s.Meetings.Meetings)
            {
                var item = new ListBoxItem { Tag = m.Id, Content = ListRow(m), Padding = new Thickness(8, 6, 8, 6), ContextMenu = ContextMenuFor(m) };
                _list.Items.Add(item);
                if (m.Id == _selected) item.IsSelected = true;
            }
            if (_list.SelectedItem is ListBoxItem selected) _list.ScrollIntoView(selected);
        }
        finally
        {
            _updatingList = false;
        }
    }

    private static UIElement ListRow(Meeting m)
    {
        var title = Ui.Text(m.DisplayTitle, 13, FontWeights.SemiBold);
        title.MaxHeight = 36;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var meta = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var info = m.StartDate.ToLocalTime().ToString("d MMM yyyy  HH:mm", Ui.Ru) + (m.Title is not null ? "  " + m.AppName : "");
        meta.Children.Add(Ui.Text(info, 11, color: Ui.Brush("Secondary"), wrap: false));
        var (label, color) = StatusBadge(m.Status);
        meta.Children.Add(Ui.Badge(label, color));
        var count = m.SuggestedTasks?.Count ?? 0;
        if (count > 0) meta.Children.Add(Ui.Badge($"☑ {count} возможных задач", m.ActionItemsReviewed == true ? "Gray" : "Blue"));
        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(meta);
        return panel;
    }

    public static (string Label, string Color) StatusBadge(MeetingStatus s) => s switch
    {
        MeetingStatus.Recording => ("идёт запись", "Red"),
        MeetingStatus.Converting => ("сведение", "Orange"),
        MeetingStatus.Interrupted => ("прервано", "Orange"),
        MeetingStatus.Recorded => ("записано", "Gray"),
        MeetingStatus.Skipped => ("короткая", "Gray"),
        MeetingStatus.Uploading => ("загрузка", "Orange"),
        MeetingStatus.Processing => ("обработка", "Orange"),
        MeetingStatus.Done => ("готово", "Green"),
        MeetingStatus.Failed => ("ошибка", "Red"),
        _ => ("", "Gray"),
    };

    private ContextMenu ContextMenuFor(Meeting m)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Открыть папку встречи", () => Shell.OpenFolder(_s.Meetings.FolderPath(m))));
        if (m.SpeakrRecordingID is { } rid && !m.DeletedRemotely && SpeakrClient.WebUrl(_s.Settings.SpeakrBaseUrl, rid) is { } url)
        {
            menu.Items.Add(MenuItem("Открыть в Speakr", () => Shell.Open(url)));
        }
        if (m.HasEncodedAudio) menu.Items.Add(MenuItem("Транскрибировать заново", () => _s.Upload.Retranscribe(m)));
        menu.Items.Add(new Separator());
        var delete = MenuItem("Удалить…", () => ConfirmDelete(m));
        delete.IsEnabled = m.Status is not (MeetingStatus.Recording or MeetingStatus.Converting);
        menu.Items.Add(delete);
        return menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ConfirmDelete(Meeting m)
    {
        var answer = MessageBox.Show(this,
            $"Папка «{m.FolderName}» со всеми файлами будет перемещена в Корзину. На Speakr запись не трогаем.",
            "Удалить встречу?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _s.Upload.Cancel(m.Id);
        if (_s.Meetings.Delete(m, _s.RecycleBin) && _selected == m.Id)
        {
            _selected = null;
            RenderDetail(force: true);
        }
    }

    // ----- Карточка -----

    private void RenderDetail(bool force)
    {
        var meeting = _selected is { } id ? _s.Meetings.Find(id) : null;
        if (meeting is null)
        {
            _renderedJson = null;
            _detailScroll.Content = new TextBlock { Text = "Выберите встречу слева", Foreground = Ui.Brush("Secondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return;
        }
        var json = MeetingJson.Serialize(meeting);
        if (!force && json == _renderedJson) return;
        _renderedJson = json;
        _detailScroll.Content = Detail(meeting);
    }

    private UIElement Detail(Meeting m)
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text(m.DisplayTitle, 20, FontWeights.Bold));
        var when = m.EndDate is { } end ? $"{Ui.DateTimeShort(m.StartDate)} — {Ui.TimeShort(end)}" : Ui.DateTimeShort(m.StartDate);
        if (m.Title is not null) when += $" · {m.AppName}";
        var whenText = Ui.Caption(when);
        whenText.Margin = new Thickness(0, 2, 0, 14);
        panel.Children.Add(whenText);

        if (LifecycleBanner(m) is { } banner) panel.Children.Add(banner);

        if (m.Status == MeetingStatus.Failed && m.ErrorMessage is { } error)
        {
            panel.Children.Add(Ui.Banner("Red", Ui.Text(error, color: Ui.Brush("Red")), Ui.Button("Повторить", () => _s.Upload.Retranscribe(m))));
        }

        if (_s.Settings.DeleteAfterUploadEnabled && m.Status == MeetingStatus.Done && m.SpeakrRecordingID is not null && !m.DeletedRemotely)
        {
            var items = new List<UIElement> { Ui.Text("⚠ Запись не удалена из Speakr", color: Ui.Brush("Orange")) };
            if (m.ErrorMessage is { } e) items.Add(Ui.Caption(e));
            items.Add(Ui.Button("Удалить в Speakr", () => _s.Upload.DeleteRemotely(m)));
            panel.Children.Add(Ui.Banner("Orange", items.ToArray()));
        }

        if (SuggestedSection(m) is { } suggested) panel.Children.Add(suggested);

        var notesPath = _s.Meetings.NotesPath(m);
        if (notesPath is not null && File.Exists(notesPath))
        {
            var notes = SafeRead(notesPath);
            if (!string.IsNullOrEmpty(notes)) panel.Children.Add(Ui.Group("Митинг-ноуты", MarkdownView.Create(notes)));
        }

        var transcript = LoadTranscript(m);
        if (transcript.Count > 0)
        {
            panel.Children.Add(Ui.Group("Транскрипт", TranscriptView(transcript)));
        }
        else if (m.Status is MeetingStatus.Processing or MeetingStatus.Uploading)
        {
            panel.Children.Add(Ui.Row(Ui.Spinner(), Ui.Text(m.RemoteStatus ?? "Обработка…")));
        }
        return panel;
    }

    private UIElement? LifecycleBanner(Meeting m) => m.Status switch
    {
        MeetingStatus.Recording => Ui.Banner("Red", Ui.Text("● Идёт запись", color: Ui.Brush("Red"))),
        MeetingStatus.Converting => Ui.Banner("Orange", Ui.Row(Ui.Spinner(), Ui.Text("Сведение записи в аудиофайл…"))),
        MeetingStatus.Interrupted => Ui.Banner("Orange",
            Ui.Text("⚠ Запись не сведена", color: Ui.Brush("Orange")),
            Ui.Caption(m.ErrorMessage ?? "Приложение завершилось до конца звонка. Сырые дорожки сохранены — сведение запустится автоматически при следующем запуске.")),
        MeetingStatus.Skipped => Ui.Banner("Gray",
            Ui.Text($"Не выгружена в Speakr: запись короче {(int)UploadPipeline.MinUploadDuration.TotalMinutes} минут"),
            Ui.Caption("Так настроено во вкладке «Speakr». Файлы встречи лежат на месте — выгрузить можно вручную."),
            Ui.Button("Выгрузить в Speakr", () => _s.Upload.Retranscribe(m))),
        _ => null,
    };

    private UIElement? SuggestedSection(Meeting m)
    {
        if (m.Status != MeetingStatus.Done || !(_s.Settings.ExtractTasksEnabled || m.ExtractionState is not null)) return null;
        var body = new StackPanel();
        var suggestions = m.SuggestedTasks ?? new List<SuggestedTask>();
        var state = m.ExtractionState;
        switch (state)
        {
            case ExtractionState.Pending:
                body.Children.Add(Ui.Row(Ui.Spinner(), Ui.Text("Ищу задачи в транскрипте…")));
                break;
            case ExtractionState.Failed:
                body.Children.Add(Ui.Text(m.ExtractionError ?? "Не удалось найти задачи", color: Ui.Brush("Red")));
                body.Children.Add(Ui.Button("Повторить", () => _ = _s.ActionItems.Extract(m.Id)));
                break;
            case ExtractionState.Done when suggestions.Count == 0:
                body.Children.Add(Ui.Caption("Нет предложений: задач для вас не найдено или все уже разобраны."));
                body.Children.Add(Ui.Button("Проверить заново", () => _ = _s.ActionItems.Extract(m.Id)));
                break;
            case ExtractionState.Done:
                body.Children.Add(new SuggestedTasksReviewView(m, _s.ActionItems));
                // Пользователь увидел предложения — бейдж в трее перестаёт считать их новыми.
                Dispatcher.BeginInvoke(() => _s.ActionItems.MarkReviewed(m.Id));
                break;
            default:
                body.Children.Add(Ui.Caption("Задачи из этой встречи ещё не искали."));
                body.Children.Add(Ui.Button("Найти задачи", () => _ = _s.ActionItems.Extract(m.Id)));
                break;
        }
        foreach (var child in body.Children.OfType<FrameworkElement>()) child.Margin = new Thickness(0, 0, 0, 6);
        return Ui.Group("☑ Возможные задачи", body);
    }

    private List<SpeakrTranscriptSegment> LoadTranscript(Meeting m)
    {
        try
        {
            if (_s.Meetings.TranscriptPath(m) is { } path && File.Exists(path)) return SpeakrTranscriptResponse.Parse(File.ReadAllBytes(path)).Segments;
        }
        catch
        {
        }
        return new List<SpeakrTranscriptSegment>();
    }

    private static UIElement TranscriptView(List<SpeakrTranscriptSegment> segments)
    {
        // Один выделяемый TextBox на весь транскрипт — быстро даже на тысячах реплик и копируется целиком.
        var box = new RichTextBox { IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, IsDocumentEnabled = true };
        var doc = new System.Windows.Documents.FlowDocument { PagePadding = new Thickness(0), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13 };
        foreach (var s in segments)
        {
            var head = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 6, 0, 0), FontSize = 11 };
            if (s.Speaker is { } speaker) head.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run(speaker + "  ")));
            if (s.StartTime is { } start) head.Inlines.Add(new System.Windows.Documents.Run(Ui.Timecode(start)) { Foreground = Ui.Brush("Secondary") });
            doc.Blocks.Add(head);
            doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(s.Sentence)) { Margin = new Thickness(0) });
        }
        box.Document = doc;
        return box;
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return "";
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) _s.Meetings.Refresh();
        base.OnKeyDown(e);
    }
}
