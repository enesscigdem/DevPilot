using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DevPilot.Tests.AiModels;

/// <summary>
/// Minimal loopback HTTP server that replays canned responses in order and records each request.
/// Lets tests exercise real HTTP clients (including vendor SDKs) without network access.
/// </summary>
internal sealed class FakeHttpServer : IDisposable
{
    internal sealed record Reply(int Status, string Body, string ContentType = "text/event-stream", string? ExtraHeaders = null);

    internal sealed record Recorded(string RequestLine, IReadOnlyDictionary<string, string> Headers, string Body);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Queue<Reply> _replies;
    private readonly Reply _last;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeHttpServer(params Reply[] replies)
    {
        _replies = new Queue<Reply>(replies);
        _last = replies[^1];
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public List<Recorded> Requests { get; } = new();

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync() ?? string.Empty;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync() is { Length: > 0 } line)
            {
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }
            }

            var body = string.Empty;
            if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
            {
                var buffer = new char[length];
                var read = 0;
                while (read < length)
                {
                    var n = await reader.ReadAsync(buffer, read, length - read);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                body = new string(buffer, 0, read);
            }

            Reply reply;
            lock (Requests)
            {
                Requests.Add(new Recorded(requestLine, headers, body));
                reply = _replies.Count > 0 ? _replies.Dequeue() : _last;
            }

            var payload = Encoding.UTF8.GetBytes(reply.Body);
            var head = $"HTTP/1.1 {reply.Status} {(HttpStatusCode)reply.Status}\r\n" +
                       $"Content-Type: {reply.ContentType}\r\n" +
                       $"Content-Length: {payload.Length}\r\n" +
                       (reply.ExtraHeaders ?? string.Empty) +
                       "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }
    }
}
