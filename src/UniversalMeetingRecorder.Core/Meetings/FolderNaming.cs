using System.Globalization;

namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>
/// Имена папок встреч — та же схема, что на Mac. Время локальное.
/// В процессе записи: <c>yyyyMMdd-HHmmss-recording-inprogress-App</c> (секунды — чтобы две записи
/// в одну минуту не делили папку); после сведения: <c>yyyyMMdd-HHmm-HHmm-App</c>.
/// </summary>
public static class FolderNaming
{
    public const string InProgressMarker = "recording-inprogress-";

    public static string InProgressName(DateTime startUtc, string appName)
    {
        var local = ToLocal(startUtc);
        return $"{local.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{local.ToString("HHmmss", CultureInfo.InvariantCulture)}-{InProgressMarker}{Sanitize(appName)}";
    }

    public static string FinalName(DateTime startUtc, DateTime endUtc, string appName)
    {
        var s = ToLocal(startUtc);
        var e = ToLocal(endUtc);
        return $"{s.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{s.ToString("HHmm", CultureInfo.InvariantCulture)}-{e.ToString("HHmm", CultureInfo.InvariantCulture)}-{Sanitize(appName)}";
    }

    /// <summary>
    /// Mac заменяет только «/» и «:»; на Windows недопустимых символов больше, плюс имя не может
    /// оканчиваться точкой или пробелом.
    /// </summary>
    public static string Sanitize(string s)
    {
        var chars = s.Select(c => c is '/' or ':' or '\\' or '*' or '?' or '"' or '<' or '>' or '|' || c < 0x20 ? '-' : c).ToArray();
        return new string(chars).TrimEnd('.', ' ');
    }

    private static DateTime ToLocal(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();
}
