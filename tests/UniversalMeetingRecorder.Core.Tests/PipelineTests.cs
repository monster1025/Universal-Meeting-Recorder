using UniversalMeetingRecorder.Core.Audio;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Speakr;
using UniversalMeetingRecorder.Core.Transcription;

namespace UniversalMeetingRecorder.Core.Tests;

public class PipelineTests
{
    private static AppSettings Settings() => new(null, new InMemorySecretStore()) { SpeakrBaseUrl = "http://speakr", SpeakrApiKey = "key" };

    private static Func<string, ITranscriptionBackend?> SpeakrOnly(HttpMessageHandler handler) =>
        kind => kind == TranscriptionBackends.Speakr ? new SpeakrBackend(SpeakrClient.Create("http://speakr", "key", handler)!) : null;

    private static Meeting NewMeeting(MeetingStore store, TimeSpan duration, MeetingStatus status = MeetingStatus.Recorded)
    {
        var start = DateTime.UtcNow.AddHours(-1);
        var m = new Meeting { AppName = "Zoom", StartDate = start, EndDate = start + duration, FolderName = "m-" + Guid.NewGuid().ToString("N")[..6], Status = status };
        var saved = store.Save(m);
        File.WriteAllBytes(store.AudioPath(saved), new byte[1000]);
        return saved;
    }

    private static async Task<Meeting> WaitFor(MeetingStore store, Guid id, Func<Meeting, bool> cond)
    {
        for (var i = 0; i < 200; i++)
        {
            var m = store.Find(id)!;
            if (cond(m)) return m;
            await Task.Delay(20);
        }
        return store.Find(id)!;
    }

