using System.Text.Json;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

internal sealed record RawCall(string Method, Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string? Body)
{
    public string? Header(string name)
        => Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).LastOrDefault();
}

/// <summary>Records what the custom format asks the transport to send. The Bearer-based path must not be used.</summary>
internal sealed class FakeRawTransport : IRawTranslationTransport
{
    public Func<RawCall, TransportResponse> Handler { get; set; } =
        _ => new TransportResponse(200, """{"translations":[{"detected_source_language":"EN","text":" 你好 "}]}""", "application/json");

    public List<RawCall> Calls { get; } = new();

    public Task<TransportResponse> PostJsonAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken ct)
        => throw new InvalidOperationException("The custom format must not use the Bearer-based request path.");

    public Task<TransportResponse> SendAsync(string method, Uri url, IReadOnlyList<KeyValuePair<string, string>> headers, string? body, TimeSpan timeout, CancellationToken ct)
    {
        var call = new RawCall(method, url, headers, body);
        Calls.Add(call);
        return Task.FromResult(Handler(call));
    }
}

internal static class CustomFixtures
{
    public const string Endpoint = "https://api.example.test/v2/translate";

    /// <summary>The shape of the official DeepL API: texts as an array, translations in translations[].text.</summary>
    public static readonly CustomApiTemplate DeepL = new(
        "DeepL",
        "POST",
        "Authorization: DeepL-Auth-Key {apiKey}",
        """{"text": {texts}, "target_lang": "{targetLang}"}""",
        "translations[*].text",
        "message");

    public static ParsedCustomTemplate Parse(CustomApiTemplate template, string endpoint = Endpoint)
    {
        Assert.True(CustomApiProtocol.TryParseTemplate(template, endpoint, out var parsed, out var error), error);
        return parsed!;
    }
}

public class CustomApiPathTests
{
    private static string Describe(IReadOnlyList<JsonPathToken>? path)
        => path is null ? "null" : string.Join(" ", path.Select(t => t.Any ? "*" : t.Key ?? "#" + t.Index));

    [Theory]
    [InlineData("translations[0].text", "translations #0 text")]
    [InlineData(" $.data.translations[*].translatedText ", "data translations * translatedText")]
    [InlineData("[0][*][0]", "#0 * #0")]
    [InlineData("$[1]", "#1")]
    [InlineData("data", "data")]
    public void ParseJsonPath_ReadsKeysIndexesAndWildcards(string path, string expected)
        => Assert.Equal(expected, Describe(CustomApiProtocol.ParseJsonPath(path)));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a[")]
    [InlineData("a[x]")]
    [InlineData("a[-1]")]
    [InlineData("a b")]
    [InlineData("a.[0]")]
    [InlineData("a[99999999999]")]
    [InlineData(null)]
    public void ParseJsonPath_RejectsEmptyAndMalformedPaths(string? path)
        => Assert.Null(CustomApiProtocol.ParseJsonPath(path));

    [Theory]
    [InlineData("translations[*].text", "x|y")]
    [InlineData("translations[1].text", "y")]
    [InlineData("data", "z")]
    [InlineData("translations[2].text", "")]
    [InlineData("translations.text", "")]
    [InlineData("data[0]", "")]
    [InlineData("missing.deep", "")]
    public void ReadJsonPath_ReturnsEveryValueThePathReaches(string path, string expected)
    {
        using var doc = JsonDocument.Parse("""{"translations":[{"text":"x"},{"text":"y"}],"data":"z"}""");
        var values = CustomApiProtocol.ReadJsonPath(doc.RootElement, CustomApiProtocol.ParseJsonPath(path)!);
        Assert.Equal(expected, string.Join("|", values.Select(v => v.GetString())));
    }
}

public class CustomApiTemplateTests
{
    private static readonly CustomApiTemplate DeepL = CustomFixtures.DeepL;

