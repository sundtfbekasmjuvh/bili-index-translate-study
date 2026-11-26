using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>What the user enters for the custom API format; see <see cref="CustomApiProtocol"/>.</summary>
public sealed record CustomApiTemplate(
    string Name = "",
    string Method = "POST",
    string Headers = "",
    string Body = "",
    string ResultPath = "",
    string ErrorPath = "")
{
    /// <summary>Shown where a model name would be.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? CustomApiProtocol.DefaultLabel : Name.Trim();

    /// <summary>Changes whenever the template would send a different request or read a different result. Part of the cache key.</summary>
    public string Signature =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', Method, Headers, Body, ResultPath))))[..16];
}

/// <summary>One step of a result path: a key, an array index, or <c>[*]</c> (every element).</summary>
public readonly record struct JsonPathToken(string? Key, int Index, bool Any);

public sealed record ParsedCustomTemplate(
    string Method,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string BodyTemplate,
    bool IsForm,
    bool IsJson,
    bool IsBatch,
    IReadOnlyList<JsonPathToken> ResultPath,
    string ResultPathText,
    IReadOnlyList<JsonPathToken>? ErrorPath);

public sealed record CustomApiRequest(Uri Url, string Method, IReadOnlyList<KeyValuePair<string, string>> Headers, string? Body);

/// <summary>
/// A translation API described by a template instead of by code: URL, method, headers and body
/// are templates, and a path says where the translation is in the JSON reply. Placeholders:
/// <c>{text}</c> the text; <c>{texts}</c> every text of the request as a JSON array (body only);
/// <c>{apiKey}</c>; <c>{target}</c> the target language code of the app (zh-Hans, en);
/// <c>{targetLang}</c> the same as a DeepL-style code (ZH, EN) and <c>{targetLangLower}</c> in lower case.
/// Values are escaped for where they land: URL-encoded in the URL and in form bodies, JSON-escaped in
/// JSON bodies (write "{text}" inside the quotes of a string), stripped of line breaks in headers.
/// Like DeepLX, such an API has no prompt: plain text only, no glossary, no Study Card, no images.
/// </summary>
public static partial class CustomApiProtocol
{
    public const string DefaultLabel = "Custom API";

    private const string Names = "texts|text|apiKey|targetLangLower|targetLang|target";

    [GeneratedRegex(@"\{\{(" + Names + @")\}\}|\{(" + Names + @")\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^(?:[^.\[\]\s]+|\[(?:\d+|\*)\])(?:\.[^.\[\]\s]+|\[(?:\d+|\*)\])*$")]
    private static partial Regex PathShapeRegex();

    [GeneratedRegex(@"\[(\d+|\*)\]|[^.\[\]]+")]
    private static partial Regex PathTokenRegex();

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s*:\s*(.*)$")]
    private static partial Regex HeaderLineRegex();

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private enum Slot { Url, Header, Json, Form, Raw }

    private sealed record Values(IReadOnlyList<string> Texts, string ApiKey, string Target, string TargetLang);

    /// <summary>Paths like <c>data.translations[0].text</c>. Returns null when the path is empty or malformed.</summary>
    public static IReadOnlyList<JsonPathToken>? ParseJsonPath(string? path)
    {
        var src = (path ?? string.Empty).Trim();
        if (src.StartsWith("$.", StringComparison.Ordinal)) src = src[2..];
        else if (src.StartsWith('$')) src = src[1..];
        if (!PathShapeRegex().IsMatch(src)) return null;

        var tokens = new List<JsonPathToken>();
        foreach (Match m in PathTokenRegex().Matches(src))
        {
            if (!m.Groups[1].Success) tokens.Add(new JsonPathToken(m.Value, 0, false));
            else if (m.Groups[1].Value == "*") tokens.Add(new JsonPathToken(null, 0, true));
            else if (int.TryParse(m.Groups[1].Value, out var index)) tokens.Add(new JsonPathToken(null, index, false));
            else return null;
        }
        return tokens;
    }

    /// <summary>Every value the path reaches: at most one without <c>[*]</c>, any number with it.</summary>
    public static List<JsonElement> ReadJsonPath(JsonElement root, IReadOnlyList<JsonPathToken> tokens)
    {
        var nodes = new List<JsonElement> { root };
        foreach (var t in tokens)
        {
            var next = new List<JsonElement>();
            foreach (var n in nodes)
            {
                if (t.Any)
                {
                    if (n.ValueKind == JsonValueKind.Array) next.AddRange(n.EnumerateArray());
                }
                else if (t.Key is null)
                {
                    if (n.ValueKind == JsonValueKind.Array && t.Index < n.GetArrayLength()) next.Add(n[t.Index]);
                }
                else if (n.ValueKind == JsonValueKind.Object && n.TryGetProperty(t.Key, out var child))
                {
                    next.Add(child);
                }
            }
            nodes = next;
        }
        return nodes;
    }

