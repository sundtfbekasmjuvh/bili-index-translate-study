using BiliIndexTranslate.Core.Speech;
using BiliIndexTranslate.App.Localization;

namespace BiliIndexTranslate.App.Popup;

/// <summary>Localized card messages for a speech result that did not start.</summary>
internal static class SpeechMessages
{
    /// <summary>"No Japanese voice is installed ..." with the language name in the app's interface language (see <see cref="LanguageNames"/>).</summary>
    public static string NoVoice(string? bcp47)
    {
        var tag = string.IsNullOrWhiteSpace(bcp47) ? "en-US" : bcp47.Trim();
        return Loc.T("Speech_NoVoiceForLanguage", LanguageNames.For(tag, Loc.Instance.Language));
    }

    /// <summary>Message for a result other than <see cref="SpeakResult.Started"/>: no voice names the language, a failure says speech is unavailable.</summary>
    public static string For(SpeakResult result, string? bcp47) =>
        result == SpeakResult.NoVoice ? NoVoice(bcp47) : Loc.T("Study_NoVoice");
}