    [Fact]
    public void Template_DefaultsToAJsonBody_DetectsBatch_AndDropsTheBodyForGet()
    {
        var tpl = CustomFixtures.Parse(DeepL);
        Assert.Equal("POST", tpl.Method);
        Assert.True(tpl.IsBatch);
        Assert.True(tpl.IsJson);
        Assert.False(tpl.IsForm);
        Assert.Equal(new[] { "Authorization", "Content-Type" }, tpl.Headers.Select(h => h.Key));
        Assert.Equal("application/json", tpl.Headers[1].Value);

        var form = CustomFixtures.Parse(DeepL with { Headers = "# comment\r\n\r\ncontent-type: application/x-www-form-urlencoded", Body = "q={text}" });
        Assert.True(form.IsForm);
        Assert.False(form.IsJson);
        Assert.False(form.IsBatch);
        Assert.Single(form.Headers);

        var get = CustomFixtures.Parse(DeepL with { Method = "get", Headers = "" }, "https://api.example.test/t?q={text}");
        Assert.Equal("GET", get.Method);
        Assert.Equal(string.Empty, get.BodyTemplate);
        Assert.Empty(get.Headers);
    }

    [Fact]
    public void Template_ThatCannotWork_IsRejectedWithTheReason()
    {
        static void Rejects(CustomApiTemplate template, string fragment)
        {
            Assert.False(CustomApiProtocol.TryParseTemplate(template, CustomFixtures.Endpoint, out var parsed, out var error));
            Assert.Null(parsed);
            Assert.Contains(fragment, error);
        }

        Rejects(DeepL with { ResultPath = " " }, "result path is empty");
        Rejects(DeepL with { ResultPath = "a..b" }, "result path \"a..b\" is not valid");
        Rejects(DeepL with { ErrorPath = "[x]" }, "error path \"[x]\" is not valid");
        Rejects(DeepL with { Headers = "Authorization Bearer x" }, "is not in the form");
        Rejects(DeepL with { Body = """{"target_lang": "{targetLang}"}""" }, "never sends the text");
        Rejects(DeepL with { Method = "GET" }, "never sends the text");
    }

