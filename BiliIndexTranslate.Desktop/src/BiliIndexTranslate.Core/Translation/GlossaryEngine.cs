using System.Text.RegularExpressions;

namespace BiliIndexTranslate.Core.Translation;

public readonly record struct GlossaryEntry(string Source, string Target);

/// <summary>
/// Dynamic hit-only glossary &amp; immutable term lock engine aligned with Bilibili Index-Translate's
/// official <c>instTrans</c> constrained translation template.
/// </summary>
public static partial class GlossaryEngine
{
    [GeneratedRegex(@"^(.*?)\s*(?:=>|->|→|\t|=)\s*(.+)$")]
    private static partial Regex ArrowSeparatorRegex();

    [GeneratedRegex(@"`[^`\r\n]{1,120}`")]
    private static partial Regex InlineBacktickCodeRegex();

    public static IReadOnlyList<GlossaryEntry> Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return Array.Empty<GlossaryEntry>();

        var entries = new List<GlossaryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in rawText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            string src;
            string tgt;
            var m = ArrowSeparatorRegex().Match(line);
            if (m.Success)
            {
                src = m.Groups[1].Value.Trim();
                tgt = m.Groups[2].Value.Trim();
            }
            else
            {
                // Single term on a line = immutable no-translate lock (src -> src)
                src = line;
                tgt = line;
            }

            if (src.Length == 0 || tgt.Length == 0) continue;
            if (!seen.Add(src)) continue;
            entries.Add(new GlossaryEntry(src, tgt));
        }

        // Match longer multi-word terms before shorter substrings
        entries.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));
        return entries;
    }

    public static IReadOnlyList<GlossaryEntry> MatchHits(string? rawGlossary, string? sourceText, int maxHits = 24)
    {
        if (string.IsNullOrWhiteSpace(rawGlossary) || string.IsNullOrWhiteSpace(sourceText))
            return Array.Empty<GlossaryEntry>();

        var parsed = Parse(rawGlossary);
        if (parsed.Count == 0) return Array.Empty<GlossaryEntry>();

        var hits = new List<GlossaryEntry>();
        foreach (var entry in parsed)
        {
            if (sourceText.Contains(entry.Source, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(entry);
                if (hits.Count >= maxHits) break;
            }
        }
        return hits;
    }

    public static string Signature(IReadOnlyList<GlossaryEntry> hits)
        => hits.Count == 0 ? string.Empty : string.Join(';', hits.Select(h => $"{h.Source}->{h.Target}"));

    public static bool HasInlineBacktickCode(string? text)
        => !string.IsNullOrEmpty(text) && InlineBacktickCodeRegex().IsMatch(text);
}
