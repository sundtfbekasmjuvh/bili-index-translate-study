using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BiliIndexTranslate.App.UiTests;

public sealed record RecordedRequest(string Method, string Path, string Body, bool? Stream, string? Model, string? Authorization);

/// <summary>
/// In-process stand-in for the translation service. POST /v1/chat/completions answers with an SSE stream
/// (three deltas, 400 ms apart, then [DONE]) when the request has "stream": true, otherwise with plain JSON.
/// Listens on "localhost", which Windows allows without administrator rights (127.0.0.1 may not).
/// </summary>
public sealed class MockUpstream : IDisposable
{
    public static readonly string[] Segments = ["第一段，", "第二段，", "第三段。"];
    public static string FullText => string.Concat(Segments);

    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly CancellationTokenSource _cts = new();

    public TimeSpan SegmentDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>When set, every chat request answers with this HTTP status and a small JSON error body.</summary>
    public int? FailWithStatus { get; set; }

    public int Port { get; }
    public string BaseUrl => $"http://localhost:{Port}/v1";

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();
    public IReadOnlyList<RecordedRequest> ChatRequests
        => _requests.Where(r => r.Path.EndsWith("/chat/completions", StringComparison.Ordinal)).ToArray();

    public MockUpstream()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // Port taken between probing and listening: try another one.
            }
        }
        _ = Task.Run(AcceptLoop);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { return; } // listener closed
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            bool? stream = null;
            string? model = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    stream = s.GetBoolean();
                if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    model = m.GetString();
            }
            catch (JsonException) { }

            var path = req.Url?.AbsolutePath ?? string.Empty;
            _requests.Enqueue(new RecordedRequest(req.HttpMethod, path, body, stream, model, req.Headers["Authorization"]));

            var resp = ctx.Response;
            if (req.HttpMethod != "POST" || !path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                resp.StatusCode = 404;
                resp.Close();
                return;
            }

            if (FailWithStatus is { } status)
            {
                await WriteAsync(resp, status, "application/json", "{\"error\":{\"message\":\"mock failure\"}}").ConfigureAwait(false);
                return;
            }

            if (stream == true)
            {
                resp.StatusCode = 200;
                resp.ContentType = "text/event-stream";
                resp.SendChunked = true;
                resp.Headers["Cache-Control"] = "no-cache";
                var output = resp.OutputStream;
                for (var i = 0; i < Segments.Length; i++)
                {
                    if (i > 0) await Task.Delay(SegmentDelay, _cts.Token).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Segments[i] } } } });
                    var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                    await output.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                    await output.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                await output.WriteAsync(done, _cts.Token).ConfigureAwait(false);
                await output.FlushAsync(_cts.Token).ConfigureAwait(false);
                resp.Close();
                return;
            }

            var plain = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = FullText } } } });
            await WriteAsync(resp, 200, "application/json", plain).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch (Exception) { }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse resp, int status, string contentType, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        resp.StatusCode = status;
        resp.ContentType = contentType;
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        resp.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch (Exception) { }
        _cts.Dispose();
    }
}
