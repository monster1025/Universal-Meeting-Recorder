using System.Diagnostics;
using UniversalMeetingRecorder.Core.Util;
using UniversalMeetingRecorder.Core.Watcher;
using Microsoft.Win32;

namespace UniversalMeetingRecorder.App.Recorder;

/// <summary>
/// Детектор «начался звонок» — аналог хвоста лога audiomxd на Mac. Windows сама ведёт учёт
/// приложений, использующих микрофон (это же питает значок микрофона в трее и «Конфиденциальность →
/// Микрофон → Недавняя активность»): HKCU\…\CapabilityAccessManager\ConsentStore\microphone.
/// У каждого приложения есть LastUsedTimeStart/LastUsedTimeStop; Stop == 0 при ненулевом Start
/// значит «держит микрофон прямо сейчас». Подключи пакетов — по имени пакета, обычные exe — в
/// подключе NonPackaged с путём, где «\» заменены на «#».
///
/// Интеграции по приложениям нет, как и на Mac: звонком считается любой, кто открыл микрофон.
/// Собственный exe исключается обязательно — наш захват микрофона тоже попадает в этот список,
/// и без фильтра запись «держала бы сама себя» бесконечно.
/// </summary>
internal sealed class MicUsageWatcher : IDisposable
{
    private const string ConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly MicSessionTracker _tracker = new();
    private readonly Func<IReadOnlyCollection<string>> _excludedApps;
    private readonly string _ownExeKey;
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private int _polling;

    public event Action<string>? ConferenceStarted;
    public event Action? ConferenceEnded;

    public MicUsageWatcher(Func<IReadOnlyCollection<string>> excludedApps)
    {
        _excludedApps = excludedApps;
        _ownExeKey = Platform.AppPaths.ExecutablePath.Replace('\\', '#');
    }

    /// <summary>Кто держит микрофон сейчас — для имени ручной записи посреди звонка.</summary>
    public string? ActiveAppName => _tracker.ActiveAppName;

    public void Start()
    {
        Poll();
        _timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var users = Snapshot();
            var (kind, name) = _tracker.Apply(users, _excludedApps());
            if (kind == MicSessionTracker.EventKind.Started) ConferenceStarted?.Invoke(name!);
            else if (kind == MicSessionTracker.EventKind.Ended) ConferenceEnded?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("MicUsageWatcher: опрос реестра", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private List<MicUser> Snapshot()
    {
        var result = new List<MicUser>();
        using var root = Registry.CurrentUser.OpenSubKey(ConsentKey);
        if (root is null) return result;
        foreach (var sub in root.GetSubKeyNames())
        {
            if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                using var nonPackaged = root.OpenSubKey(sub);
                if (nonPackaged is null) continue;
                foreach (var exeKey in nonPackaged.GetSubKeyNames())
                {
                    if (exeKey.Equals(_ownExeKey, StringComparison.OrdinalIgnoreCase)) continue;
                    using var app = nonPackaged.OpenSubKey(exeKey);
                    if (app is not null && IsActive(app)) result.Add(new MicUser(exeKey, ExeName(exeKey)));
                }
            }
            else
            {
                using var app = root.OpenSubKey(sub);
                if (app is not null && IsActive(app)) result.Add(new MicUser(sub, PackageName(sub)));
            }
        }
        return result;
    }

    private static bool IsActive(RegistryKey app)
    {
        var start = ToLong(app.GetValue("LastUsedTimeStart"));
        var stop = ToLong(app.GetValue("LastUsedTimeStop"));
        return start != 0 && stop == 0;
    }

    private static long ToLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        byte[] { Length: 8 } b => BitConverter.ToInt64(b),
        _ => 0,
    };

    /// <summary>MSTeams_8wekyb3d8bbwe → MSTeams.</summary>
    private static string PackageName(string packageFamily)
    {
        var i = packageFamily.IndexOf('_');
        return i > 0 ? packageFamily[..i] : packageFamily;
    }

    /// <summary>
    /// C:#Program Files#Zoom#bin#Zoom.exe → описание файла («Zoom Meetings»), иначе имя exe без расширения.
    /// Имя попадает в имя папки встречи, как «TrueConf Client» на Mac.
    /// </summary>
    private string ExeName(string exeKey)
    {
        if (_nameCache.TryGetValue(exeKey, out var cached)) return cached;
        var path = exeKey.Replace('#', '\\');
        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
            {
                name = description.Trim();
            }
        }
        catch
        {
            // нет доступа к файлу — остаётся имя exe
        }
        _nameCache[exeKey] = name;
        return name;
    }
}
