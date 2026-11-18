using System.Windows;
using System.Windows.Controls;
using UniversalMeetingRecorder.App.Platform;
using UniversalMeetingRecorder.Core.Calendar;
using UniversalMeetingRecorder.Core.LLM;
using UniversalMeetingRecorder.Core.Transcription;

namespace UniversalMeetingRecorder.App.UI;

/// <summary>Настройки — те же пять вкладок, что на Mac (Общее, Запись, Транскрипция, Задачи, Календарь).</summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppServices _s;
    private StackPanel? _calendarStatus;
    private StackPanel? _calendarList;

    public SettingsWindow(AppServices services)
    {
        _s = services;
        Title = "Настройки Universal Meeting Recorder";
        Width = 600;
        Height = 680;
        MinWidth = 480;
        MinHeight = 420;
        Style = (Style)Application.Current.Resources["AppWindow"];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = WindowManager.AppIcon;

        var tabs = new TabControl { Margin = new Thickness(8) };
        tabs.Items.Add(Tab("Общее", General()));
        tabs.Items.Add(Tab("Запись", Recording()));
        tabs.Items.Add(Tab("Транскрипция", Transcription()));
        tabs.Items.Add(Tab("Задачи", Tasks()));
        tabs.Items.Add(Tab("Календарь", CalendarTab()));
        Content = tabs;

        _s.Mendeleev.Changed += OnMendeleevChanged;
        _s.Calendar.Changed += OnCalendarChanged;
        _s.ReminderScheduler.PropertyChanged += OnSchedulerChanged;
        Closed += (_, _) =>
        {
            _s.Mendeleev.Changed -= OnMendeleevChanged;
            _s.Calendar.Changed -= OnCalendarChanged;
            _s.ReminderScheduler.PropertyChanged -= OnSchedulerChanged;
        };
    }

    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 8, 16, 16) },
    };

    private static TextBlock Section(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["SectionHeader"] };

    private static TextBlock Note(string text)
    {
        var t = Ui.Caption(text);
        t.Margin = new Thickness(0, 2, 0, 8);
        return t;
    }

    private static CheckBox Toggle(string title, bool value, Action<bool> onChange)
    {
        var c = new CheckBox { Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 4, 0, 2) };
        c.Checked += (_, _) => onChange(true);
        c.Unchecked += (_, _) => onChange(false);
        return c;
    }

    private static TextBox Field(string value, Action<string> onChange, string? tooltip = null)
    {
        var t = new TextBox { Text = value, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 4), ToolTip = tooltip };
        t.TextChanged += (_, _) => onChange(t.Text.Trim());
        return t;
    }

    private static PasswordBox Secret(string value, Action<string> onChange)
    {
        var p = new PasswordBox { Password = value, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 4) };
        p.PasswordChanged += (_, _) => onChange(p.Password.Trim());
        return p;
    }

    private static StackPanel Labeled(string label, UIElement field)
    {
        var p = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        p.Children.Add(Ui.Text(label, 12));
        p.Children.Add(field);
        return p;
    }

    // ----- Общее -----

    private UIElement General()
    {
        var p = new StackPanel();
        p.Children.Add(Toggle("Запускать при входе в Windows", LoginItemManager.IsEnabled, on =>
        {
            try
            {
                LoginItemManager.SetEnabled(on);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Не удалось изменить автозапуск", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
        p.Children.Add(Note("Приложение живёт в области уведомлений (трее) и не показывается на панели задач."));
        p.Children.Add(Section("Диагностика"));
        p.Children.Add(Ui.Button("Открыть папку журналов", () => Shell.OpenFolder(AppPaths.LogDirectory)));
        return p;
    }

    // ----- Запись -----

    private UIElement Recording()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Папка записей"));
        var path = Field(_s.Settings.RecordingsRootPath, v => _s.Settings.RecordingsRootPath = v);
        p.Children.Add(path);
        p.Children.Add(Ui.Button("Выбрать папку…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _s.Settings.RecordingsRootPath };
            if (dialog.ShowDialog(this) != true) return;
            _s.Settings.RecordingsRootPath = dialog.FolderName;
            path.Text = dialog.FolderName;
        }));
        p.Children.Add(Note("Новая папка начнёт использоваться после перезапуска приложения."));

        p.Children.Add(Section("Приложения-исключения"));
        p.Children.Add(Note("Использование микрофона этими приложениями не считается конференцией (например, диктовка). Сравнение — по подстроке в имени приложения или пути к exe, без учёта регистра."));
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void RenderList()
        {
            list.Children.Clear();
            foreach (var app in _s.Settings.ExcludedApps)
            {
                var remove = new Button { Content = "−", Width = 24, Margin = new Thickness(8, 1, 0, 1), ToolTip = "Удалить" };
                remove.Click += (_, _) =>
                {
                    _s.Settings.ExcludedApps = _s.Settings.ExcludedApps.Where(a => a != app).ToList();
                    RenderList();
                };
                var row = new DockPanel();
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(Ui.Text(app));
                list.Children.Add(row);
            }
        }
        RenderList();
        p.Children.Add(scroll);
        var newApp = new TextBox { Padding = new Thickness(4) };
        var add = Ui.Button("Добавить", () =>
        {
            var trimmed = newApp.Text.Trim();
            if (trimmed.Length == 0 || _s.Settings.ExcludedApps.Contains(trimmed)) return;
            _s.Settings.ExcludedApps = _s.Settings.ExcludedApps.Append(trimmed).ToList();
            newApp.Text = "";
            RenderList();
        });
        add.Margin = new Thickness(8, 0, 0, 0);
        var addRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        addRow.Children.Add(add);
        addRow.Children.Add(newApp);
        p.Children.Add(addRow);

        p.Children.Add(Section("Mute микрофона"));
        p.Children.Add(Toggle("Показывать кнопку системного mute во время записи", _s.Settings.SystemMicMuteOverlayEnabled, on => _s.Settings.SystemMicMuteOverlayEnabled = on));
        p.Children.Add(Note("Плавающая кнопка появляется на время записи. Она глушит микрофон на уровне Windows (устройство ввода по умолчанию и устройство связи), так что звук не уходит ни в сам звонок, ни в запись, пока mute включён."));
        return p;
    }

    // ----- Транскрипция (Speakr / НейроМенделеев) -----

    private TextBlock? _mendeleevStatus;

    private UIElement Transcription()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Сервис транскрипции"));
        var speakr = new RadioButton { Content = "Speakr", GroupName = "backend", Margin = new Thickness(0, 2, 0, 2), IsChecked = _s.Settings.TranscriptionBackend == TranscriptionBackends.Speakr };
        var mendeleev = new RadioButton { Content = "НейроМенделеев", GroupName = "backend", Margin = new Thickness(0, 2, 0, 2), IsChecked = _s.Settings.TranscriptionBackend == TranscriptionBackends.Mendeleev };
        p.Children.Add(speakr);
        p.Children.Add(mendeleev);
        p.Children.Add(Note("Работает один сервис: новые записи уходят в выбранный. Уже загруженные встречи дорабатывает тот сервис, в который они были отправлены, — переключение их не ломает."));

        var speakrPanel = new StackPanel();
        speakrPanel.Children.Add(Section("Speakr"));
        speakrPanel.Children.Add(Labeled("Base URL", Field(_s.Settings.SpeakrBaseUrl, v => _s.Settings.SpeakrBaseUrl = v, "https://speakr.example.com")));
        speakrPanel.Children.Add(Labeled("API-ключ", Secret(_s.Settings.SpeakrApiKey, v => _s.Settings.SpeakrApiKey = v)));
        speakrPanel.Children.Add(Note("Ключ выдаётся в Speakr: Settings → API Tokens. Хранится в диспетчере учётных данных Windows, не в файле настроек."));
        p.Children.Add(speakrPanel);

        var mendeleevPanel = new StackPanel();
        mendeleevPanel.Children.Add(Section("НейроМенделеев"));
        mendeleevPanel.Children.Add(Labeled("Адрес сервера", Field(_s.Settings.MendeleevBaseUrl, v =>
        {
            _s.Settings.MendeleevBaseUrl = v;
            RenderMendeleevStatus();
        }, "https://mendeleev.example.com")));
        _mendeleevStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        mendeleevPanel.Children.Add(_mendeleevStatus);
        var signIn = Ui.Button("Войти", async () =>
        {
            _mendeleevStatus!.Text = "Выполняется вход…";
            _mendeleevStatus.Foreground = Ui.Brush("Secondary");
            try
            {
                await _s.Mendeleev.SignIn(default);
            }
            catch (Exception ex)
            {
                _mendeleevStatus.Text = "⚠ " + ex.Message;
                _mendeleevStatus.Foreground = Ui.Brush("Orange");
                return;
            }
            RenderMendeleevStatus();
        });
        var signOut = Ui.Button("Выйти", () => _s.Mendeleev.SignOut());
        mendeleevPanel.Children.Add(Ui.Row(signIn, signOut));
        mendeleevPanel.Children.Add(Note("Вход — через SSO (Keycloak) во встроенном браузере, настраивать Keycloak не нужно. Если на компьютере работает Windows-авторизация, окно мелькнёт на секунду и закроется само; иначе откроется обычная форма входа. Токен хранится в диспетчере учётных данных Windows и продлевается автоматически; когда продлить нельзя, приложение входит заново."));
        p.Children.Add(mendeleevPanel);

        void ShowPanel()
        {
            var isMendeleev = mendeleev.IsChecked == true;
            speakrPanel.Visibility = isMendeleev ? Visibility.Collapsed : Visibility.Visible;
            mendeleevPanel.Visibility = isMendeleev ? Visibility.Visible : Visibility.Collapsed;
        }
        speakr.Checked += (_, _) =>
        {
            _s.Settings.TranscriptionBackend = TranscriptionBackends.Speakr;
            ShowPanel();
        };
        mendeleev.Checked += (_, _) =>
        {
            _s.Settings.TranscriptionBackend = TranscriptionBackends.Mendeleev;
            ShowPanel();
        };
        ShowPanel();
        RenderMendeleevStatus();

        p.Children.Add(Section("Что выгружать"));
        p.Children.Add(Toggle("Не выгружать записи короче 2 минут", _s.Settings.SkipShortRecordingsEnabled, on => _s.Settings.SkipShortRecordingsEnabled = on));
        p.Children.Add(Note("Короткие записи остаются локально со статусом «короткая, не выгружена» — любую из них можно отправить вручную из карточки встречи."));
        p.Children.Add(Section("После обработки"));
        p.Children.Add(Toggle("Удалять загруженные конференции с сервера транскрипции по API", _s.Settings.DeleteAfterUploadEnabled, on => _s.Settings.DeleteAfterUploadEnabled = on));
        p.Children.Add(Note("Локальные файлы не удаляются никогда — только копия на сервере."));
        return p;
    }

    private void RenderMendeleevStatus()
    {
        if (_mendeleevStatus is null) return;
        var session = _s.Mendeleev;
        if (session.BaseUri is null)
        {
            _mendeleevStatus.Text = "Укажите адрес сервера.";
            _mendeleevStatus.Foreground = Ui.Brush("Secondary");
        }
        else if (session.IsValid)
        {
            _mendeleevStatus.Text = $"✓ Вход выполнен: {session.UserName ?? "пользователь"} (токен действует до {session.ExpiresUtc!.Value.ToLocalTime():d MMM HH:mm})";
            _mendeleevStatus.Foreground = Ui.Brush("Green");
        }
        else
        {
            _mendeleevStatus.Text = session.HasToken ? "⚠ Токен истёк — приложение войдёт заново при следующей выгрузке или по кнопке «Войти»." : "Вход не выполнен.";
            _mendeleevStatus.Foreground = Ui.Brush("Orange");
        }
    }

    // ----- Задачи -----

    private UIElement Tasks()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Напоминания о задачах"));
        p.Children.Add(Toggle("Раз в день напоминать об открытых задачах", _s.Settings.TaskDigestEnabled, on => _s.Settings.TaskDigestEnabled = on));
        var hours = new ComboBox { Width = 60, ItemsSource = Enumerable.Range(0, 24).Select(h => h.ToString("00")), SelectedIndex = _s.Settings.TaskDigestMinutes / 60 };
        var minutes = new ComboBox { Width = 60, ItemsSource = Enumerable.Range(0, 12).Select(m => (m * 5).ToString("00")), SelectedIndex = _s.Settings.TaskDigestMinutes % 60 / 5 };
        void SaveTime() => _s.Settings.TaskDigestMinutes = Math.Max(0, hours.SelectedIndex) * 60 + Math.Max(0, minutes.SelectedIndex) * 5;
        hours.SelectionChanged += (_, _) => SaveTime();
        minutes.SelectionChanged += (_, _) => SaveTime();
        var timeRow = Ui.Row(Ui.Text("Во сколько:  ", wrap: false), hours, Ui.Text(" : ", wrap: false), minutes);
        timeRow.Margin = new Thickness(0, 4, 0, 4);
        p.Children.Add(timeRow);
        p.Children.Add(Note("Одно уведомление в сутки: сколько открыто задач, что просрочено и что на сегодня. Если открытых задач нет, уведомления не будет. Если в это время приложение было закрыто или компьютер спал, сводка придёт при запуске, но не позже чем через 6 часов."));
        var digestNote = Ui.Caption("");
        var show = Ui.Button("Показать сейчас", () => digestNote.Text = _s.Digest.SendNow() ? "" : "Открытых задач нет — показывать нечего.");
        p.Children.Add(Ui.Row(show, digestNote));
        if (_s.Notifier.NotificationsEnabled == false)
        {
            p.Children.Add(Ui.Text("⚠ Уведомления приложения выключены в параметрах Windows — сводка не появится.", color: Ui.Brush("Orange")));
            p.Children.Add(Ui.Button("Открыть параметры уведомлений", () => Shell.Open("ms-settings:notifications")));
        }

        p.Children.Add(Section("Задачи из встреч"));
        p.Children.Add(Toggle("Находить задачи в транскриптах и предлагать их на подтверждение", _s.Settings.ExtractTasksEnabled, on => _s.Settings.ExtractTasksEnabled = on));
        p.Children.Add(Note("Транскрипт и саммари встречи отправляются на указанный ниже LLM-endpoint. Задачи никогда не создаются сами: после обработки встречи вы увидите список возможных задач и сами выберете, какие добавить."));

        p.Children.Add(Section("Как вас зовут на встречах"));
        p.Children.Add(Field(string.Join(", ", _s.Settings.MySpeakerNames), v =>
            _s.Settings.MySpeakerNames = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(), "Иван, Иванов Иван Иванович"));
        p.Children.Add(Note("Имена через запятую: как вас называют в разговоре и как подписан ваш голос в транскрипте. Нужны, чтобы искать только ваши обязательства, а не чужие."));

        p.Children.Add(Section("LLM (OpenAI-совместимый API)"));
        p.Children.Add(Labeled("Адрес", Field(_s.Settings.LlmBaseUrl, v => _s.Settings.LlmBaseUrl = v, "http://localhost:4000")));
        p.Children.Add(Labeled("Модель", Field(_s.Settings.LlmModel, v => _s.Settings.LlmModel = v)));
        p.Children.Add(Labeled("API-ключ", Secret(_s.Settings.LlmApiKey, v => _s.Settings.LlmApiKey = v)));
        p.Children.Add(Note("Ключ хранится в диспетчере учётных данных Windows. Подойдёт любой сервис с /v1/chat/completions (OpenRouter, LiteLLM, локальный сервер)."));
        var result = Ui.Text("");
        Button? test = null;
        test = Ui.Button("Проверить подключение", async () =>
        {
            if (_s.Settings.MakeLlmClient() is not { } client)
            {
                result.Text = "⚠ Заполните адрес, модель и ключ";
                result.Foreground = Ui.Brush("Orange");
                return;
            }
            test!.IsEnabled = false;
            result.Text = "Проверяю…";
            result.Foreground = Ui.Brush("Secondary");
            try
            {
                var models = await client.AvailableModels();
                if (models.Contains(client.Model))
                {
                    result.Text = $"✓ Подключение работает, модель «{client.Model}» найдена";
                    result.Foreground = Ui.Brush("Green");
                }
                else
                {
                    result.Text = $"⚠ Подключение работает, но модели «{client.Model}» на сервере нет. Доступны: {string.Join(", ", models)}";
                    result.Foreground = Ui.Brush("Orange");
                }
            }
            catch (Exception ex)
            {
                result.Text = "⚠ " + ex.Message;
                result.Foreground = Ui.Brush("Orange");
            }
            test.IsEnabled = true;
        });
        p.Children.Add(test);
        p.Children.Add(result);

        p.Children.Add(Section("Виджет задач на рабочем столе"));
        p.Children.Add(Toggle("Показывать виджет «Задачи» на рабочем столе", _s.Settings.TaskWidgetEnabled, on =>
        {
            _s.Settings.TaskWidgetEnabled = on;
            _s.Windows.SyncTaskWidget();
        }));
        var filter = new ComboBox { Width = 140, ItemsSource = new[] { "Все", "Личные", "Рабочие" }, SelectedIndex = Array.IndexOf(new[] { "all", "personal", "work" }, _s.Settings.TaskWidgetFilter) };
        filter.SelectionChanged += (_, _) => _s.Settings.TaskWidgetFilter = new[] { "all", "personal", "work" }[Math.Max(0, filter.SelectedIndex)];
        var limit = new ComboBox { Width = 60, ItemsSource = Enumerable.Range(1, 10), SelectedItem = _s.Settings.TaskWidgetLimit };
        limit.SelectionChanged += (_, _) => _s.Settings.TaskWidgetLimit = limit.SelectedItem is int n ? n : 5;
        var widgetRow = Ui.Row(Ui.Text("Показывать:  ", wrap: false), filter, Ui.Text("   не больше  ", wrap: false), limit);
        widgetRow.Margin = new Thickness(0, 4, 0, 4);
        p.Children.Add(widgetRow);
        p.Children.Add(Note($"Аналог виджета macOS: открытые задачи с ближайшими сроками. Окно можно перетащить; правый клик — меню. Снимок задач также отдаётся по http://127.0.0.1:{Core.Tasks.TaskFeed.Port}/tasks (токен — как на Mac)."));
        return p;
    }

    // ----- Календарь -----

    private UIElement CalendarTab()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Outlook"));
        p.Children.Add(Toggle("Подключить календарь Outlook", _s.Settings.OutlookCalendarEnabled, on =>
        {
            _s.Settings.OutlookCalendarEnabled = on;
            if (on) _ = _s.Calendar.ReloadFeeds();
            _reminderToggle!.IsEnabled = on;
            _s.ReminderScheduler.Tick();
            RenderCalendar();
        }));
        _calendarStatus = new StackPanel();
        p.Children.Add(_calendarStatus);
        p.Children.Add(Ui.Button("Переподключиться к Outlook", () =>
        {
            if (_s.Settings.OutlookCalendarEnabled) _ = _s.Calendar.ReloadFeeds();
        }));
        p.Children.Add(Note("Встречи берутся из классического Outlook для Windows: он должен быть установлен, а если не запущен — приложение запустит его в фоне. «Новый Outlook» подключения не поддерживает. Пока календарь не подключён, приложение к Outlook не обращается."));

        p.Children.Add(Section("Напоминания"));
        _reminderToggle = Toggle("Напоминать, если встреча идёт, а запись не включена", _s.Settings.MeetingReminderEnabled, on =>
        {
            _s.Settings.MeetingReminderEnabled = on;
            _s.ReminderScheduler.Tick();
        });
        _reminderToggle.IsEnabled = _s.Settings.OutlookCalendarEnabled;
        p.Children.Add(_reminderToggle);
        p.Children.Add(Note("Уведомление приходит со звуком: сразу на старте встречи, затем раз в 5 минут, пока запись не включена. Кнопки в нём — отложить на 1 или 5 минут либо отключить до конца этой встречи. Звук дублируется самим приложением и слышен даже в режиме «Не беспокоить»."));
        p.Children.Add(Toggle("Красная обводка экрана, пока встреча идёт без записи", _s.Settings.ReminderBorderEnabled, on =>
        {
            _s.Settings.ReminderBorderEnabled = on;
            _s.ReminderScheduler.Tick();
        }));
        p.Children.Add(Note("Обводка не зависит от уведомлений: если Windows прячет баннеры («Не беспокоить», игровой режим), красная рамка вокруг экрана всё равно видна. Прячется кнопками «Отложить/Отключить» и сразу после старта записи."));
        if (_s.Notifier.NotificationsEnabled == false)
        {
            p.Children.Add(Ui.Text("⚠ Уведомления приложения выключены в параметрах Windows — напоминания не появятся.", color: Ui.Brush("Orange")));
            p.Children.Add(Ui.Button("Открыть параметры уведомлений", () => Shell.Open("ms-settings:notifications")));
        }

        p.Children.Add(Section("Состояние"));
        var ongoing = new StackPanel { Name = "Ongoing" };
        p.Children.Add(ongoing);
        p.Children.Add(Note("Встречей считается событие со ссылкой на звонок (TrueConf, Telemost, Zoom, Meet, Teams и т.п.) в месте или описании. События на весь день, отменённые и те, где вы отказались, не учитываются."));

        p.Children.Add(Section("Календари"));
        _calendarList = new StackPanel();
        p.Children.Add(_calendarList);

        _ongoingPanel = ongoing;
        RenderCalendar();
        if (_s.Settings.OutlookCalendarEnabled) _ = _s.Calendar.ReloadFeeds();
        return p;
    }

    private StackPanel? _ongoingPanel;
    private CheckBox? _reminderToggle;

    private void OnMendeleevChanged() => Dispatcher.BeginInvoke(RenderMendeleevStatus);

    private void OnCalendarChanged() => Dispatcher.BeginInvoke(RenderCalendar);

    private void OnSchedulerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RenderCalendar);

    private void RenderCalendar()
    {
        if (_calendarStatus is null || _calendarList is null || _ongoingPanel is null) return;
        _calendarStatus.Children.Clear();
        if (!_s.Settings.OutlookCalendarEnabled)
        {
            _calendarStatus.Children.Add(Ui.Caption("Календарь не подключён."));
            _ongoingPanel.Children.Clear();
            _ongoingPanel.Children.Add(Ui.Caption("Нет доступа к календарю."));
            _calendarList.Children.Clear();
            return;
        }
        _calendarStatus.Children.Add(_s.Calendar.IsConnected
            ? Ui.Text("✓ " + _s.Calendar.Status, color: Ui.Brush("Green"))
            : Ui.Text("⚠ " + _s.Calendar.Status, color: Ui.Brush("Orange")));

        _ongoingPanel.Children.Clear();
        if (!_s.Calendar.IsConnected) _ongoingPanel.Children.Add(Ui.Caption("Нет подключения к Outlook."));
        else if (_s.ReminderScheduler.Ongoing is { } m) _ongoingPanel.Children.Add(Ui.Text("📡 Сейчас идёт: " + m.Title));
        else _ongoingPanel.Children.Add(Ui.Caption("Подходящей встречи сейчас нет."));

        _calendarList.Children.Clear();
        var feeds = _s.Calendar.Feeds;
        if (feeds.Count == 0)
        {
            _calendarList.Children.Add(Ui.Caption("Список календарей пуст — нет подключения к Outlook или ни одного календаря."));
            return;
        }
        var selected = _s.Settings.ReminderCalendarIds;
        if (selected.Count == 0) _calendarList.Children.Add(Ui.Caption("Ничего не выбрано — следим за всеми календарями."));
        foreach (var group in feeds.GroupBy(f => f.SourceTitle).OrderBy(g => g.Key))
        {
            var header = Ui.Text(group.Key, 12, FontWeights.SemiBold);
            header.Margin = new Thickness(0, 8, 0, 2);
            _calendarList.Children.Add(header);
            foreach (var feed in group)
            {
                var box = new CheckBox { Content = feed.Title, IsChecked = selected.Count == 0 || selected.Contains(feed.Id), Margin = new Thickness(12, 2, 0, 2) };
                box.Checked += (_, _) => SetCalendar(feed, true);
                box.Unchecked += (_, _) => SetCalendar(feed, false);
                _calendarList.Children.Add(box);
            }
        }
        var available = feeds.Select(f => f.Id).ToHashSet();
        var unavailable = selected.Count(id => !available.Contains(id));
        if (unavailable > 0)
        {
            _calendarList.Children.Add(Note($"Сохранённых календарей сейчас недоступно: {unavailable} (ящик отключён). Выбор не сбрасывается."));
        }
    }

    /// <summary>Пустой выбор = «все»: снятие галочки материализует полный список без этого календаря.</summary>
    private void SetCalendar(CalendarFeed feed, bool on)
    {
        var ids = _s.Settings.ReminderCalendarIds.Count == 0 ? _s.Calendar.Feeds.Select(f => f.Id).ToList() : _s.Settings.ReminderCalendarIds.ToList();
        if (on && !ids.Contains(feed.Id)) ids.Add(feed.Id);
        if (!on) ids.Remove(feed.Id);
        _s.Settings.ReminderCalendarIds = ids;
        _s.ReminderScheduler.Tick();
    }
}
