using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Speakr;

namespace UniversalMeetingRecorder.Core.Tests;

public class TranscriptMarkdownTests
{
    private static Meeting Meeting() => new()
    {
        AppName = "Zoom",
        Title = "Планёрка: \"итоги\"",
        StartDate = new DateTime(2026, 8, 12, 5, 14, 32, DateTimeKind.Utc),
        EndDate = new DateTime(2026, 8, 12, 6, 30, 0, DateTimeKind.Utc),
        FolderName = "f",
        SpeakrRecordingID = 7,
    };

    [Fact]
    public void MergesSpeakerBlocksAndFormatsTimes()
    {
        var segments = new List<SpeakrTranscriptSegment>
        {
            new("A", "Привет.", 0, 2.5),
            new("A", " Как дела? ", 2.5, 65),
            new("B", "", 70, 71),
            new("B", "Норм.", 3700, 3600),
            new(null, "Без спикера", null, null),
        };
        var md = TranscriptMarkdown.Render(segments, Meeting());
        var expected =
            "---\n" +
            "title: \"Планёрка: \\\"итоги\\\"\"\n" +
            "app: \"Zoom\"\n" +
            "started: \"2026-08-12T05:14:32Z\"\n" +
            "ended: \"2026-08-12T06:30:00Z\"\n" +
            "speakr_recording_id: 7\n" +
            "speakers: [\"A\", \"B\"]\n" +
            "segments: 5\n" +
            "---\n\n" +
            "# Планёрка: \"итоги\"\n\n" +
            "**A** · 00:00–01:05\n\nПривет. Как дела?\n\n" +
            "**B** · 1:01:40\n\nНорм.\n\n" +
            "Без спикера\n\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void EmptyTranscript()
    {
        var md = TranscriptMarkdown.Render(new List<SpeakrTranscriptSegment>(), Meeting());
        Assert.EndsWith("_Транскрипт пуст: Speakr не вернул ни одного сегмента._\n", md);
    }

    /// <summary>
    /// Golden: transcript.md, созданный на Mac, совпадает с рендером Windows. Рендер на Mac делается
    /// до получения заголовка от Speakr (или позже бэкфиллом) — поэтому сверяем с обоими вариантами.
    /// </summary>
    [Fact]
    public void MacTranscriptMarkdownMatches()
    {
        var count = 0;
        foreach (var folder in MacData.MeetingFolders())
        {
            var mdPath = Path.Combine(folder, "transcript.md");
            var jsonPath = Path.Combine(folder, "transcript.json");
            if (!File.Exists(mdPath) || !File.Exists(jsonPath)) continue;
            var meeting = MeetingJson.Deserialize(File.ReadAllText(Path.Combine(folder, "meeting.json")));
            var segments = SpeakrTranscriptResponse.Parse(File.ReadAllBytes(jsonPath)).Segments;
            var expected = File.ReadAllText(mdPath);
            // Заголовок мог смениться после рендера (повторная транскрибация) — берём его из самого .md.
            var titleLine = expected.Split('\n')[1];
            var mdTitle = titleLine["title: \"".Length..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            var asRendered = meeting.Clone();
            asRendered.Title = mdTitle;
            var rendered = TranscriptMarkdown.Render(segments, asRendered);
            Assert.True(expected == rendered, $"transcript.md расходится: {folder}");
            count++;
        }
        Console.WriteLine($"golden transcript.md: {count}");
    }
}
