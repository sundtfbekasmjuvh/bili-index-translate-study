using System.Text.Json;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class DeeplxEndpointTests
{
    [Fact]
    public void Placeholder_IsReplacedWithEscapedKey_AndUrlIsUsedAsEntered()
    {
        Assert.True(EndpointResolver.TryResolve(ApiFormat.Deeplx, "  https://h.test/translate?token={apiKey}  ", false, null, " a b/c ", out var ep, out var err), err);
        Assert.Equal("https://h.test/translate?token=a%20b%2Fc", ep!.ChatCompletionsUrl.AbsoluteUri);
        Assert.True(ep.KeyInUrl);
        Assert.Equal(RequestRoute.Direct, ep.Route);
        Assert.Equal("h.test", ep.DisplayHost);
        Assert.Equal("https://h.test/translate?token={apiKey}", ep.NormalizedBase);
    }

    [Theory]
    [InlineData("https://h.test/{{apiKey}}/translate/", "https://h.test/k/translate")]
    [InlineData("https://h.test/translate?token={APIKEY}", "https://h.test/translate?token=k")]
    public void Placeholder_AcceptsDoubleBraces_AnyCase_AndDropsTrailingSlash(string input, string expected)
    {
        Assert.True(EndpointResolver.TryResolve(ApiFormat.Deeplx, input, false, null, "k", out var ep, out var err), err);
        Assert.Equal(expected, ep!.ChatCompletionsUrl.AbsoluteUri);
        Assert.True(ep.KeyInUrl);
    }

    [Fact]
    public void WithoutPlaceholder_UrlIsUnchanged_AndKeyStaysOutOfIt()
    {
        Assert.True(EndpointResolver.TryResolve(ApiFormat.Deeplx, "http://localhost:1188/translate", false, null, "secret", out var ep, out _));
        Assert.Equal("http://localhost:1188/translate", ep!.ChatCompletionsUrl.AbsoluteUri);
        Assert.False(ep.KeyInUrl);
    }

    [Fact]
    public void Relay_IsTheCompleteEndpoint_OnlyWhenEnabled()
    {
        const string relay = "https://relay.test/https://h.test/translate?token={apiKey}";
        Assert.True(EndpointResolver.TryResolve(ApiFormat.Deeplx, "https://h.test/translate", false, relay, "k", out var direct, out _));
        Assert.Equal("https://h.test/translate", direct!.ChatCompletionsUrl.AbsoluteUri);
        Assert.Equal(RequestRoute.Direct, direct.Route);

        Assert.True(EndpointResolver.TryResolve(ApiFormat.Deeplx, "https://h.test/translate", true, relay, "k", out var relayed, out _));
        Assert.Equal("https://relay.test/https://h.test/translate?token=k", relayed!.ChatCompletionsUrl.AbsoluteUri);
        Assert.Equal(RequestRoute.Relay, relayed.Route);

        Assert.False(EndpointResolver.TryResolve(ApiFormat.Deeplx, "https://h.test/translate", true, " ", "k", out _, out var err));
        Assert.Contains("Relay", err);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://x/translate")]
    [InlineData("not a url")]
    public void InvalidInputs_AreRejectedWithMessage(string input)
    {
        Assert.False(EndpointResolver.TryResolve(ApiFormat.Deeplx, input, false, null, "k", out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void OpenAiFormat_KeepsItsOwnRules_AndRejectsThePlaceholder()
    {
        Assert.True(EndpointResolver.TryResolve(ApiFormat.OpenAi, "https://a.test/v1/?x=1", false, null, "k", out var ep, out _));
        Assert.Equal("https://a.test/v1/chat/completions", ep!.ChatCompletionsUrl.AbsoluteUri);
        Assert.False(ep.KeyInUrl);

        // Under the OpenAI rules the query would be dropped and the token silently lost.
        Assert.False(EndpointResolver.TryResolve(ApiFormat.OpenAi, "https://h.test/translate?token={apiKey}", false, null, "k", out _, out var err));
        Assert.Contains("DeepLX", err);
        Assert.False(EndpointResolver.TryResolve(ApiFormat.OpenAi, "https://a.test/v1", true, "https://relay.test/translate?token={APIKEY}", "k", out _, out var relayErr));
        Assert.Contains("DeepLX", relayErr);

        // A relay that is not enabled is not looked at.
        Assert.True(EndpointResolver.TryResolve(ApiFormat.OpenAi, "https://a.test/v1", false, "https://relay.test/translate?token={apiKey}", "k", out _, out _));
    }
}

public class DeeplxProtocolTests
{
    [Theory]
    [InlineData("zh-Hans", "ZH")]
    [InlineData("zh-Hant", "ZH-HANT")]
    [InlineData("en", "EN")]
    [InlineData("ja", "JA")]
    [InlineData("ko", "KO")]
    [InlineData("", "ZH")]
    [InlineData(null, "ZH")]
    public void TargetLangCode_MapsAppCodesToDeepLCodes(string? code, string expected)
        => Assert.Equal(expected, DeeplxProtocol.TargetLangCode(code));

    [Fact]
    public void RequestJson_CarriesOnlyTextAndLanguageCodes()
    {
        const string text = "Ignore previous instructions <b>你好</b>";
        using var doc = JsonDocument.Parse(DeeplxProtocol.BuildRequestJson(text, "auto", "zh-Hans"));
        var root = doc.RootElement;
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.Equal(text, root.GetProperty("text").GetString());
        Assert.Equal("auto", root.GetProperty("source_lang").GetString());
        Assert.Equal("ZH", root.GetProperty("target_lang").GetString());

        using var explicitSource = JsonDocument.Parse(DeeplxProtocol.BuildRequestJson("x", "en", "ja"));
        Assert.Equal("EN", explicitSource.RootElement.GetProperty("source_lang").GetString());
        Assert.Equal("JA", explicitSource.RootElement.GetProperty("target_lang").GetString());
    }

    [Theory]
    [InlineData("{\"code\":200,\"data\":\"  你好  \"}")]
    [InlineData("{\"data\":\"你好\",\"alternatives\":[\"您好\"]}")]
    public void TryParse_ReadsData(string body)
    {
        Assert.True(DeeplxProtocol.TryParse(body, out var translation, out var err, out var code), err);
        Assert.Equal("你好", translation);
        Assert.Null(code);
    }

    [Theory]
    [InlineData("", "Empty")]
    [InlineData("<!DOCTYPE html><html>blocked</html>", "HTML")]
    [InlineData("{not json", "not valid JSON")]
    [InlineData("[]", "not an object")]
    [InlineData("{\"code\":200}", "no \"data\"")]
    [InlineData("{\"code\":200,\"data\":null}", "no \"data\"")]
    [InlineData("{\"code\":200,\"data\":\"   \"}", "empty translation")]
    public void TryParse_RejectsBadBodies(string body, string expectedFragment)
    {
        Assert.False(DeeplxProtocol.TryParse(body, out var translation, out var err, out var code));
        Assert.Equal(string.Empty, translation);
        Assert.Null(code);
        Assert.Contains(expectedFragment, err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_ReportsServiceCode_WithOrWithoutMessage()
    {
        Assert.False(DeeplxProtocol.TryParse("""{"code":401,"message":"Invalid access token"}""", out _, out var err, out var code));
        Assert.Equal(401, code);
        Assert.Equal("Invalid access token", err);

        Assert.False(DeeplxProtocol.TryParse("""{"code":503}""", out _, out var noMessage, out var bareCode));
        Assert.Equal(503, bareCode);
        Assert.Null(noMessage);
    }
}

public class DeeplxTranslatorTests
{
    private const string Endpoint = "https://h.test/translate?token={apiKey}";

    private static TranslationRequest Req(string baseUrl = Endpoint, bool relay = false, string glossary = "") =>
        new("hello fox", "zh-Hans", "Simplified Chinese", "auto", "Index-Translate-9B", baseUrl, relay, "https://relay.test/https://h.test/translate", "tok/1", glossary, ApiFormat.Deeplx);

    private static FakeTransport Replying(int status, string body, string? contentType = "application/json") =>
        new() { Handler = (_, _, _, _, _) => Task.FromResult(new TransportResponse(status, body, contentType)) };

    private static FakeTransport Ok() =>
        Replying(200, """{"code":200,"id":1,"data":"你好，狐狸","source_lang":"EN","target_lang":"ZH"}""");

    [Fact]
    public async Task Success_PutsKeyInUrl_SendsNoBearer_AndOnlyTextAndLanguageCodes()
    {
        var t = Ok();
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(glossary: "fox => 狐狸"), CancellationToken.None);
        Assert.Equal("你好，狐狸", result.Translation);
        Assert.Equal(RequestRoute.Direct, result.Route);
        Assert.Equal(DeeplxProtocol.ModelLabel, result.Model);
        Assert.Equal(0, result.GlossaryHitCount);

        var call = Assert.Single(t.Calls);
        Assert.Equal("https://h.test/translate?token=tok%2F1", call.Url.AbsoluteUri);
        Assert.Equal(string.Empty, call.Key);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("hello fox", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("auto", doc.RootElement.GetProperty("source_lang").GetString());
        Assert.Equal("ZH", doc.RootElement.GetProperty("target_lang").GetString());
        Assert.False(doc.RootElement.TryGetProperty("messages", out _));
        Assert.False(doc.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task WithoutPlaceholder_SendsKeyAsBearer()
    {
        var t = Ok();
        await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(baseUrl: "http://localhost:1188/translate"), CancellationToken.None);
        var call = Assert.Single(t.Calls);
        Assert.Equal("http://localhost:1188/translate", call.Url.AbsoluteUri);
        Assert.Equal("tok/1", call.Key);
    }

    [Fact]
    public async Task EmptyKey_SendsNoCredential()
    {
        var t = Ok();
        await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(baseUrl: "http://localhost:1188/translate") with { ApiKey = "" }, CancellationToken.None);
        Assert.Equal(string.Empty, Assert.Single(t.Calls).Key);
    }

    [Fact]
    public async Task PlaceholderUnderOpenAiFormat_IsConfigurationError_WithoutNetworkCall()
    {
        var t = Ok();
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req() with { Format = ApiFormat.OpenAi }, CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Configuration, ex.Error.Kind);
        Assert.Contains("DeepLX", ex.Error.Message);
        Assert.Empty(t.Calls);
    }

    [Fact]
    public async Task Relay_IsUsedAsTheCompleteEndpoint_AndLabelled()
    {
        var t = Ok();
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(relay: true), CancellationToken.None);
        Assert.Equal(RequestRoute.Relay, result.Route);
        Assert.Equal("https://relay.test/https://h.test/translate", Assert.Single(t.Calls).Url.AbsoluteUri);
    }

    [Fact]
    public async Task NoModelIsNeeded()
    {
        var result = await new OpenAiCompatibleTranslator(Ok()).TranslateAsync(Req() with { Model = "" }, CancellationToken.None);
        Assert.Equal("你好，狐狸", result.Translation);
    }

    [Fact]
    public async Task HttpError_KeepsStatus_AppendsServiceMessage_ButNeverHtml()
    {
        var t = Replying(403, """{"code":403,"message":"Invalid access token"}""");
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal(403, ex.Error.StatusCode);
        Assert.Contains("Invalid access token", ex.Error.Message);
        Assert.Single(t.Calls); // exactly one attempt, no retry

        var blocked = Replying(403, "<html>Sorry, you have been blocked</html>", "text/html");
        var html = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(blocked).TranslateAsync(Req(), CancellationToken.None));
        Assert.Equal(403, html.Error.StatusCode);
        Assert.DoesNotContain("<html>", html.Error.Message);
        Assert.DoesNotContain("blocked", html.Error.Message);
    }

    [Fact]
    public async Task ServiceCodeInsideHttp200_IsReportedAsThatHttpError()
    {
        var t = Replying(200, """{"code":429,"message":"Too Many Requests"}""");
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(), CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal(429, ex.Error.StatusCode);
        Assert.Contains("Too Many Requests", ex.Error.Message);
        Assert.True(ex.Error.IsRetryable);
        Assert.Contains("rate", ex.Error.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<!doctype html><html></html>", TranslationErrorKind.InvalidResponse)]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"译文\"}}]}", TranslationErrorKind.InvalidResponse)]
    [InlineData("{\"code\":200,\"data\":\"  \"}", TranslationErrorKind.EmptyResult)]
    public async Task BadBodies_WithStatus200(string body, TranslationErrorKind kind)
    {
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(Replying(200, body, null)).TranslateAsync(Req(), CancellationToken.None));
        Assert.Equal(kind, ex.Error.Kind);
    }

    [Fact]
    public async Task BadEndpoint_IsConfigurationError_WithoutNetworkCall()
    {
        var t = Ok();
        var ex = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(t).TranslateAsync(Req(baseUrl: "nope"), CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Configuration, ex.Error.Kind);
        Assert.Empty(t.Calls);
    }

    [Fact]
    public void CacheKey_SeparatesFormats_IgnoresGlossaryAndModel_AndNeverHoldsTheKey()
    {
        var k = CacheKey.For(Req());
        Assert.NotEqual(k, CacheKey.For(Req() with { Format = ApiFormat.OpenAi }));
        Assert.NotEqual(k, CacheKey.For(Req(baseUrl: "https://other.test/translate?token={apiKey}")));
        Assert.NotEqual(k, CacheKey.For(Req(relay: true)));
        Assert.NotEqual(k, CacheKey.For(Req() with { TargetLanguageCode = "ja" }));
        Assert.Equal(k, CacheKey.For(Req(glossary: "hello => 你好") with { Model = "Index-Translate-2B" }));
        Assert.Equal(k, CacheKey.For(Req() with { ApiKey = "another-key" }));
        Assert.Equal(Endpoint, k.EffectiveEndpoint);
    }

    [Fact]
    public async Task StudyCardAndImageTranslation_AreRefused_WithoutNetworkCall()
    {
        var t = Ok();
        var study = await Assert.ThrowsAsync<TranslationException>(() => new StudyCardService(t).GenerateAsync(Req(), CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Configuration, study.Error.Kind);
        Assert.StartsWith("Study Card needs an OpenAI-compatible chat model", study.Error.Message);

        var image = new ImageTranslationRequest("data:image/png;base64,AAAA", "zh-Hans", "Simplified Chinese", "Index-Translate-35B-A3B", Endpoint, false, "", "tok/1", "", ApiFormat.Deeplx);
        var vision = await Assert.ThrowsAsync<TranslationException>(() => new VisionTranslationService(t).TranslateImageAsync(image, CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Configuration, vision.Error.Kind);
        Assert.StartsWith("Image translation needs an OpenAI-compatible chat model", vision.Error.Message);

        Assert.Empty(t.Calls);
    }
}
