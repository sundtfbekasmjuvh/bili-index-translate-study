using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// Screen reader support: when the final translation is shown the card raises UIA LiveRegionChanged on its
/// <c>TranslationText</c> element, and the element's Name is the translation.
/// </summary>
public class LiveRegionTests
{
    private static readonly string Full = MockUpstream.FullText;

    private sealed record LiveEvent(string? AutomationId, string? Name);

    [Fact]
    public void Final_translation_raises_LiveRegionChanged_with_the_translation_as_name()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            // First selection: makes the card window exist. Esc hides it (the window is reused, not destroyed).
            ui.SelectAll();
            ui.WaitForCardText(Full);
            var card = ui.FindCardWindow(includeHidden: false);
            Assert.NotEqual(IntPtr.Zero, card);
            ui.PressEscape();
            Assert.True(ui.WaitForCardGone(), "card still visible after Esc");

            // Subscribe on the card window (found by Win32, hidden is fine) before the second selection.
            var events = new ConcurrentQueue<LiveEvent>();
            var window = ui.Automation.FromHandle(card);
            _ = window.RegisterAutomationEvent(ui.Automation.EventLibrary.Element.LiveRegionChangedEvent, TreeScope.Subtree, (sender, _) =>
            {
                try { events.Enqueue(new LiveEvent(sender.Properties.AutomationId.ValueOrDefault, sender.Properties.Name.ValueOrDefault)); }
                catch (Exception) { /* element gone */ }
            });
            try
            {
                // Second selection: three words, a different text than Ctrl+A's, so it is not a cache hit and not "the same selection again".
                ui.SelectFirstWords();
                ui.WaitForCardText(Full);

                var arrived = Retry.WhileFalse(
                    () => events.Any(e => e.AutomationId == "TranslationText" && e.Name == Full),
                    TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
                Assert.True(arrived.Success,
                    "no LiveRegionChanged from TranslationText with the full translation as Name; events: " +
                    (events.IsEmpty ? "(none)" : string.Join(" ; ", events.Select(e => $"{e.AutomationId}='{e.Name}'"))));

                // Partial text must not announce: every event from the translation element carries the final text.
                Assert.All(events.Where(e => e.AutomationId == "TranslationText"), e => Assert.Equal(Full, e.Name));
            }
            finally
            {
                ui.Automation.UnregisterAllEvents();
            }
        });
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
