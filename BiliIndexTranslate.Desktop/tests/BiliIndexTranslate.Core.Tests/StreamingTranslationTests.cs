using System.Text.Json;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

/// <summary>Scriptable transport: streams the chunks it is given, or answers with a plain body.</summary>
internal sealed class ScriptedSseTransport : ISseTranslationTransport, IRawTranslationTransport
{
    public string[] Chunks { get; set; } = [];
    public string SseContentType { get; set; } = "text/event-stream";
    public string JsonBody { get; set; } = """{"choices":[{"message":{"content":"译文"}}]}""";
    /// <summary>Runs after each chunk has been handed over, with the chunk's index.</summary>
    public Action<int>? AfterChunk { get; set; }
    public List<string> Bodies { get; } = new();
    public int SseCalls { get; private set; }
    public int JsonCalls { get; private set; }

    public Task<TransportResponse> PostJsonAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken ct)
    {
        JsonCalls++;
        Bodies.Add(jsonBody);
        return Task.FromResult(new TransportResponse(200, JsonBody, "application/json"));
    }

    public Task<TransportResponse> SendAsync(string method, Uri url, IReadOnlyList<KeyValuePair<string, string>> headers, string? body, TimeSpan timeout, CancellationToken ct)
    {
        JsonCalls++;
        Bodies.Add(body ?? string.Empty);
        return Task.FromResult(new TransportResponse(200, JsonBody, "application/json"));
    }

    public Task<TransportResponse> PostSseAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, Action<string> onDataLine, CancellationToken ct)
    {
        SseCalls++;
        Bodies.Add(jsonBody);
        for (var i = 0; i < Chunks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            onDataLine(Chunks[i]);
            AfterChunk?.Invoke(i);
        }
        return Task.FromResult(new TransportResponse(200, string.Empty, SseContentType));
    }

    public static string Chunk(string text) => JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });
}

public class StreamingTextRulesTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("你好", "你好")]
    [InlineData("  你好  \n", "你好")]
    [InlineData("<", "")]
    [InlineData("<th", "")]
    [InlineData("  <THINK", "")]
    [InlineData("<think>", "")]
    [InlineData("<think>still thinking", "")]
    [InlineData("  <Think>reasoning\nmore", "")]
    [InlineData("<think>r</think>", "")]
    [InlineData("<think>r</think>\n\n你好", "你好")]
    [InlineData("<think>a</think>x<think>b</think> 你好 ", "你好")]
    [InlineData("<b>bold</b>", "<b>bold</b>")]
    [InlineData("1 < 2", "1 < 2")]
    public void VisibleStreamingText_FollowsTheRules(string accumulated, string expected)
        => Assert.Equal(expected, ChatCompletionParser.VisibleStreamingText(accumulated));

    [Fact]
    public void TryFinalize_StripsAndRejectsEmpty()
    {
        Assert.True(ChatCompletionParser.TryFinalize("<think>x</think>  译文 ", out var t, out var err));
        Assert.Equal("译文", t);
        Assert.Null(err);

        Assert.False(ChatCompletionParser.TryFinalize("<think>x", out _, out err));
        Assert.Contains("empty translation", err);
        Assert.False(ChatCompletionParser.TryFinalize("  ", out _, out err));
        Assert.Contains("empty translation", err);
    }
}

public class StreamingTranslatorTests
{
    private static TranslationRequest Req(ApiFormat format = ApiFormat.OpenAi, CustomApiTemplate? custom = null) =>
        new("hello", "zh-Hans", "Simplified Chinese", "auto", "Index-Translate-9B",
            format == ApiFormat.Deeplx ? "https://h.test/translate" : format == ApiFormat.Custom ? CustomFixtures.Endpoint : "https://index-translate.bilibili.com/v1",
            false, "", "bili", "", format, custom);

    private static string[] Chunks(params string[] texts) => texts.Select(ScriptedSseTransport.Chunk).ToArray();

    [Fact]
    public async Task Streams_PartialsInOrder_ThenTheFinalResult()
    {
        var t = new ScriptedSseTransport { Chunks = Chunks("你", "好", "，世界") };
        var partials = new List<string>();

        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add);

