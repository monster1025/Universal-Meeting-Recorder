using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UniversalMeetingRecorder.Core.Settings;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Transcription;

/// <summary>
/// Встроенный браузер для входа (на Windows — WebView2). Открывает страницу Keycloak и ждёт, пока
/// та перенаправит на <paramref name="redirectPrefix"/>; возвращает этот итоговый адрес (с code и state)
/// или null, если пользователь закрыл окно / вышло время.
/// </summary>
public interface ISsoBrowser
{
    Task<Uri?> WaitForRedirect(Uri authUrl, Uri redirectPrefix, CancellationToken ct);
}

/// <summary>
/// Вход в НейроМенделеев через SSO (Keycloak) без каких-либо настроек Keycloak: это тот же authorization
/// code flow, что у веб-интерфейса, с его же redirect_uri (<c>{сервер}/login</c>, уже зарегистрированным).
///   1. GET /api/auth/keycloak-auth-url?redirect_uri=…  → ссылка на Keycloak + state;
///   2. встроенный браузер проходит вход (с Windows-аутентификацией — автоматически) и доходит до redirect_uri;
///   3. POST /api/auth/keycloak-callback {code, redirect_uri} → access_token (обмен делает сам бэкенд);
///   4. токен продлевается через POST /api/auth/refresh, пока он не истёк.
/// Токен хранится в хранилище секретов. Если продлить нельзя — вход повторяется (с Kerberos — без участия
/// пользователя). Неудачная автоматическая попытка не повторяется <see cref="LoginCooldown"/>, чтобы
/// фоновая загрузка не открывала окно входа каждые 10 секунд; явное действие пользователя кулдаун обходит.
/// </summary>
public sealed class MendeleevSession
{
    public const string SecretService = "com.monster.UniversalMeetingRecorder.Mendeleev";
    public const string SecretAccount = "accessToken";
    public static readonly TimeSpan LoginCooldown = TimeSpan.FromMinutes(5);
    /// <summary>За сколько до истечения токен пытаются продлить.</summary>
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(30);

    private readonly ISecretStore _secrets;
    private readonly Func<string> _baseUrl;
    private readonly ISsoBrowser? _browser;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastFailedLoginUtc = DateTime.MinValue;
    private string? _token;

    public event Action? Changed;

