using System.Net;
using System.Net.Sockets;
using System.Text;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class HttpTranslationTransportTests
{
    [Fact]
    public async Task PostSse_StopsAtDoneTerminator()
    {
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
            "data: [DONE]\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n";

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeOnceAsync(listener, sse);

            using var transport = new HttpTranslationTransport();
            var lines = new List<string>();
            var response = await transport.PostSseAsync(
                new Uri($"http://127.0.0.1:{port}/v1/chat/completions"),
                "bili",
                "{}",
                TimeSpan.FromSeconds(10),
                lines.Add,
                CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
            Assert.Equal(["{\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}"], lines);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Deeplx_OverTheWire_SendsTheKeyOnlyInTheQuery_AndReadsData()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeJsonOnceAsync(listener, "{\"code\":200,\"data\":\"你好，世界。\"}");

            using var transport = new HttpTranslationTransport();
            var request = new TranslationRequest(
                "Hello, world.", "zh-Hans", "Simplified Chinese", "auto", "Index-Translate-9B",
                $"http://127.0.0.1:{port}/translate?token={{apiKey}}", false, "", "tok/1", "", ApiFormat.Deeplx);
            var result = await new OpenAiCompatibleTranslator(transport, TimeSpan.FromSeconds(10)).TranslateAsync(request, CancellationToken.None);

            Assert.Equal("你好，世界。", result.Translation);
            var wire = await server;
            Assert.StartsWith("POST /translate?token=tok%2F1 HTTP/1.1", wire);
            Assert.DoesNotContain("Authorization:", wire, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"text\":\"Hello, world.\"", wire);
            Assert.Contains("\"source_lang\":\"auto\"", wire);
            Assert.Contains("\"target_lang\":\"ZH\"", wire);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Send_OverTheWire_UsesTheGivenMethodHeadersAndBody_WithoutABearerHeader()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new HttpTranslationTransport();

            var post = ServeJsonOnceAsync(listener, "{\"ok\":true}");
            var response = await transport.SendAsync(
                "POST",
                new Uri($"http://127.0.0.1:{port}/custom?x=1"),
                [new("X-Api-Key", "k 1/2"), new("Content-Type", "application/x-www-form-urlencoded")],
                "q=hello%20world",
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{\"ok\":true}", response.Body);
            var wire = await post;
            Assert.StartsWith("POST /custom?x=1 HTTP/1.1", wire);
            Assert.Contains("X-Api-Key: k 1/2", wire);
            Assert.Contains("Content-Type: application/x-www-form-urlencoded\r\n", wire);
            Assert.DoesNotContain("Authorization:", wire, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("q=hello%20world", wire);

            var get = ServeJsonOnceAsync(listener, "{\"ok\":true}");
            await transport.SendAsync("GET", new Uri($"http://127.0.0.1:{port}/t?q=hi"), [], null, TimeSpan.FromSeconds(10), CancellationToken.None);
            var getWire = await get;
            Assert.StartsWith("GET /t?q=hi HTTP/1.1", getWire);
            Assert.DoesNotContain("Content-Type:", getWire, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization:", getWire, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, string sseBody)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[64 * 1024];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) return;
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var payload = Encoding.UTF8.GetBytes(sseBody);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
            $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    /// <summary>Accepts one request, reads it completely (headers and body), answers with JSON and returns what was received.</summary>
    private static async Task<string> ServeJsonOnceAsync(TcpListener listener, string jsonBody)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[64 * 1024];
        var received = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            received.Append(Encoding.ASCII.GetString(buffer, 0, read));

            var text = received.ToString();
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd < 0) continue;
            var lengthLine = text[..headEnd].Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var bodyLength = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
            if (text.Length - (headEnd + 4) >= bodyLength) break;
        }

        var payload = Encoding.UTF8.GetBytes(jsonBody);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        return received.ToString();
    }
}
