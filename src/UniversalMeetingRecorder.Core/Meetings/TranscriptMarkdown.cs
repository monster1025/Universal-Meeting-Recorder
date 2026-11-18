using System.Globalization;
using System.Text;
using UniversalMeetingRecorder.Core.Speakr;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Meetings;

/// <summary>
/// Рендер транскрипта Speakr в Markdown (transcript.md рядом с transcript.json) — один в один
/// с macOS-версией: YAML-фронтматтер + реплики, слитые по спикеру. JSON — источник истины,
/// .md — производный файл, его можно пересобрать в любой момент.
/// </summary>
public static class TranscriptMarkdown
{
    public const string FileName = "transcript.md";

    public static string Render(IReadOnlyList<SpeakrTranscriptSegment> segments, Meeting meeting)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"title: {Yaml(meeting.DisplayTitle)}\n");
        sb.Append($"app: {Yaml(meeting.AppName)}\n");
        sb.Append($"started: {Yaml(IsoDate.Format(meeting.StartDate))}\n");
        if (meeting.EndDate is { } end) sb.Append($"ended: {Yaml(IsoDate.Format(end))}\n");
        if (meeting.SpeakrRecordingID is { } rid) sb.Append($"speakr_recording_id: {rid}\n");
        var speakers = OrderedSpeakers(segments);
        if (speakers.Count > 0) sb.Append($"speakers: [{string.Join(", ", speakers.Select(Yaml))}]\n");
        sb.Append($"segments: {segments.Count}\n");
        sb.Append("---\n\n");
        sb.Append($"# {meeting.DisplayTitle}\n\n");

        if (segments.Count == 0)
        {
            sb.Append("_Транскрипт пуст: Speakr не вернул ни одного сегмента._\n");
            return sb.ToString();
        }

        foreach (var block in Blocks(segments))
        {
            var heading = "";
            if (block.Speaker is not null) heading += $"**{block.Speaker}**";
            if (TimeRange(block) is { } range) heading += heading.Length == 0 ? range : $" · {range}";
            if (heading.Length > 0) sb.Append(heading).Append("\n\n");
            sb.Append(string.Join(" ", block.Sentences)).Append("\n\n");
        }
        return sb.ToString();
    }

    private sealed class Block
    {
        public string? Speaker;
        public List<string> Sentences = new();
        public double? StartTime;
        public double? EndTime;
    }

    private static List<Block> Blocks(IReadOnlyList<SpeakrTranscriptSegment> segments)
    {
        var result = new List<Block>();
        foreach (var segment in segments)
        {
            var text = segment.Sentence.Trim();
            if (text.Length == 0) continue;
            if (result.Count > 0 && result[^1].Speaker == segment.Speaker)
            {
                var last = result[^1];
                last.Sentences.Add(text);
                last.EndTime = segment.EndTime ?? last.EndTime;
            }
            else
            {
                result.Add(new Block { Speaker = segment.Speaker, Sentences = { text }, StartTime = segment.StartTime, EndTime = segment.EndTime });
            }
        }
        return result;
    }

    private static List<string> OrderedSpeakers(IReadOnlyList<SpeakrTranscriptSegment> segments)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var s in segments)
        {
            if (s.Speaker is { } sp && seen.Add(sp)) result.Add(sp);
        }
        return result;
    }

    private static string? TimeRange(Block block)
    {
        if (block.StartTime is not { } start) return null;
        if (block.EndTime is not { } end || end <= start) return Timecode(start);
        return $"{Timecode(start)}–{Timecode(end)}";
    }

    /// <summary>mm:ss, для встреч длиннее часа — h:mm:ss.</summary>
    public static string Timecode(double seconds)
    {
        var total = Math.Max(0, (int)seconds);
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", m, s);
    }

    /// <summary>Строки фронтматтера всегда в кавычках: в заголовках Speakr бывают «:» и «#».</summary>
    private static string Yaml(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Рендерит сырой ответ Speakr и кладёт рядом transcript.md. null — не получилось.</summary>
    public static string? Write(byte[] transcriptJson, Meeting meeting, string folder)
    {
        SpeakrTranscriptResponse parsed;
        try
        {
            parsed = SpeakrTranscriptResponse.Parse(transcriptJson);
        }
        catch (Exception ex)
        {
            Log.Error($"TranscriptMarkdown: не удалось разобрать transcript.json встречи «{meeting.FolderName}»", ex);
            return null;
        }
        try
        {
            AtomicFile.WriteAllText(Path.Combine(folder, FileName), Render(parsed.Segments, meeting));
            return FileName;
        }
        catch (Exception ex)
        {
            Log.Error($"TranscriptMarkdown: не удалось записать {FileName} для «{meeting.FolderName}»", ex);
            return null;
        }
    }
}
