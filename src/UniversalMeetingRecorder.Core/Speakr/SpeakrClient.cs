using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UniversalMeetingRecorder.Core.Speakr;

public class SpeakrException : Exception
{
    public SpeakrException(string message) : base(message) { }
}

public sealed class SpeakrHttpException : SpeakrException
{
    public int StatusCode { get; }

    public SpeakrHttpException(int code, string body)
        : base($"Speakr вернул ошибку {code}: {Prefix(body, 200)}")
    {
        StatusCode = code;
    }

    internal static string Prefix(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>
/// Клиент REST API Speakr. Только то, что нужно пайплайну: upload → status → transcript/summary → (delete).
/// Базовый путь — {instance}/api/v1 (путь без версии отдаёт 404). Невалидный токен на сервере
/// даёт 302 на /login, а не 401: HttpClient следует редиректу молча, как и URLSession на Mac.
/// </summary>
public sealed class SpeakrClient
{
    private readonly Uri _baseUri;
    private readonly string _apiKey;
    private readonly HttpClient _http;

    private SpeakrClient(Uri baseUri, string apiKey, HttpClient http)
    {
        _baseUri = baseUri;
        _apiKey = apiKey;
        _http = http;
    }

    /// <summary>null, если URL или ключ пусты/некорректны — «Speakr не настроен».</summary>
    public static SpeakrClient? Create(string baseUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        var trimmed = (baseUrl ?? "").Trim();
        if (trimmed.Length == 0 || string.IsNullOrEmpty(apiKey)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        // appendingPathComponent("api/v1") — со слешем на конце или без.
        var basePath = uri.AbsoluteUri.TrimEnd('/') + "/api/v1/";
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        return new SpeakrClient(new Uri(basePath), apiKey, http);
    }

    /// <summary>Ссылка на запись в веб-интерфейсе Speakr («Открыть в Speakr»).</summary>
    public static string? WebUrl(string baseUrl, int recordingId)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        return trimmed.Length == 0 ? null : $"{trimmed}/recordings/{recordingId}";
    }

    private HttpRequestMessage Request(string path, HttpMethod method, string? query = null)
    {
        var uri = new Uri(_baseUri, path + (query is null ? "" : "?" + query));
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return req;
    }

    private async Task<byte[]> SendRaw(HttpRequestMessage request, CancellationToken ct)
    {
        var (data, code) = await Perform(request, ct).ConfigureAwait(false);
        if (code is < 200 or > 299) throw new SpeakrHttpException(code, Encoding.UTF8.GetString(data));
        return data;
    }

    private async Task<T> Send<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var data = await SendRaw(request, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data) ?? throw new SpeakrException("Некорректный ответ от Speakr");
    }

    /// <summary>Запрос без проверки кода — там, где не-2xx осмысленный ответ (404 при проверке удаления).</summary>
    private async Task<(byte[] Data, int Code)> Perform(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
        {
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return (data, (int)response.StatusCode);
        }
    }

    public async Task<SpeakrUploadResponse> Upload(string filePath, CancellationToken ct = default)
    {
        var req = Request("recordings/upload", HttpMethod.Post);
        var boundary = "Boundary-" + Guid.NewGuid().ToString("D").ToUpperInvariant();
        var fileName = Path.GetFileName(filePath);
        var head = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n" +
            $"Content-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\n" +
            "Content-Type: audio/mp4\r\n\r\n");
        var tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        var file = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        var body = new byte[head.Length + file.Length + tail.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length);
        Buffer.BlockCopy(file, 0, body, head.Length, file.Length);
        Buffer.BlockCopy(tail, 0, body, head.Length + file.Length, tail.Length);
        var content = new ByteArrayContent(body);
        // Ручная установка: MediaTypeHeaderValue иначе берёт boundary в кавычки.
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        req.Content = content;
        return await Send<SpeakrUploadResponse>(req, ct).ConfigureAwait(false);
    }

    public Task<SpeakrStatusResponse> Status(int id, CancellationToken ct = default) =>
        Send<SpeakrStatusResponse>(Request($"recordings/{id}/status", HttpMethod.Get), ct);

    public Task<SpeakrRecordingResponse> Recording(int id, CancellationToken ct = default) =>
        Send<SpeakrRecordingResponse>(Request($"recordings/{id}", HttpMethod.Get, "format=minimal"), ct);

    /// <summary>Сырые байты — сохраняются на диск verbatim (transcript.json).</summary>
    public Task<byte[]> TranscriptRaw(int id, CancellationToken ct = default) =>
        SendRaw(Request($"recordings/{id}/transcript", HttpMethod.Get, "format=json"), ct);

    public Task<SpeakrSummaryResponse> Summary(int id, CancellationToken ct = default) =>
        Send<SpeakrSummaryResponse>(Request($"recordings/{id}/summary", HttpMethod.Get), ct);

    /// <summary>
    /// Удаляет запись и проверяет результат отдельным GET (успех — только 404), а не по телу ответа:
    /// форма ответа DELETE не зафиксирована, а редирект на /login при плохом токене выглядит как 2xx.
    /// Если одиночный DELETE не сработал, пробуется батч-эндпоинт (на v0.8.5.1 одиночный отвечает 500).
    /// </summary>
    public async Task Delete(int id, CancellationToken ct = default)
    {
        var (body, deleteCode) = await Perform(Request($"recordings/{id}", HttpMethod.Delete), ct).ConfigureAwait(false);
        if (await IsGone(id, ct).ConfigureAwait(false)) return;

        var batch = Request("recordings/batch", HttpMethod.Delete);
        batch.Content = new StringContent(JsonSerializer.Serialize(new { recording_ids = new[] { id } }), Encoding.UTF8, "application/json");
        var (batchBody, batchCode) = await Perform(batch, ct).ConfigureAwait(false);
        if (await IsGone(id, ct).ConfigureAwait(false)) return;

        throw new SpeakrException(
            $"Speakr не удалил запись и она осталась на сервере: DELETE записи ответил {deleteCode}, батч-удаление — {batchCode}. " +
            $"Ответ сервера: {SpeakrHttpException.Prefix(FirstLine(body) + " | батч: " + FirstLine(batchBody), 200)}");
    }

    private async Task<bool> IsGone(int id, CancellationToken ct)
    {
        var (_, code) = await Perform(Request($"recordings/{id}", HttpMethod.Get, "format=minimal"), ct).ConfigureAwait(false);
        return code == (int)HttpStatusCode.NotFound;
    }

    /// <summary>Тело ошибки Flask — HTML; берём только &lt;title&gt; или первые 200 символов.</summary>
    private static string FirstLine(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var m = Regex.Match(text, "<title>(.*?)</title>", RegexOptions.Singleline);
        if (m.Success) return m.Groups[1].Value;
        var flat = text.Replace("\n", " ");
        return flat.Length <= 200 ? flat : flat[..200];
    }
}