    public MendeleevSession(ISecretStore secrets, Func<string> baseUrl, ISsoBrowser? browser, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
    {
        _secrets = secrets;
        _baseUrl = baseUrl;
        _browser = browser;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _token = secrets.Get(SecretService, SecretAccount);
    }

    public Uri? BaseUri => Uri.TryCreate(_baseUrl().Trim().TrimEnd('/') + "/", UriKind.Absolute, out var u) && u.Host.Length > 0 ? u : null;

    public bool HasToken => !string.IsNullOrEmpty(_token);

    /// <summary>Кто вошёл — из claim'ов токена; null, если токена нет или он нечитаем.</summary>
    public string? UserName => JwtClaims.Read(_token)?.DisplayName;

    public DateTime? ExpiresUtc => JwtClaims.Read(_token)?.ExpiresUtc;

    public bool IsValid => ExpiresUtc is { } exp && exp - _utcNow() > TimeSpan.FromSeconds(30);

    /// <summary>
    /// Действующий токен. Продлевает почти истёкший, при необходимости входит заново.
    /// <paramref name="interactive"/> = true — явное действие пользователя: окно входа можно показать и после неудачи.
    /// </summary>
    public async Task<string> GetToken(bool interactive, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && JwtClaims.Read(_token) is { } claims)
            {
                var left = claims.ExpiresUtc - _utcNow();
                if (left > RefreshBefore) return _token;
                if (left > TimeSpan.FromSeconds(30))
                {
                    if (await TryRefresh(ct).ConfigureAwait(false)) return _token!;
                    return _token; // пока ещё жив — используем, вход повторим после истечения
                }
            }
            return await Login(interactive, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Токен отвергнут сервером (401): продлить нельзя — только новый вход.</summary>
    public async Task<string> Reauthenticate(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Clear(raiseChanged: false);
            return await Login(interactive: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignIn(CancellationToken ct) => await GetTokenForcingLogin(ct).ConfigureAwait(false);

    private async Task<string> GetTokenForcingLogin(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Login(interactive: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SignOut()
    {
        Clear(raiseChanged: true);
    }

    private void Clear(bool raiseChanged)
    {
        _token = null;
        _secrets.Delete(SecretService, SecretAccount);
        if (raiseChanged) Changed?.Invoke();
    }

    private async Task<bool> TryRefresh(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri!, "api/auth/refresh"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var fresh = ReadAccessToken(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (fresh is null) return false;
            Store(fresh);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Error("Mendeleev: продление токена не удалось", ex);
            return false;
        }
    }

    private async Task<string> Login(bool interactive, CancellationToken ct)
    {
        if (BaseUri is not { } baseUri) throw new RemoteAuthException("НейроМенделеев не настроен — укажите адрес сервера в настройках");
        if (_browser is null) throw new RemoteAuthException("Вход через SSO недоступен в этом окружении");
        if (!interactive && _utcNow() - _lastFailedLoginUtc < LoginCooldown)
        {
            throw new RemoteAuthException("НейроМенделеев: требуется вход — откройте настройки и нажмите «Войти»");
        }
        try
        {
            var redirect = new Uri(baseUri, "login");
            var authUrl = await GetAuthUrl(baseUri, redirect, ct).ConfigureAwait(false);
            var result = await _browser.WaitForRedirect(authUrl.Url, redirect, ct).ConfigureAwait(false)
                         ?? throw new RemoteAuthException("Вход отменён");
            var query = ParseQuery(result.Query);
            if (query.TryGetValue("error", out var error))
            {
                throw new RemoteAuthException($"Keycloak отклонил вход: {query.GetValueOrDefault("error_description") ?? error}");
            }
            if (!query.TryGetValue("code", out var code) || code.Length == 0) throw new RemoteAuthException("Keycloak не вернул код входа");
            if (authUrl.State is { Length: > 0 } state && query.GetValueOrDefault("state") is { } got && got != state)
            {
                throw new RemoteAuthException("Вход отклонён: не совпал state");
            }
            var token = await ExchangeCode(baseUri, redirect, code, ct).ConfigureAwait(false);
            Store(token);
            return token;
        }
        catch (RemoteAuthException)
        {
            _lastFailedLoginUtc = _utcNow();
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _lastFailedLoginUtc = _utcNow();
            throw new RemoteAuthException($"НейроМенделеев недоступен: {ex.Message}", ex);
        }
    }

    private sealed record AuthUrl(Uri Url, string? State);

    private async Task<AuthUrl> GetAuthUrl(Uri baseUri, Uri redirect, CancellationToken ct)
    {
        var uri = new Uri(baseUri, "api/auth/keycloak-auth-url?redirect_uri=" + Uri.EscapeDataString(redirect.AbsoluteUri));
        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RemoteAuthException($"НейроМенделеев не отдал адрес входа ({(int)response.StatusCode}): вход через Keycloak не настроен?");
        using var doc = JsonDocument.Parse(body);
        var url = doc.RootElement.GetProperty("url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) throw new RemoteAuthException("НейроМенделеев вернул некорректный адрес входа");
        var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
        return new AuthUrl(parsed, state);
    }

    private async Task<string> ExchangeCode(Uri baseUri, Uri redirect, string code, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/auth/keycloak-callback"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code, redirect_uri = redirect.AbsoluteUri }), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = MendeleevClient.ErrorDetail(body);
            throw new RemoteAuthException($"НейроМенделеев не принял вход ({(int)response.StatusCode}){(detail is null ? "" : ": " + detail)}");
        }
        return ReadAccessToken(body) ?? throw new RemoteAuthException("НейроМенделеев не вернул токен");
    }

    private void Store(string token)
    {
        _token = token;
        _secrets.Set(SecretService, SecretAccount, token);
        Changed?.Invoke();
    }

    private static string? ReadAccessToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((i < 0 ? pair : pair[..i]).Replace('+', ' '));
            result[key] = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
        }
        return result;
    }
}

/// <summary>Нужные поля JWT. Подпись не проверяется — токен проверяет сервер; здесь только срок и имя для показа.</summary>
public sealed record JwtClaims(DateTime ExpiresUtc, string? DisplayName)
{
    public static JwtClaims? Read(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;
            if (!root.TryGetProperty("exp", out var exp)) return null;
            string? name = null;
            foreach (var key in new[] { "display_name", "name", "preferred_username", "email" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                {
                    name = s;
                    break;
                }
            }
            return new JwtClaims(IsoDate.FromUnixSeconds(exp.GetDouble()), name);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