    /// <summary>Checks the template and prepares it for use. <paramref name="endpointTemplate"/> is the URL as entered (it may carry <c>{text}</c>).</summary>
    public static bool TryParseTemplate(CustomApiTemplate template, string? endpointTemplate, out ParsedCustomTemplate? parsed, out string? error)
    {
        parsed = null;
        error = null;

        var method = string.Equals(template.Method?.Trim(), "GET", StringComparison.OrdinalIgnoreCase) ? "GET" : "POST";
        var bodyTemplate = method == "GET" ? string.Empty : (template.Body ?? string.Empty).Trim();

        var headers = new List<KeyValuePair<string, string>>();
        var contentType = string.Empty;
        foreach (var rawLine in (template.Headers ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = HeaderLineRegex().Match(line);
            if (!m.Success)
            {
                error = $"The header line \"{(line.Length > 80 ? line[..80] : line)}\" is not in the form \"Name: value\".";
                return false;
            }
            headers.Add(new(m.Groups[1].Value, m.Groups[2].Value));
            if (m.Groups[1].Value.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = m.Groups[2].Value;
        }
        if (bodyTemplate.Length > 0 && contentType.Length == 0)
        {
            contentType = "application/json";
            headers.Add(new("Content-Type", contentType));
        }

        var resultPathText = (template.ResultPath ?? string.Empty).Trim();
        var resultPath = ParseJsonPath(resultPathText);
        if (resultPath is null)
        {
            error = resultPathText.Length > 0
                ? $"The result path \"{resultPathText}\" is not valid. Use a path like translations[0].text or data."
                : "The result path is empty. It says where the translation is in the JSON reply, for example translations[0].text.";
            return false;
        }

        var errorPathText = (template.ErrorPath ?? string.Empty).Trim();
        var errorPath = errorPathText.Length > 0 ? ParseJsonPath(errorPathText) : null;
        if (errorPathText.Length > 0 && errorPath is null)
        {
            error = $"The error path \"{errorPathText}\" is not valid.";
            return false;
        }

        var batch = Uses(bodyTemplate, "texts");
        if (!batch && !Uses((endpointTemplate ?? string.Empty) + "\n" + bodyTemplate, "text"))
        {
            error = "The template never sends the text. Put {text} in the URL or the body, or {texts} in the body.";
            return false;
        }

        parsed = new ParsedCustomTemplate(
            method,
            headers,
            bodyTemplate,
            contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase),
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase),
            batch,
            resultPath,
            resultPathText,
            errorPath);
        return true;
    }

    /// <summary>The URL template with its placeholders filled in and URL-encoded.</summary>
    public static string FillUrl(string endpointTemplate, string? apiKey, IReadOnlyList<string> texts, string? targetLanguageCode)
        => Fill(endpointTemplate, ValuesFor(apiKey, texts, targetLanguageCode), Slot.Url);

