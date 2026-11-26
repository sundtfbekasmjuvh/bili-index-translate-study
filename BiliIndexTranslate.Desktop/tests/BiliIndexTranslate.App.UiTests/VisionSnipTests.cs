using System.Drawing;
using System.Runtime.CompilerServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// Characterization test of the screen snip to vision translation to card path, end to end against the mock upstream:
/// Alt+Shift+S opens the snip overlay (a frozen screenshot), a dragged rectangle over the target's text box is the
/// snip, the app sends one chat request with an image part (OpenAI format, VisionMode Auto = cloud vision), and the
/// card shows the translation of the mock's dual-output answer. Esc closes the card.
/// </summary>
public class VisionSnipTests
{
    private static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(10);
    private const int SnipWidth = 300;
    private const int SnipHeight = 120;

    private readonly ITestOutputHelper _output;

    public VisionSnipTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Snip_drag_translates_through_vision_and_shows_the_card()
    {
        using var ui = new UiSession(Name(), snipHotkeyEnabled: true);
        ui.Guard(() =>
        {
            try
            {
                // 1. Where the snip goes: inside the target's text box, read before the overlay covers the screen.
                ui.TargetWindow.SetForeground();
                var box = ui.TargetWindow.FindFirstDescendant(ui.Automation.ConditionFactory.ByAutomationId("SourceText"))
                          ?? throw new InvalidOperationException("SourceText not found in the target window");
                var rect = box.BoundingRectangle;
                _output.WriteLine($"step 1: SourceText rectangle {rect.Left},{rect.Top} {rect.Width}x{rect.Height}");
                var width = Math.Min(SnipWidth, Math.Max(20, rect.Width - 40));
                var height = Math.Min(SnipHeight, Math.Max(20, rect.Height - 40));
                var start = new Point(rect.Left + 20, rect.Top + 20);
                var end = new Point(start.X + width, start.Y + height);

                // 2. Open the overlay.
                ui.PressSnipHotkey();
                _output.WriteLine("step 2: Alt+Shift+S pressed");
                WaitForLog(ui, "snip overlay shown");
                Thread.Sleep(500); // let the overlay paint its frozen screenshot and take the mouse

                // 3. Drag the rectangle in small steps, then release.
                Mouse.MoveTo(start);
                Mouse.Down(MouseButton.Left);
                const int steps = 10;
                for (var i = 1; i <= steps; i++)
                {
                    Mouse.MoveTo(new Point(start.X + (end.X - start.X) * i / steps, start.Y + (end.Y - start.Y) * i / steps));
                    Thread.Sleep(30);
                }
                Mouse.Up(MouseButton.Left);
                _output.WriteLine($"step 3: dragged {start.X},{start.Y} to {end.X},{end.Y} ({width}x{height}) and released");

                // 4. The vision answer reaches the card (the mock streams it in three deltas).
                var seen = ui.WaitForCardText(MockUpstream.VisionTranslation);
                _output.WriteLine("step 4: card text reached the vision translation; states seen: "
                                  + string.Join(" ; ", seen.Select(s => $"'{s.Text}' [{s.Caption}]")));
                // The first streamed delta already parses to the full translation, so the card alone does not prove the
                // request finished: wait for the app's "vision ok" line as well.
                var finished = FlaUI.Core.Tools.Retry.WhileFalse(
                    () => ui.DiagnosticsLog().Split('\n').Any(l => l.Contains(" vision ", StringComparison.Ordinal) && l.Contains(" ok via ", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
                Assert.True(finished.Success, "the app never logged the finished vision request ('vision ... ok via ...')");

                // 5. Exactly one chat request, and it carried an image.
                var chats = ui.Mock.ChatRequests;
                Assert.Single(chats);
                Assert.True(MockUpstream.HasImagePart(chats[0]), "the chat request has no image_url content part; body starts: " + Head(chats[0].Body));
                _output.WriteLine("step 5: one chat request, with an image_url part");

                // 6. Esc closes the card.
                ui.PressEscape();
                Assert.True(ui.WaitForCardGone(), "card still visible after Esc");
                _output.WriteLine("step 6: Esc closed the card");
            }
            finally
            {
                WriteAppLog(ui);
            }
        });
    }

    private void WriteAppLog(UiSession ui)
    {
        var lines = ui.DiagnosticsLog()
            .Split('\n')
            .Where(l => l.Contains("snip", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("vision", StringComparison.OrdinalIgnoreCase)
                        || l.Contains("translate", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.TrimEnd('\r'));
        _output.WriteLine("app log (snip / vision / translate lines):" + Environment.NewLine + string.Join(Environment.NewLine, lines));
    }

    private static string Head(string body) => body.Length <= 300 ? body : body[..300] + "...";

    private static void WaitForLog(UiSession ui, string line)
    {
        var found = Retry.WhileFalse(() => ui.DiagnosticsLog().Contains(line, StringComparison.Ordinal), LogTimeout, TimeSpan.FromMilliseconds(200));
        Assert.True(found.Success, $"app did not log '{line}' within {LogTimeout.TotalSeconds:F0} s; log:\n{ui.DiagnosticsLog()}");
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
