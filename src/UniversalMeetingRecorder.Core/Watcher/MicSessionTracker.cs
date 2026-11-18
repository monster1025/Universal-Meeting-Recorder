namespace UniversalMeetingRecorder.Core.Watcher;

/// <summary>Приложение, которое прямо сейчас держит микрофон (по данным ОС).</summary>
/// <param name="Key">Уникальный ключ источника (путь exe или имя пакета).</param>
/// <param name="Name">Человекочитаемое имя для папки встречи.</param>
public sealed record MicUser(string Key, string Name);

/// <summary>
/// Чистая логика детекции звонка (порт состояния ConferenceWatcher): из снимка «кто держит
/// микрофон» делает события по фронтам. Начало — когда появился хотя бы один не исключённый
/// пользователь микрофона; конец — когда не осталось ни одного. Исключения проверяются на
/// каждом снимке (подстрока без учёта регистра), поэтому правка списка действует сразу.
/// Собственный процесс отфильтровывает вызывающий код: наш же WASAPI-захват микрофона тоже
/// попадает в список пользователей микрофона.
/// </summary>
public sealed class MicSessionTracker
{
    private bool _active;
    private List<MicUser> _current = new();

    /// <summary>Сортированные имена через «+» — для ручного старта посреди идущего звонка.</summary>
    public string? ActiveAppName => _current.Count == 0 ? null : JoinNames(_current);

    public bool IsActive => _active;

    public enum EventKind
    {
        None,
        Started,
        Ended,
    }

    /// <summary>Применяет снимок; возвращает событие-фронт и (для старта) имя встречи.</summary>
    public (EventKind Kind, string? AppName) Apply(IEnumerable<MicUser> users, IReadOnlyCollection<string> excludedApps)
    {
        _current = users
            .Where(u => !excludedApps.Any(e => !string.IsNullOrWhiteSpace(e) && (u.Name.Contains(e.Trim(), StringComparison.OrdinalIgnoreCase) || u.Key.Contains(e.Trim(), StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var active = _current.Count > 0;
        if (active && !_active)
        {
            _active = true;
            var name = JoinNames(_current);
            return (EventKind.Started, name.Length == 0 ? "unknown" : name);
        }
        if (!active && _active)
        {
            _active = false;
            return (EventKind.Ended, null);
        }
        return (EventKind.None, null);
    }

    private static string JoinNames(IEnumerable<MicUser> users) =>
        string.Join("+", users.Select(u => u.Name).OrderBy(n => n, StringComparer.Ordinal));
}
