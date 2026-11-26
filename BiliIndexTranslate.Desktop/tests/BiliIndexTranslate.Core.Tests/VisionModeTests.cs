using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class VisionRoutingTests
{
    [Theory]
    [InlineData(VisionMode.Auto, ApiFormat.OpenAi, VisionRoute.Cloud)]
    [InlineData(VisionMode.Auto, ApiFormat.Deeplx, VisionRoute.Local)]
    [InlineData(VisionMode.Auto, ApiFormat.Custom, VisionRoute.Local)]
    [InlineData(VisionMode.Local, ApiFormat.OpenAi, VisionRoute.Local)]
    [InlineData(VisionMode.Local, ApiFormat.Deeplx, VisionRoute.Local)]
    [InlineData(VisionMode.Local, ApiFormat.Custom, VisionRoute.Local)]
    [InlineData(VisionMode.Cloud, ApiFormat.OpenAi, VisionRoute.Cloud)]
    [InlineData(VisionMode.Cloud, ApiFormat.Deeplx, VisionRoute.Unsupported)]
    [InlineData(VisionMode.Cloud, ApiFormat.Custom, VisionRoute.Unsupported)]
    public void Decide(VisionMode mode, ApiFormat format, VisionRoute expected)
        => Assert.Equal(expected, VisionRouting.Decide(mode, format));
}

public class VisionModeSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bit-vm-tests-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private SettingsLoadResult LoadJson(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, json);
        return new SettingsStore(File_, new ReversibleProtector()).Load();
    }

    [Fact]
    public void DefaultIsAuto() => Assert.Equal(VisionMode.Auto, new AppSettings().Normalize().VisionMode);

    [Fact]
    public void OldFileWithoutField_UsesAuto_AndKeepsOtherValues()
    {
        var r = LoadJson("{\"schemaVersion\":2,\"model\":\"Index-Translate-2B\"}");
        Assert.False(r.WasMalformed);
        Assert.Equal(VisionMode.Auto, r.Settings.VisionMode);
        Assert.Equal("Index-Translate-2B", r.Settings.Model);
    }

    [Theory]
    [InlineData("\"Local\"", VisionMode.Local)]
    [InlineData("\"cloud\"", VisionMode.Cloud)]
    [InlineData("\"Auto\"", VisionMode.Auto)]
    public void KnownValues_AreRead(string token, VisionMode expected)
        => Assert.Equal(expected, LoadJson("{\"visionMode\":" + token + "}").Settings.VisionMode);

    [Theory]
    [InlineData("\"Quantum\"")]
    [InlineData("\"\"")]
    [InlineData("\"1\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void UnknownValues_FallBackToAuto_WithoutLosingTheFile(string token)
    {
        var r = LoadJson("{\"visionMode\":" + token + ",\"model\":\"Index-Translate-2B\"}");
        Assert.False(r.WasMalformed);
        Assert.Equal(VisionMode.Auto, r.Settings.VisionMode);
        Assert.Equal("Index-Translate-2B", r.Settings.Model);
    }

    [Fact]
    public void Normalize_ResetsUndefinedValue()
    {
        var s = new AppSettings { VisionMode = (VisionMode)99 }.Normalize();
        Assert.Equal(VisionMode.Auto, s.VisionMode);
    }

    [Fact]
    public void RoundTrip_PersistsByName()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        store.Save(new AppSettings { VisionMode = VisionMode.Local });
        Assert.Contains("\"visionMode\": \"Local\"", File.ReadAllText(File_));
        Assert.Equal(VisionMode.Local, store.Load().Settings.VisionMode);
    }
}

public class OcrTextTests
{
    private static string J(params string?[] lines) => OcrText.Join(lines);

    [Fact]
    public void LatinLines_AreJoinedWithSpace() => Assert.Equal("Hello world again", J("Hello", "world", "again"));

    [Fact]
    public void CjkLines_AreJoinedDirectly() => Assert.Equal("今天天气很好我们出去玩", J("今天天气很好", "我们出去玩"));

    [Fact]
    public void JapaneseAndKoreanLines_AreJoinedDirectly()
    {
        Assert.Equal("これはテストです。", J("これは", "テストです。"));
        Assert.Equal("안녕하세요반갑습니다", J("안녕하세요", "반갑습니다"));
    }

    [Fact]
    public void CjkPunctuationAtLineEnd_CountsAsCjk() => Assert.Equal("你好，世界", J("你好，", "世界"));

    [Fact]
    public void CjkNextToLatin_GetsSpace()
    {
        Assert.Equal("使用 Windows 系统", J("使用", "Windows", "系统"));
        Assert.Equal("你好 world", J("你好", "world"));
    }

    [Fact]
    public void SurrogatePairCjk_IsRecognized() => Assert.Equal("\U00020000\U00020001", J("\U00020000", "\U00020001"));

    [Fact]
    public void HyphenatedLatinWord_IsMerged() => Assert.Equal("international", J("inter-", "national"));

    [Fact]
    public void Hyphen_NotBetweenLatinLetters_IsKept()
    {
        Assert.Equal("2024- 2025", J("2024-", "2025"));
        Assert.Equal("well- 2", J("well-", "2"));
        Assert.Equal("你好- world", J("你好-", "world"));
        Assert.Equal("a - b", J("a -", "b"));
    }

    [Fact]
    public void LoneHyphenLine_IsNotMerged() => Assert.Equal("- next", J("-", "next"));

    [Fact]
    public void JoinWords_RemovesSpacesBetweenCjkWordsOnly()
    {
        Assert.Equal("今天天气很好", OcrText.JoinWords(["今天", "天气", "很好"]));
        Assert.Equal("Hello world", OcrText.JoinWords(["Hello", "world"]));
        Assert.Equal("使用 Windows 系统", OcrText.JoinWords(["使用", "Windows", "系统"]));
        Assert.Equal("a b", OcrText.JoinWords(["a", "", " ", "b"]));
    }

    [Fact]
    public void EmptyAndWhitespaceLines_AreSkipped()
    {
        Assert.Equal("one two", J("one", "", "   ", null, "two"));
        Assert.Equal("今天好", J("今天", "", "好"));
    }

    [Fact]
    public void EdgesAreTrimmed_AndResultMayBeEmpty()
    {
        Assert.Equal("a b", J("  a  ", "\tb\t"));
        Assert.Equal(string.Empty, J());
        Assert.Equal(string.Empty, J("", "  "));
    }
}
