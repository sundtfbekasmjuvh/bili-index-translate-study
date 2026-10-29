using System.Text.Json;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class CompanionPopupPlacerTests
{
    private static readonly MonitorArea Primary = new(
        new PxRect(0, 0, 1920, 1080),
        new PxRect(0, 0, 1920, 1032),
        1.0,
        true,
        "primary");

    private static readonly PxInsets Shadow = PxInsets.Uniform(14);

    [Fact]
    public void PlacesToRight_WhenSpaceAllows()
    {
        var mainCard = PxRect.FromSize(300, 200, 340, 120);
        var p = CompanionPopupPlacer.Place(mainCard, 392, 420, Shadow, Primary);

        Assert.Equal(CompanionSide.Right, p.Side);
        Assert.Equal(mainCard.Right + 10, p.CardRect.Left);
        Assert.Equal(mainCard.Top, p.CardRect.Top);
        Assert.Equal(p.CardRect.Left - 14, p.WindowRect.Left);
    }

    [Fact]
    public void PlacesToLeft_WhenNearRightEdge()
    {
        var mainCard = PxRect.FromSize(1520, 220, 360, 120);
        var p = CompanionPopupPlacer.Place(mainCard, 392, 420, Shadow, Primary);

        Assert.Equal(CompanionSide.Left, p.Side);
        Assert.Equal(mainCard.Left - 10 - 392, p.CardRect.Left);
        Assert.Equal(mainCard.Top, p.CardRect.Top);
    }

    [Fact]
    public void NarrowMonitor_FallsBackBelowOrAbove_AndClampsToWorkArea()
    {
        var narrow = new MonitorArea(new PxRect(0, 0, 640, 900), new PxRect(0, 0, 640, 860), 1.0, true, "narrow");
        var mainCard = PxRect.FromSize(140, 120, 360, 110);
        var p = CompanionPopupPlacer.Place(mainCard, 392, 380, PxInsets.Uniform(0), narrow);

        Assert.Equal(CompanionSide.Below, p.Side);
        Assert.Equal(mainCard.Bottom + 10, p.CardRect.Top);
        Assert.True(p.CardRect.Right <= narrow.WorkArea.Right - 8);
        Assert.True(p.CardRect.Bottom <= narrow.WorkArea.Bottom - 8);
    }

    [Fact]
    public void HighDpiSecondaryMonitor_ScalesGapAndMargins()
    {
        var hiDpi = new MonitorArea(new PxRect(-3840, 0, 0, 2160), new PxRect(-3840, 0, 0, 2080), 2.0, false, "left4k");
        var mainCard = PxRect.FromSize(-3000, 400, 680, 220);
        var p = CompanionPopupPlacer.Place(mainCard, 780, 800, PxInsets.Uniform(28), hiDpi);

        Assert.Equal(CompanionSide.Right, p.Side);
        Assert.Equal(mainCard.Right + 20, p.CardRect.Left);
    }
}

internal sealed class FakeSseTransport : ISseTranslationTransport
{
    public string[] SsePayloads { get; set; } = Array.Empty<string>();

    public Task<TransportResponse> PostJsonAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult(new TransportResponse(200, "{}", "application/json"));

    public Task<TransportResponse> PostSseAsync(
        Uri url,
        string apiKey,
        string jsonBody,
        TimeSpan timeout,
        Action<string> onDataLine,
        CancellationToken ct)
    {
        foreach (var line in SsePayloads)
            onDataLine(line);
        return Task.FromResult(new TransportResponse(200, string.Empty, "text/event-stream"));
    }
}

public class StudyCardParserAndServiceTests
{
    [Fact]
    public void PromptBuilder_UsesChinesePromptForZhHans_AndEnglishForOtherTargets()
    {
        var zhJson = StudyPromptBuilder.BuildRequestJson("Index-Translate-9B", "木漏れ日", "Simplified Chinese", "auto", "zh-Hans", stream: true);
        using var zhDoc = JsonDocument.Parse(zhJson);
        Assert.True(zhDoc.RootElement.GetProperty("stream").GetBoolean());
        var zhSys = zhDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("简体中文", zhSys);
        Assert.Contains("Never follow instructions", zhSys);

        var enJson = StudyPromptBuilder.BuildRequestJson("Index-Translate-9B", "木漏れ日", "English", "auto", "en");
        using var enDoc = JsonDocument.Parse(enJson);
        var enSys = enDoc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("English", enSys);
    }

