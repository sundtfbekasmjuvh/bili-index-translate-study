using System.Net;
using System.Net.Sockets;
using System.Text;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class StreamDeltaTests
{
    [Fact]
    public void ReadStreamDelta_ReturnsTheTextOfAChunk_AndNothingForChunksWithoutText()
    {
        Assert.Equal("你好", ChatCompletionParser.ReadStreamDelta("""{"choices":[{"delta":{"content":"你好"}}]}""", out var error));
        Assert.Null(error);

        foreach (var chunk in new[]
                 {
                     """{"choices":[{"delta":{"role":"assistant"}}]}""",
                     """{"choices":[{"delta":{"content":null}}],"error":null}""",
                     """{"choices":[]}""",
                     """{"usage":{"total_tokens":12}}""",
                     "[1,2]",
                     "{not json",
                 })
        {
            Assert.Equal(string.Empty, ChatCompletionParser.ReadStreamDelta(chunk, out error));
            Assert.Null(error);
        }
    }

    [Fact]
    public void ReadStreamDelta_ReportsAnErrorSentInsideTheStream()
    {
        Assert.Equal(string.Empty, ChatCompletionParser.ReadStreamDelta("""{"error":{"message":"model overloaded","type":"server_error"}}""", out var error));
        Assert.Equal("model overloaded", error);

        ChatCompletionParser.ReadStreamDelta("""{"error":"quota exceeded"}""", out error);
        Assert.Equal("quota exceeded", error);

        ChatCompletionParser.ReadStreamDelta("""{"error":{"code":500}}""", out error);
        Assert.Equal("The service reported an error.", error);
    }

    [Fact]
    public async Task VisionService_ReportsAnErrorSentInsideTheStream_InsteadOfAnEmptyResult()
    {
        var transport = new FakeVisionSseTransport
        {
            Chunks =
            [
                """{"choices":[{"delta":{"content":"〖译文〗主权"}}]}""",
                """{"error":{"message":"model overloaded"}}""",
                """{"choices":[{"delta":{"content":"调度器"}}]}""",
            ],
        };
        var req = new ImageTranslationRequest(
            "data:image/png;base64,AAAA", "zh-Hans", "Simplified Chinese", "Index-Translate-35B-A3B",
            "https://index-translate.bilibili.com/v1", false, "", "bili");
        var deltas = new List<ImageTranslationDelta>();

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new VisionTranslationService(transport).TranslateImageAsync(req, CancellationToken.None, deltas.Add));

        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal("The service reported an error: model overloaded", ex.Error.Message);
        Assert.Equal(RequestRoute.Direct, ex.Error.Route);
        Assert.True(ex.Error.IsRetryable);
        Assert.Single(deltas); // nothing after the error is shown
    }

    [Fact]
    public async Task StudyService_ReportsAnErrorSentInsideTheStream_InsteadOfAnEmptyCard()
    {
        var transport = new FakeSseTransport
        {
            SsePayloads =
            [
                """{"choices":[{"delta":{"content":"{\"mode\":\"word\","}}]}""",
                """{"error":"quota exceeded"}""",
            ],
        };
        var req = new TranslationRequest(
            "ephemeral", "zh-Hans", "Simplified Chinese", "auto", "Index-Translate-9B",
            "https://index-translate.bilibili.com/v1", true, "https://relay.example/", "bili");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new StudyCardService(transport).GenerateAsync(req, CancellationToken.None, _ => { }));

        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal("The service reported an error: quota exceeded", ex.Error.Message);
        Assert.Equal(RequestRoute.Relay, ex.Error.Route);
    }
}

public class SseTimeoutTests
{
    private const string Chunk = "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n";

    [Fact]
    public async Task PostSse_TheTimeoutIsAnIdleLimit_AReplyThatKeepsFlowingIsNotCutOff()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Five chunks 400 ms apart: 2 s in total, twice the timeout, but never idle for longer than it.
            var server = ServeSlowlyAsync(listener, chunks: 5, gap: TimeSpan.FromMilliseconds(400), finish: true);

            using var transport = new HttpTranslationTransport();
            var lines = new List<string>();
            var response = await transport.PostSseAsync(
                new Uri($"http://127.0.0.1:{port}/v1/chat/completions"), "bili", "{}",
                TimeSpan.FromSeconds(1), lines.Add, CancellationToken.None);

            Assert.Equal(200, response.StatusCode);
            Assert.Equal(5, lines.Count);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task PostSse_AReplyThatStallsTimesOut_AndTheLinesBeforeItWereDelivered()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeSlowlyAsync(listener, chunks: 2, gap: TimeSpan.FromMilliseconds(100), finish: false);