        Assert.Equal(new[] { "你", "你好", "你好，世界" }, partials);
        Assert.Equal("你好，世界", result.Translation);
        Assert.Equal(RequestRoute.Direct, result.Route);
        Assert.Equal(1, t.SseCalls);
        Assert.Equal(0, t.JsonCalls);
        using var doc = JsonDocument.Parse(t.Bodies[0]);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Streams_SkipsChunksWithoutNewVisibleText()
    {
        var t = new ScriptedSseTransport
        {
            Chunks = ["""{"choices":[{"delta":{"role":"assistant"}}]}""", ScriptedSseTransport.Chunk("  "), ScriptedSseTransport.Chunk("你好"), ScriptedSseTransport.Chunk("  ")],
        };
        var partials = new List<string>();
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add);
        Assert.Equal(new[] { "你好" }, partials);
        Assert.Equal("你好", result.Translation);
    }

    [Fact]
    public async Task Streams_ThinkBlocksNeverShow()
    {
        var t = new ScriptedSseTransport { Chunks = Chunks("<", "think>", "reasoning ", "here</th", "ink>", "\n\n", "译", "文") };
        var partials = new List<string>();

        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add);

        Assert.Equal(new[] { "译", "译文" }, partials);
        Assert.Equal("译文", result.Translation);
    }

    [Fact]
    public async Task Streams_AHalfTagThatTurnsOutToBeText_IsShownOnceItIsKnown()
    {
        var t = new ScriptedSseTransport { Chunks = Chunks("<", "b>粗体</b>") };
        var partials = new List<string>();
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add);
        Assert.Equal(new[] { "<b>粗体</b>" }, partials);
        Assert.Equal("<b>粗体</b>", result.Translation);
    }

    [Fact]
    public async Task ServerThatIgnoresStream_AnswersWithJson_AndStillSucceeds()
    {
        var sse = new JsonOnSseTransport("""{"choices":[{"message":{"content":"<think>x</think>整段译文"}}]}""");
        var partials = new List<string>();

        var result = await new OpenAiCompatibleTranslator(sse).TranslateAsync(Req(), CancellationToken.None, partials.Add);

        Assert.Equal("整段译文", result.Translation);
        Assert.Empty(partials);
    }

    [Fact]
    public async Task ErrorInsideTheStream_BecomesATranslationException()
    {
        var t = new ScriptedSseTransport { Chunks = ["""{"error":{"message":"model overloaded"}}""", ScriptedSseTransport.Chunk("late")] };
        var partials = new List<string>();

        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add));

        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal("The service reported an error: model overloaded", ex.Error.Message);
        Assert.Empty(partials);
    }

    [Fact]
    public async Task ErrorAfterSomeText_StillFails_NotASuccessWithHalfATranslation()
    {
        var t = new ScriptedSseTransport { Chunks = [ScriptedSseTransport.Chunk("半句"), """{"error":"quota exceeded"}"""] };
        var partials = new List<string>();
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add));
        Assert.Equal("The service reported an error: quota exceeded", ex.Error.Message);
        Assert.Equal(new[] { "半句" }, partials);
    }

    public static TheoryData<string[]> EmptyReplies => new()
    {
        Array.Empty<string>(),
        new[] { "  ", "\n" },
        new[] { "<think>only thoughts" },
        new[] { "<think>done</think>" },
    };

    [Theory]
    [MemberData(nameof(EmptyReplies))]
    public async Task EmptyTranslation_IsEmptyResult(string[] texts)
    {
        var t = new ScriptedSseTransport { Chunks = Chunks(texts) };
        var partials = new List<string>();

        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add));

        Assert.Equal(TranslationErrorKind.EmptyResult, ex.Error.Kind);
        Assert.Empty(partials);
    }

    [Fact]
    public async Task Cancellation_MidStream_IsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var t = new ScriptedSseTransport { Chunks = Chunks("你", "好", "吗") };
        t.AfterChunk = i => { if (i == 0) cts.Cancel(); };
        var partials = new List<string>();

        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), cts.Token, partials.Add));

        Assert.Equal(TranslationErrorKind.Cancelled, ex.Error.Kind);
        Assert.Equal(new[] { "你" }, partials);
    }

    [Fact]
    public async Task Http412_OnAStreamingRequest_KeepsTheSameMapping()
    {
        var t = new StatusSseTransport(412);
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, _ => { }));
        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal(412, ex.Error.StatusCode);
        Assert.Contains("412", ex.Error.Message);
    }

    [Fact]
    public async Task Deeplx_NeverStreams_EvenWithACallback()
    {
        var t = new ScriptedSseTransport { JsonBody = """{"code":200,"data":"你好"}""", Chunks = Chunks("x") };
        var partials = new List<string>();

        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(ApiFormat.Deeplx), CancellationToken.None, partials.Add);

        Assert.Equal("你好", result.Translation);
        Assert.Empty(partials);
        Assert.Equal(0, t.SseCalls);
        Assert.DoesNotContain("stream", t.Bodies[0]);
    }

    [Fact]
    public async Task Custom_NeverStreams_EvenWithACallback()
    {
        var t = new ScriptedSseTransport { JsonBody = """{"translations":[{"text":"你好"}]}""", Chunks = Chunks("x") };
        var partials = new List<string>();

        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(ApiFormat.Custom, CustomFixtures.DeepL), CancellationToken.None, partials.Add);

        Assert.Equal("你好", result.Translation);
        Assert.Empty(partials);
        Assert.Equal(0, t.SseCalls);
        Assert.DoesNotContain("\"stream\"", t.Bodies[0]);
    }

    [Fact]
    public async Task TransportWithoutSse_UsesTheNonStreamingPath()
    {
        var t = new FakeTransport();
        var partials = new List<string>();

        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None, partials.Add);

        Assert.Equal("译文", result.Translation);
        Assert.Empty(partials);
        using var doc = JsonDocument.Parse(t.Calls[0].Body);
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task WithoutACallback_TheRequestIsNotStreamed()
    {
        var t = new ScriptedSseTransport();
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None);
        Assert.Equal("译文", result.Translation);
        Assert.Equal(0, t.SseCalls);
        Assert.Equal(1, t.JsonCalls);
        using var doc = JsonDocument.Parse(t.Bodies[0]);
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    private sealed class JsonOnSseTransport(string body) : ISseTranslationTransport
    {
        public Task<TransportResponse> PostJsonAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new TransportResponse(200, body, "application/json"));

        public Task<TransportResponse> PostSseAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, Action<string> onDataLine, CancellationToken ct)
            => Task.FromResult(new TransportResponse(200, body, "application/json"));
    }

    private sealed class StatusSseTransport(int status) : ISseTranslationTransport
    {
        public Task<TransportResponse> PostJsonAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new TransportResponse(status, "<html>blocked</html>", "text/html"));

        public Task<TransportResponse> PostSseAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, Action<string> onDataLine, CancellationToken ct)
            => Task.FromResult(new TransportResponse(status, "<html>blocked</html>", "text/html"));
    }
}

