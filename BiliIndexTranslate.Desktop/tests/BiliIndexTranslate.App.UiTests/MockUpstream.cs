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
/// A chat request that carries an image (an <c>image_url</c> content part, as the vision service sends it) is answered
/// in the vision dual-output format instead: <c>〖译文〗</c>translation, then <c>〖原文〗</c>recognized text, in three deltas.
/// Listens on "localhost", which Windows allows without administrator rights (127.0.0.1 may not).
/// </summary>
public sealed class MockUpstream : IDisposable
{
    public static readonly string[] Segments = ["第一段，", "第二段，", "第三段。"];
    public static string FullText => string.Concat(Segments);

    /// <summary>The recognized source line of the vision answer (what the parser returns as the original text).</summary>
    public const string VisionSource = "Snipped sample text.";

    /// <summary>The translation of the vision answer (what the card shows).</summary>
    public const string VisionTranslation = "截图识别译文：你好，世界。";

    /// <summary>The vision answer in three stream deltas; joined it is the dual output <c>VisionPromptBuilder.ParseDualOutput</c> splits.</summary>
    public static readonly string[] VisionSegments = ["〖译文〗" + VisionTranslation, "\n〖原文〗", VisionSource];
    public static string VisionFullText => string.Concat(VisionSegments);

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
            var hasImage = false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    stream = s.GetBoolean();
                if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    model = m.GetString();
                hasImage = ContainsImagePart(doc.RootElement);
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

            var segments = hasImage ? VisionSegments : Segments;
            if (stream == true)
            {
                resp.StatusCode = 200;
                resp.ContentType = "text/event-stream";
                resp.SendChunked = true;
                resp.Headers["Cache-Control"] = "no-cache";
                var output = resp.OutputStream;
                for (var i = 0; i < segments.Length; i++)
                {
                    if (i > 0) await Task.Delay(SegmentDelay, _cts.Token).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = segments[i] } } } });
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

            var plain = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = string.Concat(segments) } } } });
            await WriteAsync(resp, 200, "application/json", plain).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try { ctx.Response.Abort(); } catch (Exception) { }
        }
    }

    /// <summary>True when a message of the request has a content part of type <c>image_url</c> (the shape of <c>VisionPromptBuilder.BuildRequestJson</c>).</summary>
    public static bool ContainsImagePart(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return false;
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString() == "image_url")
                    return true;
        }
        return false;
    }

    /// <summary>True when the recorded chat request body has an <c>image_url</c> content part.</summary>
    public static bool HasImagePart(RecordedRequest request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            return ContainsImagePart(doc.RootElement);
        }
        catch (JsonException) { return false; }
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
