using System.Text;
using System.Text.Json.Nodes;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Vocabulary;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public sealed class TempFolder : IDisposable
{
    public TempFolder() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bit-vocab-" + Guid.NewGuid().ToString("N")[..8]);

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (Exception) { /* best effort */ }
    }
}

public class VocabularyEntryTests
{
    private static StudyCardItem Card() => new(
        "run up", "ran up", "rʌn ʌp", "phrasal verb", "informal", "to accumulate (a bill)", "stronger than 'add'", "a bill that runs up the stairs",
        ["run up a bill", "run up against"], new StudyExample("She ran up a huge bill.", "她欠下了一大笔账单。"));

    [Fact]
    public void ForStudyCard_copies_every_field()
    {
        var deck = new StudyDeck(StudyCardMode.Word, "en-US", "", [Card()]);
        var at = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.FromHours(8));
        var e = VocabularyEntry.ForStudyCard(Card(), deck, "zh-Hans", at);

        Assert.Equal(VocabularyKind.Word, e.Kind);
        Assert.Equal("run up", e.Text);
        Assert.Equal("", e.Translation);
        Assert.Equal("rʌn ʌp", e.Reading);
        Assert.Equal("phrasal verb", e.PartOfSpeech);
        Assert.Equal("informal", e.Register);
        Assert.Equal("to accumulate (a bill)", e.Meaning);
        Assert.Equal("stronger than 'add'", e.Nuance);
        Assert.Equal("a bill that runs up the stairs", e.MemoryAid);
        Assert.Equal(["run up a bill", "run up against"], e.Collocations);
        Assert.Equal("She ran up a huge bill.", e.ExampleSource);
        Assert.Equal("她欠下了一大笔账单。", e.ExampleTarget);
        Assert.Equal("en-US", e.SourceLanguage);
        Assert.Equal("zh-Hans", e.TargetLanguage);
        Assert.Equal(TimeSpan.Zero, e.AddedAt.Offset);
        Assert.Equal(at.ToUniversalTime(), e.AddedAt);
        Assert.Equal(32, e.Id.Length);
        Assert.DoesNotContain('-', e.Id);
    }

    [Fact]
    public void ForStudyCard_without_example_leaves_the_example_empty()
    {
        var card = Card() with { Example = null };
        var e = VocabularyEntry.ForStudyCard(card, new StudyDeck(StudyCardMode.Word, "ja-JP", "", [card]), "en");
        Assert.Equal("", e.ExampleSource);
        Assert.Equal("", e.ExampleTarget);
    }

    [Theory]
    [InlineData("auto", "")]
    [InlineData("AUTO", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("ja", "ja")]
    public void ForSentence_stores_the_source_language_only_when_it_is_explicit(string? setting, string expected)
    {
        var e = VocabularyEntry.ForSentence("  Hello\n", " 你好 ", setting, "zh-Hans");
        Assert.Equal(VocabularyKind.Sentence, e.Kind);
        Assert.Equal("Hello", e.Text);
        Assert.Equal("你好", e.Translation);
        Assert.Equal(expected, e.SourceLanguage);
        Assert.Equal("zh-Hans", e.TargetLanguage);
    }

    [Theory]
    [InlineData("今日は良い天気ですね", "auto", "ja-JP")]
    [InlineData("¿Dónde está la estación de tren?", null, "es-ES")]
    [InlineData("Hello", "auto", "")] // one ambiguous word: no language rather than a guessed en-US
    [InlineData("Hello there, this is a test", "ja", "ja")] // an explicit setting wins over the text
    public void ForSentence_with_auto_stores_the_language_the_text_clearly_shows(string text, string? setting, string expected)
        => Assert.Equal(expected, VocabularyEntry.ForSentence(text, "x", setting, "zh-Hans").SourceLanguage);

    [Fact]
    public void Identity_ignores_case_and_whitespace_but_not_kind_or_target_language()
    {
        var a = VocabularyEntry.ForSentence("Hello   World", "x", null, "zh-Hans");
        var b = VocabularyEntry.ForSentence("  hello\tworld\n", "y", "en", "zh-Hans");
        var otherTarget = VocabularyEntry.ForSentence("Hello World", "x", null, "ja");
        var word = a with { Kind = VocabularyKind.Word };
        Assert.Equal(a.IdentityKey, b.IdentityKey, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(a.IdentityKey, otherTarget.IdentityKey, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(a.IdentityKey, word.IdentityKey, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Matches_searches_text_translation_and_meaning_case_insensitively()
    {
        var sentence = VocabularyEntry.ForSentence("The Quick Fox", "快狐", null, "zh-Hans");
        var word = VocabularyEntry.ForStudyCard(Card(), new StudyDeck(StudyCardMode.Word, "en-US", "", [Card()]), "zh-Hans");
        Assert.True(sentence.Matches("quick"));
        Assert.True(sentence.Matches("快"));
        Assert.True(word.Matches("ACCUMULATE"));
        Assert.True(word.Matches("  "));
        Assert.True(word.Matches(null));
        Assert.False(word.Matches("quick"));
        Assert.False(word.Matches("bill that runs")); // memory aid is not searched
    }
}

public class VocabularyStoreTests
{
    private static VocabularyEntry Sentence(string text, string target = "zh-Hans", DateTimeOffset? at = null)
        => VocabularyEntry.ForSentence(text, "译：" + text, null, target, at);

    private static VocabularyEntry Word(string term)
        => VocabularyEntry.ForStudyCard(
            new StudyCardItem(term, term, "", "noun", "", "meaning of " + term, "", "", ["a b"], new StudyExample("ex", "例")),
            new StudyDeck(StudyCardMode.Word, "en-US", "", []), "zh-Hans");

    [Fact]
    public void Round_trip_keeps_every_field_and_the_order()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        var first = new VocabularyStore(path, null);
        var older = first.Add(Sentence("older", at: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))!;
        var newer = first.Add(Word("newer"))!;
        Assert.Equal([newer.Id, older.Id], first.Entries.Select(e => e.Id).ToArray());

        var second = new VocabularyStore(path, null);
        Assert.False(second.IsReadOnly);
        Assert.Equal(first.Entries.Select(e => e.Id).ToArray(), second.Entries.Select(e => e.Id).ToArray());
        Assert.Equal(first.Entries.Select(e => e.Text).ToArray(), second.Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Round_trip_compares_collections_and_dates()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        var store = new VocabularyStore(path, null);
        var word = store.Add(Word("tree"))!;

        var loaded = Assert.Single(new VocabularyStore(path, null).Entries);
        Assert.Equal(word.Id, loaded.Id);
        Assert.Equal(VocabularyKind.Word, loaded.Kind);
        Assert.Equal(word.Collocations, loaded.Collocations);
        Assert.Equal(word.AddedAt, loaded.AddedAt);
        Assert.Equal("meaning of tree", loaded.Meaning);
        Assert.Equal("ex", loaded.ExampleSource);
    }

    [Fact]
    public void File_is_camel_case_json_with_schema_version_and_string_enums()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        new VocabularyStore(path, null).Add(Sentence("hello"));

        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(1, (int)root["schemaVersion"]!);
        var entry = root["entries"]!.AsArray().Single()!.AsObject();
        Assert.Equal("sentence", (string)entry["kind"]!);
        Assert.Equal("hello", (string)entry["text"]!);
        Assert.NotNull(entry["targetLanguage"]);
        Assert.Contains("\n", text, StringComparison.Ordinal); // indented
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "no byte order mark");
    }

    [Fact]
    public void Missing_file_gives_an_empty_store_and_writes_nothing()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        var store = new VocabularyStore(path, null);
        Assert.Empty(store.Entries);
        Assert.False(store.IsReadOnly);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Malformed_file_is_moved_aside_with_its_bytes_and_the_store_starts_empty()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        Directory.CreateDirectory(dir.Path);
        var original = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"entries\": [ {oops");
        File.WriteAllBytes(path, original);
        var log = new DiagnosticsLog();

        var store = new VocabularyStore(path, log);

        Assert.Empty(store.Entries);
        Assert.False(store.IsReadOnly);
        Assert.False(File.Exists(path));
        var backup = Assert.Single(Directory.GetFiles(dir.Path, "vocabulary.json.bad-*"));
        Assert.Matches(@"vocabulary\.json\.bad-\d{14}$", backup);
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.Equal(Path.GetFileName(backup), store.SetAsideFileName);
        Assert.Equal(VocabularyReadOnlyReason.None, store.ReadOnlyReason);
        Assert.Contains(log.Snapshot(), e => e.Level == DiagnosticLevel.Warning && e.Category == "vocab");
        Assert.NotNull(store.Add(Sentence("works after recovery")));
    }

    [Fact]
    public void A_second_malformed_file_does_not_overwrite_the_first_backup()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        Directory.CreateDirectory(dir.Path);
        var first = Encoding.UTF8.GetBytes("first {");
        var second = Encoding.UTF8.GetBytes("second [");
        File.WriteAllBytes(path, first);
        _ = new VocabularyStore(path, null);
        File.WriteAllBytes(path, second);
        _ = new VocabularyStore(path, null);

        var backups = Directory.GetFiles(dir.Path, "vocabulary.json.bad-*");
        Assert.Equal(2, backups.Length);
        var contents = backups.Select(File.ReadAllBytes).ToList();
        Assert.Contains(contents, c => c.SequenceEqual(first));
        Assert.Contains(contents, c => c.SequenceEqual(second));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("")]
    [InlineData("{\"schemaVersion\": \"one\", \"entries\": []}")]
    [InlineData("{\"schemaVersion\": 1, \"entries\": {}}")]
    [InlineData("{\"schemaVersion\": 1, \"entries\": [ {\"kind\": \"nonsense\", \"text\": \"x\"} ]}")]
    public void Other_unusable_content_is_treated_as_malformed(string content)
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(path, content);
        var store = new VocabularyStore(path, null);
        Assert.Empty(store.Entries);
        Assert.False(store.IsReadOnly);
        Assert.Single(Directory.GetFiles(dir.Path, "vocabulary.json.bad-*"));
    }

    [Fact]
    public void Newer_schema_is_loaded_read_only_and_the_file_is_never_touched()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        Directory.CreateDirectory(dir.Path);
        const string json = """
            {
              "schemaVersion": 2,
              "futureField": { "a": 1 },
              "entries": [
                { "id": "aaa", "kind": "word", "text": "tree", "meaning": "a plant", "targetLanguage": "zh-Hans", "addedAt": "2026-11-01T00:00:00+00:00", "newThing": 5 },
                { "id": "bbb", "kind": "somethingNew", "text": "skipped" },
                { "id": "ccc", "kind": "sentence", "text": "Hello", "translation": "你好", "targetLanguage": "zh-Hans", "addedAt": "2026-11-02T00:00:00+00:00" }
              ]
            }
            """;
        File.WriteAllText(path, json);
        var before = File.ReadAllBytes(path);

        var store = new VocabularyStore(path, null);

        Assert.True(store.IsReadOnly);
        Assert.Equal(VocabularyReadOnlyReason.NewerVersion, store.ReadOnlyReason);
        Assert.Null(store.SetAsideFileName);
        Assert.Equal(["ccc", "aaa"], store.Entries.Select(e => e.Id).ToArray());
        Assert.Null(store.Add(Sentence("new")));
        Assert.False(store.Remove("aaa"));
        Assert.False(store.RemoveMany(["aaa", "ccc"]));
        Assert.False(store.Clear());
        Assert.Equal(VocabularyToggleResult.ReadOnly, store.Toggle(Sentence("new")));
        Assert.Equal(VocabularyToggleResult.ReadOnly, store.Toggle(store.Entries[0]));
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void An_unreadable_file_is_left_alone_and_the_store_is_read_only()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(path, "{ \"schemaVersion\": 1, \"entries\": [] }");
        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (!OperatingSystem.IsWindows()) return; // other systems do not enforce the lock

        var store = new VocabularyStore(path, null);
        Assert.True(store.IsReadOnly);
        Assert.Equal(VocabularyReadOnlyReason.Unreadable, store.ReadOnlyReason);
        Assert.Null(store.Add(Sentence("x")));
    }

    [Fact]
    public void Saving_leaves_no_temp_file_behind()
    {
        using var dir = new TempFolder();
        var path = dir.File("sub" + Path.DirectorySeparatorChar + "vocabulary.json"); // the folder is created on demand
        var store = new VocabularyStore(path, null);
        store.Add(Sentence("a"));
        store.Add(Sentence("b"));
        store.Remove(store.Entries[0].Id);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void A_failed_save_keeps_the_previous_file_and_undoes_the_change()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        var log = new DiagnosticsLog();
        var store = new VocabularyStore(path, log);
        var kept = store.Add(Sentence("kept"))!;
        var before = File.ReadAllBytes(path);

        // A directory in the place of the temp file makes writing it fail on every platform.
        Directory.CreateDirectory(path + ".tmp");
        var changed = 0;
        store.Changed += () => changed++;

        Assert.Null(store.Add(Sentence("lost")));
        Assert.False(store.Remove(kept.Id));
        Assert.False(store.Clear());
        Assert.Equal(VocabularyToggleResult.SaveFailed, store.Toggle(Sentence("lost")));
        Assert.Equal(VocabularyToggleResult.SaveFailed, store.Toggle(kept));

        Assert.Equal(0, changed);
        Assert.Equal([kept.Id], store.Entries.Select(e => e.Id).ToArray());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains(log.Snapshot(), e => e.Level == DiagnosticLevel.Warning && e.Message.Contains("could not be saved", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Snapshot(), e => e.Message.Contains("kept", StringComparison.Ordinal) || e.Message.Contains("lost", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_is_a_no_op_that_returns_the_existing_entry_for_the_same_identity()
    {
        using var dir = new TempFolder();
        var store = new VocabularyStore(dir.File("vocabulary.json"), null);
        var changed = 0;
        store.Changed += () => changed++;

        var first = store.Add(Sentence("Hello   World"))!;
        var again = store.Add(Sentence("  hello world "));
        var otherTarget = store.Add(Sentence("hello world", target: "ja"));

        Assert.Equal(first.Id, again!.Id);
        Assert.NotEqual(first.Id, otherTarget!.Id);
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Find_and_Contains_use_the_identity()
    {
        using var dir = new TempFolder();
        var store = new VocabularyStore(dir.File("vocabulary.json"), null);
        var s = store.Add(Sentence("Hello World"))!;
        Assert.Equal(s.Id, store.Find(VocabularyKind.Sentence, " hello\nWORLD ", "zh-Hans")!.Id);
        Assert.True(store.Contains(VocabularyKind.Sentence, "HELLO WORLD", "zh-Hans"));
        Assert.False(store.Contains(VocabularyKind.Word, "Hello World", "zh-Hans"));
        Assert.False(store.Contains(VocabularyKind.Sentence, "Hello World", "en"));
        Assert.False(store.Contains(VocabularyKind.Sentence, null, "zh-Hans"));
    }

    [Fact]
    public void Toggle_adds_then_removes_and_raises_Changed_each_time()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        var store = new VocabularyStore(path, null);
        var changed = 0;
        store.Changed += () => changed++;

        Assert.Equal(VocabularyToggleResult.Added, store.Toggle(Sentence("Hello")));
        Assert.Single(new VocabularyStore(path, null).Entries);
        Assert.Equal(VocabularyToggleResult.Removed, store.Toggle(Sentence("  HELLO ")));
        Assert.Empty(new VocabularyStore(path, null).Entries);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Remove_RemoveMany_and_Clear()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        var store = new VocabularyStore(path, null);
        var a = store.Add(Sentence("a"))!;
        var b = store.Add(Sentence("b"))!;
        var c = store.Add(Sentence("c"))!;
        var d = store.Add(Word("d"))!;
        var changed = 0;
        store.Changed += () => changed++;

        Assert.False(store.Remove("nope"));
        Assert.True(store.Remove(a.Id));
        Assert.False(store.Remove(a.Id));
        Assert.True(store.RemoveMany([b.Id, "nope"]));
        Assert.False(store.RemoveMany([]));
        Assert.Equal([d.Id, c.Id], store.Entries.Select(e => e.Id).ToArray());
        Assert.Equal(2, changed);

        Assert.True(store.Clear());
        Assert.False(store.Clear());
        Assert.Empty(store.Entries);
        Assert.Equal(3, changed);
        Assert.Empty(new VocabularyStore(path, null).Entries);
        Assert.True(File.Exists(path)); // an empty book is still a valid file
    }

    [Fact]
    public void Entries_is_a_snapshot()
    {
        using var dir = new TempFolder();
        var store = new VocabularyStore(dir.File("vocabulary.json"), null);
        store.Add(Sentence("a"));
        var snapshot = store.Entries;
        store.Add(Sentence("b"));
        Assert.Single(snapshot);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void Logs_carry_counts_and_never_entry_text()
    {
        using var dir = new TempFolder();
        var log = new DiagnosticsLog();
        var store = new VocabularyStore(dir.File("vocabulary.json"), log);
        store.Add(VocabularyEntry.ForSentence("SECRET-SENTENCE", "SECRET-TRANSLATION", null, "zh-Hans"));
        store.Toggle(VocabularyEntry.ForSentence("SECRET-SENTENCE", "SECRET-TRANSLATION", null, "zh-Hans"));
        store.Add(VocabularyEntry.ForSentence("SECRET-OTHER", "x", null, "zh-Hans"));
        store.Clear();
        _ = new VocabularyStore(dir.File("vocabulary.json"), log);

        var entries = log.Snapshot();
        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.Equal("vocab", e.Category);
            Assert.DoesNotContain("SECRET", e.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Duplicate_and_blank_entries_in_the_file_are_left_out_on_load()
    {
        using var dir = new TempFolder();
        var path = dir.File("vocabulary.json");
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(path, """
            { "schemaVersion": 1, "entries": [
              { "id": "1", "kind": "sentence", "text": "Hello", "targetLanguage": "en", "addedAt": "2026-01-01T00:00:00+00:00" },
              { "id": "2", "kind": "sentence", "text": "  hello ", "targetLanguage": "en", "addedAt": "2026-01-02T00:00:00+00:00" },
              { "id": "3", "kind": "sentence", "text": "   ", "targetLanguage": "en" },
              { "id": "4", "kind": "word", "text": "Hello", "targetLanguage": "en", "meaning": null }
            ] }
            """);
        var store = new VocabularyStore(path, null);
        Assert.Equal(["4", "2"], store.Entries.Select(e => e.Id).ToArray()); // newest first; the older duplicate and the blank one are gone
        Assert.Equal("", store.Entries[0].Meaning);
    }
}

public class AnkiExportTests
{
    private static readonly string[] ExpectedHeader =
    [
        "#separator:Tab",
        "#html:true",
        "#columns:Front\tBack\tTags\tDeck\tGUID",
        "#tags column:3",
        "#deck column:4",
        "#guid column:5",
    ];

    private static string[] Lines(string text) => text.Split('\n');

    private static string[] Fields(string row)
    {
        var fields = row.Split('\t');
        Assert.Equal(5, fields.Length);
        Assert.All(fields, f => { Assert.StartsWith("\"", f, StringComparison.Ordinal); Assert.EndsWith("\"", f, StringComparison.Ordinal); });
        return fields.Select(f => f[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public void Header_lines_are_exact_and_an_empty_export_has_only_the_header()
    {
        var text = AnkiExport.ToText([]);
        Assert.Equal(string.Join("\n", ExpectedHeader) + "\n", text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void Sentence_row_has_text_translation_tags_deck_and_guid()
    {
        var e = VocabularyEntry.ForSentence("Hello, world.", "你好，世界。", "en", "zh-Hans");
        var lines = Lines(AnkiExport.ToText([e]));
        Assert.Equal(ExpectedHeader, lines.Take(6).ToArray());
        Assert.Equal(8, lines.Length); // header, one row, trailing newline
        Assert.Equal("", lines[7]);

        var f = Fields(lines[6]);
        Assert.Equal("Hello, world.", f[0]);
        Assert.Equal("你好，世界。", f[1]);
        Assert.Equal("bili-index-translate sentence lang-en", f[2]);
        Assert.Equal("Bili Index Translate", f[3]);
        Assert.Equal("bit-" + e.Id, f[4]);
    }

    [Fact]
    public void Tabs_newlines_carriage_returns_quotes_and_markup_are_escaped()
    {
        var e = VocabularyEntry.ForSentence("a\tb\r\nc\nd \"q\" <b>&amp;</b>", "x\ry", null, "zh-Hans");
        var row = Lines(AnkiExport.ToText([e]))[6];
        Assert.Equal(4, row.Count(c => c == '\t')); // only the field separators
        Assert.DoesNotContain('\r', row);
        var f = Fields(row);
        Assert.Equal("a b<br>c<br>d \"q\" &lt;b&gt;&amp;amp;&lt;/b&gt;", f[0]);
        Assert.Equal("xy", f[1]);
        Assert.Contains("\"a b<br>c<br>d \"\"q\"\" &lt;b&gt;", row, StringComparison.Ordinal); // quotes doubled in the file
    }

    [Fact]
    public void Guid_is_bit_plus_the_id_and_stable_across_exports()
    {
        var e = VocabularyEntry.ForSentence("one", "1", null, "en");
        var first = AnkiExport.ToText([e]);
        var second = AnkiExport.ToText([e]);
        Assert.Equal(first, second);
        Assert.Contains("\"bit-" + e.Id + "\"", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ja-JP", "ja")]
    [InlineData("ja", "ja")]
    [InlineData("zh-Hant", "zh")]
    [InlineData("zh_CN", "zh")]
    [InlineData("es-419", "es")]
    [InlineData("EN-us", "en")]
    [InlineData("Japanese", "japanese")]
    [InlineData("", "")]
    public void Language_tag_keeps_the_primary_subtag_of_a_bcp47_tag(string sourceLanguage, string expected)
        => Assert.Equal(expected, AnkiExport.LanguageTag(sourceLanguage));

    [Fact]
    public void Tags_are_sanitized_and_the_language_tag_is_left_out_when_unknown()
    {
        var word = VocabularyEntry.ForSentence("x", "y", null, "en") with { Kind = VocabularyKind.Word, SourceLanguage = "Zh Hans/CN!_x" };
        var none = VocabularyEntry.ForSentence("x", "y", null, "en");
        var odd = none with { SourceLanguage = "日本語" };
        var rows = Lines(AnkiExport.ToText([word, none, odd])).Skip(6).Where(l => l.Length > 0).Select(Fields).ToArray();
        Assert.Equal("bili-index-translate word lang-zh-hanscn_x", rows[0][2]);
        Assert.Equal("bili-index-translate sentence", rows[1][2]);
        Assert.Equal("bili-index-translate sentence", rows[2][2]); // nothing usable is left of the name
    }

    [Fact]
    public void Word_back_lists_the_parts_in_order_and_skips_empty_ones()
    {
        var e = new VocabularyEntry
        {
            Kind = VocabularyKind.Word,
            Text = "run up",
            Reading = "rʌn ʌp",
            PartOfSpeech = "phrasal verb",
            Register = "informal",
            Meaning = "to <accumulate> a bill",
            Nuance = "stronger\nthan add",
            MemoryAid = "stairs & bills",
            Collocations = ["run up a bill", "run up against"],
            ExampleSource = "She ran up a bill.",
            ExampleTarget = "她欠了账单。",
            SourceLanguage = "en-US",
            TargetLanguage = "zh-Hans",
        };
        var f = Fields(Lines(AnkiExport.ToText([e]))[6]);
        Assert.Equal("run up", f[0]);
        Assert.Equal(
            "rʌn ʌp<br>phrasal verb · informal<br>to &lt;accumulate&gt; a bill<br>stronger<br>than add<br><i>She ran up a bill.<br>她欠了账单。</i><br>stairs &amp; bills<br>run up a bill · run up against",
            f[1]);
        Assert.Equal("bili-index-translate word lang-en", f[2]);

        var sparse = new VocabularyEntry { Kind = VocabularyKind.Word, Text = "tree", PartOfSpeech = "noun", Meaning = "a plant", ExampleTarget = "树" };
        Assert.Equal("noun<br>a plant<br><i>树</i>", Fields(Lines(AnkiExport.ToText([sparse]))[6])[1]);

        var bare = new VocabularyEntry { Kind = VocabularyKind.Word, Text = "x" };
        Assert.Equal("", Fields(Lines(AnkiExport.ToText([bare]))[6])[1]);
    }

    [Fact]
    public void WriteFile_writes_utf8_without_a_byte_order_mark_and_leaves_no_temp_file()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(dir.Path);
        var target = dir.File("anki.txt");
        File.WriteAllText(target, "old");
        AnkiExport.WriteFile(target, [VocabularyEntry.ForSentence("你好", "hello", null, "en")]);

        var bytes = File.ReadAllBytes(target);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "a byte order mark can break the header");
        Assert.StartsWith("#separator:Tab\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("你好", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal([target], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void WriteFile_failure_deletes_the_temp_file_and_throws()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(dir.Path);
        var target = dir.File("blocked.txt");
        Directory.CreateDirectory(target); // a directory in the way: the move fails after the temp file was written
        Assert.ThrowsAny<Exception>(() => AnkiExport.WriteFile(target, [VocabularyEntry.ForSentence("a", "b", null, "en")]));
        Assert.False(File.Exists(target + ".tmp"));
    }
}
