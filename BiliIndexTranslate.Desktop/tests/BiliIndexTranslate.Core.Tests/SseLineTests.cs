using System.Net;
using System.Net.Sockets;
using System.Text;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class SseLineTests
{
    [Fact]
    public async Task PostSse_ReadsDataLines_LikeBefore()
    {
        var body =
            ": keep-alive comment\r\n" +
            "data:{\"n\":1}\r\n" +               // no space after the colon
            "data: {\"n\":2}\r\n" +              // one space
            "\r\n" +                             // blank line
            "event: message\r\n" +               // other field
            "data:\r\n" +                        // empty data
            "data:    \r\n" +                    // blank data
            "  data:   {\"n\":3}  \r\n" +        // surrounding whitespace
            "data:[DONE]\r\n" +                  // terminator without a space
            "data: {\"n\":4}\r\n";               // after the terminator: ignored

        var lines = await ReadAsync(body);
        Assert.Equal(["{\"n\":1}", "{\"n\":2}", "{\"n\":3}"], lines);
    }

    [Fact]
    public async Task PostSse_LfOnlyLines_AndSpacedDoneTerminator_StopTheRead()
    {
        var lines = await ReadAsync("data: a\ndata: b\n\ndata: [DONE]\ndata: c\n");
        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public async Task PostSse_AStreamWithoutDone_ReadsToTheEnd_AndDoneInsideTextIsKept()
    {
        var lines = await ReadAsync("data: x [DONE]\r\ndata: y\r\n");
        Assert.Equal(["x [DONE]", "y"], lines);
    }

    private static async Task<List<string>> ReadAsync(string sseBody)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeAsync(listener, sseBody);
            using var transport = new HttpTranslationTransport();
            var lines = new List<string>();
            var response = await transport.PostSseAsync(
                new Uri($"http://127.0.0.1:{port}/v1/chat/completions"), "bili", "{}",
                TimeSpan.FromSeconds(10), lines.Add, CancellationToken.None);
            Assert.Equal(200, response.StatusCode);
            await server;
            return lines;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeAsync(TcpListener listener, string sseBody)
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
}
