using System.Text;
using System.Text.Json;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Transcription;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Tests;

public class MendeleevTests
{
    private const string Base = "https://m.example";

    /// <summary>JWT без подписи (клиент её не проверяет): только exp и имя.</summary>
    private static string Jwt(DateTime expUtc, string name = "Иван Иванов")
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = JsonSerializer.Serialize(new { exp = (long)IsoDate.ToUnixSeconds(expUtc), display_name = name });
        return B64("{\"alg\":\"HS256\"}") + "." + B64(payload) + ".sig";
    }

    private sealed class FakeBrowser : ISsoBrowser
    {
        public int Calls;
        public Func<Uri, Uri?> Respond = _ => null;

        public Task<Uri?> WaitForRedirect(Uri authUrl, Uri redirectPrefix, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Respond(authUrl));
        }
    }

    private static MendeleevSession Session(FakeHandler handler, ISsoBrowser? browser, ISecretStore? secrets = null, Func<DateTime>? now = null) =>
        new(secrets ?? new InMemorySecretStore(), () => Base, browser, handler, now);

    private static HttpResponseMessage AuthUrl(string state = "S1") =>
        FakeHandler.Json($$"""{"url":"https://id.example/realms/r/auth?client_id=c&state={{state}}","state":"{{state}}"}""");

    [Fact]
    public async Task SsoLoginExchangesCodeAndStoresToken()
    {
        var token = Jwt(DateTime.UtcNow.AddHours(8));
        string? callbackBody = null;
        string? authQuery = null;
        var handler = new FakeHandler(req =>
        {
            var p = req.RequestUri!.PathAndQuery;
            if (p.StartsWith("/api/auth/keycloak-auth-url"))
            {
                authQuery = p;
                return AuthUrl();
            }
            if (p == "/api/auth/keycloak-callback")
            {
                callbackBody = req.Content!.ReadAsStringAsync().Result;
                return FakeHandler.Json($$"""{"access_token":"{{token}}"}""");
            }
            return FakeHandler.Json("{}", 500);
        });
        var secrets = new InMemorySecretStore();
        var browser = new FakeBrowser { Respond = _ => new Uri($"{Base}/login?code=abc%20123&state=S1") };
        var session = Session(handler, browser, secrets);

        Assert.Equal(token, await session.GetToken(interactive: false, default));
        Assert.Equal("/api/auth/keycloak-auth-url?redirect_uri=" + Uri.EscapeDataString($"{Base}/login"), authQuery);
        using var body = JsonDocument.Parse(callbackBody!);
        Assert.Equal("abc 123", body.RootElement.GetProperty("code").GetString());
        Assert.Equal($"{Base}/login", body.RootElement.GetProperty("redirect_uri").GetString());
        Assert.Equal(token, secrets.Get(MendeleevSession.SecretService, MendeleevSession.SecretAccount));
        Assert.Equal("Иван Иванов", session.UserName);
        Assert.True(session.IsValid);

        // Токен живой — повторный вход не нужен.
        await session.GetToken(false, default);
        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public async Task SsoRejectsForeignStateAndErrors()
    {
        var handler = new FakeHandler(req => req.RequestUri!.PathAndQuery.StartsWith("/api/auth/keycloak-auth-url") ? AuthUrl() : FakeHandler.Json("{}", 500));
        var browser = new FakeBrowser { Respond = _ => new Uri($"{Base}/login?code=x&state=OTHER") };
        var session = Session(handler, browser);
        var ex = await Assert.ThrowsAsync<RemoteAuthException>(() => session.GetToken(true, default));
        Assert.Contains("state", ex.Message);

        browser.Respond = _ => new Uri($"{Base}/login?error=access_denied&error_description=Нет+доступа&state=S1");
        ex = await Assert.ThrowsAsync<RemoteAuthException>(() => session.GetToken(true, default));
        Assert.Contains("Нет доступа", ex.Message);

        browser.Respond = _ => null;
        ex = await Assert.ThrowsAsync<RemoteAuthException>(() => session.GetToken(true, default));
        Assert.Equal("Вход отменён", ex.Message);
    }

    [Fact]
    public async Task FailedLoginIsNotRetriedInBackgroundButUserCanRetry()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var token = Jwt(now.AddHours(8));
        var handler = new FakeHandler(req =>
        {
            var p = req.RequestUri!.PathAndQuery;
            if (p.StartsWith("/api/auth/keycloak-auth-url")) return AuthUrl();
            return FakeHandler.Json($$"""{"access_token":"{{token}}"}""");
        });
        var browser = new FakeBrowser { Respond = _ => null };
        var clock = now;
        var session = Session(handler, browser, now: () => clock);

        await Assert.ThrowsAsync<RemoteAuthException>(() => session.GetToken(false, default));
        Assert.Equal(1, browser.Calls);
        // Фоновая загрузка не должна открывать окно входа каждые 10 секунд.
        var ex = await Assert.ThrowsAsync<RemoteAuthException>(() => session.GetToken(false, default));
        Assert.Contains("требуется вход", ex.Message);
        Assert.Equal(1, browser.Calls);
        // Явное действие пользователя кулдаун обходит.
        browser.Respond = _ => new Uri($"{Base}/login?code=c&state=S1");
        Assert.Equal(token, await session.GetToken(true, default));
        Assert.Equal(2, browser.Calls);
    }

    [Fact]
    public async Task RefreshesTokenCloseToExpiry()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var old = Jwt(now.AddMinutes(10));
        var fresh = Jwt(now.AddHours(8));
        string? auth = null;
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/auth/refresh")
            {
                auth = req.Headers.Authorization!.Parameter;
                return FakeHandler.Json($$"""{"access_token":"{{fresh}}"}""");
            }
            return FakeHandler.Json("{}", 500);
        });
        var secrets = new InMemorySecretStore();
        secrets.Set(MendeleevSession.SecretService, MendeleevSession.SecretAccount, old);
        var browser = new FakeBrowser();
        var session = Session(handler, browser, secrets, () => now);
        Assert.Equal(fresh, await session.GetToken(false, default));
        Assert.Equal(old, auth);
        Assert.Equal(0, browser.Calls);
        Assert.Equal(fresh, secrets.Get(MendeleevSession.SecretService, MendeleevSession.SecretAccount));
    }

    [Fact]
    public async Task ExpiredTokenTriggersNewLogin()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var fresh = Jwt(now.AddHours(8));
        var handler = new FakeHandler(req => req.RequestUri!.PathAndQuery.StartsWith("/api/auth/keycloak-auth-url") ? AuthUrl() : FakeHandler.Json($$"""{"access_token":"{{fresh}}"}"""));
        var secrets = new InMemorySecretStore();
        secrets.Set(MendeleevSession.SecretService, MendeleevSession.SecretAccount, Jwt(now.AddMinutes(-5)));
        var browser = new FakeBrowser { Respond = _ => new Uri($"{Base}/login?code=c&state=S1") };
        var session = Session(handler, browser, secrets, () => now);
        Assert.Equal(fresh, await session.GetToken(false, default));
        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public void TranscriptIsConvertedToSpeakrFormat()
    {
        using var doc = JsonDocument.Parse("""{"id":"x","segments":[{"start":0.031,"end":1.673,"text":"Привет","speaker":"SPEAKER_00","words":[]},{"start":2,"end":null,"text":"Пока"}]}""");
        var bytes = MendeleevClient.ToSpeakrTranscript(doc.RootElement);
        var parsed = Core.Speakr.SpeakrTranscriptResponse.Parse(bytes);
        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal(new Core.Speakr.SpeakrTranscriptSegment("SPEAKER_00", "Привет", 0.031, 1.673), parsed.Segments[0]);
        Assert.Equal(new Core.Speakr.SpeakrTranscriptSegment(null, "Пока", 2, null), parsed.Segments[1]);
    }

    private static Meeting NewMeeting(MeetingStore store)
    {
        var start = DateTime.UtcNow.AddHours(-1);
        var m = store.Save(new Meeting { AppName = "Zoom", StartDate = start, EndDate = start.AddMinutes(10), FolderName = "m-" + Guid.NewGuid().ToString("N")[..6], Status = MeetingStatus.Recorded });
        File.WriteAllBytes(store.AudioPath(m), new byte[1000]);
        return m;
    }

    [Fact]
    public async Task PipelineUploadsToMendeleevWhenSelected()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        var settings = new AppSettings(null, new InMemorySecretStore()) { TranscriptionBackend = TranscriptionBackends.Mendeleev, MendeleevBaseUrl = Base, DeleteAfterUploadEnabled = true };
        const string id = "11111111-2222-3333-4444-555555555555";
        var polls = 0;
        var deleted = false;
        var token = Jwt(DateTime.UtcNow.AddHours(8));
        var handler = new FakeHandler(req =>
        {
            var p = req.RequestUri!.AbsolutePath;
            Assert.Equal("Bearer " + token, req.Headers.Authorization?.ToString());
            if (p == "/api/recordings/upload" && req.Method == HttpMethod.Post)
            {
                Assert.StartsWith("multipart/form-data", req.Content!.Headers.ContentType!.MediaType);
                return FakeHandler.Json($$"""{"meeting_id":"{{id}}"}""", 200);
            }
            if (p == $"/api/recordings/meetings/{id}" && req.Method == HttpMethod.Get)
            {
                if (deleted) return FakeHandler.Json("""{"detail":"not found"}""", 404);
                polls++;
                return polls < 3
                    ? FakeHandler.Json("""{"id":"x","title":"f.m4a","processing_status":"queued","processing_step":"transcribing"}""")
                    : FakeHandler.Json("""{"id":"x","title":" Планёрка ","processing_status":"done","processing_step":null}""");
            }
            if (p == $"/api/recordings/meetings/{id}/transcript") return FakeHandler.Json("""{"segments":[{"start":0,"end":1.5,"text":"Привет","speaker":"SPEAKER_00","words":[]}]}""");
            if (p == $"/api/recordings/meetings/{id}/protocol") return FakeHandler.Json("""{"content":"# Протокол"}""");
            if (p == $"/api/recordings/meetings/{id}" && req.Method == HttpMethod.Delete)
            {
                deleted = true;
                return FakeHandler.Json("{}", 200);
            }
            return FakeHandler.Json("{}", 500);
        });
        var secrets = new InMemorySecretStore();
        secrets.Set(MendeleevSession.SecretService, MendeleevSession.SecretAccount, token);
        var session = new MendeleevSession(secrets, () => settings.MendeleevBaseUrl, null, handler);
        var pipeline = new UploadPipeline(store, settings, kind => kind == TranscriptionBackends.Mendeleev ? new MendeleevBackend(new MendeleevClient(session, handler)) : null)
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
        };
        var m = NewMeeting(store);
        pipeline.Enqueue(m);

        Meeting final = m;
        for (var i = 0; i < 300; i++)
        {
            final = store.Find(m.Id)!;
            if (final.Status == MeetingStatus.Done && final.DeletedRemotely) break;
            await Task.Delay(20);
        }
        Assert.Equal(MeetingStatus.Done, final.Status);
        Assert.True(final.DeletedRemotely);
        Assert.Equal(id, final.MendeleevMeetingId);
        Assert.Null(final.SpeakrRecordingID);
        Assert.Equal("Планёрка", final.Title);
        Assert.Equal("# Протокол", File.ReadAllText(store.NotesPath(final)!));
        // transcript.json — в формате Speakr, как у всех встреч.
        var segments = Core.Speakr.SpeakrTranscriptResponse.Parse(File.ReadAllBytes(store.TranscriptPath(final)!)).Segments;
        Assert.Equal("Привет", Assert.Single(segments).Sentence);
        var md = File.ReadAllText(store.TranscriptMarkdownPath(final)!);
        Assert.Contains($"mendeleev_meeting_id: \"{id}\"", md);
        // Файл meeting.json сохраняет новый ключ и читается обратно.
        Assert.Equal(id, MeetingJson.Deserialize(MeetingJson.Serialize(final)).MendeleevMeetingId);
    }

    [Fact]
    public async Task ExpiredTokenOn401LogsInAgainOnceAndRetries()
    {
        var oldToken = Jwt(DateTime.UtcNow.AddHours(8), "Старый");
        var newToken = Jwt(DateTime.UtcNow.AddHours(8), "Новый");
        var attempts = new List<string>();
        var handler = new FakeHandler(req =>
        {
            var p = req.RequestUri!.PathAndQuery;
            if (p.StartsWith("/api/auth/keycloak-auth-url")) return AuthUrl();
            if (p == "/api/auth/keycloak-callback") return FakeHandler.Json($$"""{"access_token":"{{newToken}}"}""");
            attempts.Add(req.Headers.Authorization!.Parameter!);
            return req.Headers.Authorization!.Parameter == oldToken
                ? FakeHandler.Json("""{"detail":"expired"}""", 401)
                : FakeHandler.Json("""{"processing_status":"queued"}""");
        });
        var secrets = new InMemorySecretStore();
        secrets.Set(MendeleevSession.SecretService, MendeleevSession.SecretAccount, oldToken);
        var browser = new FakeBrowser { Respond = _ => new Uri($"{Base}/login?code=c&state=S1") };
        var session = Session(handler, browser, secrets);
        var status = await new MendeleevClient(session, handler).GetStatus("id1", default);
        Assert.Equal(RemoteState.Pending, status.State);
        Assert.Equal(new[] { oldToken, newToken }, attempts);
        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public async Task ErrorsAreReadable()
    {
        var token = Jwt(DateTime.UtcNow.AddHours(8));
        var secrets = new InMemorySecretStore();
        secrets.Set(MendeleevSession.SecretService, MendeleevSession.SecretAccount, token);
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"detail":"Слишком большой файл"}""", 413));
        var client = new MendeleevClient(Session(handler, null, secrets), handler);
        var path = Path.Combine(Path.GetTempPath(), "umr-" + Guid.NewGuid().ToString("N") + ".m4a");
        File.WriteAllBytes(path, new byte[10]);
        try
        {
            var ex = await Assert.ThrowsAsync<MendeleevException>(() => client.Upload(path, default));
            Assert.Equal("НейроМенделеев вернул ошибку 413: Слишком большой файл", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MeetingsStayWithTheirOwnBackendAfterSwitching()
    {
        using var dir = new TempDir();
        var store = new MeetingStore(dir.Path);
        // В настройках выбран Speakr, но встреча уже загружена в НейроМенделеев — дорабатывать её должен он.
        var settings = new AppSettings(null, new InMemorySecretStore()) { TranscriptionBackend = TranscriptionBackends.Speakr };
        var asked = new List<string>();
        var pipeline = new UploadPipeline(store, settings, kind =>
        {
            asked.Add(kind);
            return null;
        });
        var m = NewMeeting(store);
        m = store.Update(m.Id, x =>
        {
            x.Status = MeetingStatus.Processing;
            x.MendeleevMeetingId = "abc";
        })!;
        pipeline.Enqueue(m);
        for (var i = 0; i < 100 && store.Find(m.Id)!.Status != MeetingStatus.Failed; i++) await Task.Delay(20);
        Assert.Equal(new[] { TranscriptionBackends.Mendeleev }, asked);
        Assert.Equal("НейроМенделеев не настроен — укажите адрес сервера в настройках", store.Find(m.Id)!.ErrorMessage);
    }

    /// <summary>
    /// Живая проверка на настоящем сервере, только чтение: UMR_MENDELEEV_URL и UMR_MENDELEEV_TOKEN в окружении
    /// (токен — временный, из sessionStorage браузера). Без них тест молча проходит. Ничего не загружает и не удаляет.
    /// </summary>
    [Fact]
    public async Task LiveReadOnlyAgainstRealServer()
    {
        var url = Environment.GetEnvironmentVariable("UMR_MENDELEEV_URL");
        var token = Environment.GetEnvironmentVariable("UMR_MENDELEEV_TOKEN");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return;
        var secrets = new InMemorySecretStore();
        secrets.Set(MendeleevSession.SecretService, MendeleevSession.SecretAccount, token);
        var session = new MendeleevSession(secrets, () => url, null);
        var client = new MendeleevClient(session);

        // Список встреч — тем же токеном напрямую, чтобы взять id существующей встречи.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var list = JsonDocument.Parse(await http.GetStringAsync(url.TrimEnd('/') + "/api/recordings/meetings?limit=5"));
        var items = list.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return;
        var id = items[0].GetProperty("id").GetString()!;

        var status = await client.GetStatus(id, default);
        Console.WriteLine($"live: status {status.State}/{status.Raw}");
        if (status.State != RemoteState.Completed) return;
        var result = await client.GetResult(id, default);
        var segments = Core.Speakr.SpeakrTranscriptResponse.Parse(result.TranscriptJson).Segments;
        Console.WriteLine($"live: сегментов {segments.Count}, заметок {result.Notes.Length} симв., заголовок {(result.Title is null ? "нет" : "есть")}");
        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.NotNull(s.Sentence));
        // Markdown рендерится из преобразованного транскрипта.
        var meeting = new Meeting { AppName = "x", StartDate = DateTime.UtcNow, FolderName = "f", MendeleevMeetingId = id };
        Assert.Contains("segments: " + segments.Count, TranscriptMarkdown.Render(segments, meeting));
    }
}
