using BiliIndexTranslate.Core.Speech;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class SpeechLanguageTests
{
    [Theory]
    [InlineData("ja", "ja-JP")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("KO", "ko-KR")]
    [InlineData("es", "es-ES")]
    [InlineData("fr", "fr-FR")]
    [InlineData("de", "de-DE")]
    public void ExplicitSettingWinsOverText(string setting, string expected)
        => Assert.Equal(expected, SpeechLanguage.Guess("The quick brown fox and the dog.", setting));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("klingon")]
    public void AutoOrUnknownSettingDetectsFromText(string? setting)
        => Assert.Equal("ja-JP", SpeechLanguage.Guess("これは日本語の文章です", setting));

    [Fact] public void JapaneseWithKanjiAndKana() => Assert.Equal("ja-JP", SpeechLanguage.Guess("私は東京に住んでいます", "auto"));
    [Fact] public void PureHanIsChinese() => Assert.Equal("zh-CN", SpeechLanguage.Guess("我今天去了图书馆", "auto"));
    [Fact] public void HangulIsKorean() => Assert.Equal("ko-KR", SpeechLanguage.Guess("안녕하세요 만나서 반갑습니다", "auto"));
    [Fact] public void CyrillicIsRussian() => Assert.Equal("ru-RU", SpeechLanguage.Guess("Привет, как дела?", "auto"));
    [Fact] public void GreekThaiHebrewArabicHindi()
    {
        Assert.Equal("el-GR", SpeechLanguage.Guess("Καλημέρα κόσμε", "auto"));
        Assert.Equal("th-TH", SpeechLanguage.Guess("สวัสดีครับ", "auto"));
        Assert.Equal("he-IL", SpeechLanguage.Guess("שלום עולם", "auto"));
        Assert.Equal("ar-SA", SpeechLanguage.Guess("مرحبا بالعالم", "auto"));
        Assert.Equal("hi-IN", SpeechLanguage.Guess("नमस्ते दुनिया", "auto"));
    }

    [Fact] public void SpanishSentence() => Assert.Equal("es-ES", SpeechLanguage.Guess("¿Qué es el amor de la vida que quieres?", "auto"));
    [Fact] public void FrenchSentence() => Assert.Equal("fr-FR", SpeechLanguage.Guess("Je ne sais pas pourquoi les enfants sont dans la maison et du jardin.", "auto"));
    [Fact] public void GermanSentence() => Assert.Equal("de-DE", SpeechLanguage.Guess("Die Straße ist nicht groß und das Haus ist alt.", "auto"));
    [Fact] public void EnglishSentence() => Assert.Equal("en-US", SpeechLanguage.Guess("The quick brown fox jumps over the lazy dog and it is fine.", "auto"));

    [Fact]
    public void KanaWinsInMixedText()
        => Assert.Equal("ja-JP", SpeechLanguage.Guess("The word sushi is written すし in Japanese and the menu is long.", "auto"));

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData("12345 !?")]
    [InlineData("xyzzy")]
    public void EmptyDigitsOrUnknownWordFallBackToEnglish(string text)
        => Assert.Equal("en-US", SpeechLanguage.Guess(text, "auto"));

    [Fact]
    public void AstralCharactersDoNotCrash()
    {
        Assert.Equal("en-US", SpeechLanguage.Guess("😀😀 the cat and the hat 🎉", "auto"));
        Assert.Equal("zh-CN", SpeechLanguage.Guess("𠮷野家 𠀀𠀁𠀂", "auto"));
        Assert.Equal("en-US", SpeechLanguage.Guess("😀", "auto"));
        Assert.Equal("en-US", SpeechLanguage.Guess("\uD83D", "auto")); // lone surrogate
    }
}
