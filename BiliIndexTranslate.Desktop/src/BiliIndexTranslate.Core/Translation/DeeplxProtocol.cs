using System.Text.Json;
using System.Text.Json.Nodes;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>
/// DeepLX-style translate contract: POST { text, source_lang, target_lang } and read "data" from
/// { code: 200, data: "translation" }. There is no prompt, so this format carries plain text only:
/// the glossary is not applied, and Study Cards and image translation are refused up front instead
/// of being sent to an endpoint that cannot do them.
/// </summary>
public static class DeeplxProtocol
{
    /// <summary>Shown where a model name would be; DeepLX endpoints have no model to choose.</summary>
    public const string ModelLabel = "DeepLX";

    /// <summary>Maps the app's target language codes to DeepL codes (zh-Hans → ZH, zh-Hant → ZH-HANT, en → EN).</summary>
    public static string TargetLangCode(string? targetLanguageCode)
    {
        var code = (targetLanguageCode ?? string.Empty).Trim();
        if (code.Length == 0 || code.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)) return "ZH";
        if (code.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)) return "ZH-HANT";
        return code.ToUpperInvariant();
    }

    public static string SourceLangCode(string? sourceLanguage)
    {
        var code = (sourceLanguage ?? string.Empty).Trim();
        return code.Length == 0 || code.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : code.ToUpperInvariant();
    }

    public static string BuildRequestJson(string text, string? sourceLanguage, string? targetLanguageCode)
    {
        var body = new JsonObject
        {
            ["text"] = text,
            ["source_lang"] = SourceLangCode(sourceLanguage),
            ["target_lang"] = TargetLangCode(targetLanguageCode),
        };
        return body.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// Reads the translation from a response body. A non-200 "code" in the body is a service error even
    /// when the HTTP status is 200: <paramref name="serviceCode"/> is set and <paramref name="error"/>
    /// carries the service's message, if it sent one. HTML or other non-JSON bodies are reported as
    /// invalid responses, never rendered as a translation.
    /// </summary>
    public static bool TryParse(string? body, out string translation, out string? error, out int? serviceCode)
    {
        translation = string.Empty;
        error = null;
        serviceCode = null;

        if (string.IsNullOrWhiteSpace(body)) { error = "Empty response body."; return false; }
        if (body.TrimStart().StartsWith('<')) { error = "The endpoint returned an HTML page instead of JSON."; return false; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { error = "Response is not valid JSON."; return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "Response JSON is not an object."; return false; }

            if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                && codeEl.TryGetInt32(out var code) && code != 200)
            {
                serviceCode = code;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                    error = ChatCompletionParser.Shorten(message.GetString()!);
                return false;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            {
                error = "Response has no \"data\" translation.";
                return false;
            }

            var text = data.GetString()!.Trim();
            if (text.Length == 0) { error = "The service returned an empty translation."; return false; }

            translation = text;
            return true;
        }
    }

    /// <summary>Error for features that need a chat model. Raised before any request is sent.</summary>
    public static TranslationError Unsupported(string feature, RequestRoute route)
        => new(TranslationErrorKind.Configuration,
            $"{feature} needs an OpenAI-compatible chat model; the DeepLX API format only translates text.",
            route);
}
