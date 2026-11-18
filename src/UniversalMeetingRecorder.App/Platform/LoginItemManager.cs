using Microsoft.Win32;

namespace UniversalMeetingRecorder.App.Platform;

/// <summary>
/// Автозапуск при входе в систему — значение в HKCU\...\Run (аналог SMAppService на Mac).
/// Состояние читается из реестра, а не из настроек: пользователь может выключить его в Диспетчере задач.
/// </summary>
public static class LoginItemManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Command => $"\"{AppPaths.ExecutablePath}\" --autostart";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppPaths.AppId) is string value &&
                   value.Contains(AppPaths.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(AppPaths.AppId, Command);
        else key.DeleteValue(AppPaths.AppId, throwOnMissingValue: false);
    }
}
