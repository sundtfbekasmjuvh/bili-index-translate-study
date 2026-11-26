using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// The most common real-world case: selecting text on a web page in Microsoft Edge with the mouse. Edge is started
/// without --force-renderer-accessibility. The test itself searches Edge's UIA tree for the paragraph, which also
/// turns on Chromium's accessibility, so this does not show that the app manages that on its own.
/// Skipped (not failed) when Edge is not installed.
/// </summary>
public class EdgeSelectionTests
{
    private readonly ITestOutputHelper _output;

    public EdgeSelectionTests(ITestOutputHelper output) => _output = output;

    [AppFact("edge")]
    public void Edge_page_selection_shows_full_card_and_escape_closes_it()
    {
        using var ui = new UiSession(nameof(Edge_page_selection_shows_full_card_and_escape_closes_it), external: SelectionApps.Edge.Target());
        ui.Guard(() =>
        {
            var full = MockUpstream.FullText;
            var window = ui.TargetWindow;

            // Triple click on the paragraph, as a reader would. Chromium builds its accessibility tree lazily, hence
            // the generous timeout. No keyboard fallback: the test is about the mouse path.
            var how = SelectionApps.Edge.Perform(window, Gesture.TripleClick);
            _output.WriteLine("selection: " + how);
            try
            {
                ui.WaitForCardText(full, TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                var nl = Environment.NewLine;
                _output.WriteLine("no card after the triple click; error card: " + (ui.ErrorText() ?? "none")
                                  + "; chat requests: " + ui.Mock.ChatRequests.Count);
                _output.WriteLine("UIA seen from the test:" + nl + UiaDiagnostics.DescribeFocus(ui.Automation));
                _output.WriteLine("app log:" + nl + ui.DiagnosticsLog());
                throw;
            }
            _output.WriteLine("app log:" + Environment.NewLine + ui.DiagnosticsLog());

            Assert.NotEmpty(ui.Mock.ChatRequests);
            Assert.All(ui.Mock.ChatRequests, r => Assert.True(r.Stream, "the card should ask for a streamed answer"));

            ui.PressEscape();
            Assert.True(ui.WaitForCardGone(), "card still visible 8 s after Esc");
        });
    }
}
