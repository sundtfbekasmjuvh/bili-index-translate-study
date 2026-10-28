using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.Core.Study;

public enum StudyCardMode
{
    Word,
    Sentence,
}

public sealed record StudyExample(string Source, string Target);

public sealed record StudyCardItem(
    string Term,
    string Surface,
    string Reading,
    string PartOfSpeech,
    string Register,
    string Meaning,
    string Nuance,
    string MemoryAid,
    IReadOnlyList<string> Collocations,
    StudyExample? Example);

public sealed record StudyDeck(
    StudyCardMode Mode,
    string SourceLanguage,
    string GrammarNote,
    IReadOnlyList<StudyCardItem> Cards,
    bool IsStreaming = false);

public sealed record StudyDeckResult(
    StudyDeck Deck,
    RequestRoute Route,
    double ElapsedMs,
    string Model,
    bool IsCached = false);
