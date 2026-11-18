using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace UniversalMeetingRecorder.Core.Transcription;

public sealed class MendeleevException : Exception
{
    public int StatusCode { get; }

    public MendeleevException(int code, string message) : base(message) => StatusCode = code;
}

/// <summary>
/// REST API НейроМенделеева. Эндпоинты взяты из кода его веб-интерфейса (публичной схемы нет), формы ответов
/// проверены на живом сервере:
///   POST /api/recordings/upload (multipart, поле file) → {meeting_id}
///   GET  /api/recordings/meetings/{id} → {processing_status: queued|…|done|failed, processing_step, error_message, title}
///   GET  /api/recordings/meetings/{id}/transcript → {segments:[{start,end,text,speaker,words}]}
///   GET  /api/recordings/meetings/{id}/protocol → {content: markdown}
///   DELETE /api/recordings/meetings/{id}
/// Авторизация — Bearer-токен из <see cref="MendeleevSession"/>; на 401 вход повторяется один раз.
/// </summary>
public sealed class MendeleevClient
{
    /// <summary>Максимальный размер записи на сервере.</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    private readonly MendeleevSession _session;
    private readonly HttpClient _http;

    public MendeleevClient(MendeleevSession session, HttpMessageHandler? handler = null)
    {
        _session = session;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Таймаут — на каждый запрос отдельно (загрузка большого файла идёт минутами).
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    private Uri Url(string path) => new(_session.BaseUri ?? throw new RemoteAuthException("НейроМенделеев не настроен — укажите адрес сервера в настройках"), path);

    /// <summary>Запрос с токеном; 401 → новый вход и один повтор. <paramref name="build"/> вызывается заново для повтора.</summary>
    private async Task<(HttpStatusCode Code, byte[] Body)> Send(Func<HttpRequestMessage> build, TimeSpan timeout, CancellationToken ct)
    {
        var token = await _session.GetToken(interactive: false, ct).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var req = build();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                using var response = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    token = await _session.Reauthenticate(ct).ConfigureAwait(false);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new RemoteAuthException("НейроМенделеев отклонил токен — войдите заново в настройках");
                return (response.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException($"НейроМенделеев не ответил за {timeout.TotalSeconds:0} с");
            }
        }
    }

    private async Task<JsonDocument> GetJson(string path, CancellationToken ct)
    {
        var (code, body) = await Send(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if ((int)code is < 200 or > 299) throw Error(code, body);
        return JsonDocument.Parse(body);
    }

    public async Task<string> Upload(string filePath, CancellationToken ct)
    {
        var length = new FileInfo(filePath).Length;
        if (length > MaxUploadBytes) throw new MendeleevException(413, "Файл слишком большой: максимальный размер записи — 2 ГБ.");
        var (code, body) = await Send(() =>
        {
            // Файл читается потоком (запись может быть больше гигабайта), а не грузится в память.
            var file = new StreamContent(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true));
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
            var form = new MultipartFormDataContent { { file, "file", Path.GetFileName(filePath) } };
            return new HttpRequestMessage(HttpMethod.Post, Url("api/recordings/upload")) { Content = form };
        }, TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
        if ((int)code is < 200 or > 299) throw Error(code, body);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("meeting_id", out var id) && id.GetString() is { Length: > 0 } s) return s;
        throw new MendeleevException((int)code, "НейроМенделеев принял файл, но не вернул ID встречи");
    }

