using UniversalMeetingRecorder.Core.Audio;
using UniversalMeetingRecorder.Core.Calendar;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Watcher;

namespace UniversalMeetingRecorder.Core.Tests;

public class PolicyTests
{
    [Fact]
    public void TrackerEdges()
    {
        var t = new MicSessionTracker();
        var excluded = new List<string> { "aquadictate" };
        Assert.Equal(MicSessionTracker.EventKind.None, t.Apply(new[] { new MicUser("a", "AquaDictate") }, excluded).Kind);
        var start = t.Apply(new[] { new MicUser("z", "zoom"), new MicUser("t", "TrueConf") }, excluded);
        Assert.Equal(MicSessionTracker.EventKind.Started, start.Kind);
        Assert.Equal("TrueConf+zoom", start.AppName);
        Assert.Equal(MicSessionTracker.EventKind.None, t.Apply(new[] { new MicUser("z", "zoom") }, excluded).Kind);
        Assert.Equal("zoom", t.ActiveAppName);
        // Исключение, добавленное на лету, действует сразу.
        Assert.Equal(MicSessionTracker.EventKind.Ended, t.Apply(new[] { new MicUser("z", "zoom") }, new[] { "ZOOM" }).Kind);
    }

    [Fact]
    public void Markers()
    {
        Assert.True(ConferenceMarkers.HasConferenceLink(null, "TrueConf", null));
        Assert.False(ConferenceMarkers.HasConferenceLink(null, "Переговорка 5", "обсудить"));
        Assert.Equal("https://zoom.us/j/123",
            ConferenceMarkers.ExtractLink(null, "TrueConf", "Ссылка: https://zoom.us/j/123. Пароль 1"));
        Assert.Equal("https://ktalk.me/abc", ConferenceMarkers.ExtractLink(null, "ktalk.me/abc", null));
        Assert.Equal("trueconf:\\c\\123@uc.sibur.ru", ConferenceMarkers.ExtractLink(null, null, "trueconf:\\c\\123@uc.sibur.ru"));
        Assert.Null(ConferenceMarkers.ExtractLink(null, "TrueConf", null));
        Assert.True(ConferenceLinkRouting.WantsChrome("trueconf:\\c\\1"));
        Assert.True(ConferenceLinkRouting.WantsChrome("https://x.ktalk.ru/r"));
        Assert.False(ConferenceLinkRouting.WantsChrome("https://zoom.us/j/1"));
    }

    [Fact]
    public void OngoingFilter()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        CalendarEventData Ev(string key, int startMin, int endMin, bool allDay = false, bool cancelled = false, bool declined = false, string? loc = "zoom.us/j/1") =>
            new(key, key, now.AddMinutes(startMin), now.AddMinutes(endMin), allDay, cancelled, declined, null, loc, null);
        var result = OngoingMeetingFilter.Filter(new[]
        {
            Ev("later", -5, 30), Ev("first", -30, 30), Ev("ended", -60, 0), Ev("future", 1, 30),
            Ev("allday", -60, 60, allDay: true), Ev("cancel", -5, 5, cancelled: true), Ev("decl", -5, 5, declined: true),
            Ev("nolink", -5, 5, loc: "Комната"),
        }, now);
        Assert.Equal(new[] { "first", "later" }, result.Select(r => r.Title));
        Assert.EndsWith("|" + (long)Util.IsoDate.ToUnixSeconds(now.AddMinutes(-30)), result[0].Id);
    }

    [Fact]
    public void ReminderSchedule()
    {
        var p = new ReminderPolicy();
        var t0 = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var meeting = new OngoingMeeting("k|1", "Стендап", t0.AddMinutes(-1), t0.AddMinutes(30), null);
        var first = p.Tick(t0, true, false, meeting);
        Assert.True(first.BorderVisible);
        Assert.NotNull(first.Notify);
        Assert.Equal(ReminderPolicy.NotificationTitle, first.Notify!.Value.Title);
        Assert.Null(p.Tick(t0.AddSeconds(15), true, false, meeting).Notify);
        Assert.NotNull(p.Tick(t0.AddSeconds(300), true, false, meeting).Notify);

        p.Snooze(t0.AddSeconds(301), TimeSpan.FromSeconds(60));
        var snoozed = p.Tick(t0.AddSeconds(315), true, false, meeting);
        Assert.False(snoozed.BorderVisible);
        Assert.Null(snoozed.Notify);
        // Снуз истёк, но плановый момент (t0+600) ещё не наступил — молчим до него.
        Assert.Null(p.Tick(t0.AddSeconds(362), true, false, meeting).Notify);
        Assert.NotNull(p.Tick(t0.AddSeconds(600), true, false, meeting).Notify);

        p.DisableForMeeting();
        Assert.False(p.Tick(t0.AddSeconds(900), true, false, meeting).BorderVisible);

        // Запись началась — всё снимается.
        var recording = p.Tick(t0.AddSeconds(901), true, true, meeting);
        Assert.False(recording.BorderVisible);
        Assert.True(recording.ClearNotification);

        // Новая встреча — сразу напоминание.
        var next = new OngoingMeeting("k2|2", "Другая", t0, t0.AddHours(1), null);
        Assert.NotNull(p.Tick(t0.AddSeconds(902), true, false, next).Notify);
    }

    [Fact]
    public void MixMathAndSilence()
    {
        Assert.Equal(48000, MixMath.OffsetFrames(1.0));
        Assert.Equal(-2400, MixMath.OffsetFrames(-0.05));
        Assert.Equal(0, MixMath.OffsetFrames(31));
        Assert.Equal(0, MixMath.OffsetFrames(double.NaN));

        var d = new SilenceDetector();
        var silent = new float[48000];
        for (var i = 0; i < 4; i++) Assert.Null(d.Feed(silent, 1, 48000, false));
        Assert.True(d.Feed(silent, 1, 48000, false));
        Assert.Null(d.Feed(silent, 1, 48000, false));
        Assert.False(d.Feed(new[] { 0.5f }, 1, 48000, false));
        for (var i = 0; i < 5; i++) d.Feed(silent, 1, 48000, false);
        Assert.False(d.Feed(silent, 1, 48000, true));
    }

    [Fact]
    public void FolderNames()
    {
        var start = new DateTime(2026, 8, 12, 8, 14, 32, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("20260812-081432-recording-inprogress-a-b-c", FolderNaming.InProgressName(start, "a/b:c"));
        Assert.Equal("20260812-0814-0815-TrueConf+zoom", FolderNaming.FinalName(start, start.AddMinutes(1), "TrueConf+zoom"));
        Assert.Equal("x-y-", FolderNaming.Sanitize("x?y*."));
    }
}
