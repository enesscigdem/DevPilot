using System.Net;
using System.Net.Sockets;

namespace DevPilot.Infrastructure.Executions.Visual;

/// <summary>
/// Serves a built single-page app from a folder on a loopback port, with an index.html fallback for client-side
/// routes. Only files below the root are ever served.
/// </summary>
public sealed class StaticSiteServer : IDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json", [".svg"] = "image/svg+xml", [".png"] = "image/png",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp", [".gif"] = "image/gif",
        [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf",
        [".txt"] = "text/plain; charset=utf-8", [".map"] = "application/json", [".webmanifest"] = "application/manifest+json"
    };

    private readonly string _root;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public StaticSiteServer(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        Port = FreePort();
        BaseUrl = $"http://localhost:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var file = ResolveFile(context.Request.Url?.AbsolutePath ?? "/");
            if (file == null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            context.Response.ContentType = ContentTypes.TryGetValue(Path.GetExtension(file), out var type)
                ? type
                : "application/octet-stream";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch
        {
            try { context.Response.StatusCode = 500; } catch { /* connection already gone */ }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* best effort */ }
        }
    }

    internal string? ResolveFile(string urlPath)
    {
        var relative = Uri.UnescapeDataString(urlPath).TrimStart('/');
        if (relative.Length == 0)
        {
            relative = "index.html";
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Client-side routes have no file extension; anything else that is missing is a real 404.
        if (Path.HasExtension(relative))
        {
            return null;
        }

        var index = Path.Combine(_root, "index.html");
        return File.Exists(index) ? index : null;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _listener.Close(); } catch { /* already closed */ }
        _cts.Dispose();
    }
}