    [Fact]
    public async Task UploadPollFinishDelete()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var settings = Settings();
        settings.DeleteAfterUploadEnabled = true;
        var polls = 0;
        var deleted = false;
        var handler = new FakeHandler(req =>
        {
            var p = req.RequestUri!.PathAndQuery;
            if (p == "/api/v1/recordings/upload") return FakeHandler.Json("""{"id":42,"status":"PENDING"}""", 202);
            if (p == "/api/v1/recordings/42/status") return FakeHandler.Json(++polls < 2 ? """{"id":42,"status":"QUEUED"}""" : """{"id":42,"status":"COMPLETED"}""");
            if (p == "/api/v1/recordings/42/transcript?format=json") return FakeHandler.Json("""{"segments":[{"speaker":"A","sentence":"hi","start_time":0,"end_time":1}]}""");
            if (p == "/api/v1/recordings/42/summary") return FakeHandler.Json("""{"summary":"# notes","has_summary":true}""");
            if (p == "/api/v1/recordings/42?format=minimal" && req.Method == HttpMethod.Get)
                return deleted ? FakeHandler.Json("{}", 404) : FakeHandler.Json("""{"id":42,"title":" Заголовок "}""");
            if (p == "/api/v1/recordings/42" && req.Method == HttpMethod.Delete)
            {
                deleted = true;
                return FakeHandler.Json("{}", 200);
            }
            return FakeHandler.Json("{}", 500);
        });
        var pipeline = new UploadPipeline(store, settings, SpeakrOnly(handler)) { PollInterval = TimeSpan.FromMilliseconds(10) };
        Guid? done = null;
        pipeline.MeetingDone += id => done = id;
        var m = NewMeeting(store, TimeSpan.FromMinutes(5));
        pipeline.Enqueue(m);
        var final = await WaitFor(store, m.Id, x => x.Status == MeetingStatus.Done && x.DeletedRemotely);
        Assert.Equal(MeetingStatus.Done, final.Status);
        Assert.True(final.DeletedRemotely);
        Assert.Equal(42, final.SpeakrRecordingID);
        Assert.Equal("Заголовок", final.Title);
        Assert.Equal("# notes", File.ReadAllText(store.NotesPath(final)!));
        Assert.True(File.Exists(Path.Combine(store.FolderPath(final), "transcript.md")));
        Assert.Equal(m.Id, done);
        // Идентификатор записи Speakr на диске до поллинга — проверяем, что upload был один.
        Assert.Single(handler.Requests, r => r.EndsWith("/upload"));
    }

    [Fact]
    public async Task ShortRecordingSkippedUnlessForced()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var handler = new FakeHandler(_ => FakeHandler.Json("{}", 500));
        var pipeline = new UploadPipeline(store, Settings(), SpeakrOnly(handler));
        var m = NewMeeting(store, TimeSpan.FromSeconds(60));
        pipeline.Enqueue(m);
        Assert.Equal(MeetingStatus.Skipped, store.Find(m.Id)!.Status);
        Assert.Empty(handler.Requests);

        pipeline.Retranscribe(store.Find(m.Id)!);
        var failed = await WaitFor(store, m.Id, x => x.Status == MeetingStatus.Failed);
        Assert.StartsWith("Загрузка не удалась:", failed.ErrorMessage);
    }

    [Fact]
    public async Task NotConfiguredFails()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var settings = new AppSettings(null, new InMemorySecretStore());
        var pipeline = new UploadPipeline(store, settings);
        var m = NewMeeting(store, TimeSpan.FromMinutes(5));
        pipeline.Enqueue(m);
        var failed = await WaitFor(store, m.Id, x => x.Status == MeetingStatus.Failed);
        Assert.Equal("Speakr не настроен — заполните URL и API-ключ в настройках", failed.ErrorMessage);
    }

    [Fact]
    public async Task DeleteFallsBackToBatchAndReportsFailure()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.Method == HttpMethod.Get) return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html><title>Still here</title></html>") };
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("<title>500 Internal</title>") };
        });
        var client = SpeakrClient.Create("http://s/", "k", handler)!;
        var ex = await Assert.ThrowsAsync<SpeakrException>(() => client.Delete(5));
        Assert.Contains("DELETE записи ответил 500, батч-удаление — 500", ex.Message);
        Assert.Contains("500 Internal | батч: 500 Internal", ex.Message);
        Assert.Contains(handler.Requests, r => r == "DELETE /api/v1/recordings/batch");
    }

    private sealed class FakeMixer : IAudioMixer
    {
        public bool Result { get; set; } = true;
        public (RawTrack? Sys, RawTrack? Mic, double Offset)? Call;

        public bool MergeTracks(RawTrack? sys, RawTrack? mic, double micOffsetSeconds, string outputPath, out string? error)
        {
            Call = (sys, mic, micOffsetSeconds);
            error = Result ? null : "boom";
            if (Result) File.WriteAllBytes(outputPath, new byte[500]);
            return Result;
        }
    }

    private static Meeting Recording(MeetingStore store, DateTime start, string app, int sysBytes, int micBytes, double? micRate = 48000, uint? micCh = 1)
    {
        var m = new Meeting
        {
            AppName = app,
            StartDate = start,
            FolderName = FolderNaming.InProgressName(start, app),
            Status = MeetingStatus.Recording,
            MicSampleRate = micRate,
            MicChannels = micCh,
            SysSampleRate = 44100,
            SysChannels = 2,
        };
        m = store.Save(m);
        var folder = store.FolderPath(m);
        if (sysBytes > 0) File.WriteAllBytes(Path.Combine(folder, RecordingRecovery.RawSysFileName), new byte[sysBytes]);
        if (micBytes > 0) File.WriteAllBytes(Path.Combine(folder, RecordingRecovery.RawMicFileName), new byte[micBytes]);
        return m;
    }

    [Fact]
    public void FinalizerMergesRenamesAndEstimates()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var mixer = new FakeMixer();
        var start = new DateTime(2026, 11, 18, 7, 0, 0, DateTimeKind.Utc);
        // sys: 44100×2×4 = 352800 байт/с → 10 с; mic: 48000×1×4 = 192000 байт/с → 9 с.
        var m = Recording(store, start, "Zoom", 352800 * 10, 192000 * 9);
        var result = new RecordingFinalizer(store, mixer).Convert(m.Id)!;
        Assert.Equal(MeetingStatus.Recorded, result.Status);
        Assert.Equal(FolderNaming.FinalName(start, start.AddSeconds(10), "Zoom"), result.FolderName);
        Assert.Equal(start.AddSeconds(10), result.EndDate);
        Assert.Equal(1.0, mixer.Call!.Value.Offset, 6);
        Assert.Equal(44100, mixer.Call.Value.Sys!.SampleRate);
        Assert.False(File.Exists(Path.Combine(store.FolderPath(result), RecordingRecovery.RawSysFileName)));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, m.FolderName)));
        // meeting.json лежит в новой папке
        store.Refresh();
        Assert.Equal(result.FolderName, store.Find(m.Id)!.FolderName);
    }

    [Fact]
    public void FinalizerRefusesUnknownMicFormatAndKeepsRaw()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var m = Recording(store, DateTime.UtcNow, "Zoom", 5000, 5000, null, null);
        Assert.Null(new RecordingFinalizer(store, new FakeMixer()).Convert(m.Id));
        var after = store.Find(m.Id)!;
        Assert.Equal(MeetingStatus.Interrupted, after.Status);
        Assert.Equal(RecordingFinalizer.ErrorNoMicFormat, after.ErrorMessage);
    }

    [Fact]
    public void FinalizerReportsMixFailure()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var m = Recording(store, DateTime.UtcNow, "Zoom", 5000, 0);
        Assert.Null(new RecordingFinalizer(store, new FakeMixer { Result = false }).Convert(m.Id));
        var after = store.Find(m.Id)!;
        Assert.Equal(MeetingStatus.Interrupted, after.Status);
        Assert.StartsWith(RecordingFinalizer.ErrorMergeFailed, after.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(store.FolderPath(after), RecordingRecovery.RawSysFileName)));
    }

    [Fact]
    public void FinalizerNoAudio()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var m = Recording(store, DateTime.UtcNow, "Zoom", 0, 0);
        Assert.Null(new RecordingFinalizer(store, new FakeMixer()).Convert(m.Id));
        Assert.Equal(RecordingFinalizer.ErrorNoAudio, store.Find(m.Id)!.ErrorMessage);
    }

    [Fact]
    public void FinalizerKeepsInProgressNameOnCollision()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var start = new DateTime(2026, 11, 18, 7, 0, 0, DateTimeKind.Utc);
        var m = Recording(store, start, "Zoom", 352800, 0);
        Directory.CreateDirectory(Path.Combine(dir.Path, FolderNaming.FinalName(start, start.AddSeconds(1), "Zoom")));
        var result = new RecordingFinalizer(store, new FakeMixer()).Convert(m.Id)!;
        Assert.Equal(m.FolderName, result.FolderName);
    }

    [Fact]
    public void StoreRefreshUsesDiskFolderName()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var m = store.Save(new Meeting { AppName = "x", StartDate = DateTime.UtcNow, FolderName = "old" });
        Directory.Move(Path.Combine(dir.Path, "old"), Path.Combine(dir.Path, "new"));
        store.Refresh();
        Assert.Equal("new", store.Find(m.Id)!.FolderName);
    }
}
