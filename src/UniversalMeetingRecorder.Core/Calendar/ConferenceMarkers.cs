using System.Text.RegularExpressions;

namespace UniversalMeetingRecorder.Core.Calendar;

/// <summary>
/// Признак «в событии есть ссылка на звонок» и извлечение этой ссылки — порт CalendarService.swift.
/// Список маркеров плоский и в одном месте: свой сервис добавляется одной строкой.
/// </summary>
public static partial class ConferenceMarkers
{
    public static readonly string[] Markers =
    {
        "trueconf", "uc.sibur.ru", "telemost", "zoom.us", "zoommtg", "meet.google",
        "teams.microsoft", "teams.live", "skype", "ktalk", "jazz.sber", "salutejazz",
        "dion.vc", "videomost", "mts-link", "webinar.ru", "whereby", "jit.si",
        "bigbluebutton", "vinteo", "webex", "vk.com/call", "yandex.ru/j/", "meet.",
    };

    public static bool HasMarker(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lower = text.ToLowerInvariant();
        return Markers.Any(lower.Contains);
    }

    /// <summary>Ищется в URL, месте и заметках — разные клиенты кладут ссылку в разные поля.</summary>
    public static bool HasConferenceLink(params string?[] fields) =>
        HasMarker(string.Join(" ", fields.Where(f => f is not null)));

    // Аналог NSDataDetector для http(s): ссылка до пробела/угловой скобки/кавычки.
    [GeneratedRegex(@"https?://[^\s<>""'\)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrl();

    /// <summary>
    /// Ссылка на звонок. Два прохода, порядок важен (см. Mac): сначала первая http(s)-ссылка
    /// в любом поле с маркером (место «TrueConf» не должно перебить настоящую ссылку из заметок);
    /// затем поле целиком как URL — «ktalk.me/…» без схемы и псевдо-ссылки trueconf:\c\…@….
    /// </summary>
    public static string? ExtractLink(params string?[] fields)
    {
        var candidates = fields.Where(f => f is not null).Select(f => f!.Trim()).Where(f => f.Length > 0 && HasMarker(f)).ToList();
        foreach (var candidate in candidates)
        {
            var m = HttpUrl().Match(candidate);
            if (m.Success) return m.Value.TrimEnd('.', ',', ';');
        }
        foreach (var candidate in candidates)
        {
            if (candidate.Contains(' ') || candidate.Contains('\n')) continue;
            var schemeEnd = candidate.IndexOf(':');
            var hasScheme = schemeEnd > 0 && candidate[..schemeEnd].All(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '.');
            if (hasScheme)
            {
                return candidate;
            }
            // Достраиваем схему только если строка похожа на домен: «TrueConf» без точки — не ссылка.
            if (candidate.Contains('.')) return "https://" + candidate;
        }
        return null;
    }
}

/// <summary>Идущее вхождение события календаря.</summary>
public sealed record OngoingMeeting(string Id, string Title, DateTime StartUtc, DateTime EndUtc, string? Link);

/// <summary>Календарь-фид в том виде, в каком его показывают настройки.</summary>
public sealed record CalendarFeed(string Id, string Title, string SourceTitle);

/// <summary>Сырое событие календаря — то, что достаёт платформенный источник (Outlook).</summary>
public sealed record CalendarEventData(
    string Key,
    string Title,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay,
    bool IsCancelled,
    bool IsDeclined,
    string? Url,
    string? Location,
    string? Notes);

public static class OngoingMeetingFilter
{
    /// <summary>
    /// Точная проверка «идёт прямо сейчас и это звонок»: не весь день, не отменено, не отклонено,
    /// start ≤ now &lt; end, есть маркер конференции. Ключ вхождения — key|unixStart.
    /// </summary>
    public static List<OngoingMeeting> Filter(IEnumerable<CalendarEventData> events, DateTime nowUtc) =>
        events
            .Where(e => !e.IsAllDay && !e.IsCancelled && !e.IsDeclined && e.StartUtc <= nowUtc && e.EndUtc > nowUtc)
            .Where(e => ConferenceMarkers.HasConferenceLink(e.Url, e.Location, e.Notes))
            .Select(e => new OngoingMeeting(
                $"{e.Key}|{(long)Util.IsoDate.ToUnixSeconds(e.StartUtc)}",
                string.IsNullOrWhiteSpace(e.Title) ? "Без названия" : e.Title,
                e.StartUtc,
                e.EndUtc,
                ConferenceMarkers.ExtractLink(e.Url, e.Location, e.Notes)))
            .OrderBy(m => m.StartUtc)
            .ToList();
}

/// <summary>
/// Маршрутизация ссылки по браузеру (порт ConferenceLinkOpener): trueconf: и ktalk/mts-link — в Chrome,
/// если он есть; остальное — в браузер по умолчанию (на Mac — Safari).
/// </summary>
public static class ConferenceLinkRouting
{
    private static readonly string[] ChromeProviders = { "ktalk", "mts-link" };

    public static string Normalize(string url) =>
        url.Contains(':') ? url : "https://" + url;

    public static bool WantsChrome(string url)
    {
        var lower = url.ToLowerInvariant();
        return lower.StartsWith("trueconf:", StringComparison.Ordinal) || ChromeProviders.Any(lower.Contains);
    }
}