    [Fact]
    public void BuildRequest_EscapesPlaceholdersForWhereTheyLand()
    {
        const string nasty = "He said \"hi\"\\ \n第二行\t{text} </b>";
        var tpl = CustomFixtures.Parse(DeepL with
        {
            Headers = "X-Key: {{apiKey}}\nX-Target: {target}|{targetLang}|{targetLangLower}",
            Body = """{"q": "{text}", "all": {texts}, "to": "{targetLangLower}", "nested": {"key": "{apiKey}"}}""",
        });

        Assert.True(CustomApiProtocol.TryBuildRequest(tpl, "https://h.test/t/?k={apiKey}&to={targetLang}", " a b/c\n", [nasty, "second"], "zh-Hant", out var request, out var error), error);
        Assert.Equal("https://h.test/t/?k=a%20b%2Fc&to=ZH-HANT", request!.Url.AbsoluteUri);
        Assert.Equal("POST", request.Method);
        Assert.Equal(
            new[] { "X-Key: a b/c", "X-Target: zh-Hant|ZH-HANT|zh-hant", "Content-Type: application/json" },
            request.Headers.Select(h => h.Key + ": " + h.Value));

        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        Assert.Equal(nasty, root.GetProperty("q").GetString());
        Assert.Equal(new[] { nasty, "second" }, root.GetProperty("all").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("zh-hant", root.GetProperty("to").GetString());
        Assert.Equal("a b/c", root.GetProperty("nested").GetProperty("key").GetString());

        var header = CustomFixtures.Parse(DeepL with { Headers = "X-Text: {text}" });
        Assert.True(CustomApiProtocol.TryBuildRequest(header, CustomFixtures.Endpoint, "k", ["line1\r\nline2"], "en", out var oneLine, out _));
        Assert.Equal("line1 line2", oneLine!.Headers[0].Value);
    }

    [Fact]
    public void BuildRequest_FormBodiesAreUrlEncoded_GetHasNoBody_AndBrokenJsonIsRejected()
    {
        var form = CustomFixtures.Parse(DeepL with { Headers = "Content-Type: application/x-www-form-urlencoded", Body = "q={text}&target={target}" });
        Assert.True(CustomApiProtocol.TryBuildRequest(form, CustomFixtures.Endpoint, "k", ["a&b=c d"], "zh-Hans", out var formRequest, out _));
        Assert.Equal("q=a%26b%3Dc%20d&target=zh-Hans", formRequest!.Body);

        const string getEndpoint = "https://h.test/t?q={text}&to={targetLangLower}";
        var get = CustomFixtures.Parse(DeepL with { Method = "GET", Headers = "" }, getEndpoint);
        Assert.True(CustomApiProtocol.TryBuildRequest(get, getEndpoint, "", ["你好 world?"], "en", out var getRequest, out _));
        Assert.Equal("https://h.test/t?q=%E4%BD%A0%E5%A5%BD%20world%3F&to=en", getRequest!.Url.AbsoluteUri);
        Assert.Null(getRequest.Body);
        Assert.Empty(getRequest.Headers);

        var unquoted = CustomFixtures.Parse(DeepL with { Body = """{"q": {text}}""" });
        Assert.False(CustomApiProtocol.TryBuildRequest(unquoted, CustomFixtures.Endpoint, "k", ["hello"], "en", out var none, out var jsonError));
        Assert.Null(none);
        Assert.Contains("not valid JSON", jsonError);

        Assert.False(CustomApiProtocol.TryBuildRequest(CustomFixtures.Parse(DeepL), "nope/{apiKey}", "k", ["hello"], "en", out _, out var urlError));
        Assert.Contains("absolute http(s) URL", urlError);
    }

    [Fact]
    public void ParseResult_ReadsSingleTexts_JoinsPieces_AndChecksBatchCounts()
    {
        var single = CustomFixtures.Parse(DeepL with { Body = """{"q": "{text}"}""", ResultPath = "data.out" });
        Assert.True(CustomApiProtocol.TryParseResult(single, """{"data":{"out":"  你好 "}}""", 1, out var one, out _));
        Assert.Equal(new[] { "你好" }, one);

        // Some services return one translation in pieces; without {texts} everything the path reaches is one text.
        var pieces = CustomFixtures.Parse(DeepL with { Body = """{"q": "{text}"}""", ResultPath = "[0][*][0]" });
        Assert.True(CustomApiProtocol.TryParseResult(pieces, """[[["你好，", "Hello, "], ["世界。", "world."]], null, "en"]""", 1, out var joined, out _));
        Assert.Equal(new[] { "你好，世界。" }, joined);

        var batch = CustomFixtures.Parse(DeepL);
        Assert.True(CustomApiProtocol.TryParseResult(batch, """{"translations":[{"text":" 一 "},{"text":"二"}]}""", 2, out var two, out _));
        Assert.Equal(new[] { "一", "二" }, two);

        // The LibreTranslate shape: the path points at an array of strings.
        var arrayAtPath = CustomFixtures.Parse(DeepL with { ResultPath = "translatedText" });
        Assert.True(CustomApiProtocol.TryParseResult(arrayAtPath, """{"translatedText":["一","二"]}""", 2, out var fromArray, out _));
        Assert.Equal(new[] { "一", "二" }, fromArray);

        Assert.False(CustomApiProtocol.TryParseResult(batch, """{"translations":[{"text":"一"}]}""", 2, out var none, out var mismatch));
        Assert.Empty(none);
        Assert.Equal("Expected 2 translation(s) at \"translations[*].text\" but found 1.", mismatch);
    }

    [Theory]
    [InlineData("", "Empty response body.")]
    [InlineData("<html>ok</html>", "The endpoint returned an HTML page instead of JSON.")]
    [InlineData("{not json", "Response is not valid JSON.")]
    [InlineData("{\"translations\":[]}", "Response has no translation at \"translations[*].text\".")]
    [InlineData("{\"translations\":[{\"text\":42}]}", "Response has no translation at \"translations[*].text\".")]
    [InlineData("null", "Response has no translation at \"translations[*].text\".")]
    [InlineData("{\"message\":\" Quota exceeded \"}", "Quota exceeded")]
    [InlineData("{\"translations\":[{\"text\":\"  \"}]}", "The service returned an empty translation.")]
    public void ParseResult_ReportsWhyThereIsNoTranslation(string body, string expected)
    {
        Assert.False(CustomApiProtocol.TryParseResult(CustomFixtures.Parse(DeepL), body, 1, out var translations, out var error));
        Assert.Empty(translations);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void ReadErrorText_ReturnsTheServiceMessage_ButNeverHtmlOrNonStrings()
    {
        var tpl = CustomFixtures.Parse(DeepL with { ErrorPath = "error.message" });
        Assert.Equal("Wrong key", CustomApiProtocol.ReadErrorText(tpl, """{"error":{"message":" Wrong key "}}"""));
        Assert.Null(CustomApiProtocol.ReadErrorText(tpl, "<html>blocked</html>"));
        Assert.Null(CustomApiProtocol.ReadErrorText(tpl, """{"error":{"message":42}}"""));
        Assert.Null(CustomApiProtocol.ReadErrorText(tpl, "{not json"));
        Assert.Null(CustomApiProtocol.ReadErrorText(CustomFixtures.Parse(DeepL with { ErrorPath = "" }), """{"message":"x"}"""));
    }

    [Fact]
    public void Presets_AreWorkingTemplates_ThatSendABatchInOneRequest()
    {
        Assert.Equal(new[] { "deepl-free", "deepl-pro", "libretranslate" }, CustomApiPreset.All.Select(p => p.Id));
        foreach (var preset in CustomApiPreset.All)
        {
            var tpl = CustomFixtures.Parse(preset.Template, preset.BaseUrl);
            Assert.True(tpl.IsBatch, preset.Id);
            Assert.True(CustomApiProtocol.TryBuildRequest(tpl, preset.BaseUrl, "k", ["Hello", "World"], "zh-Hans", out var request, out var error), error);
            Assert.Equal(preset.BaseUrl, request!.Url.AbsoluteUri);
            using var doc = JsonDocument.Parse(request.Body!);
            var root = doc.RootElement;

            if (preset.Id == "libretranslate")
            {
                Assert.Equal(new[] { "Hello", "World" }, root.GetProperty("q").EnumerateArray().Select(e => e.GetString()));
                Assert.Equal("auto", root.GetProperty("source").GetString());
                Assert.Equal("zh", root.GetProperty("target").GetString());
                Assert.Equal("k", root.GetProperty("api_key").GetString());
                Assert.True(CustomApiProtocol.TryParseResult(tpl, """{"translatedText":["你好","世界"]}""", 2, out var libre, out _));
                Assert.Equal(new[] { "你好", "世界" }, libre);
                Assert.Equal("Invalid API key", CustomApiProtocol.ReadErrorText(tpl, """{"error":"Invalid API key"}"""));
            }
            else
            {
                Assert.Equal(new[] { "Hello", "World" }, root.GetProperty("text").EnumerateArray().Select(e => e.GetString()));
                Assert.Equal("ZH", root.GetProperty("target_lang").GetString());
                Assert.Contains(request.Headers, h => h.Key == "Authorization" && h.Value == "DeepL-Auth-Key k");
                Assert.True(CustomApiProtocol.TryParseResult(tpl, """{"translations":[{"detected_source_language":"EN","text":"你好"},{"detected_source_language":"EN","text":"世界"}]}""", 2, out var deepl, out _));
                Assert.Equal(new[] { "你好", "世界" }, deepl);
            }
        }
    }
}

public class CustomApiTranslatorTests
{
    private static readonly CustomApiTemplate DeepL = CustomFixtures.DeepL;

    private static TranslationRequest Req(CustomApiTemplate? template = null, string baseUrl = CustomFixtures.Endpoint, bool relay = false, string glossary = "") =>
        new("hello \"fox\"", "zh-Hans", "Simplified Chinese", "auto", "Index-Translate-9B", baseUrl, relay,
            "https://relay.test/https://api.example.test/v2/translate", "k 1/2", glossary, ApiFormat.Custom, template ?? DeepL);

    private static FakeRawTransport Replying(int status, string body, string? contentType = "application/json") =>
        new() { Handler = _ => new TransportResponse(status, body, contentType) };

    private static Task<TranslationException> Fails(ITranslationTransport transport, TranslationRequest request)
        => Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslator(transport).TranslateAsync(request, CancellationToken.None));

    [Fact]
    public async Task Success_SendsMethodHeadersAndBodyFromTheTemplate_WithoutPromptOrGlossary()
    {
        var t = new FakeRawTransport();
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(glossary: "fox => 狐狸"), CancellationToken.None);
        Assert.Equal("你好", result.Translation);
        Assert.Equal(RequestRoute.Direct, result.Route);
        Assert.Equal("DeepL", result.Model);
        Assert.Equal(0, result.GlossaryHitCount);

        var call = Assert.Single(t.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal(CustomFixtures.Endpoint, call.Url.AbsoluteUri);
        Assert.Equal("DeepL-Auth-Key k 1/2", call.Header("Authorization"));
        Assert.Equal("application/json", call.Header("Content-Type"));
        using var doc = JsonDocument.Parse(call.Body!);
        Assert.Equal(new[] { "hello \"fox\"" }, doc.RootElement.GetProperty("text").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("ZH", doc.RootElement.GetProperty("target_lang").GetString());
        Assert.False(doc.RootElement.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task GetTemplate_PutsTextAndKeyInTheUrl_AndAnUnnamedTemplateIsLabelledCustomApi()
    {
        var t = Replying(200, """{"data":{"out":"Hello"}}""");
        var template = new CustomApiTemplate(" ", "GET", "", "", "data.out");
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(
            Req(template, "https://api.example.test/t?q={text}&to={targetLangLower}&key={apiKey}"), CancellationToken.None);
        Assert.Equal("Hello", result.Translation);
        Assert.Equal(CustomApiProtocol.DefaultLabel, result.Model);

        var call = Assert.Single(t.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Null(call.Body);
        Assert.Empty(call.Headers);
        Assert.Equal("https://api.example.test/t?q=hello%20%22fox%22&to=zh&key=k%201%2F2", call.Url.AbsoluteUri);
    }

    [Fact]
    public async Task Relay_IsUsedAsTheCompleteUrlTemplate_AndLabelled()
    {
        var t = new FakeRawTransport();
        var result = await new OpenAiCompatibleTranslator(t).TranslateAsync(Req(relay: true), CancellationToken.None);
        Assert.Equal(RequestRoute.Relay, result.Route);
        Assert.Equal("https://relay.test/https://api.example.test/v2/translate", Assert.Single(t.Calls).Url.AbsoluteUri);
    }

    [Fact]
    public async Task HttpError_KeepsStatus_AppendsTheTextAtTheErrorPath_ButNeverHtml()
    {
        var t = Replying(403, """{"message":"Wrong endpoint. Use the free host."}""");
        var ex = await Fails(t, Req());
        Assert.Equal(TranslationErrorKind.Http, ex.Error.Kind);
        Assert.Equal(403, ex.Error.StatusCode);
        Assert.Equal("Forbidden (HTTP 403). Wrong endpoint. Use the free host.", ex.Error.Message);
        Assert.Single(t.Calls); // exactly one attempt, no retry

        var html = await Fails(Replying(403, "<html>Sorry, you have been blocked</html>", "text/html"), Req());
        Assert.Equal("Forbidden (HTTP 403).", html.Error.Message);
    }

    [Theory]
    [InlineData("{\"message\":\"Quota exceeded\"}", TranslationErrorKind.InvalidResponse, "Quota exceeded")]
    [InlineData("<!doctype html><html></html>", TranslationErrorKind.InvalidResponse, "The endpoint returned an HTML page instead of JSON.")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"译文\"}}]}", TranslationErrorKind.InvalidResponse, "Response has no translation at \"translations[*].text\".")]
    [InlineData("{\"translations\":[{\"text\":\"一\"},{\"text\":\"二\"}]}", TranslationErrorKind.InvalidResponse, "Expected 1 translation(s) at \"translations[*].text\" but found 2.")]
    [InlineData("{\"translations\":[{\"text\":\"  \"}]}", TranslationErrorKind.EmptyResult, "The service returned an empty translation.")]
    public async Task BadBodies_WithStatus200(string body, TranslationErrorKind kind, string message)
    {
        var ex = await Fails(Replying(200, body, null), Req());
        Assert.Equal(kind, ex.Error.Kind);
        Assert.Equal(message, ex.Error.Message);
    }

    [Fact]
    public async Task TemplateThatCannotWork_IsAConfigurationError_WithoutNetworkCall()
    {
        var t = new FakeRawTransport();
        var noPath = await Fails(t, Req(DeepL with { ResultPath = "" }));
        Assert.Equal(TranslationErrorKind.Configuration, noPath.Error.Kind);
        Assert.Contains("result path is empty", noPath.Error.Message);

        var badJson = await Fails(t, Req(DeepL with { Body = """{"text": [{text}]}""" }));
        Assert.Equal(TranslationErrorKind.Configuration, badJson.Error.Kind);
        Assert.Contains("not valid JSON", badJson.Error.Message);

        var badUrl = await Fails(t, Req(baseUrl: "nope"));
        Assert.Equal(TranslationErrorKind.Configuration, badUrl.Error.Kind);

        var missingTemplate = await Fails(t, Req() with { Custom = null });
        Assert.Equal(TranslationErrorKind.Configuration, missingTemplate.Error.Kind);

        // A transport that can only send Bearer-authenticated JSON cannot serve a template.
        var plain = new FakeTransport();
        var unsupported = await Fails(plain, Req());
        Assert.Equal(TranslationErrorKind.Configuration, unsupported.Error.Kind);
        Assert.Empty(plain.Calls);

        Assert.Empty(t.Calls);
    }

    [Fact]
    public void CacheKey_FollowsWhatTheTemplateSendsAndReads_AndNeverHoldsTheKey()
    {
        var k = CacheKey.For(Req());
        Assert.NotEqual(k, CacheKey.For(Req() with { Format = ApiFormat.Deeplx }));
        Assert.NotEqual(k, CacheKey.For(Req(DeepL with { Body = """{"text": {texts}, "target_lang": "{targetLang}", "formality": "more"}""" })));
        Assert.NotEqual(k, CacheKey.For(Req(DeepL with { ResultPath = "translations[0].text" })));
        Assert.NotEqual(k, CacheKey.For(Req(DeepL with { Headers = "X-Other: 1" })));
        Assert.NotEqual(k, CacheKey.For(Req(DeepL with { Name = "Other" })));
        Assert.NotEqual(k, CacheKey.For(Req(baseUrl: "https://other.test/v2/translate")));
        Assert.NotEqual(k, CacheKey.For(Req(relay: true)));
        Assert.NotEqual(k, CacheKey.For(Req() with { TargetLanguageCode = "ja" }));

        // No prompt and no model: the glossary, the model setting and the key do not change the result,
        // and neither does where the error message is read from.
        Assert.Equal(k, CacheKey.For(Req(glossary: "hello => 你好") with { Model = "Index-Translate-2B", ApiKey = "another-key" }));
        Assert.Equal(k, CacheKey.For(Req(DeepL with { ErrorPath = "error.message" })));

        var keyInUrl = CacheKey.For(Req(baseUrl: "https://h.test/t?key={apiKey}"));
        Assert.Equal("https://h.test/t?key={apiKey}", keyInUrl.EffectiveEndpoint);
    }

    [Fact]
    public async Task StudyCardAndImageTranslation_AreRefused_WithoutNetworkCall()
    {
        var t = new FakeRawTransport();
        var study = await Assert.ThrowsAsync<TranslationException>(() => new StudyCardService(t).GenerateAsync(Req(), CancellationToken.None));
        Assert.Equal(TranslationErrorKind.Configuration, study.Error.Kind);
        Assert.Equal("Study Card needs an OpenAI-compatible chat model; the custom API format only translates text.", study.Error.Message);

        var image = new ImageTranslationRequest("data:image/png;base64,AAAA", "zh-Hans", "Simplified Chinese", "Index-Translate-35B-A3B", CustomFixtures.Endpoint, false, "", "k", "", ApiFormat.Custom);
        var vision = await Assert.ThrowsAsync<TranslationException>(() => new VisionTranslationService(t).TranslateImageAsync(image, CancellationToken.None));
        Assert.Equal("Image translation needs an OpenAI-compatible chat model; the custom API format only translates text.", vision.Error.Message);

        Assert.Empty(t.Calls);
    }

    [Fact]
    public void Endpoint_IsValidatedWithSampleValues_AndShowsTheHost()
    {
        Assert.True(EndpointResolver.TryResolve(ApiFormat.Custom, " https://api.example.test/t?q={text}&key={apiKey}/ ", false, null, "k", out var ep, out var err), err);
        Assert.Equal("api.example.test", ep!.DisplayHost);
        Assert.Equal("https://api.example.test/t?q={text}&key={apiKey}", ep.NormalizedBase);
        Assert.Equal(RequestRoute.Direct, ep.Route);

        Assert.False(EndpointResolver.TryResolve(ApiFormat.Custom, "", false, null, "k", out _, out var empty));
        Assert.Contains("empty", empty);
        Assert.False(EndpointResolver.TryResolve(ApiFormat.Custom, "ftp://x/{text}", false, null, "k", out _, out var scheme));
        Assert.Contains("http(s)", scheme);
        Assert.False(EndpointResolver.TryResolve(ApiFormat.Custom, "https://a.test/t", true, " ", "k", out _, out var relay));
        Assert.Contains("Relay", relay);
    }
}

public class CustomApiSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bit-tests-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Template_RoundTrips_IsNormalized_AndAnEmptyKeyStaysEmpty()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        store.Save(new AppSettings
        {
            ApiFormat = ApiFormat.Custom,
            BaseUrl = CustomFixtures.Endpoint,
            ApiKey = " ",
            CustomName = "  DeepL ",
            CustomMethod = "get",
            CustomHeaders = "A: 1\r\nB: 2\r\n",
            CustomBody = CustomFixtures.DeepL.Body,
            CustomResultPath = " translations[*].text ",
            CustomErrorPath = "message",
        });

        var raw = File.ReadAllText(File_);
        Assert.Contains("\"apiFormat\": \"Custom\"", raw);
        Assert.Contains("\"customResultPath\": \"translations[*].text\"", raw);
        Assert.DoesNotContain("customTemplate", raw);

        var loaded = store.Load().Settings;
        Assert.Equal(ApiFormat.Custom, loaded.ApiFormat);
        Assert.Equal(new CustomApiTemplate("DeepL", "GET", "A: 1\nB: 2", CustomFixtures.DeepL.Body, "translations[*].text", "message"), loaded.CustomTemplate);
        Assert.Equal(string.Empty, loaded.ApiKey);
    }

    [Fact]
    public void DefaultsAndOlderFiles_HaveAnEmptyPostTemplate()
    {
        Assert.Equal(new CustomApiTemplate(), new AppSettings().CustomTemplate);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "schemaVersion": 2, "apiFormat": "Deeplx", "baseUrl": "http://localhost:1188/translate", "customMethod": null }""");
        var loaded = new SettingsStore(File_, new ReversibleProtector()).Load();
        Assert.False(loaded.WasMalformed);
        Assert.Equal(ApiFormat.Deeplx, loaded.Settings.ApiFormat);
        Assert.Equal(new CustomApiTemplate(), loaded.Settings.CustomTemplate);
    }
}
