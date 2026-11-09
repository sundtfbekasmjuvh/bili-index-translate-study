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
}
