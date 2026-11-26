using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// The most common real-world case: selecting text on a web page in Microsoft Edge. Edge is started without
/// --force-renderer-accessibility, so this also checks that the app gets Chromium to expose its text selection.
/// Skipped (not failed) when Edge is not installed.
/// </summary>
public class EdgeSelectionTests
{
    private readonly ITestOutputHelper _output;

    public EdgeSelectionTests(ITestOutputHelper output) => _output = output;

    [EdgeFact]
    public void Edge_page_selection_shows_full_card_and_escape_closes_it()
    {
        using var ui = new UiSession(nameof(Edge_page_selection_shows_full_card_and_escape_closes_it), external: EdgeTarget.Target());
        ui.Guard(() =>
        {
            var full = MockUpstream.FullText;
            var window = ui.TargetWindow;

            // Primary: triple click on the paragraph. Chromium's accessibility tree starts out empty and the first
            // selection may arrive before the app's UIA queries have woken it up, hence the generous timeout.
            var how = EdgeTarget.SelectByTripleClick(window);
            _output.WriteLine("selection: " + how);
            try
            {
                ui.WaitForCardText(full, TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException ex)
            {
                _output.WriteLine("triple click gave no card (" + ex.Message + "); falling back to Ctrl+A");
                how = EdgeTarget.SelectByCtrlA(window);
                _output.WriteLine("selection: " + how);
                ui.WaitForCardText(full);
            }

            Assert.NotEmpty(ui.Mock.ChatRequests);
            Assert.All(ui.Mock.ChatRequests, r => Assert.True(r.Stream, "the card should ask for a streamed answer"));

            ui.PressEscape();
            Assert.True(ui.WaitForCardGone(), "card still visible 8 s after Esc");
        });
    }
}
