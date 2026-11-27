using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Vocabulary;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// The vocabulary book end to end: the star on the selection card writes and removes an entry in vocabulary.json; the Vocabulary
/// window (opened with <c>--vocabulary</c>) lists the entries, exports them to an Anki text file through the Save dialog and deletes
/// the selected one. English UI (see <see cref="UiSession"/>).
/// </summary>
public class VocabularyTests
{
    private const string AddName = "Add to vocabulary";
    private const string RemoveName = "Remove from vocabulary";

    private readonly ITestOutputHelper _output;

    public VocabularyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Star_on_the_selection_card_adds_the_sentence_and_a_second_click_removes_it()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            var targetText = TargetText(ui);
            ui.SelectAll();
            ui.WaitForCardText(MockUpstream.FullText);

            var star = UiWait.WaitFor(() => ui.FindInApp("SaveWordButton"), "SaveWordButton (shown once the translation is complete)");
            Assert.Equal(AddName, star.Name);
            Assert.False(File.Exists(Path.Combine(ui.DataDir, "vocabulary.json")), "nothing may be saved before the star is clicked");
            _output.WriteLine("step 1: the card shows the full text and an empty star");

            star.AsButton().Invoke();
            var entries = WaitForEntries(ui.DataDir, 1);
            var entry = entries[0]!.AsObject();
            _output.WriteLine($"step 2: one entry saved, caption '{ui.CaptionText()}'");
            Assert.Equal("sentence", (string?)entry["kind"]);
            Assert.Equal(targetText.Trim(), (string?)entry["text"]);
            Assert.Equal(MockUpstream.FullText, (string?)entry["translation"]);
            Assert.Equal("en-US", (string?)entry["sourceLanguage"]); // the setting is "auto": detected from the English text
            Assert.False(string.IsNullOrEmpty((string?)entry["targetLanguage"]));
            Assert.False(string.IsNullOrEmpty((string?)entry["id"]));
            UiWait.WaitUntil(() => ui.FindInApp("SaveWordButton")?.Name == RemoveName, "the star showing the remove state");

            ui.FindInApp("SaveWordButton")!.AsButton().Invoke();
            WaitForEntries(ui.DataDir, 0);
            UiWait.WaitUntil(() => ui.FindInApp("SaveWordButton")?.Name == AddName, "the star showing the add state again");
            _output.WriteLine("step 3: the second click removed the entry");
        });
    }

    [Fact]
    public void Vocabulary_window_lists_exports_to_anki_and_deletes_the_selected_entry()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "bit-vocab-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        const string sentenceText = "Good morning, everyone.";
        const string wordTerm = "run up";
        try
        {
            using var ui = new UiSession(Name(), appArgs: ["--vocabulary"], seedDataDir: dir =>
            {
                var store = new VocabularyStore(Path.Combine(dir, "vocabulary.json"), null);
                var card = new StudyCardItem(wordTerm, wordTerm, "rʌn ʌp", "phrasal verb", "informal", "to accumulate", "", "",
                    ["run up a bill"], new StudyExample("She ran up a bill.", "她欠了账单。"));
                store.Add(VocabularyEntry.ForStudyCard(card, new StudyDeck(StudyCardMode.Word, "en-US", "", [card]), "zh-Hans"));
                store.Add(VocabularyEntry.ForSentence(sentenceText, "大家早上好。", null, "zh-Hans"));
            });
            ui.Guard(() =>
            {
                var list = UiWait.WaitFor(() => ui.FindInApp("EntryList"), "EntryList (Vocabulary window opened by --vocabulary)");
                var cf = ui.Automation.ConditionFactory;
                UiWait.WaitUntil(() => list.FindAllDescendants(cf.ByControlType(ControlType.ListItem)).Length == 2, "two entries in the list");
                _output.WriteLine("step 1: the Vocabulary window lists 2 entries");

                var target = Path.Combine(outDir, "anki-test.txt");
                UiWait.WaitFor(() => ui.FindInApp("ExportButton"), "ExportButton").AsButton().Invoke();
                _output.WriteLine("step 2: invoked Export to Anki");
                SaveDialog.SaveAs(ui, target, _output);

                var status = UiWait.WaitFor(() =>
                {
                    var e = ui.FindInApp("StatusText");
                    return e is not null && !string.IsNullOrEmpty(e.Name) ? e : null;
                }, "StatusText");
                _output.WriteLine("step 6: status '" + status.Name + "'");
                Assert.Equal("Exported 2 entries to anki-test.txt", status.Name);

                Assert.True(File.Exists(target), "the export was not saved under the typed path");
                Assert.False(File.Exists(target + ".tmp"), "temporary file left behind");
                var bytes = File.ReadAllBytes(target);
                Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "the file must not start with a byte order mark");
                var text = new UTF8Encoding(false).GetString(bytes);
                Assert.StartsWith("#separator:Tab\n", text, StringComparison.Ordinal);
                var rows = text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
                Assert.Equal(2, rows.Length);
                Assert.All(rows, r => Assert.Contains("\"bit-", r, StringComparison.Ordinal));
                _output.WriteLine("step 7: the Anki file has the headers and 2 rows with bit- GUIDs");

                var sentence = UiWait.WaitFor(() => list.FindAllDescendants(cf.ByControlType(ControlType.ListItem)).FirstOrDefault(i => i.Name == sentenceText),
                    "the sentence in the list");
                sentence.Patterns.SelectionItem.Pattern.Select();
                var delete = UiWait.WaitFor(() => ui.FindInApp("DeleteButton"), "DeleteButton");
                UiWait.WaitUntil(() => delete.IsEnabled, "DeleteButton enabled by the selection");
                delete.AsButton().Invoke();
                var remaining = WaitForEntries(ui.DataDir, 1)[0]!.AsObject();
                Assert.Equal("word", (string?)remaining["kind"]);
                Assert.Equal(wordTerm, (string?)remaining["text"]);
                UiWait.WaitUntil(() => list.FindAllDescendants(cf.ByControlType(ControlType.ListItem)).Length == 1, "one entry left in the list");
                _output.WriteLine("step 8: the selected entry was deleted from the file and the list");
            });
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string TargetText(UiSession ui)
    {
        var box = ui.TargetWindow.FindFirstDescendant(ui.Automation.ConditionFactory.ByAutomationId("SourceText"))
                  ?? throw new InvalidOperationException("SourceText not found in the target window");
        return box.Patterns.Value.Pattern.Value.Value;
    }

    /// <summary>Waits until vocabulary.json holds <paramref name="count"/> entries and returns them (the file is replaced atomically, so a read can briefly fail).</summary>
    private static JsonArray WaitForEntries(string dataDir, int count)
    {
        var path = Path.Combine(dataDir, "vocabulary.json");
        JsonArray? found = null;
        UiWait.WaitUntil(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            var entries = root["entries"]!.AsArray();
            if (entries.Count != count) return false;
            found = entries;
            return true;
        }, $"vocabulary.json with {count} entries");
        return found!;
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