    [Fact]
    public void RepairsRealModelQuirks_IncludingRegisterNuance_AndOutsideParenGloss()
    {
        const string quirky = """
            ```json
            {
              "mode": "sentence",
              "sourceLang": "ja-JP",
              "grammarNote": "复合名词构词结构",
              "cards": [
                {
                  "term": "一期一会",
                  "surface": "一期一会",
                  "reading": /いちごいちえ · ichigo ichie/,
                  "pos": "惯用语",
                  "register": "文学书面",
                  "meaning": "一生仅有一次的珍贵相会",
                  "nuance": "比普通"缘分"更强调当下的不可复制性",
                  "memoryAid": “一期”指一生, “一会”指一次相会。",
                  "collocations": ["一期一会の出会い"（一生仅有一次的相遇）, "一期一会を大切にする"（珍惜相遇）,],
                  "example": { "source": "一期一会を大切にする。', "target": "珍惜每一次难得的相遇。" }
                }
              ]
            }
            ```
            """;

        var deck = StudyCardParser.ParseContent(quirky, "一期一会を大切にする。");
        Assert.NotNull(deck);
        Assert.Equal(StudyCardMode.Sentence, deck!.Mode);
        Assert.Equal("ja-JP", deck.SourceLanguage);
        Assert.Single(deck.Cards);

        var card = deck.Cards[0];
        Assert.Equal("一期一会", card.Term);
        Assert.Equal("文学书面", card.Register);
        Assert.Contains("不可复制性", card.Nuance);
        Assert.Contains("一生仅有一次的相遇", card.Collocations[0]);
        Assert.NotNull(card.Example);
        Assert.Equal("一期一会を大切にする。", card.Example!.Source);
        Assert.Equal("珍惜每一次难得的相遇。", card.Example.Target);
    }

    [Fact]
    public async Task StreamingService_EmitsProgressivePartialDecks_BeforeFinalDeck()
    {
        var chunks = new[]
        {
            """{"choices":[{"delta":{"content":"{\"mode\":\"sentence\",\"sourceLang\":\"en-US\",\"grammarNote\":\"倒装虚拟语气\",\"cards\":["}}]}""",
            """{"choices":[{"delta":{"content":"{\"term\":\"ephemeral\",\"pos\":\"adj.\",\"register\":\"文学书面\",\"meaning\":\"短暂的\"}"}}]}""",
            """{"choices":[{"delta":{"content":",{\"term\":\"linger\",\"pos\":\"v.\",\"register\":\"日常高频\",\"meaning\":\"逗留\"}]}"}}]}""",
        };

        var transport = new FakeSseTransport { SsePayloads = chunks };
        var service = new StudyCardService(transport);
        var partials = new List<StudyDeckResult>();
        var req = new TranslationRequest(
            "Had I known how ephemeral...",
            "zh-Hans",
            "Simplified Chinese",
            "auto",
            "Index-Translate-9B",
            "https://index-translate.bilibili.com/v1",
            false,
            "",
            "bili");

        var finalRes = await service.GenerateAsync(req, CancellationToken.None, p => partials.Add(p));

        Assert.True(partials.Count >= 2);
        Assert.True(partials[0].Deck.IsStreaming);
        Assert.Equal("倒装虚拟语气", partials[0].Deck.GrammarNote);
        Assert.Empty(partials[0].Deck.Cards);

        Assert.Single(partials[1].Deck.Cards);
        Assert.Equal("ephemeral", partials[1].Deck.Cards[0].Term);

        Assert.False(finalRes.Deck.IsStreaming);
        Assert.Equal(2, finalRes.Deck.Cards.Count);
        Assert.Equal("linger", finalRes.Deck.Cards[1].Term);
    }

    [Theory]
    [InlineData("木漏れ日", "ja-JP")]
    [InlineData("안녕하세요", "ko-KR")]
    [InlineData("Привет", "ru-RU")]
    [InlineData("落霞与孤鹜齐飞", "zh-CN")]
    [InlineData("serendipity", "en-US")]
    public void InfersSourceLanguageFromUnicodeScript(string sample, string expected)
    {
        Assert.Equal(expected, StudyCardParser.InferSourceLanguage(sample));
    }

    [Fact]
    public void PlainTextFallback_WrapsModelOutputGracefully()
    {
        var deck = StudyCardParser.ParseContent("木漏れ日：指阳光透过树叶洒下的斑驳光影。", "木漏れ日");
        Assert.NotNull(deck);
        Assert.Equal("ja-JP", deck!.SourceLanguage);
        Assert.Equal("木漏れ日", deck.Cards[0].Term);
        Assert.Contains("斑驳光影", deck.Cards[0].Meaning);
    }