    public static bool TryBuildRequest(
        ParsedCustomTemplate template,
        string endpointTemplate,
        string? apiKey,
        IReadOnlyList<string> texts,
        string? targetLanguageCode,
        out CustomApiRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        var values = ValuesFor(apiKey, texts, targetLanguageCode);

        if (!Uri.TryCreate(Fill(endpointTemplate, values, Slot.Url), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            error = "Endpoint must be an absolute http(s) URL.";
            return false;
        }

        var headers = template.Headers.Select(h => new KeyValuePair<string, string>(h.Key, Fill(h.Value, values, Slot.Header))).ToList();

        string? body = null;
        if (template.BodyTemplate.Length > 0)
        {
            body = Fill(template.BodyTemplate, values, template.IsForm ? Slot.Form : template.IsJson ? Slot.Json : Slot.Raw);
            if (template.IsJson)
            {
                try { using var _ = JsonDocument.Parse(body); }
                catch (JsonException)
                {
                    error = "The request body is not valid JSON once the placeholders are filled in. Write {text} inside the quotes of a string and {texts} without quotes.";
                    return false;
                }
            }
        }

        request = new CustomApiRequest(url, template.Method, headers, body);
        return true;
    }

    /// <summary>The service's own message at the error path, when the body is JSON and has one. Never HTML.</summary>
    public static string? ReadErrorText(ParsedCustomTemplate template, string? body)
    {
        if (template.ErrorPath is null || string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith('<')) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var v in ReadJsonPath(doc.RootElement, template.ErrorPath))
            {
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return ChatCompletionParser.Shorten(v.GetString()!);
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// Reads the translations at the result path. A batch template ({texts}) must return one translation per
    /// text; otherwise everything the path reaches is one translation (some services split it into pieces).
    /// </summary>
    public static bool TryParseResult(ParsedCustomTemplate template, string? body, int expected, out IReadOnlyList<string> translations, out string? error)
    {
        translations = [];
        error = null;

        if (string.IsNullOrWhiteSpace(body)) { error = "Empty response body."; return false; }
        if (body.TrimStart().StartsWith('<')) { error = "The endpoint returned an HTML page instead of JSON."; return false; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { error = "Response is not valid JSON."; return false; }

        using (doc)
        {
            var texts = new List<string>();
            var valid = true;
            foreach (var v in ReadJsonPath(doc.RootElement, template.ResultPath))
            {
                if (v.ValueKind == JsonValueKind.String) texts.Add(v.GetString()!);
                else if (v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
                    texts.AddRange(v.EnumerateArray().Select(x => x.GetString()!));
                else valid = false;
            }

            if (!valid || texts.Count == 0)
            {
                error = ReadErrorText(template, body) ?? $"Response has no translation at \"{template.ResultPathText}\".";
                return false;
            }

            if (template.IsBatch)
            {
                var trimmed = texts.Select(x => x.Trim()).ToList();
                var filled = trimmed.Count(x => x.Length > 0);
                if (trimmed.Count != expected || filled != expected)
                {
                    error = filled == 0
                        ? "The service returned an empty translation."
                        : $"Expected {expected} translation(s) at \"{template.ResultPathText}\" but found {filled}.";
                    return false;
                }
                translations = trimmed;
                return true;
            }

            var joined = string.Concat(texts).Trim();
            if (joined.Length == 0) { error = "The service returned an empty translation."; return false; }
            translations = [joined];
            return true;
        }
    }

    private static bool Uses(string template, string name)
        => PlaceholderRegex().Matches(template).Any(m => string.Equals(NameOf(m), name, StringComparison.OrdinalIgnoreCase));

    private static string NameOf(Match m) => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;

    private static Values ValuesFor(string? apiKey, IReadOnlyList<string> texts, string? targetLanguageCode)
        => new(texts, (apiKey ?? string.Empty).Trim(), (targetLanguageCode ?? string.Empty).Trim(), DeeplxProtocol.TargetLangCode(targetLanguageCode));

    private static string Fill(string template, Values values, Slot slot)
        => PlaceholderRegex().Replace(template, m => Encode(NameOf(m).ToLowerInvariant(), values, slot));

    private static string Encode(string name, Values v, Slot slot)
    {
        if (name == "texts")
        {
            var json = JsonSerializer.Serialize(v.Texts, RelaxedJson);
            return slot switch
            {
                Slot.Url or Slot.Form => Uri.EscapeDataString(json),
                Slot.Header => OneLine(json),
                _ => json,
            };
        }

        var value = name switch
        {
            "text" => v.Texts.Count > 0 ? v.Texts[0] : string.Empty,
            "apikey" => v.ApiKey,
            "target" => v.Target,
            "targetlang" => v.TargetLang,
            "targetlanglower" => v.TargetLang.ToLowerInvariant(),
            _ => string.Empty,
        };
        return slot switch
        {
            Slot.Url or Slot.Form => Uri.EscapeDataString(value),
            Slot.Header => OneLine(value),
            // Inside a JSON string: the escaped characters without the surrounding quotes.
            Slot.Json => JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
            _ => value,
        };
    }

    private static string OneLine(string s) => s.ReplaceLineEndings(" ");
}

/// <summary>Starting points for the custom format, taken from each service's API reference.</summary>
public sealed record CustomApiPreset(string Id, string DisplayName, string BaseUrl, CustomApiTemplate Template)
{
    private static readonly CustomApiTemplate DeepL = new(
        "DeepL",
        "POST",
        "Authorization: DeepL-Auth-Key {apiKey}",
        "{\"text\": {texts}, \"target_lang\": \"{targetLang}\"}",
        "translations[*].text");

    public static readonly IReadOnlyList<CustomApiPreset> All =
    [
        new("deepl-free", "DeepL API Free (api-free.deepl.com)", "https://api-free.deepl.com/v2/translate", DeepL),
        new("deepl-pro", "DeepL API Pro (api.deepl.com)", "https://api.deepl.com/v2/translate", DeepL),
        new("libretranslate", "LibreTranslate (replace the host with your instance)", "https://libretranslate.com/translate", new CustomApiTemplate(
            "LibreTranslate",
            "POST",
            string.Empty,
            "{\"q\": {texts}, \"source\": \"auto\", \"target\": \"{targetLangLower}\", \"format\": \"text\", \"api_key\": \"{apiKey}\"}",
            "translatedText",
            "error")),
    ];
}
