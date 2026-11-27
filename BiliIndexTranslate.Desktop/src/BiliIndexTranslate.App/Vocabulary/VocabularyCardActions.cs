using BiliIndexTranslate.App.Localization;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Vocabulary;

namespace BiliIndexTranslate.App.Vocabulary;

/// <summary>
/// What the two cards' stars do: build the entry from the original text and translation (or the study card) the card window
/// holds, toggle it in the <see cref="VocabularyStore"/> and say what happened in the interface language. Nothing is ever saved
/// except through <see cref="ToggleSentence"/> and <see cref="ToggleWord"/>, which only a click on a star calls.
/// </summary>
internal sealed class VocabularyCardActions
{
    private readonly VocabularyStore _store;
    private readonly Func<AppSettings> _settings;

    public VocabularyCardActions(VocabularyStore store, Func<AppSettings> settings)
    {
        _store = store;
        _settings = settings;
    }

    /// <summary>Raised after the book changed, whoever changed it (a card, the Vocabulary window). May be on any thread.</summary>
    public event Action? Changed
    {
        add => _store.Changed += value;
        remove => _store.Changed -= value;
    }

    private string TargetCode => _settings().ResolvedTargetLanguage.Code;

    public bool IsSentenceSaved(string? source) => !string.IsNullOrWhiteSpace(source) && _store.Contains(VocabularyKind.Sentence, source, TargetCode);

    public bool IsWordSaved(StudyCardItem card) => !string.IsNullOrWhiteSpace(card.Term) && _store.Contains(VocabularyKind.Word, card.Term, TargetCode);

    public VocabularyToggleResult ToggleSentence(string source, string translation)
    {
        var settings = _settings();
        return _store.Toggle(VocabularyEntry.ForSentence(source, translation, settings.SourceLanguage, settings.ResolvedTargetLanguage.Code));
    }

    public VocabularyToggleResult ToggleWord(StudyCardItem card, StudyDeck deck)
        => _store.Toggle(VocabularyEntry.ForStudyCard(card, deck, TargetCode));

    /// <summary>The caption shown after a click on a star.</summary>
    public string Message(VocabularyToggleResult result) => result switch
    {
        VocabularyToggleResult.Added => Loc.T("Vocab_Added"),
        VocabularyToggleResult.Removed => Loc.T("Vocab_Removed"),
        VocabularyToggleResult.ReadOnly => Loc.T(_store.ReadOnlyReason == VocabularyReadOnlyReason.Unreadable ? "Vocab_UnreadableFlash" : "Vocab_ReadOnlyFlash"),
        _ => Loc.T("Vocab_SaveFailed"),
    };
}
