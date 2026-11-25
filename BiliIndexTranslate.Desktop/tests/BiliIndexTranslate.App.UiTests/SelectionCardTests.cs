using System.Runtime.CompilerServices;
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
        });
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
