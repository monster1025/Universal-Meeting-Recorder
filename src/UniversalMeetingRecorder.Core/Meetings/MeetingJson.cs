using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>
/// Чтение/запись meeting.json в формате macOS-версии (см. <see cref="AppleJson"/>):
/// camelCase-ключи Swift-свойств, null-поля не пишутся, UUID в верхнем регистре,
/// даты ISO8601 UTC без дробной части. Маппинг ручной — так формат виден целиком в одном месте
/// и не зависит от настроек сериализатора.
/// </summary>
public static class MeetingJson
{
    public static string StatusToString(MeetingStatus s) => s switch
    {
        MeetingStatus.Recording => "recording",
        MeetingStatus.Converting => "converting",
        MeetingStatus.Interrupted => "interrupted",
        MeetingStatus.Recorded => "recorded",
        MeetingStatus.Skipped => "skipped",
        MeetingStatus.Uploading => "uploading",
        MeetingStatus.Processing => "processing",
        MeetingStatus.Done => "done",
        MeetingStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static MeetingStatus StatusFromString(string s) => s switch
    {
        "recording" => MeetingStatus.Recording,
        "converting" => MeetingStatus.Converting,
        "interrupted" => MeetingStatus.Interrupted,
        "recorded" => MeetingStatus.Recorded,
        "skipped" => MeetingStatus.Skipped,
        "uploading" => MeetingStatus.Uploading,
        "processing" => MeetingStatus.Processing,
        "done" => MeetingStatus.Done,
        "failed" => MeetingStatus.Failed,
        _ => throw new FormatException($"Неизвестный статус встречи: {s}"),
    };

    private static string ExtractionToString(ExtractionState s) => s switch
    {
        ExtractionState.Pending => "pending",
        ExtractionState.Done => "done",
        ExtractionState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static ExtractionState ExtractionFromString(string s) => s switch
    {
        "pending" => ExtractionState.Pending,
        "done" => ExtractionState.Done,
        "failed" => ExtractionState.Failed,
        _ => throw new FormatException($"Неизвестное состояние извлечения: {s}"),
    };

    public static string Uuid(Guid id) => id.ToString("D").ToUpperInvariant();

    public static string Serialize(Meeting m) => AppleJson.Serialize(ToNode(m));

    public static JsonObject ToNode(Meeting m)
    {
        var o = new JsonObject
        {
            ["id"] = Uuid(m.Id),
            ["appName"] = m.AppName,
            ["startDate"] = IsoDate.Format(m.StartDate),
            ["folderName"] = m.FolderName,
            ["audioFileName"] = m.AudioFileName,
            ["status"] = StatusToString(m.Status),
            ["deletedRemotely"] = m.DeletedRemotely,
            ["lastUpdated"] = IsoDate.Format(m.LastUpdated),
        };
        Put(o, "title", m.Title);
        if (m.EndDate is { } end) o["endDate"] = IsoDate.Format(end);
        Put(o, "remoteStatus", m.RemoteStatus);
        if (m.SpeakrRecordingID is { } rid) o["speakrRecordingID"] = rid;
        Put(o, "transcriptFileName", m.TranscriptFileName);
        Put(o, "transcriptMarkdownFileName", m.TranscriptMarkdownFileName);
        Put(o, "summaryFileName", m.SummaryFileName);
        Put(o, "errorMessage", m.ErrorMessage);
        if (m.MicSampleRate is { } rate) o["micSampleRate"] = rate;
        if (m.MicChannels is { } ch) o["micChannels"] = ch;
        if (m.MicStartOffset is { } off) o["micStartOffset"] = off;
        if (m.SysSampleRate is { } srate) o["sysSampleRate"] = srate;
        if (m.SysChannels is { } sch) o["sysChannels"] = sch;
        if (m.ExtractionState is { } es) o["extractionState"] = ExtractionToString(es);
        Put(o, "extractionError", m.ExtractionError);
        if (m.SuggestedTasks is { } tasks)
        {
            var arr = new JsonArray();
            foreach (var t in tasks) arr.Add(SuggestedToNode(t));
            o["suggestedTasks"] = arr;
        }
        if (m.ActionItemsReviewed is { } reviewed) o["actionItemsReviewed"] = reviewed;
        if (m.DismissedSuggestions is { } dismissed)
        {
            var arr = new JsonArray();
            foreach (var d in dismissed) arr.Add(d);
            o["dismissedSuggestions"] = arr;
        }
        return o;
    }

    private static JsonObject SuggestedToNode(SuggestedTask t)
    {
        var o = new JsonObject
        {
            ["id"] = Uuid(t.Id),
            ["title"] = t.Title,
            ["quote"] = t.Quote,
            ["confirmed"] = t.Confirmed,
        };
        if (t.Timestamp is { } ts) o["timestamp"] = ts;
        if (t.DueDate is { } due) o["dueDate"] = IsoDate.Format(due);
        if (t.Confidence is { } c) o["confidence"] = c;
        return o;
    }

    private static void Put(JsonObject o, string key, string? value)
    {
        if (value is not null) o[key] = value;
    }

    public static Meeting Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var m = new Meeting
        {
            Id = Guid.Parse(Req(r, "id").GetString()!),
            AppName = Req(r, "appName").GetString()!,
            Title = Str(r, "title"),
            StartDate = IsoDate.Parse(Req(r, "startDate").GetString()!),
            EndDate = Str(r, "endDate") is { } e ? IsoDate.Parse(e) : null,
            FolderName = Req(r, "folderName").GetString()!,
            AudioFileName = Req(r, "audioFileName").GetString()!,
            Status = StatusFromString(Req(r, "status").GetString()!),
            RemoteStatus = Str(r, "remoteStatus"),
            SpeakrRecordingID = Opt(r, "speakrRecordingID") is { } rid ? rid.GetInt32() : null,
            TranscriptFileName = Str(r, "transcriptFileName"),
            TranscriptMarkdownFileName = Str(r, "transcriptMarkdownFileName"),
            SummaryFileName = Str(r, "summaryFileName"),
            DeletedRemotely = Req(r, "deletedRemotely").GetBoolean(),
            ErrorMessage = Str(r, "errorMessage"),
            LastUpdated = IsoDate.Parse(Req(r, "lastUpdated").GetString()!),
            MicSampleRate = Opt(r, "micSampleRate")?.GetDouble(),
            MicChannels = Opt(r, "micChannels")?.GetUInt32(),
            MicStartOffset = Opt(r, "micStartOffset")?.GetDouble(),
            SysSampleRate = Opt(r, "sysSampleRate")?.GetDouble(),
            SysChannels = Opt(r, "sysChannels")?.GetUInt32(),
            ExtractionState = Str(r, "extractionState") is { } es ? ExtractionFromString(es) : null,
            ExtractionError = Str(r, "extractionError"),
            ActionItemsReviewed = Opt(r, "actionItemsReviewed")?.GetBoolean(),
        };
        if (Opt(r, "suggestedTasks") is { } tasks)
        {
            m.SuggestedTasks = tasks.EnumerateArray().Select(ParseSuggested).ToList();
        }
        if (Opt(r, "dismissedSuggestions") is { } dismissed)
        {
            m.DismissedSuggestions = dismissed.EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        return m;
    }

    private static SuggestedTask ParseSuggested(JsonElement e) => new()
    {
        Id = Guid.Parse(Req(e, "id").GetString()!),
        Title = Req(e, "title").GetString()!,
        Quote = Req(e, "quote").GetString()!,
        Timestamp = Opt(e, "timestamp")?.GetDouble(),
        DueDate = Str(e, "dueDate") is { } d ? IsoDate.Parse(d) : null,
        Confirmed = Req(e, "confirmed").GetBoolean(),
        Confidence = Opt(e, "confidence")?.GetDouble(),
    };

    private static JsonElement Req(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : throw new FormatException($"В meeting.json нет обязательного поля «{key}»");

    private static JsonElement? Opt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string? Str(JsonElement e, string key) => Opt(e, key)?.GetString();
}
