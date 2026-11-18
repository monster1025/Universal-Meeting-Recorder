namespace UniversalMeetingRecorder.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cr-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Sub(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

/// <summary>Настоящие данные macOS-версии — для golden-тестов совместимости форматов (если есть на машине).</summary>
internal static class MacData
{
    public static string Root => Environment.GetEnvironmentVariable("CR_MAC_DATA")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", Settings.AppSettings.SharedDataFolderName);

    public static string Recordings => System.IO.Path.Combine(Root, "recordings");

    public static IEnumerable<string> MeetingFolders() =>
        Directory.Exists(Recordings)
            ? Directory.EnumerateDirectories(Recordings).Where(d => File.Exists(System.IO.Path.Combine(d, "meeting.json")))
            : Enumerable.Empty<string>();
}

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<string> Requests { get; } = new();

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Json(string json, int code = 200) =>
        new((System.Net.HttpStatusCode)code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
