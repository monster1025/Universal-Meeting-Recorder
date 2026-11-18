using System.Media;
using UniversalMeetingRecorder.Core.Util;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace UniversalMeetingRecorder.App.Notifications;

public enum ReminderAction
{
    Snooze1,
    Snooze5,
    Disable,
}

/// <summary>
/// Toast-уведомления Windows (аналог MeetingReminderNotifier + сводки задач на Mac).
/// Напоминание о встрече — сценарий Reminder (висит, пока на него не ответят) с тремя кнопками,
/// фоновая активация (окно не всплывает). Звук тоста отключён, приложение играет свой напрямую:
/// режим «Не беспокоить» глушит тосты, но не звук приложения — как Frog на Mac поверх Focus.
/// Каждое напоминание заменяет предыдущее (тот же tag); сводка задач — свой tag.
/// </summary>
internal sealed class ToastNotifier
{
    private const string Group = "cr";
    private const string ReminderTag = "meeting-reminder";
    private const string DigestTag = "task-digest";

    public event Action<ReminderAction>? ActionInvoked;

    /// <summary>Подписка на нажатия кнопок. Вызывать на старте (до первого уведомления).</summary>
    public void Initialize()
    {
        ToastNotificationManagerCompat.OnActivated += args =>
        {
            var parsed = ToastArguments.Parse(args.Argument);
            if (!parsed.TryGetValue("action", out var action)) return; // клик по телу — ничего не делаем
            switch (action)
            {
                case "snooze-1": ActionInvoked?.Invoke(ReminderAction.Snooze1); break;
                case "snooze-5": ActionInvoked?.Invoke(ReminderAction.Snooze5); break;
                case "disable": ActionInvoked?.Invoke(ReminderAction.Disable); break;
            }
        };
    }

    /// <summary>Разрешены ли уведомления приложению (Параметры → Система → Уведомления).</summary>
    public bool? NotificationsEnabled
    {
        get
        {
            try
            {
                return ToastNotificationManagerCompat.CreateToastNotifier().Setting == NotificationSetting.Enabled;
            }
            catch
            {
                return null;
            }
        }
    }

    public void PresentReminder(string title, string body)
    {
        ClearReminder();
        PlaySound();
        try
        {
            new ToastContentBuilder()
                .AddArgument("kind", "reminder")
                .AddText(title)
                .AddText(body)
                .AddButton(new ToastButton().SetContent("Отложить на 1 минуту").AddArgument("action", "snooze-1").SetBackgroundActivation())
                .AddButton(new ToastButton().SetContent("Отложить на 5 минут").AddArgument("action", "snooze-5").SetBackgroundActivation())
                .AddButton(new ToastButton().SetContent("Отключить до конца встречи").AddArgument("action", "disable").SetBackgroundActivation())
                .SetToastScenario(ToastScenario.Reminder)
                .AddAudio(new ToastAudio { Silent = true })
                .Show(t =>
                {
                    t.Tag = ReminderTag;
                    t.Group = Group;
                });
        }
        catch (Exception ex)
        {
            Log.Error("ToastNotifier: не удалось показать напоминание", ex);
        }
    }

    public void ClearReminder()
    {
        try
        {
            ToastNotificationManagerCompat.History.Remove(ReminderTag, Group);
        }
        catch
        {
        }
    }

    public void PresentDigest(string title, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("kind", "digest")
                .AddText(title)
                .AddText(body)
                .Show(t =>
                {
                    t.Tag = DigestTag;
                    t.Group = Group;
                });
        }
        catch (Exception ex)
        {
            Log.Error("ToastNotifier: не удалось показать сводку", ex);
        }
    }

    private static void PlaySound()
    {
        try
        {
            var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Calendar.wav");
            if (File.Exists(wav)) new SoundPlayer(wav).Play();
            else SystemSounds.Exclamation.Play();
        }
        catch
        {
        }
    }
}
