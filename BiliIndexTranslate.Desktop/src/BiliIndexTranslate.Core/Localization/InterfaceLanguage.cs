namespace BiliIndexTranslate.Core.Localization;

/// <summary>The interface language setting and how it resolves to a language the app has text for.</summary>
public static class InterfaceLanguage
{
    /// <summary>Follow the Windows display language.</summary>
    public const string Auto = "auto";
    public const string English = "en";
    public const string SimplifiedChinese = "zh-CN";

    public static readonly IReadOnlyList<string> Supported = [English, SimplifiedChinese];

    /// <summary>The setting as it is stored: one of the supported languages, otherwise <see cref="Auto"/>.</summary>
    public static string NormalizeSetting(string? setting)
    {
        var s = (setting ?? string.Empty).Trim();
        return Supported.FirstOrDefault(l => string.Equals(l, s, StringComparison.OrdinalIgnoreCase)) ?? Auto;
    }

    /// <summary>
    /// The language to show. An explicit setting wins; <see cref="Auto"/> gives Simplified Chinese for any
    /// Chinese system language (zh, zh-CN, zh-Hant-TW ...) and English for everything else.
    /// </summary>
    public static string Resolve(string? setting, string? systemLanguage)
    {
        var normalized = NormalizeSetting(setting);
        if (normalized != Auto) return normalized;
        var system = (systemLanguage ?? string.Empty).Trim();
        var isChinese = system.Equals("zh", StringComparison.OrdinalIgnoreCase) || system.StartsWith("zh-", StringComparison.OrdinalIgnoreCase);
        return isChinese ? SimplifiedChinese : English;
    }
}
