using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Speakr;

namespace UniversalMeetingRecorder.Core.Transcription;

public static class TranscriptionBackendFactory
{
    /// <summary>Сервис по его виду. null — не настроен (нет адреса/ключа).</summary>
    public static Func<string, ITranscriptionBackend?> Create(AppSettings settings, MendeleevSession? mendeleev) => kind =>
    {
        if (kind == TranscriptionBackends.Mendeleev)
        {
            return mendeleev is null || mendeleev.BaseUri is null ? null : new MendeleevBackend(new MendeleevClient(mendeleev));
        }
        return SpeakrClient.Create(settings.SpeakrBaseUrl, settings.SpeakrApiKey) is { } client ? new SpeakrBackend(client) : null;
    };

    public static string NotConfiguredMessage(string kind) => kind == TranscriptionBackends.Mendeleev
        ? "НейроМенделеев не настроен — укажите адрес сервера в настройках"
        : "Speakr не настроен — заполните URL и API-ключ в настройках";
}

public static class RemoteLinks
{
    /// <summary>Ссылка на запись в веб-интерфейсе сервиса, куда она загружена; null — нет записи или адрес не задан.</summary>
    public static string? WebUrl(Meetings.Meeting meeting, AppSettings settings)
    {
        if (meeting.MendeleevMeetingId is { } mid)
        {
            var b = settings.MendeleevBaseUrl.Trim().TrimEnd('/');
            return b.Length == 0 ? null : $"{b}/meetings/{Uri.EscapeDataString(mid)}";
        }
        return meeting.SpeakrRecordingID is { } rid ? SpeakrClient.WebUrl(settings.SpeakrBaseUrl, rid) : null;
    }

    /// <summary>Какой сервис ведёт встречу (для подписей в карточке).</summary>
    public static string BackendOf(Meetings.Meeting meeting) =>
        meeting.MendeleevMeetingId is not null ? TranscriptionBackends.Mendeleev : TranscriptionBackends.Speakr;
}