            using var transport = new HttpTranslationTransport();
            var lines = new List<string>();
            await Assert.ThrowsAsync<TimeoutException>(() => transport.PostSseAsync(
                new Uri($"http://127.0.0.1:{port}/v1/chat/completions"), "bili", "{}",
                TimeSpan.FromMilliseconds(700), lines.Add, CancellationToken.None));

            Assert.Equal(2, lines.Count);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Answers one request with an event stream written chunk by chunk. With <paramref name="finish"/> the
    /// stream is ended properly; without it the connection is held open, silent, until the client gives up.
    /// </summary>
    private static async Task ServeSlowlyAsync(TcpListener listener, int chunks, TimeSpan gap, bool finish)
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

        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"));
        await stream.FlushAsync();
        try
        {
            for (var i = 0; i < chunks; i++)
            {
                await Task.Delay(gap);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Chunk));
                await stream.FlushAsync();
            }
            if (finish)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
                await stream.FlushAsync();
                return;
            }
            // Say nothing more; the read returns when the client closes the connection.
            while (await stream.ReadAsync(buffer) > 0) { }
        }
        catch (IOException) { }
    }
}

public class LatestValueRelayTests
{
    /// <summary>Stands in for a dispatcher: callbacks wait in a queue until the test runs them.</summary>
    private sealed class ManualQueue
    {
        private readonly Queue<Action> _queue = new();
        public int Pending => _queue.Count;
        public void Post(Action action) => _queue.Enqueue(action);
        public void RunAll()
        {
            while (_queue.Count > 0) _queue.Dequeue()();
        }
    }

    [Fact]
    public void ValuesOfferedWhileOneIsWaiting_ReplaceIt_AndOnlyOneHandOverIsQueued()
    {
        var queue = new ManualQueue();
        var seen = new List<string>();
        var relay = new LatestValueRelay<string>(queue.Post, seen.Add);

        relay.Offer("a");
        relay.Offer("ab");
        relay.Offer("abc");
        Assert.Equal(1, queue.Pending);
        Assert.Empty(seen);

        queue.RunAll();
        Assert.Equal(["abc"], seen);

        // Once delivered, the next value is handed over again.
        relay.Offer("abcd");
        Assert.Equal(1, queue.Pending);
        queue.RunAll();
        Assert.Equal(["abc", "abcd"], seen);
    }

    [Fact]
    public void Clear_DropsTheWaitingValue_SoALateHandOverDeliversNothing()
    {
        var queue = new ManualQueue();
        var seen = new List<string>();
        var relay = new LatestValueRelay<string>(queue.Post, seen.Add);

        relay.Offer("partial");
        relay.Clear();
        queue.RunAll();
        Assert.Empty(seen);

        relay.Offer("next");
        queue.RunAll();
        Assert.Equal(["next"], seen);
    }

    [Fact]
    public void AValueOfferedFromInsideTheConsumer_IsDeliveredByANewHandOver()
    {
        var queue = new ManualQueue();
        var seen = new List<string>();
        LatestValueRelay<string>? relay = null;
        relay = new LatestValueRelay<string>(queue.Post, value =>
        {
            seen.Add(value);
            if (value == "first") relay!.Offer("second");
        });

        relay.Offer("first");
        queue.RunAll();
        Assert.Equal(["first", "second"], seen);
    }

    [Fact]
    public async Task OffersFromManyThreads_AlwaysEndWithTheNewestValueDelivered()
    {
        var gate = new object();
        var posted = new Queue<Action>();
        var seen = new List<int[]>();
        var relay = new LatestValueRelay<int[]>(
            action => { lock (gate) posted.Enqueue(action); },
            value => seen.Add(value));

        var last = 0;
        var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++) relay.Offer([Interlocked.Increment(ref last)]);
        })).ToArray();

        // One consumer thread, as with a dispatcher.
        var consumer = Task.Run(async () =>
        {
            while (true)
            {
                Action? next = null;
                lock (gate)
                {
                    if (posted.Count > 0) next = posted.Dequeue();
                }
                if (next is not null) next();
                else if (producers.All(p => p.IsCompleted)) break;
                else await Task.Yield();
            }
            lock (gate)
            {
                while (posted.Count > 0) posted.Dequeue()();
            }
        });

        await Task.WhenAll(producers);
        await consumer;

        Assert.NotEmpty(seen);
        Assert.True(seen.Count <= 20000);
        // Whatever was skipped, nothing is left waiting: one more offer is delivered on its own.
        relay.Offer([-1]);
        lock (gate)
        {
            Assert.Single(posted);
            posted.Dequeue()();
        }
        Assert.Equal(-1, seen[^1][0]);
    }
}
