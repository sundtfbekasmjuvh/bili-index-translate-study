using System.Text;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>Joins the lines of an OCR result into text that reads as running prose.</summary>
public static class OcrText
{
    public static string Join(IEnumerable<string?> lines)
    {
        var sb = new StringBuilder();
        string? prev = null;
        foreach (var raw in lines)
        {
            var line = (raw ?? string.Empty).Trim();
            if (line.Length == 0) continue;

            if (prev is null)
            {
                sb.Append(line);
            }
            else if (EndsWithHyphen(prev) && IsLatinLetterAt(prev, prev.Length - 2) && IsLatinLetterAt(line, 0))
            {
                sb.Length -= 1; // drop the hyphen
                sb.Append(line);
            }
            else if (IsCjk(LastRune(prev)) && IsCjk(FirstRune(line)))
            {
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line);
            }
            prev = line;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Joins the words of one OCR line. The Windows engine reports Chinese and Japanese as separate words, so
    /// two CJK neighbours are joined without a space and everything else with one.
    /// </summary>
    public static string JoinWords(IEnumerable<string?> words)
    {
        var sb = new StringBuilder();
        string? prev = null;
        foreach (var raw in words)
        {
            var word = (raw ?? string.Empty).Trim();
            if (word.Length == 0) continue;
            if (prev is not null && !(IsCjk(LastRune(prev)) && IsCjk(FirstRune(word)))) sb.Append(' ');
            sb.Append(word);
            prev = word;
        }
        return sb.ToString();
    }

    private static bool EndsWithHyphen(string s) => s.Length >= 2 && s[^1] == '-';

    private static bool IsLatinLetterAt(string s, int index)
    {
        if (index < 0 || index >= s.Length) return false;
        var c = s[index];
        return char.IsLetter(c) && (c <= 'ɏ' || (c >= 'Ḁ' && c <= 'ỿ'));
    }

    private static Rune FirstRune(string s) => Rune.GetRuneAt(s, 0);

    private static Rune LastRune(string s)
    {
        var i = s.Length - 1;
        if (i > 0 && char.IsLowSurrogate(s[i]) && char.IsHighSurrogate(s[i - 1])) i--;
        return Rune.GetRuneAt(s, i);
    }

    /// <summary>Han, kana, Hangul, CJK symbols and punctuation, and full-width forms.</summary>
    public static bool IsCjk(Rune r)
    {
        var v = r.Value;
        return (v >= 0x1100 && v <= 0x11FF)
            || (v >= 0x2E80 && v <= 0x2FDF)
            || (v >= 0x3000 && v <= 0x303F)
            || (v >= 0x3040 && v <= 0x30FF)
            || (v >= 0x3130 && v <= 0x318F)
            || (v >= 0x31F0 && v <= 0x31FF)
            || (v >= 0x3400 && v <= 0x4DBF)
            || (v >= 0x4E00 && v <= 0x9FFF)
            || (v >= 0xAC00 && v <= 0xD7AF)
            || (v >= 0xF900 && v <= 0xFAFF)
            || (v >= 0xFF00 && v <= 0xFFEF)
            || (v >= 0x20000 && v <= 0x2FA1F)
            || (v >= 0x30000 && v <= 0x3134F);
    }
}