public class StreamingCoordinatorTests
{
    private static TranslationRequest Req(string text) =>
        new(text, "zh-Hans", "Simplified Chinese", "auto", "Index-Translate-9B", "https://index-translate.bilibili.com/v1", false, "", "bili");

    private static string[] Chunks(params string[] texts) => texts.Select(ScriptedSseTransport.Chunk).ToArray();

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Partials_AreDeliveredWhileValid_ThenTheFinalResult_AndOnlyTheFinalIsCached()
    {
        var t = new ScriptedSseTransport { Chunks = Chunks("你", "好") };
        var cache = new TranslationCache();
        var events = new List<string>();
        var done = new TaskCompletionSource();
        using var coord = new TranslationCoordinator(new OpenAiCompatibleTranslator(t), cache, a => a());

        coord.Start(
            1, Req("x"),
            (id, o) => { lock (events) events.Add("final:" + Assert.IsType<TranslationOutcome.Success>(o).Translation); done.TrySetResult(); },
            (id, text) => { lock (events) events.Add($"partial#{id}:{text}"); });
        await done.Task;

        lock (events) Assert.Equal(new[] { "partial#1:你", "partial#1:你好", "final:你好" }, events);
        var cached = coord.TryGetCached(Req("x"));
        Assert.Equal("你好", cached!.Translation);
    }

    [Fact]
    public async Task Partials_AreNotCached_WhenTheStreamFails()
    {
        var t = new ScriptedSseTransport { Chunks = [ScriptedSseTransport.Chunk("半句"), """{"error":"boom"}"""] };
        var cache = new TranslationCache();
        var done = new TaskCompletionSource<TranslationOutcome>();
        using var coord = new TranslationCoordinator(new OpenAiCompatibleTranslator(t), cache, a => a());

        coord.Start(1, Req("x"), (_, o) => done.SetResult(o), (_, _) => { });

        Assert.IsType<TranslationOutcome.Failure>(await done.Task);
        Assert.Null(coord.TryGetCached(Req("x")));
    }

