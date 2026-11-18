using System.Diagnostics;
using UniversalMeetingRecorder.Core.Calendar;
using UniversalMeetingRecorder.Core.Util;
using Microsoft.Win32;

namespace UniversalMeetingRecorder.App.Platform;

/// <summary>Открытие папок, ссылок и т.п. через оболочку Windows.</summary>
public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Shell: не удалось открыть {target}", ex);
        }
    }

    public static void OpenFolder(string path) => Open(path);

    /// <summary>
    /// Ссылка на звонок с маршрутизацией по браузеру (порт ConferenceLinkOpener): trueconf: и ktalk/mts-link —
    /// в Chrome, если он установлен; остальное — в браузер по умолчанию.
    /// </summary>
    public static void OpenConferenceLink(string url)
    {
        var target = ConferenceLinkRouting.Normalize(url);
        if (ConferenceLinkRouting.WantsChrome(target) && ChromePath() is { } chrome)
        {
            try
            {
                Process.Start(new ProcessStartInfo(chrome) { ArgumentList = { target }, UseShellExecute = false });
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Shell: Chrome не запустился, открываю браузером по умолчанию", ex);
            }
        }
        Open(target);
    }

    private static string? ChromePath()
    {
        const string appPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(appPaths);
            if (key?.GetValue(null) is string path && File.Exists(path)) return path;
        }
        return null;
    }
}
