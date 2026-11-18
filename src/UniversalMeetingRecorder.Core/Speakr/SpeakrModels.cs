using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniversalMeetingRecorder.Core.Speakr;

public sealed record SpeakrUploadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("status")] string Status);

public sealed record SpeakrStatusResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error_message")] string? ErrorMessage);

public sealed record SpeakrTranscriptSegment(
    [property: JsonPropertyName("speaker")] string? Speaker,
    [property: JsonPropertyName("sentence")] string Sentence,
    [property: JsonPropertyName("start_time")] double? StartTime,
    [property: JsonPropertyName("end_time")] double? EndTime);

public sealed record SpeakrTranscriptResponse(
    [property: JsonPropertyName("segments")] List<SpeakrTranscriptSegment> Segments)
{
    /// <summary>Разбор сырого transcript.json (сохранённого verbatim). Бросает при несовпадении формы.</summary>
    public static SpeakrTranscriptResponse Parse(byte[] json)
    {
        var parsed = JsonSerializer.Deserialize<SpeakrTranscriptResponse>(json)
                     ?? throw new FormatException("transcript.json пуст");
        if (parsed.Segments is null) throw new FormatException("В transcript.json нет segments");
        foreach (var s in parsed.Segments)
        {
            if (s.Sentence is null) throw new FormatException("Сегмент транскрипта без sentence");
        }
        return parsed;
    }
}

public sealed record SpeakrSummaryResponse(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("has_summary")] bool HasSummary);

/// <summary>Ответ GET /recordings/{id}?format=minimal — нужен только title.</summary>
public sealed record SpeakrRecordingResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status);

public static class SpeakrRemoteStatus
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Summarizing = "SUMMARIZING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}
