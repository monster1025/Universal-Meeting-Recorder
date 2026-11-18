using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalMeetingRecorder.Core.LLM;

public enum LlmErrorKind
{
    NotConfigured,
    InvalidResponse,
    Http,
    EmptyContent,
}

public sealed class LlmException : Exception
{
    public LlmErrorKind Kind { get; }
    public int StatusCode { get; }

    public LlmException(LlmErrorKind kind, string message, int statusCode = 0) : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public static LlmException Http(int code, string body) =>
        new(LlmErrorKind.Http, $"LLM вернула ошибку {code}: {(body.Length <= 200 ? body : body[..200])}", code);

    public static LlmException Empty() => new(LlmErrorKind.EmptyContent, "LLM вернула пустой ответ");
    public static LlmException Invalid() => new(LlmErrorKind.InvalidResponse, "Некорректный ответ от LLM");
}

/// <summary>Источник ответов LLM (подменяется в тестах экстрактора).</summary>
public interface ILlmProvider
{
    /// <summary>Один запрос — один ответ в JSON-режиме; содержимое без обёрток.</summary>
    Task<string> CompleteJson(string prompt, CancellationToken ct = default);
}

/// <summary>
/// Клиент OpenAI-совместимого API: POST {base}/v1/chat/completions. Адрес принимается и с /v1
/// на конце, и без. Ответ не стримится, поэтому таймауты большие: медленные модели отвечают минутами.
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmProvider
{
    private readonly Uri _base;
    private readonly string _apiKey;
    private readonly HttpClient _http;

    public string Model { get; }

    private OpenAiCompatibleClient(Uri baseUri, string apiKey, string model, HttpClient http)
    {
        _base = baseUri;
        _apiKey = apiKey;
        Model = model;
        _http = http;
    }

    public static OpenAiCompatibleClient? Create(string baseUrl, string apiKey, string model, HttpMessageHandler? handler = null)
    {
        var trimmed = (baseUrl ?? "").Trim();
        while (trimmed.EndsWith('/')) trimmed = trimmed[..^1];
        if (trimmed.EndsWith("/v1", StringComparison.Ordinal)) trimmed = trimmed[..^3];
        model = (model ?? "").Trim();
        if (trimmed.Length == 0 || string.IsNullOrEmpty(apiKey) || model.Length == 0) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        var http = handler is null
            ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
            : new HttpClient(handler);
        // Таймаут — до конца ответа (на Mac: 900 с до первого байта, 1200 с на ресурс).
        http.Timeout = TimeSpan.FromSeconds(1200);
        return new OpenAiCompatibleClient(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), apiKey, model, http);
    }

    private async Task<byte[]> Perform(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        if (code is < 200 or > 299) throw LlmException.Http(code, Encoding.UTF8.GetString(data));
        return data;
    }

    public async Task<string> CompleteJson(string prompt, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "v1/chat/completions"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var data = await Perform(req, ct).ConfigureAwait(false);
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(data);
            // reasoning_content намеренно не читаем — только content.
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw LlmException.Invalid();
        }
        if (string.IsNullOrWhiteSpace(content)) throw LlmException.Empty();
        return content;
    }

    /// <summary>Проверка подключения: GET /v1/models — быстро и без токенов. Возвращает id моделей.</summary>
    public async Task<List<string>> AvailableModels(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "v1/models"));
        var data = await Perform(req, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw LlmException.Invalid();
        }
    }
}