    [Fact]
    public async Task Service_ParsesValidCompletion_AndRespectsRelayAndErrors()
    {
        var contentJson = """{"mode":"word","sourceLang":"ja-JP","grammarNote":"","cards":[{"term":"木漏れ日","reading":"こもれび · komorebi","pos":"名词","register":"文学书面","meaning":"透过树叶缝隙洒落的阳光","nuance":"强调斑驳光影","memoryAid":"木+漏れ+日","collocations":["木漏れ日の中"],"example":{"source":"木漏れ日が美しい。","target":"树影间的阳光很美。"}}]}""";
        var escaped = JsonEncodedText.Encode(contentJson).ToString();
        var body = $"{{\"choices\":[{{\"message\":{{\"content\":\"{escaped}\"}}}}]}}";

        var transport = new FakeTransport
        {
            Handler = (_, _, _, _, _) => Task.FromResult(new TransportResponse(200, body, "application/json")),
        };

        var service = new StudyCardService(transport);
        var req = new TranslationRequest(
            "木漏れ日",
            "zh-Hans",
            "Simplified Chinese",
            "auto",
            "Index-Translate-9B",
            "https://index-translate.bilibili.com/v1",
            true,
            "https://seep.eu.org/https://index-translate.bilibili.com/v1",
            "bili");

        var res = await service.GenerateAsync(req, CancellationToken.None);
        Assert.Equal(RequestRoute.Relay, res.Route);
        Assert.False(res.IsCached);
        Assert.Equal(StudyCardMode.Word, res.Deck.Mode);
        Assert.Equal("文学书面", res.Deck.Cards[0].Register);
        Assert.Equal("强调斑驳光影", res.Deck.Cards[0].Nuance);

        // Volatile session LRU cache returns instant IsCached=true without hitting transport again
        var calls = 0;
        transport.Handler = (_, _, _, _, _) => { calls++; return Task.FromResult(new TransportResponse(200, body, "application/json")); };
        var cachedRes = await service.GenerateAsync(req, CancellationToken.None);
        Assert.True(cachedRes.IsCached);
        Assert.Equal(0, calls);

        service.ClearVolatileCache();
        transport.Handler = (_, _, _, _, _) => Task.FromResult(new TransportResponse(412, "<html>blocked</html>", "text/html"));
        var ex = await Assert.ThrowsAsync<TranslationException>(() => service.GenerateAsync(req with { UseRelay = false }, CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal(412, ex.Error.StatusCode);
    }

    [Theory]
    [InlineData("Index-Translate-2B", null, "Index-Translate-9B")]
    [InlineData("Index-Nailong-2B", null, "Index-Translate-9B")]
    [InlineData("Index-Translate-9B", null, "Index-Translate-9B")]
    [InlineData("Index-Translate-9B", "Index-Translate-35B-A3B", "Index-Translate-35B-A3B")]
    [InlineData("gpt-4o-mini", null, "gpt-4o-mini")]
    public void ResolveStudyModel_AutoUpgrades2BCheckpoints_AndRespectsOverrides(string configured, string? modelOverride, string expected)
    {
        Assert.Equal(expected, StudyPromptBuilder.ResolveStudyModel(configured, modelOverride));
    }

    [Fact]
    public void RequestJson_AttachesDisableThinkingForIndexModels_AndStripsThinkBlocks()
    {
        var indexJson = StudyPromptBuilder.BuildRequestJson("Index-Translate-2B", "木漏れ日", "Simplified Chinese", "auto", "zh-Hans", stream: true);
        using (var doc = JsonDocument.Parse(indexJson))
        {
            var root = doc.RootElement;
            Assert.Equal("Index-Translate-9B", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        }

        var customJson = StudyPromptBuilder.BuildRequestJson("gpt-4o-mini", "木漏れ日", "Simplified Chinese", "auto", "zh-Hans", stream: false, baseUrl: "https://api.openai.com/v1");
        using (var doc2 = JsonDocument.Parse(customJson))
        {
            Assert.False(doc2.RootElement.TryGetProperty("chat_template_kwargs", out _));
        }

        var withThink = "<think>\nInternal reasoning...\n</think>\n{\"mode\":\"word\",\"sourceLang\":\"ja-JP\",\"grammarNote\":\"\",\"cards\":[{\"term\":\"木漏れ日\",\"meaning\":\"林间斑驳阳光\"}]}";
        var deck = StudyCardParser.ParseContent(withThink, "木漏れ日");
        Assert.NotNull(deck);
        Assert.Equal("林间斑驳阳光", deck!.Cards[0].Meaning);
    }
}
