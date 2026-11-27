using System.Text;

namespace BiliIndexTranslate.Core.Vocabulary;

/// <summary>
/// Anki text-file export (Anki 2.1.54 and later, https://docs.ankiweb.net/importing/text-files.html): file headers, then one
/// tab-separated row per entry with five double-quoted fields: Front, Back, Tags, Deck, GUID. The GUID is <c>bit-</c> plus the entry id,
/// so importing the file again updates the same notes instead of adding duplicates. The file is UTF-8 without a byte order mark
/// (a mark in front of <c>#separator</c> can break the header) with <c>\n</c> line endings.
/// </summary>
public static class AnkiExport
{
    public const string DeckName = "Bili Index Translate";
    public const string GuidPrefix = "bit-";

    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] Header =
    [
        "#separator:Tab",
        "#html:true",
        "#columns:Front\tBack\tTags\tDeck\tGUID",
        "#tags column:3",
        "#deck column:4",
        "#guid column:5",
    ];

    public static void Write(TextWriter writer, IEnumerable<VocabularyEntry> entries)
    {
        foreach (var line in Header) writer.Write(line + "\n");
        foreach (var entry in entries)
        {
            var (front, back) = entry.Kind == VocabularyKind.Word ? WordFields(entry) : SentenceFields(entry);
            writer.Write(string.Join('\t', Quote(front), Quote(back), Quote(Tags(entry)), Quote(DeckName), Quote(GuidPrefix + entry.Id)) + "\n");
        }
    }

    public static string ToText(IEnumerable<VocabularyEntry> entries)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture) { NewLine = "\n" };
        Write(writer, entries);
        return writer.ToString();
    }

    /// <summary>Writes <c>&lt;target&gt;.tmp</c> and moves it over the target; the temporary file is deleted when anything fails.</summary>
    public static void WriteFile(string target, IEnumerable<VocabularyEntry> entries)
    {
        var text = ToText(entries);
        var tmp = target + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception)
        {
            try { File.Delete(tmp); } catch (Exception) { /* best effort */ }
            throw;
        }
    }

    // ---- fields ------------------------------------------------------------------------------

    private static (string Front, string Back) SentenceFields(VocabularyEntry e) => (Html(e.Text), Html(e.Translation));

    private static (string Front, string Back) WordFields(VocabularyEntry e)
    {
        var lines = new List<string>();
        AddIfAny(lines, Html(e.Reading));
        AddIfAny(lines, string.Join(" · ", new[] { e.PartOfSpeech, e.Register }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Html)));
        AddIfAny(lines, Html(e.Meaning));
        AddIfAny(lines, Html(e.Nuance));
        var example = new[] { e.ExampleSource, e.ExampleTarget }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Html).ToArray();
        if (example.Length > 0) lines.Add("<i>" + string.Join("<br>", example) + "</i>");
        AddIfAny(lines, Html(e.MemoryAid));
        AddIfAny(lines, string.Join(" · ", e.Collocations.Where(s => !string.IsNullOrWhiteSpace(s)).Select(Html)));
        return (Html(e.Text), string.Join("<br>", lines));
    }

    private static void AddIfAny(List<string> lines, string html)
    {
        if (html.Length > 0) lines.Add(html);
    }

    private static string Tags(VocabularyEntry e)
    {
        var tags = new List<string> { "bili-index-translate", e.Kind == VocabularyKind.Word ? "word" : "sentence" };
        var lang = LanguageTag(e.SourceLanguage);
        if (lang.Length > 0) tags.Add("lang-" + lang);
        return string.Join(' ', tags);
    }

    /// <summary>
    /// The language part of the tag. A BCP-47 tag keeps only its primary subtag (<c>ja-JP</c>, <c>ja</c> → <c>ja</c>;
    /// <c>zh-Hant</c> → <c>zh</c>), so sentences and words of one language share a tag whichever region or script the
    /// detection or the model gave. Anything else goes through <see cref="TagPart"/>.
    /// </summary>
    internal static string LanguageTag(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (Bcp47.IsMatch(v)) return v.Split('-', '_')[0].ToLowerInvariant();
        return TagPart(v);
    }

    private static readonly System.Text.RegularExpressions.Regex Bcp47 =
        new(@"^[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{1,8})*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Lowercase, spaces to hyphens, only <c>[a-z0-9_-]</c> kept.</summary>
    internal static string TagPart(string? value)
    {
        var sb = new StringBuilder();
        foreach (var ch in (value ?? string.Empty).Trim().ToLowerInvariant())
        {
            var c = ch == ' ' ? '-' : ch;
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>HTML text of a field: <c>&amp; &lt; &gt;</c> escaped, CR removed, LF as <c>&lt;br&gt;</c>, TAB as a space.</summary>
    internal static string Html(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\r': break;
                case '\n': sb.Append("<br>"); break;
                case '\t': sb.Append(' '); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string Quote(string field) => "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
