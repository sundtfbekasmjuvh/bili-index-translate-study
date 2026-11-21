using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BiliIndexTranslate.Core.Localization;
using BiliIndexTranslate.Core.Settings;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class InterfaceLanguageTests
{
    [Theory]
    [InlineData("en", "zh-CN", "en")]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("ZH-cn", "en-US", "zh-CN")]
    [InlineData(" en ", "zh-CN", "en")]
    [InlineData("auto", "zh-CN", "zh-CN")]
    [InlineData("auto", "zh-Hant-TW", "zh-CN")]
    [InlineData("auto", "zh", "zh-CN")]
    [InlineData("auto", "en-GB", "en")]
    [InlineData("auto", "ja-JP", "en")]
    [InlineData("auto", "zhx", "en")]
    [InlineData("auto", "", "en")]
    [InlineData("auto", null, "en")]
    [InlineData("fr", "zh-CN", "zh-CN")]
    [InlineData(null, "zh-CN", "zh-CN")]
    [InlineData("", "de-DE", "en")]
    public void Resolve_PrefersAnExplicitSetting_AndFollowsTheSystemLanguageOtherwise(string? setting, string? system, string expected)
        => Assert.Equal(expected, InterfaceLanguage.Resolve(setting, system));

    [Theory]
    [InlineData("en", "en")]
    [InlineData("zh-cn", "zh-CN")]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("zh-TW", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    public void NormalizeSetting_KeepsSupportedLanguages_AndTurnsEverythingElseIntoAuto(string? setting, string expected)
        => Assert.Equal(expected, InterfaceLanguage.NormalizeSetting(setting));
}

public class UiTextTests
{
    private static readonly Regex Placeholders = new(@"\{[A-Za-z0-9]+\}");

    private static string[] PlaceholdersOf(string text) => Placeholders.Matches(text).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToArray();

    [Fact]
    public void BothLanguages_HaveTheSameKeys_NoEmptyText_AndTheSamePlaceholders()
    {
        Assert.Equal(UiText.English.Keys.OrderBy(k => k, StringComparer.Ordinal), UiText.SimplifiedChinese.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (key, english) in UiText.English)
        {
            var chinese = UiText.SimplifiedChinese[key];
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.False(string.IsNullOrWhiteSpace(chinese), key);
            Assert.True(PlaceholdersOf(english).SequenceEqual(PlaceholdersOf(chinese)), $"{key}: placeholders differ");
        }
    }

    [Fact]
    public void Translate_FillsNumberedPlaceholders_AndLeavesLiteralBracesAlone()
    {
        Assert.Equal("Don't translate in notepad", UiText.Translate("en", "Tray_ExcludeApp", "notepad"));
        Assert.Equal("不在 notepad 中翻译", UiText.Translate("zh-CN", "Tray_ExcludeApp", "notepad"));
        Assert.Equal("Could not register Alt+Q (error 1409).", UiText.Translate("en", "Hotkey_RegisterFailed", "Alt+Q", 1409));
        Assert.Equal("640 × 480 px  ·  松开即识图直译", UiText.Translate("zh-CN", "Snip_Release", 640, 480));
        // No arguments: the template is returned as is, including text that looks like a placeholder.
        Assert.Equal("Don't translate in {0}", UiText.Translate("en", "Tray_ExcludeApp"));
        Assert.Contains("{apiKey}", UiText.Translate("zh-CN", "Settings_EndpointHelp"));
        Assert.Contains("\"{text}\"", UiText.Translate("en", "Settings_PlaceholdersHelp"));
        // A missing argument leaves its placeholder; numbers are formatted without the system culture.
        Assert.Equal("Settings were not saved (IOException: {1}). They stay in effect until the app exits.", UiText.Translate("en", "App_SettingsNotSavedReason", "IOException"));
        Assert.Equal("glossary ×1.5", UiText.Translate("en", "Caption_Glossary", 1.5));
    }

    [Fact]
    public void Translate_FallsBackToEnglish_ForAnUnknownLanguage_AndToTheKey_ForAnUnknownKey()
    {
        Assert.Equal("Cancel", UiText.Translate("ja", "Common_Cancel"));
        Assert.Equal("Cancel", UiText.Translate(null, "Common_Cancel"));
        Assert.Equal("取消", UiText.Translate("zh-CN", "Common_Cancel"));
        Assert.Equal("No_Such_Key", UiText.Translate("zh-CN", "No_Such_Key"));
    }

    private static string AppSourceDirectory([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "BiliIndexTranslate.App"));

    /// <summary>
    /// The app refers to text by key from XAML ({loc:Tr Key}) and from code (Loc.T / Loc.Bind). A key that is
    /// missing from the table would show up as the key itself, so the sources are checked against the table.
    /// </summary>
    [Fact]
    public void EveryKeyTheAppUses_Exists_AndEveryKeyIsUsed()
    {
        var dir = AppSourceDirectory();
        Assert.True(Directory.Exists(dir), "app sources not found at " + dir);
        var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();
        Assert.NotEmpty(files);

        var prefixes = string.Join("|", UiText.English.Keys.Select(k => k[..k.IndexOf('_')]).Distinct());
        var inXaml = new Regex(@"\{loc:Tr ([A-Za-z0-9_]+)\}");
        var inCode = new Regex("\"((?:" + prefixes + ")_[A-Za-z0-9_]+)\"");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in (file.EndsWith(".xaml") ? inXaml : inCode).Matches(text)) used.Add(m.Groups[1].Value);
        }

        Assert.True(used.Count > 100, "expected the app to use well over 100 keys, found " + used.Count);
        Assert.Empty(used.Where(k => !UiText.English.ContainsKey(k)).OrderBy(k => k));
        Assert.Empty(UiText.English.Keys.Where(k => !used.Contains(k)).OrderBy(k => k));
    }
}

public class UiLanguageSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bit-tests-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void UiLanguage_DefaultsToAuto_RoundTrips_AndUnknownValuesBecomeAuto()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        Assert.Equal("auto", store.Load().Settings.UiLanguage);

        var s = new AppSettings { UiLanguage = "zh-CN" };
        store.Save(s);
        Assert.Contains("\"uiLanguage\": \"zh-CN\"", File.ReadAllText(File_));
        Assert.Equal("zh-CN", store.Load().Settings.UiLanguage);

        File.WriteAllText(File_, File.ReadAllText(File_).Replace("\"uiLanguage\": \"zh-CN\"", "\"uiLanguage\": \"klingon\""));
        var loaded = store.Load();
        Assert.False(loaded.WasMalformed);
        Assert.Equal("auto", loaded.Settings.UiLanguage);
    }

    [Fact]
    public void FileWrittenBeforeTheLanguageOption_LoadsAsAuto()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        store.Save(new AppSettings());
        var withoutLanguage = Regex.Replace(File.ReadAllText(File_), "\\s*\"uiLanguage\": \"auto\",", string.Empty);
        Assert.DoesNotContain("uiLanguage", withoutLanguage);
        File.WriteAllText(File_, withoutLanguage);
        Assert.Equal("auto", store.Load().Settings.UiLanguage);
    }
}
