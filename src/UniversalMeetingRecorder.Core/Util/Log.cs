namespace UniversalMeetingRecorder.Core.Util;

/// <summary>
/// Минимальный файловый лог. В отличие от macOS-версии (где NSLog собранного приложения
/// никуда не попадал), на Windows лог — обычный файл в %LOCALAPPDATA%\UniversalMeetingRecorder\logs.
/// Важные для пользователя ошибки всё равно кладутся в meeting.json/UI, лог — для отладки.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Configure(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        var path = _path;
        if (path is null) return;
        try
        {
            lock (Gate) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Лог не должен ронять приложение.
        }
    }
}