    public async Task<RemoteStatus> GetStatus(string id, CancellationToken ct)
    {
        using var doc = await GetJson($"api/recordings/meetings/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var status = Str(root, "processing_status") ?? "";
        var step = Str(root, "processing_step");
        var state = status switch
        {
            "done" => RemoteState.Completed,
            "failed" => RemoteState.Failed,
            _ => RemoteState.Pending,
        };
        return new RemoteStatus(state, string.IsNullOrEmpty(step) || state != RemoteState.Pending ? status : step, Str(root, "error_message"));
    }

    public async Task<RemoteResult> GetResult(string id, CancellationToken ct)
    {
        var e = Uri.EscapeDataString(id);
        string? title;
        using (var meeting = await GetJson($"api/recordings/meetings/{e}", ct).ConfigureAwait(false)) title = Str(meeting.RootElement, "title")?.Trim();

        byte[] transcript;
        using (var doc = await GetJson($"api/recordings/meetings/{e}/transcript", ct).ConfigureAwait(false)) transcript = ToSpeakrTranscript(doc.RootElement);

        // Протокол может отсутствовать (его генерация отключена или не удалась) — это не причина терять транскрипт.
        var notes = "";
        var (code, body) = await Send(() => new HttpRequestMessage(HttpMethod.Get, Url($"api/recordings/meetings/{e}/protocol")), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if ((int)code is >= 200 and <= 299)
        {
            using var doc = JsonDocument.Parse(body);
            notes = Str(doc.RootElement, "content") ?? "";
        }
        else if (code != HttpStatusCode.NotFound)
        {
            throw Error(code, body);
        }
        return new RemoteResult(transcript, notes, string.IsNullOrEmpty(title) ? null : title);
    }

    public async Task Delete(string id, CancellationToken ct)
    {
        var path = $"api/recordings/meetings/{Uri.EscapeDataString(id)}";
        var (code, body) = await Send(() => new HttpRequestMessage(HttpMethod.Delete, Url(path)), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if ((int)code is < 200 or > 299 && code != HttpStatusCode.NotFound) throw Error(code, body);
        // Успех — запись действительно пропала, а не «сервер ответил 2xx».
        var (check, checkBody) = await Send(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (check != HttpStatusCode.NotFound)
        {
            throw new MendeleevException((int)code, $"НейроМенделеев не удалил запись: DELETE ответил {(int)code}, проверка — {(int)check}");
        }
    }

    /// <summary>
    /// Сегменты НейроМенделеева {start,end,text,speaker} → формат Speakr {speaker,sentence,start_time,end_time}:
    /// в нём transcript.json лежит у всех встреч, его читают macOS-версия и извлечение задач.
    /// </summary>
    public static byte[] ToSpeakrTranscript(JsonElement transcript)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("segments");
            if (transcript.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segments.EnumerateArray())
                {
                    w.WriteStartObject();
                    if (Str(s, "speaker") is { } speaker) w.WriteString("speaker", speaker); else w.WriteNull("speaker");
                    w.WriteString("sentence", Str(s, "text") ?? "");
                    if (Num(s, "start") is { } start) w.WriteNumber("start_time", start); else w.WriteNull("start_time");
                    if (Num(s, "end") is { } end) w.WriteNumber("end_time", end); else w.WriteNull("end_time");
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    internal static string? ErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            foreach (var key in new[] { "message", "detail", "error" })
            {
                if (root.TryGetProperty(key, out var v))
                {
                    if (v.ValueKind == JsonValueKind.String) return v.GetString();
                    if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return v.GetRawText();
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static MendeleevException Error(HttpStatusCode code, byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var detail = ErrorDetail(text) ?? text;
        if (detail.Length > 200) detail = detail[..200];
        return new MendeleevException((int)code, $"НейроМенделеев вернул ошибку {(int)code}: {detail}");
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>НейроМенделеев за общим интерфейсом сервисов транскрипции.</summary>
public sealed class MendeleevBackend : ITranscriptionBackend
{
    private readonly MendeleevClient _client;

    public MendeleevBackend(MendeleevClient client) => _client = client;

    public string Kind => TranscriptionBackends.Mendeleev;
    public Task<string> Upload(string filePath, CancellationToken ct) => _client.Upload(filePath, ct);
    public Task<RemoteStatus> GetStatus(string remoteId, CancellationToken ct) => _client.GetStatus(remoteId, ct);
    public Task<RemoteResult> GetResult(string remoteId, CancellationToken ct) => _client.GetResult(remoteId, ct);
    public Task Delete(string remoteId, CancellationToken ct) => _client.Delete(remoteId, ct);
}