    [Fact]
    public async Task QueuedPartial_IsDroppedWhenTheRequestIsCancelled()
    {
        var t = new ScriptedSseTransport { Chunks = Chunks("你") };
        var queue = new List<Action>();
        var partials = new List<string>();
        using var coord = new TranslationCoordinator(new OpenAiCompatibleTranslator(t), new TranslationCache(), a => a(), postPartial: a => { lock (queue) queue.Add(a); });

        coord.Start(7, Req("y"), (_, _) => { }, (_, text) => partials.Add(text));
        await WaitUntil(() => { lock (queue) return queue.Count == 1; });
        coord.Cancel(7);
        lock (queue) queue[0]();

        Assert.Empty(partials);
    }

    [Fact]
    public async Task QueuedPartial_IsDroppedWhenANewRequestReplacesIt()
    {
        var t = new ScriptedSseTransport { Chunks = Chunks("旧") };
        var queue = new List<Action>();
        var partials = new List<(long, string)>();
        var release = new TaskCompletionSource();
        using var coord = new TranslationCoordinator(new OpenAiCompatibleTranslator(t), new TranslationCache(), a => a(), postPartial: a => { lock (queue) queue.Add(a); });

        // Same id restarted (retry): the first run is cancelled and superseded.
        coord.Start(3, Req("a"), (_, _) => { }, (id, text) => partials.Add((id, "old:" + text)));
        await WaitUntil(() => { lock (queue) return queue.Count >= 1; });
        t.Chunks = Chunks("新");
        coord.Start(3, Req("b"), (_, _) => release.TrySetResult(), (id, text) => partials.Add((id, "new:" + text)));
        await release.Task;
        lock (queue) foreach (var a in queue.ToArray()) a();

        Assert.DoesNotContain(partials, p => p.Item2.StartsWith("old:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoPartialIsDelivered_AfterTheFinalResult_EvenIfOneWasStillQueued()
    {
        var t = new ScriptedSseTransport { Chunks = Chunks("你", "好") };
        var partialQueue = new List<Action>();
        var finalQueue = new List<Action>();
        var events = new List<string>();
        using var coord = new TranslationCoordinator(
            new OpenAiCompatibleTranslator(t), new TranslationCache(),
            a => { lock (finalQueue) finalQueue.Add(a); },
            postPartial: a => { lock (partialQueue) partialQueue.Add(a); });

        coord.Start(1, Req("x"), (_, o) => events.Add("final"), (_, text) => events.Add("partial:" + text));
        await WaitUntil(() => { lock (finalQueue) return finalQueue.Count == 1; });

        lock (finalQueue) finalQueue[0]();
        lock (partialQueue) foreach (var a in partialQueue.ToArray()) a();

        Assert.Equal(new[] { "final" }, events);
    }

    [Fact]
    public async Task CancelledRequest_DeliversNeitherPartialsNorAResult()
    {
        var gate = new TaskCompletionSource();
        var t = new ScriptedSseTransport { Chunks = Chunks("你") };
        t.AfterChunk = _ => gate.Task.Wait(2000);
        var events = new List<string>();
        using var coord = new TranslationCoordinator(new OpenAiCompatibleTranslator(t), new TranslationCache(), a => a());

        // The fake transport completes synchronously, so start off the test thread: it blocks inside AfterChunk.
        _ = Task.Run(() => coord.Start(9, Req("z"), (_, _) => { lock (events) events.Add("final"); }, (_, text) => { lock (events) events.Add("partial:" + text); }));
        await WaitUntil(() => t.SseCalls == 1);
        await WaitUntil(() => { lock (events) return events.Count == 1; });
        coord.Cancel(9);
        gate.SetResult();
        await Task.Delay(100);

        lock (events) Assert.Equal(new[] { "partial:你" }, events); // the partial came before the cancel; nothing after
        Assert.Equal(0, coord.ActiveCount);
    }

    [Fact]
    public async Task WithoutAnOnPartialCallback_NothingStreams()
    {
        var t = new ScriptedSseTransport();
        var done = new TaskCompletionSource();
        using var coord = new TranslationCoordinator(new OpenAiCompatibleTranslator(t), new TranslationCache(), a => a());
        coord.Start(1, Req("x"), (_, _) => done.SetResult());
        await done.Task;
        Assert.Equal(0, t.SseCalls);
        Assert.Equal(1, t.JsonCalls);
    }
}

public class StreamingSessionTests
{
    private static SelectionSnapshot Snap(string text)
    {
        var r = PxRect.FromSize(100, 100, 200, 20);
        return new SelectionSnapshot(text, new AnchorRect(r, r, 1, 20), 100, 7, "app", EditableKind.NotEditable, SelectionOrigin.UiAutomation, 5);
    }

    private static (long RequestId, long SelectionId) SelectText(ReadingSession s, string text)
    {
        var timer = Assert.Single(s.Handle(new SessionEvent.TriggerReceived(TriggerKind.MouseUp)).OfType<SessionEffect.StartStabilizationTimer>()).TimerId;
        var probe = Assert.Single(s.Handle(new SessionEvent.StabilizationElapsed(timer)).OfType<SessionEffect.RequestProbe>()).ProbeId;
        var fx = s.Handle(new SessionEvent.ProbeCompleted(probe, new ProbeOutcome.Selected(Snap(text))));
        var start = Assert.Single(fx.OfType<SessionEffect.StartTranslation>());
        return (start.RequestId, start.SelectionId);
    }

    private static bool Ignored(IReadOnlyList<SessionEffect> fx)
        => fx.All(e => e is SessionEffect.Report) && fx.OfType<SessionEffect.Report>().Any(r => r.Message.Contains("ignored", StringComparison.Ordinal));

    [Fact]
    public void ProgressWhileLoading_ShowsThePartial_AndStaysLoading()
    {
        var s = new ReadingSession();
        var (req, sel) = SelectText(s, "hello");

        var fx = s.Handle(new SessionEvent.TranslationProgress(req, "你"));
        var show = Assert.Single(fx.OfType<SessionEffect.ShowPartialTranslation>());
        Assert.Equal(sel, show.SelectionId);
        Assert.Equal("你", show.PartialText);
        Assert.Equal("hello", show.Snapshot.Text);
        Assert.Equal(SessionState.Loading, s.State);

        fx = s.Handle(new SessionEvent.TranslationProgress(req, "你好"));
        Assert.Equal("你好", Assert.Single(fx.OfType<SessionEffect.ShowPartialTranslation>()).PartialText);
        Assert.Equal(SessionState.Loading, s.State);

        var done = s.Handle(new SessionEvent.TranslationCompleted(req, new TranslationOutcome.Success("你好", RequestRoute.Direct, false, 10)));
        Assert.Single(done.OfType<SessionEffect.ShowTranslation>());
        Assert.Equal(SessionState.ShowingTranslation, s.State);
    }

    [Fact]
    public void ProgressWithTheWrongRequestId_IsIgnored()
    {
        var s = new ReadingSession();
        var (req, _) = SelectText(s, "hello");

        var fx = s.Handle(new SessionEvent.TranslationProgress(req + 100, "x"));

        Assert.True(Ignored(fx));
        Assert.Equal(SessionState.Loading, s.State);
    }

    [Fact]
    public void ProgressAfterCompletion_IsIgnored()
    {
        var s = new ReadingSession();
        var (req, _) = SelectText(s, "hello");
        s.Handle(new SessionEvent.TranslationCompleted(req, new TranslationOutcome.Success("你好", RequestRoute.Direct, false, 10)));

        var fx = s.Handle(new SessionEvent.TranslationProgress(req, "late"));

        Assert.True(Ignored(fx));
        Assert.Equal(SessionState.ShowingTranslation, s.State);
    }

    [Fact]
    public void ProgressAfterTheSelectionIsCleared_IsIgnored()
    {
        var s = new ReadingSession();
        var (req, sel) = SelectText(s, "hello");
        var cleared = s.Handle(new SessionEvent.GeometryRefreshed(sel, new GeometryUpdate.Cleared()));
        Assert.Contains(cleared, e => e is SessionEffect.CancelTranslation);

        var fx = s.Handle(new SessionEvent.TranslationProgress(req, "late"));

        Assert.True(Ignored(fx));
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public void ProgressAfterEscape_AndAfterANewSelection_IsIgnored()
    {
        var s = new ReadingSession();
        var (req, _) = SelectText(s, "first");
        var (req2, _) = SelectText(s, "second");
        Assert.True(Ignored(s.Handle(new SessionEvent.TranslationProgress(req, "stale"))));
        Assert.Single(s.Handle(new SessionEvent.TranslationProgress(req2, "fresh")).OfType<SessionEffect.ShowPartialTranslation>());

        s.Handle(new SessionEvent.EscapePressed());
        Assert.True(Ignored(s.Handle(new SessionEvent.TranslationProgress(req2, "after esc"))));
    }
}
