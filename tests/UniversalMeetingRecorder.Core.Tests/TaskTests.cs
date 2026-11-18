using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UniversalMeetingRecorder.Core.Meetings;
using UniversalMeetingRecorder.Core.Tasks;

namespace UniversalMeetingRecorder.Core.Tests;

public class TaskTests
{
    [Fact]
    public void StoreAddsSortsTogglesAndLinks()
    {
        using var dir = new TempDir();
        using var store = new TaskStore(Path.Combine(dir.Path, "tasks.sqlite"));
        Assert.Null(store.LastError);
        var today = DateTime.Today;
        Assert.True(store.Add("без срока", TaskScope.Personal));
        Assert.True(store.Add("завтра", TaskScope.Work, today.AddDays(1).ToUniversalTime()));
        Assert.True(store.Add("вчера", TaskScope.Work, today.AddDays(-1).ToUniversalTime()));
        Assert.False(store.Add("   ", TaskScope.Work));

        var titles = store.Tasks.Select(t => t.Title).ToList();
        Assert.Equal(new[] { "вчера", "завтра", "без срока" }, titles);
        Assert.True(store.Tasks[0].IsOverdue());
        Assert.False(store.Tasks[1].IsOverdue());

        store.ToggleComplete(store.Tasks[0]);
        Assert.Equal("вчера", store.Tasks.Last().Title);
        Assert.True(store.Tasks.Last().IsCompleted);

        var meeting = new Meeting { AppName = "Zoom", Title = "Встреча", StartDate = DateTime.UtcNow, FolderName = "f", SpeakrRecordingID = 3 };
        Assert.True(store.AddTasks(new[] { new TaskDraft(" из встречи ", TaskScope.Work, null), new TaskDraft("", TaskScope.Work, null) }, meeting));
        var linked = store.TitlesLinkedToMeeting(meeting.Id);
        Assert.Equal(new[] { "из встречи" }, linked);
        var task = store.Tasks.First(t => t.Title == "из встречи");
        var link = Assert.Single(store.LinksFor(task.Id));
        Assert.Equal(3, link.SpeakrRecordingId);
        Assert.Equal(meeting.Id, link.MeetingId);

        store.Delete(task);
        Assert.Empty(store.LinksFor(task.Id));
        Assert.DoesNotContain(store.Tasks, t => t.Title == "из встречи");
    }

    /// <summary>Golden: копия маковской tasks.sqlite открывается без повторной миграции и читается.</summary>
    [Fact]
    public void OpensMacDatabase()
    {
        var src = Path.Combine(MacData.Root, "tasks.sqlite");
        if (!File.Exists(src)) return;
        using var dir = new TempDir();
        var dst = Path.Combine(dir.Path, "tasks.sqlite");
        File.Copy(src, dst);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(src + suffix)) File.Copy(src + suffix, dst + suffix);
        }
        using var store = new TaskStore(dst);
        Assert.Null(store.LastError);
        Console.WriteLine($"golden tasks.sqlite: {store.Tasks.Count} задач");
        Assert.All(store.Tasks, t => Assert.Equal(t.Id, t.Id.ToUpperInvariant()));
    }

    [Fact]
    public void DigestTimingAndContent()
    {
        var now = new DateTime(2026, 11, 18, 10, 31, 0, DateTimeKind.Local);
        Assert.True(TaskDigest.IsDue(now, 630, null));
        Assert.False(TaskDigest.IsDue(now, 630, "2026-11-18"));
        Assert.False(TaskDigest.IsDue(now.AddMinutes(-2), 630, null));
        Assert.False(TaskDigest.IsDue(now.AddHours(7), 630, null));

        var items = new List<TodoItem>
        {
            TodoItem.Create("a", TaskScope.Work, now.Date.AddDays(-2).ToUniversalTime()),
            TodoItem.Create("b", TaskScope.Work, now.Date.AddDays(-1).ToUniversalTime()),
            TodoItem.Create("c", TaskScope.Work, now.Date.AddDays(-1).ToUniversalTime()),
            TodoItem.Create("d", TaskScope.Work, now.Date.AddDays(-1).ToUniversalTime()),
            TodoItem.Create("e", TaskScope.Work, now.Date.ToUniversalTime()),
            TodoItem.Create("f", TaskScope.Personal),
        };
        var content = TaskDigest.Content(items, now)!.Value;
        Assert.Equal("Открытых задач: 6", content.Title);
        Assert.Equal("Просрочено (4): a; b; c и ещё 1\nСегодня (1): e", content.Body);
        Assert.Null(TaskDigest.Content(new List<TodoItem>(), now));
        Assert.Equal("Срочных нет — задачи без срока или на потом.", TaskDigest.Content(new[] { TodoItem.Create("x", TaskScope.Work) }, now)!.Value.Body);
    }

    [Fact]
    public void FeedResponds()
    {
        static byte[] Snapshot() => TaskFeed.Encode(new[] { TodoItem.Create("t", TaskScope.Work, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) }, DateTime.UnixEpoch.AddSeconds(10.5));
        Assert.StartsWith("400", TaskFeedServer.Respond("", Snapshot).Status);
        var post = TaskFeedServer.Respond("POST /tasks HTTP/1.1", Snapshot);
        Assert.StartsWith("405", post.Status);
        Assert.Equal("GET", post.Extra!["Allow"]);
        Assert.StartsWith("404", TaskFeedServer.Respond("GET /x HTTP/1.1", Snapshot).Status);
        Assert.StartsWith("401", TaskFeedServer.Respond("GET /tasks HTTP/1.1\r\nAuthorization: Bearer nope", Snapshot).Status);
        var ok = TaskFeedServer.Respond($"GET /tasks?x=1 HTTP/1.1\r\nauthorization:  Bearer {TaskFeed.Token}", Snapshot);
        Assert.StartsWith("200", ok.Status);
        using var doc = JsonDocument.Parse(ok.Body);
        Assert.Equal(10.5, doc.RootElement.GetProperty("generatedAt").GetDouble());
        var t = doc.RootElement.GetProperty("tasks")[0];
        Assert.Equal("work", t.GetProperty("scope").GetString());
        Assert.Equal(1767225600, t.GetProperty("dueDate").GetDouble());
    }

    [Fact]
    public async Task FeedServesOverTcp()
    {
        var port = FreePort();
        using var server = new TaskFeedServer(() => TaskFeed.Encode(Array.Empty<TodoItem>(), DateTime.UtcNow), port);
        server.Start();
        using var http = new HttpClient();
        HttpResponseMessage? response = null;
        for (var i = 0; i < 50 && response is null; i++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/tasks");
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {TaskFeed.Token}");
                response = await http.SendAsync(req);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50);
            }
        }
        Assert.NotNull(response);
        Assert.Equal(200, (int)response!.StatusCode);
        Assert.Contains("\"tasks\":[]", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    private static int FreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
