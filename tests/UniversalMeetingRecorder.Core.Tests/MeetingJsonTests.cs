using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Tests;

public class MeetingJsonTests
{
    [Fact]
    public void RoundTripKeepsFieldsAndFormat()
    {
        var m = new Meeting
        {
            Id = Guid.Parse("6f18c2fb-24e5-4a14-b13a-e6e02eedc97c"),
            AppName = "TrueConf Client",
            Title = "CI/CD \"тест\"",
            StartDate = new DateTime(2026, 9, 21, 11, 52, 56, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 9, 21, 12, 2, 51, DateTimeKind.Utc),
            FolderName = "20260921-1452-1502-TrueConf Client",
            Status = MeetingStatus.Done,
            SpeakrRecordingID = 15,
            MicSampleRate = 44100,
            MicChannels = 1,
            MicStartOffset = 0.060209539,
            LastUpdated = new DateTime(2026, 11, 13, 7, 30, 8, DateTimeKind.Utc),
            SuggestedTasks = new List<SuggestedTask>(),
            DismissedSuggestions = new List<string> { "a b" },
        };
        var json = MeetingJson.Serialize(m);
        Assert.Contains("\"id\" : \"6F18C2FB-24E5-4A14-B13A-E6E02EEDC97C\"", json);
        Assert.Contains("\"micSampleRate\" : 44100,", json);
        Assert.Contains("\"micStartOffset\" : 0.060209539,", json);
        Assert.Contains("\"suggestedTasks\" : [\n\n  ],", json);
        Assert.Contains("\"title\" : \"CI\\/CD \\\"тест\\\"\"", json);
        Assert.Contains("\"startDate\" : \"2026-09-21T11:52:56Z\"", json);
        Assert.DoesNotContain("errorMessage", json);
        Assert.StartsWith("{\n  \"appName\"", json);
        Assert.EndsWith("\n}", json);

        var back = MeetingJson.Deserialize(json);
        Assert.Equal(json, MeetingJson.Serialize(back));
    }

    [Fact]
    public void AcceptsFractionalSecondsOnRead()
    {
        var json = """{"id":"6F18C2FB-24E5-4A14-B13A-E6E02EEDC97C","appName":"x","startDate":"2026-09-21T11:52:56.123Z","folderName":"f","audioFileName":"recording.m4a","status":"recording","deletedRemotely":false,"lastUpdated":"2026-09-21T11:52:56Z"}""";
        var m = MeetingJson.Deserialize(json);
        Assert.Equal(MeetingStatus.Recording, m.Status);
        Assert.Equal("2026-09-21T11:52:56Z", IsoDate.Format(m.StartDate));
    }

    [Fact]
    public void MissingRequiredFieldFails()
    {
        var json = """{"id":"6F18C2FB-24E5-4A14-B13A-E6E02EEDC97C","appName":"x","startDate":"2026-09-21T11:52:56Z","folderName":"f","audioFileName":"recording.m4a","status":"done","lastUpdated":"2026-09-21T11:52:56Z"}""";
        Assert.Throws<FormatException>(() => MeetingJson.Deserialize(json));
    }

    /// <summary>Golden: каждый meeting.json, записанный macOS-версией, после чтения и записи совпадает байт в байт.</summary>
    [Fact]
    public void MacMeetingJsonRoundTripsByteForByte()
    {
        var count = 0;
        foreach (var folder in MacData.MeetingFolders())
        {
            var path = Path.Combine(folder, "meeting.json");
            var original = File.ReadAllText(path);
            var reserialized = MeetingJson.Serialize(MeetingJson.Deserialize(original));
            Assert.True(original == reserialized, $"Расхождение в {path}:\n{FirstDiff(original, reserialized)}");
            count++;
        }
        Console.WriteLine($"golden meeting.json: {count}");
    }

    private static string FirstDiff(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Around(string s) => s.Substring(Math.Max(0, i - 60), Math.Min(120, s.Length - Math.Max(0, i - 60)));
        return $"mac: …{Around(a)}…\nwin: …{Around(b)}…";
    }
}
