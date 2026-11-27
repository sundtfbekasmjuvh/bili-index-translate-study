using BiliIndexTranslate.Core.Localization;

namespace BiliIndexTranslate.Core.Speech;

/// <summary>
/// The name of a speech language in the app's interface language, for messages such as "No Japanese voice is
/// installed". Only the primary subtag counts (ja-JP → Japanese), because voices are matched by it too. A fixed table
/// keeps the text the same on every machine and in the app's own language, which can differ from Windows'.
/// </summary>
public static class LanguageNames
{
    private static readonly Dictionary<string, (string En, string Zh)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ("English", "英语"),
        ["ja"] = ("Japanese", "日语"),
        ["zh"] = ("Chinese", "中文"),
        ["ko"] = ("Korean", "韩语"),
        ["es"] = ("Spanish", "西班牙语"),
        ["fr"] = ("French", "法语"),
        ["de"] = ("German", "德语"),
        ["it"] = ("Italian", "意大利语"),
        ["pt"] = ("Portuguese", "葡萄牙语"),
        ["ru"] = ("Russian", "俄语"),
        ["el"] = ("Greek", "希腊语"),
        ["ar"] = ("Arabic", "阿拉伯语"),
        ["he"] = ("Hebrew", "希伯来语"),
        ["th"] = ("Thai", "泰语"),
        ["hi"] = ("Hindi", "印地语"),
        ["nl"] = ("Dutch", "荷兰语"),
        ["pl"] = ("Polish", "波兰语"),
        ["tr"] = ("Turkish", "土耳其语"),
        ["sv"] = ("Swedish", "瑞典语"),
        ["uk"] = ("Ukrainian", "乌克兰语"),
        ["vi"] = ("Vietnamese", "越南语"),
        ["id"] = ("Indonesian", "印尼语"),
    };

    /// <param name="bcp47">A language tag such as ja-JP.</param>
    /// <param name="interfaceLanguage">The app's interface language ("en" or "zh-CN"), as <c>Loc.Instance.Language</c>.</param>
    public static string For(string? bcp47, string? interfaceLanguage)
    {
        var tag = (bcp47 ?? string.Empty).Trim();
        if (tag.Length == 0) return string.Empty;
        var primary = tag.Split('-', '_')[0];
        if (Names.TryGetValue(primary, out var names))
            return string.Equals(interfaceLanguage, InterfaceLanguage.SimplifiedChinese, StringComparison.OrdinalIgnoreCase) ? names.Zh : names.En;
        return tag; // not in the table: the tag itself, so the text never depends on the machine's culture data
    }
}
