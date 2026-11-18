using System.ComponentModel;
using System.Windows.Forms;
using UniversalMeetingRecorder.App.Platform;
using UniversalMeetingRecorder.Core.Tasks;
using Microsoft.Win32;

namespace UniversalMeetingRecorder.App.Tray;

/// <summary>
/// Иконка в трее и её меню — порт MenuBarView. Меню собирается заново при каждом открытии
/// (состояние записи, наличие идущей встречи, счётчик новых задач), иконка обновляется по
/// PropertyChanged координатора. ЛКМ открывает то же меню, что и ПКМ.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly AppServices _services;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();

    public TrayController(AppServices services)
    {
        _services = services;
        _icon = new NotifyIcon { Visible = false, ContextMenuStrip = _menu };
        _menu.Opening += (_, _) => BuildMenu();
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // Штатный способ показать ContextMenuStrip по ЛКМ так, чтобы меню закрывалось кликом мимо.
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_icon, null);
        };
        services.Coordinator.PropertyChanged += OnCoordinatorChanged;
        SystemEvents.UserPreferenceChanged += (_, _) => _services.Ui.BeginInvoke(UpdateIcon);
    }

    public void Show()
    {
        UpdateIcon();
        _icon.Visible = true;
    }

    private void OnCoordinatorChanged(object? sender, PropertyChangedEventArgs e) => _services.Ui.BeginInvoke(UpdateIcon);

    private void UpdateIcon()
    {
        var c = _services.Coordinator;
        var kind = c.IsRecording ? TrayIconRenderer.Kind.Recording : TrayIconRenderer.Kind.Idle;
        var old = _icon.Icon;
        _icon.Icon = TrayIconRenderer.Render(kind, red: AppPaths.IsDevBuild || (c.IsRecording && c.MicSilenceWarning));
        old?.Dispose();
        var text = c.IsRecording
            ? c.MicSilenceWarning ? $"Запись: {c.CurrentAppName} — микрофон молчит!" : $"Идёт запись: {c.CurrentAppName}"
            : "Universal Meeting Recorder — ожидание конференции";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var c = _services.Coordinator;
        var status = new ToolStripMenuItem(c.IsRecording ? $"🔴 Идёт запись: {c.CurrentAppName}" : "Ожидание конференции…") { Enabled = false };
        _menu.Items.Add(status);
        _menu.Items.Add(new ToolStripSeparator());

        if (c.IsRecording) _menu.Items.Add("Остановить запись", null, (_, _) => c.StopManually());
        else _menu.Items.Add("Начать запись", null, (_, _) => c.StartManually());

        var ongoing = _services.ReminderScheduler.Ongoing;
        var openLink = new ToolStripMenuItem("Открыть ссылку конференции") { Enabled = ongoing is not null };
        openLink.Click += (_, _) =>
        {
            if (_services.ReminderScheduler.Ongoing?.Link is { } link) Shell.OpenConferenceLink(link);
        };
        _menu.Items.Add(openLink);
        _menu.Items.Add(new ToolStripSeparator());

        var newSuggestions = ActionItemService.UnreviewedCount(_services.Meetings.Meetings);
        var meetingsTitle = newSuggestions > 0 ? $"Открыть список встреч… · {newSuggestions} новых задач" : "Открыть список встреч…";
        _menu.Items.Add(meetingsTitle, null, (_, _) => _services.Windows.ShowMeetings());
        _menu.Items.Add("Задачи…", null, (_, _) => _services.Windows.ShowTasks());
        _menu.Items.Add("Настройки…", null, (_, _) => _services.Windows.ShowSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => _services.Quit());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
