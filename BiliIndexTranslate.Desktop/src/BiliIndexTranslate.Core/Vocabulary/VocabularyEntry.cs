using System.Text;
using System.Text.Json.Serialization;
using BiliIndexTranslate.Core.Study;

namespace BiliIndexTranslate.Core.Vocabulary;

public enum VocabularyKind
{
    Sentence,
    Word,
}

/// <summary>
/// One saved item of the vocabulary book: a sentence (original + translation) saved from the selection card, or a word
/// (a study card) saved from the Study card. Entries exist only because the user clicked a star.
/// </summary>
public sealed record VocabularyEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public VocabularyKind Kind { get; init; }

    /// <summary>The original sentence, or the term of a word.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The translation of a sentence; empty for words.</summary>
    public string Translation { get; init; } = string.Empty;
    public string Reading { get; init; } = string.Empty;
    public string PartOfSpeech { get; init; } = string.Empty;
    public string Register { get; init; } = string.Empty;
    public string Meaning { get; init; } = string.Empty;
    public string Nuance { get; init; } = string.Empty;
    public string MemoryAid { get; init; } = string.Empty;
    public IReadOnlyList<string> Collocations { get; init; } = [];
    public string ExampleSource { get; init; } = string.Empty;
    public string ExampleTarget { get; init; } = string.Empty;

    /// <summary>Words: the study deck's source language as given. Sentences: the app's source language setting when it is explicit, otherwise empty.</summary>
    public string SourceLanguage { get; init; } = string.Empty;

    /// <summary>The app's target language code (for example <c>zh-Hans</c>).</summary>
    public string TargetLanguage { get; init; } = string.Empty;

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>What makes two entries "the same item": kind, normalized text and target language.</summary>
    [JsonIgnore]
    public string IdentityKey => MakeIdentityKey(Kind, Text, TargetLanguage);

    /// <summary>The key is compared with <see cref="StringComparer.OrdinalIgnoreCase"/>.</summary>
    public static string MakeIdentityKey(VocabularyKind kind, string? text, string? targetLanguage)
        => string.Concat(kind == VocabularyKind.Word ? "W" : "S", "\u0001", NormalizeText(text), "\u0001", (targetLanguage ?? string.Empty).Trim());

    /// <summary>Trims and collapses every run of whitespace to one space.</summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Case-insensitive search in the text, the translation and the meaning. An empty query matches everything.</summary>
    public bool Matches(string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return Text.Contains(q, StringComparison.OrdinalIgnoreCase)
               || Translation.Contains(q, StringComparison.OrdinalIgnoreCase)
               || Meaning.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="sourceLanguageSetting">
    /// The app's source language setting. An explicit value is stored as it is; with "auto" or blank the language the
    /// text clearly shows is stored (<see cref="Speech.SpeechLanguage.TryDetect"/>), or empty when it shows none.
    /// </param>
    public static VocabularyEntry ForSentence(string text, string translation, string? sourceLanguageSetting, string targetLanguage, DateTimeOffset? addedAt = null)
        => new()
        {
            Kind = VocabularyKind.Sentence,
            Text = text.Trim(),
            Translation = translation.Trim(),
            SourceLanguage = IsExplicitLanguage(sourceLanguageSetting)
                ? sourceLanguageSetting!.Trim()
                : Speech.SpeechLanguage.TryDetect(text) ?? string.Empty,
            TargetLanguage = targetLanguage,
            AddedAt = (addedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        };

    public static VocabularyEntry ForStudyCard(StudyCardItem card, StudyDeck deck, string targetLanguage, DateTimeOffset? addedAt = null)
        => new()
        {
            Kind = VocabularyKind.Word,
            Text = card.Term.Trim(),
            Reading = card.Reading ?? string.Empty,
            PartOfSpeech = card.PartOfSpeech ?? string.Empty,
            Register = card.Register ?? string.Empty,
            Meaning = card.Meaning ?? string.Empty,
            Nuance = card.Nuance ?? string.Empty,
            MemoryAid = card.MemoryAid ?? string.Empty,
            Collocations = (card.Collocations ?? []).ToArray(),
            ExampleSource = card.Example?.Source ?? string.Empty,
            ExampleTarget = card.Example?.Target ?? string.Empty,
            SourceLanguage = deck.SourceLanguage ?? string.Empty,
            TargetLanguage = targetLanguage,
            AddedAt = (addedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        };

    private static bool IsExplicitLanguage(string? setting)
        => !string.IsNullOrWhiteSpace(setting) && !string.Equals(setting.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
}
