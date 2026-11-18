using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UniversalMeetingRecorder.Core.Transcription;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.App.Platform;

/// <summary>
/// Встроенный браузер для SSO (Keycloak) на WebView2 — это Edge, поэтому работают Windows-авторизация (Kerberos/NTLM),
/// сохранённые сессии и SSO-куки. Окно создаётся ЗА ЭКРАНОМ: если вход проходит сам (Windows-авторизация),
/// пользователь ничего не увидит; если за <see cref="RevealAfter"/> не дошло до redirect_uri — значит, нужна форма входа,
/// и окно выезжает в центр экрана.
///
/// Перехват: переход на redirect_uri веб-интерфейса сервера (<c>{сервер}/login?code=…</c>) отменяется в
/// NavigationStarting, не загружаясь, — приложение забирает code и само обменивает его на токен. Поэтому
/// ничего в Keycloak настраивать не нужно: redirect_uri уже зарегистрирован для веб-интерфейса.
/// </summary>
internal sealed class WebView2SsoBrowser : ISsoBrowser
{
    private static readonly TimeSpan RevealAfter = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private readonly Dispatcher _ui;

    public WebView2SsoBrowser(Dispatcher ui) => _ui = ui;

    public Task<Uri?> WaitForRedirect(Uri authUrl, Uri redirectPrefix, CancellationToken ct) =>
        _ui.InvokeAsync(() => Run(authUrl, redirectPrefix, ct)).Task.Unwrap();

    private static async Task<Uri?> Run(Uri authUrl, Uri redirectPrefix, CancellationToken ct)
    {
        var result = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var web = new WebView2();
        var window = new Window
        {
            Title = "Вход в НейроМенделеев",
            Width = 520,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = web,
            Icon = UI.WindowManager.AppIcon,
        };
        window.Closed += (_, _) => result.TrySetResult(null);

        var reveal = new DispatcherTimer { Interval = RevealAfter };
        reveal.Tick += (_, _) =>
        {
            reveal.Stop();
            if (result.Task.IsCompleted) return;
            window.ShowInTaskbar = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var area = SystemParameters.WorkArea;
            window.Left = area.Left + (area.Width - window.Width) / 2;
            window.Top = area.Top + (area.Height - window.Height) / 2;
            window.Activate();
        };

        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var registration = linked.Token.Register(() => result.TrySetResult(null));

        try
        {
            window.Show();
            reveal.Start();

            // Постоянный профиль: SSO-куки переживают перезапуск — следующий вход проходит молча.
            // Windows-авторизацию (Negotiate) Chromium включает только для разрешённых серверов — разрешаем сервер входа.
            var userData = Path.Combine(AppPaths.SettingsDirectory, "webview");
            var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--auth-server-allowlist=\"{authUrl.Host}\"" };
            var env = await CoreWebView2Environment.CreateAsync(null, userData, options);
            await web.EnsureCoreWebView2Async(env);

            var target = redirectPrefix.GetLeftPart(UriPartial.Path);
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
                if (!string.Equals(uri.GetLeftPart(UriPartial.Path), target, StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                result.TrySetResult(uri);
            };
            web.CoreWebView2.Navigate(authUrl.AbsoluteUri);
            return await result.Task;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new RemoteAuthException("Не найден WebView2 Runtime — установите «Microsoft Edge WebView2 Runtime» для входа через SSO", ex);
        }
        finally
        {
            reveal.Stop();
            window.Close();
            web.Dispose();
        }
    }
}
