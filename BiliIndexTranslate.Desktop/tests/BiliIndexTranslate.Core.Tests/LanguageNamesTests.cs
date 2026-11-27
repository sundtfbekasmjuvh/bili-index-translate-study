using BiliIndexTranslate.Core.Speech;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class LanguageNamesTests
{
    [Theory]
    [InlineData("ja-JP", "en", "Japanese")]
    [InlineData("ja-JP", "zh-CN", "日语")]
    [InlineData("es-MX", "zh-CN", "西班牙语")]
    [InlineData("zh-TW", "en", "Chinese")]
    [InlineData("EN-us", "zh-CN", "英语")]
    [InlineData("ko_KR", "en", "Korean")]
    public void Known_languages_use_the_app_interface_language(string tag, string ui, string expected)
        => Assert.Equal(expected, LanguageNames.For(tag, ui));

    [Fact]
    public void A_language_outside_the_table_shows_its_tag()
    {
        Assert.Equal("fi-FI", LanguageNames.For("fi-FI", "zh-CN"));
        Assert.Equal("qqq-zz", LanguageNames.For("qqq-zz", "en"));
        Assert.Equal("", LanguageNames.For("  ", "en"));
        Assert.Equal("", LanguageNames.For(null, "en"));
    }
}
