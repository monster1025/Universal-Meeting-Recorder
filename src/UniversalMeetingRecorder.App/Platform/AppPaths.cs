namespace UniversalMeetingRecorder.App.Platform;

/// <summary>
/// Где приложение держит своё. Debug-сборка — в отдельных папках (аналог bundle id ".dev" на Mac):
/// иначе запуск из IDE делил бы настройки и автозапуск с установленной копией.
/// </summary>
public static class AppPaths
{
#if DEV_BUILD
    public const string AppId = "UniversalMeetingRecorder.dev";
    public const bool IsDevBuild = true;
#else
    public const string AppId = "UniversalMeetingRecorder";
    public const bool IsDevBuild = false;
#endif

    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);
    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId, "logs");

    /// <summary>Путь к exe — для автозапуска и для исключения себя из детекции микрофона.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "UniversalMeetingRecorder.exe");

    /// <summary>AUMID для toast-уведомлений.</summary>
    public const string Aumid = "com.monster." + AppId;
}
