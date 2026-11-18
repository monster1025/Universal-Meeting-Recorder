using System.Net;
using System.Net.Sockets;
using System.Text;
using UniversalMeetingRecorder.Core.Util;

namespace UniversalMeetingRecorder.Core.Tasks;

/// <summary>
/// Read-only HTTP-сервер снимка задач на loopback (порт и токен — как на Mac). На Mac им пользуется
/// песочный WidgetKit-виджет; на Windows — внешние потребители (скрипты, Rainmeter и т.п.).
/// Голый TcpListener вместо HttpListener: тому для префикса нужен urlacl (права администратора).
/// Один запрос на соединение, Connection: close.
/// </summary>
public sealed class TaskFeedServer : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly Func<byte[]> _snapshot;
    private readonly int _port;
    private CancellationTokenSource? _cts;

    public TaskFeedServer(Func<byte[]> snapshot, int port = TaskFeed.Port)
    {
        _snapshot = snapshot;
        _port = port;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoop(ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, _port);
                listener.Start();
                Log.Info($"TaskFeedServer: слушаю 127.0.0.1:{_port}");
                using var reg = ct.Register(() => listener.Stop());
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => Handle(client), ct);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Порт занят чужим процессом или TIME_WAIT — пробуем периодически.
                Log.Error($"TaskFeedServer: порт {_port} недоступен, повтор через 30 с", ex);
            }
            finally
            {
                try { listener?.Stop(); } catch { }
            }
            try
            {
                await Task.Delay(RetryInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var buffer = new List<byte>();
                var chunk = new byte[16 * 1024];
                int headerEnd = -1;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        read = 0;
                    }
                    if (read > 0) buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                    headerEnd = IndexOfHeaderEnd(buffer);
                    if (headerEnd >= 0) break;
                    if (buffer.Count > MaxHeaderBytes || read == 0)
                    {
                        await Send(stream, "400 Bad Request", "Bad request"u8.ToArray()).ConfigureAwait(false);
                        return;
                    }
                }
                var header = Encoding.UTF8.GetString(buffer.GetRange(0, headerEnd).ToArray());
                var (status, contentType, body, extra) = Respond(header, _snapshot);
                await Send(stream, status, body, contentType, extra).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("TaskFeedServer: ошибка обработки запроса", ex);
            }
        }
    }

    /// <summary>Разбор запроса и выбор ответа — чистая функция, проверяется тестами.</summary>
    internal static (string Status, string ContentType, byte[] Body, Dictionary<string, string>? Extra) Respond(string header, Func<byte[]> snapshot)
    {
        const string text = "text/plain; charset=utf-8";
        var lines = header.Split("\r\n");
        var request = lines.Length > 0 ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        if (request.Length < 2) return ("400 Bad Request", text, "Bad request"u8.ToArray(), null);
        if (request[0] != "GET")
        {
            return ("405 Method Not Allowed", text, "Only GET is supported"u8.ToArray(), new Dictionary<string, string> { ["Allow"] = "GET" });
        }
        var path = request[1].Split('?', 2)[0];
        if (path != TaskFeed.TasksPath) return ("404 Not Found", text, "Not found"u8.ToArray(), null);

        var auth = lines.Skip(1)
            .FirstOrDefault(l => l.StartsWith("authorization:", StringComparison.OrdinalIgnoreCase))
            ?["authorization:".Length..].Trim();
        if (auth != $"Bearer {TaskFeed.Token}") return ("401 Unauthorized", text, "Unauthorized"u8.ToArray(), null);

        try
        {
            return ("200 OK", "application/json; charset=utf-8", snapshot(), null);
        }
        catch
        {
            return ("500 Internal Server Error", text, "Encoding failed"u8.ToArray(), null);
        }
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (var i = 0; i + 3 < buffer.Count; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        }
        return -1;
    }

    private static async Task Send(NetworkStream stream, string status, byte[] body, string contentType = "text/plain; charset=utf-8", Dictionary<string, string>? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (extra is not null)
        {
            foreach (var (k, v) in extra) sb.Append($"{k}: {v}\r\n");
        }
        sb.Append("\r\n");
        var head = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
