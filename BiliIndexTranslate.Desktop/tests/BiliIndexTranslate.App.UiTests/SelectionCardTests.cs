using System.Runtime.CompilerServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// Main flow, end to end against the real app: select text in a window → the card appears and fills in
/// while the answer streams → Esc closes it. Every test starts its own mock service, target window and app.
/// </summary>
public class SelectionCardTests
{
    private static readonly string Full = MockUpstream.FullText;

    [Fact]
    public void Select_shows_streamed_card_and_escape_closes_it()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            ui.SelectAll();
            ui.WaitForCardText(Full);

            var chat = Assert.Single(ui.Mock.ChatRequests);
            Assert.True(chat.Stream, "the card should ask for a streamed answer");
            Assert.Equal("Index-Translate-9B", chat.Model);

            ui.PressEscape();
            Assert.True(ui.WaitForCardGone(), "card still visible 8 s after Esc");
        });
    }

    [Fact]
    public void Card_fills_in_while_streaming()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            ui.SelectAll();
            var seen = ui.WaitForCardText(Full);

            // Segments are 400 ms apart, so a poller every ~25 ms should catch at least one state with part of the text.
            var partial = seen.Where(s => !string.IsNullOrEmpty(s.Text) && s.Text != Full && Full.StartsWith(s.Text!, StringComparison.Ordinal)).ToList();
            Assert.True(partial.Count >= 1,
                "never saw a partial text; states: " + string.Join(" ; ", seen.Select(s => $"'{s.Text}' [{s.Caption}]")));
            Assert.Contains(seen, s => s.Caption?.Contains("streaming", StringComparison.OrdinalIgnoreCase) == true);
        });
    }

    [Fact]
    public void Same_selection_again_is_served_from_the_cache()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            ui.SelectAll();
            ui.WaitForCardText(Full);
            ui.PressEscape();
            Assert.True(ui.WaitForCardGone(), "card still visible after Esc");
            var before = ui.Mock.ChatRequests.Count;
            Assert.Equal(1, before);

            ui.ClearSelection(); // select "elsewhere": a click collapses the selection
            ui.SelectAll();
            var seen = ui.WaitForCardText(Full);

            Assert.All(seen, s => Assert.Equal(Full, s.Text)); // complete from the first frame, no streaming
            Thread.Sleep(1500);
            Assert.Equal(before, ui.Mock.ChatRequests.Count);
        });
    }

    [Fact]
    public void Http_412_shows_an_error_card()
    {
        using var ui = new UiSession(Name(), failWithStatus: 412);
        ui.Guard(() =>
        {
            ui.SelectAll();
            var error = ui.WaitForError();
            Assert.Contains("412", error);
            Assert.NotEmpty(ui.Mock.ChatRequests);
            Assert.Null(ui.FindInApp("SpeakButton")); // collapsed on the error card, so not in the UIA tree
        });
    }

    [Fact]
    public void Speak_button_appears_on_the_final_card_and_logs_one_request()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            ui.SelectAll();
            ui.WaitForCardText(Full);

            var button = Retry.WhileNull(() => ui.FindInApp("SpeakButton"), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100)).Result;
            Assert.NotNull(button);
            button.AsButton().Invoke();

            // Audio may be missing on the runner; the log line is written before the voice is asked, so it must not depend on it.
            var found = Retry.WhileFalse(() => ui.DiagnosticsLog().Contains("speak original (lang ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)).Success;
            Assert.True(found, "no 'speak original' log line within 5 s");
            Assert.Contains("speak original (lang en-US", ui.DiagnosticsLog(), StringComparison.Ordinal);
        });
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
