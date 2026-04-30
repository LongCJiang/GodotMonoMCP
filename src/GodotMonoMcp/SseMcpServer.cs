using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace GodotMonoMcp;

public sealed class SseMcpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly McpServer _server;
    private readonly ConcurrentDictionary<string, Channel<string>> _sessions = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public SseMcpServer(int port, McpServer server, JsonSerializerOptions jsonOptions)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server = server;
        _jsonOptions = jsonOptions;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Close();
        foreach (var session in _sessions.Values)
        {
            session.Writer.Complete();
        }
        _sessions.Clear();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        var actualPort = GetPort();
        Console.Error.WriteLine($"[GodotMonoMCP] SSE server listening on http://127.0.0.1:{actualPort}/sse");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context, cancellationToken);
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || _disposed)
        {
            // Expected during shutdown
        }
        finally
        {
            try { _listener.Stop(); } catch { }
        }
    }

    private int GetPort()
    {
        var prefix = _listener.Prefixes.FirstOrDefault() ?? "http://127.0.0.1:0/";
        if (Uri.TryCreate(prefix, UriKind.Absolute, out var uri))
        {
            return uri.Port;
        }
        return 0;
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            // CORS preflight
            if (context.Request.HttpMethod == "OPTIONS")
            {
                AddCorsHeaders(context.Response);
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            AddCorsHeaders(context.Response);

            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (path == "/sse" && context.Request.HttpMethod == "GET")
            {
                await HandleSseAsync(context, ct);
            }
            else if (path == "/message" && context.Request.HttpMethod == "POST")
            {
                await HandleMessageAsync(context, ct);
            }
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (!context.Response.OutputStream.CanWrite) return;
                context.Response.StatusCode = 500;
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }, _jsonOptions));
                await context.Response.OutputStream.WriteAsync(bytes, ct);
                context.Response.Close();
            }
            catch { }
        }
    }

    private async Task HandleSseAsync(HttpListenerContext context, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _sessions[sessionId] = channel;

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.Add("Cache-Control", "no-cache");
        context.Response.Headers.Add("Connection", "keep-alive");

        await using var writer = new StreamWriter(context.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };

        // Send endpoint event
        var endpoint = $"/message?session_id={sessionId}";
        await writer.WriteLineAsync($"event: endpoint");
        await writer.WriteLineAsync($"data: {endpoint}");
        await writer.WriteLineAsync();
        await writer.FlushAsync();

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct))
            {
                await writer.WriteLineAsync($"event: message");
                await writer.WriteLineAsync($"data: {message}");
                await writer.WriteLineAsync();
                await writer.FlushAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
        catch (IOException) { }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            channel.Writer.TryComplete();
        }
    }

    private async Task HandleMessageAsync(HttpListenerContext context, CancellationToken ct)
    {
        var sessionId = context.Request.QueryString["session_id"] ?? "";
        if (!_sessions.TryGetValue(sessionId, out var channel))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        var response = await _server.ProcessMessageAsync(body);

        if (response == McpServer.ExitMarker)
        {
            context.Response.StatusCode = 202;
            context.Response.Close();
            // Gracefully close the SSE session
            channel.Writer.TryComplete();
            return;
        }

        if (response != null)
        {
            await channel.Writer.WriteAsync(response, ct);
        }

        context.Response.StatusCode = 202; // Accepted
        context.Response.Close();
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
    }
}
