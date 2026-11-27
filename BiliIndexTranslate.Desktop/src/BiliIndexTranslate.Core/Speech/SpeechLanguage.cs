using System.Text;

namespace BiliIndexTranslate.Core.Speech;

/// <summary>
/// Picks the BCP-47 voice language for reading a selected original text aloud.
/// An explicit Source language setting wins; otherwise the language is guessed from the text.
/// Limits: detection is a small deterministic heuristic. Kana always means Japanese (so a Chinese
/// sentence quoting one kana word is read as Japanese), Han without kana means Chinese (Traditional
/// text is read with the zh-CN voice), and Latin-script text is scored with a few very common
/// function words per language, so single words and very short phrases are often ambiguous and fall
/// back to en-US.
/// </summary>
public static class SpeechLanguage
{
    public const string Fallback = "en-US";

    private static readonly Dictionary<string, string> Explicit = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en-US", ["en-us"] = "en-US", ["en-gb"] = "en-GB",
        ["ja"] = "ja-JP", ["jp"] = "ja-JP", ["ja-jp"] = "ja-JP",
        ["zh"] = "zh-CN", ["zh-cn"] = "zh-CN", ["zh-hans"] = "zh-CN",
        ["zh-hant"] = "zh-TW", ["zh-tw"] = "zh-TW", ["zh-hk"] = "zh-HK",
        ["ko"] = "ko-KR", ["ko-kr"] = "ko-KR",
        ["es"] = "es-ES", ["fr"] = "fr-FR", ["de"] = "de-DE", ["it"] = "it-IT",
        ["pt"] = "pt-PT", ["pt-br"] = "pt-BR", ["ru"] = "ru-RU", ["el"] = "el-GR",
        ["ar"] = "ar-SA", ["he"] = "he-IL", ["th"] = "th-TH", ["hi"] = "hi-IN",
        ["nl"] = "nl-NL", ["pl"] = "pl-PL", ["tr"] = "tr-TR", ["sv"] = "sv-SE", ["uk"] = "uk-UA",
    };

    private static readonly (string Tag, string[] Words, string Chars)[] LatinProfiles =
    {
        ("en-US", new[] { "the", "and", "of", "to", "is", "in", "that", "it", "for", "with", "are", "this", "you", "was", "not" }, ""),
        ("es-ES", new[] { "el", "la", "los", "las", "de", "que", "y", "en", "un", "una", "es", "por", "con", "para", "se", "no" }, "ñ¿¡"),
        ("fr-FR", new[] { "le", "la", "les", "des", "du", "et", "est", "un", "une", "que", "pour", "dans", "pas", "je", "nous", "vous" }, "çœèêàù"),
        ("de-DE", new[] { "der", "die", "das", "und", "ist", "nicht", "ein", "eine", "zu", "mit", "den", "von", "ich", "auf", "sie" }, "ßäöü"),
        ("it-IT", new[] { "il", "lo", "gli", "di", "che", "e", "è", "un", "una", "per", "con", "non", "sono", "della", "questo" }, ""),
        ("pt-PT", new[] { "o", "os", "as", "de", "que", "e", "em", "um", "uma", "para", "com", "não", "do", "da", "no", "na" }, "ãõ"),
    };

    public static string Guess(string text, string? sourceLanguageSetting)
    {
        var setting = (sourceLanguageSetting ?? string.Empty).Trim();
        if (setting.Length > 0
            && !setting.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && Explicit.TryGetValue(setting.Replace('_', '-'), out var mapped))
            return mapped;

        return Detect(text);
    }

    private static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fallback;

        int kana = 0, hangul = 0, han = 0, cyrillic = 0, greek = 0, arabic = 0, hebrew = 0, thai = 0, devanagari = 0, latin = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(r)) continue;
            var v = r.Value;
            if ((v >= 0x3040 && v <= 0x30FF) || (v >= 0x31F0 && v <= 0x31FF) || (v >= 0xFF66 && v <= 0xFF9F)) kana++;
            else if ((v >= 0x1100 && v <= 0x11FF) || (v >= 0x3130 && v <= 0x318F) || (v >= 0xAC00 && v <= 0xD7AF)) hangul++;
            else if ((v >= 0x2E80 && v <= 0x2FDF) || (v >= 0x3400 && v <= 0x4DBF) || (v >= 0x4E00 && v <= 0x9FFF)
                     || (v >= 0xF900 && v <= 0xFAFF) || (v >= 0x20000 && v <= 0x2FA1F) || (v >= 0x30000 && v <= 0x3134F)) han++;
            else if (v >= 0x0400 && v <= 0x052F) cyrillic++;
            else if ((v >= 0x0370 && v <= 0x03FF) || (v >= 0x1F00 && v <= 0x1FFF)) greek++;
            else if ((v >= 0x0600 && v <= 0x06FF) || (v >= 0x0750 && v <= 0x077F)) arabic++;
            else if (v >= 0x0590 && v <= 0x05FF) hebrew++;
            else if (v >= 0x0E00 && v <= 0x0E7F) thai++;
            else if (v >= 0x0900 && v <= 0x097F) devanagari++;
            else if (v <= 0x024F || (v >= 0x1E00 && v <= 0x1EFF)) latin++;
        }

        if (kana > 0) return "ja-JP";

        var best = Math.Max(Math.Max(Math.Max(hangul, han), Math.Max(cyrillic, greek)),
            Math.Max(Math.Max(arabic, hebrew), Math.Max(Math.Max(thai, devanagari), latin)));
        if (best == 0) return Fallback;

        if (hangul == best) return "ko-KR";
        if (han == best) return "zh-CN";
        if (cyrillic == best) return "ru-RU";
        if (greek == best) return "el-GR";
        if (arabic == best) return "ar-SA";
        if (hebrew == best) return "he-IL";
        if (thai == best) return "th-TH";
        if (devanagari == best) return "hi-IN";
        return GuessLatin(text);
    }

    private static string GuessLatin(string text)
    {
        var lower = text.ToLowerInvariant();
        var words = new Dictionary<string, int>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            var w = sb.ToString();
            words[w] = words.GetValueOrDefault(w) + 1;
            sb.Clear();
        }
        foreach (var r in lower.EnumerateRunes())
        {
            if (Rune.IsLetter(r)) sb.Append(r.ToString());
            else Flush();
        }
        Flush();

        var bestTag = Fallback;
        var bestScore = 0;
        var tie = false;
        foreach (var (tag, list, chars) in LatinProfiles)
        {
            var score = 0;
            foreach (var w in list)
                if (words.TryGetValue(w, out var n)) score += n;
            foreach (var c in lower)
                if (chars.Contains(c, StringComparison.Ordinal)) score += 2;

            if (score > bestScore) { bestScore = score; bestTag = tag; tie = false; }
            else if (score == bestScore && score > 0) tie = true;
        }
        return tie || bestScore == 0 ? Fallback : bestTag;
    }
}
