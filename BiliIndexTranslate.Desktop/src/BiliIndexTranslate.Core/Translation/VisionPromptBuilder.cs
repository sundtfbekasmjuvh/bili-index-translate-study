using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>
/// Builds OpenAI-compatible multimodal vision requests (<c>image_url</c> + dual-track
/// <c>〖译文〗</c> → <c>〖原文〗</c> prompt) and parses live/final dual-track outputs so
/// extracted foreign text unlocks TTS and Study Cards on unselectable pixels.
/// </summary>
public static partial class VisionPromptBuilder
{
    public const string DefaultVisionModel = "Index-Translate-35B-A3B";
    public const string TranslationTag = "〖译文〗";
    public const string SourceTag = "〖原文〗";

    [GeneratedRegex(@"^Index-(?:Nailong|Homura|Echo)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TextOnlyIndexModelRegex();

    [GeneratedRegex(@"〖(?:原文?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPartialSourceMarkerRegex();

    /// <summary>
    /// Defaults vision requests to <c>Index-Translate-35B-A3B</c> (3.2x faster MoE on public API)
    /// and automatically upgrades text-only Index checkpoints that lack an <c>mmproj</c> vision tower.
    /// </summary>
    public static string ResolveVisionModel(string? configuredVisionModel, string? modelOverride = null)
    {
        var ov = (modelOverride ?? string.Empty).Trim();
        if (ov.Length > 0) return ov;

        var m = (configuredVisionModel ?? DefaultVisionModel).Trim();
        if (m.Length == 0 || TextOnlyIndexModelRegex().IsMatch(m))
            return DefaultVisionModel;
        return m;
    }

    public static string BuildPromptText(string targetLanguagePromptName, bool isIndex, string? glossaryText = null)
    {
        var entries = GlossaryEngine.Parse(glossaryText).Take(28).ToList();

        if (isIndex)
        {
            var tgtName = PromptBuilder.NormalizeChineseLanguageName(targetLanguagePromptName);
            var prompt =
                $"请识别图片中的文本并将其翻译为{tgtName}。\n" +
                $"严格按以下两段标记依次输出，不要输出任何多余解释：\n" +
                $"{TranslationTag}对应的{tgtName}译文\n" +
                $"{SourceTag}逐字准确抄录图片中的原始文本（保持原拼写不变）";

            if (entries.Count > 0)
            {
                var pairs = string.Join("、", entries.Select(e => $"{e.Source}→{e.Target}"));
                prompt += $"\n\n【术语对照】{pairs}";
            }
            return prompt;
        }

        var enPrompt =
            $"Read the text in the image and translate it into {targetLanguagePromptName}.\n" +
            $"Output strictly using these two prefixes in order, with no extra commentary:\n" +
            $"{TranslationTag}<{targetLanguagePromptName} translation>\n" +
            $"{SourceTag}<exact original text from image>";

        if (entries.Count > 0)
        {
            var pairs = string.Join("; ", entries.Select(e => $"{e.Source} -> {e.Target}"));
            enPrompt += $"\n\nTerminology mapping: {pairs}";
        }
        return enPrompt;
    }

    public static string BuildRequestJson(
        string model,
        string imageDataUrl,
        string targetLanguagePromptName,
        bool stream = true,
        string? baseUrl = null,
        string? glossaryText = null)
    {
        var effectiveModel = ResolveVisionModel(model);
        var isIndex = PromptBuilder.IsIndexModelOrHost(effectiveModel, baseUrl);
        var promptText = BuildPromptText(targetLanguagePromptName, isIndex, glossaryText);

        var contentArray = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = promptText,
            },
            new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = imageDataUrl,
                },
            },
        };

        var body = new JsonObject
        {
            ["model"] = effectiveModel,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = contentArray,
                },
            },
            ["temperature"] = 0.0,
            ["max_tokens"] = 1024,
            ["stream"] = stream,
        };

        if (isIndex)
        {
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        }

        return body.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public static (string Translation, string SourceText) ParseDualOutput(string? rawContent)
    {
        var s = ChatCompletionParser.StripThink(rawContent);
        if (string.IsNullOrWhiteSpace(s))
            return (string.Empty, string.Empty);

        var tgtIdx = s.IndexOf(TranslationTag, StringComparison.Ordinal);
        var srcIdx = s.IndexOf(SourceTag, StringComparison.Ordinal);

        if (tgtIdx >= 0 && srcIdx > tgtIdx)
        {
            var translation = s[(tgtIdx + TranslationTag.Length)..srcIdx].Trim();
            var sourceText = s[(srcIdx + SourceTag.Length)..].Trim();
            return (translation, sourceText);
        }

        if (tgtIdx >= 0)
        {
            var slice = s[(tgtIdx + TranslationTag.Length)..];
            var translation = TrailingPartialSourceMarkerRegex().Replace(slice, string.Empty).Trim();
            return (translation, string.Empty);
        }

        if (srcIdx >= 0 && tgtIdx > srcIdx)
        {
            var sourceText = s[(srcIdx + SourceTag.Length)..tgtIdx].Trim();
            var translation = s[(tgtIdx + TranslationTag.Length)..].Trim();
            return (translation, sourceText);
        }

        return (s.Trim(), string.Empty);
    }
}
