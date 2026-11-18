using System.Globalization;

namespace UniversalMeetingRecorder.Core.Util;

/// <summary>
/// Даты meeting.json — как у Swift <c>.iso8601</c>: UTC, без дробной части секунд
/// (<c>2026-08-12T05:14:32Z</c>). Swift-декодер дробные секунды НЕ принимает, поэтому писать
/// их нельзя; читаем же с запасом (дробные секунды, смещения), чтобы не споткнуться о чужое.
/// </summary>
public static class IsoDate
{
    public static string Format(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    public static DateTime Parse(string text)
    {
        var dto = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return dto.UtcDateTime;
    }

    /// <summary>Отбрасывает доли секунды — так дата выглядит после круга «записать/прочитать».</summary>
    public static DateTime TruncateToSeconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    public static double ToUnixSeconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return (utc - DateTime.UnixEpoch).TotalSeconds;
    }

    public static DateTime FromUnixSeconds(double seconds) =>
        DateTime.UnixEpoch.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
}
