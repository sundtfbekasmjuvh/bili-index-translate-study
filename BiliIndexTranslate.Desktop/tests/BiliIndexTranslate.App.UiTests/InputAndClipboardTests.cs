using System.Runtime.CompilerServices;
using Xunit;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// Input paths the card tests do not cover: keyboard selection when UIA gives no event, and the explicit
/// hotkey's clipboard fallback (which must put the user's clipboard text back).
/// </summary>
public class InputAndClipboardTests
{
    private static readonly string Full = MockUpstream.FullText;

    // Same text as NoTextPatternBox.CopyText in the UiTestTarget project.
    private const string ControlText = "Custom control text from Ctrl+C";
    private const string KnownClipboardText = "Known clipboard text before the hotkey 4711";

    [Fact]
    public void Shift_end_selection_triggers_the_card_without_uia_events()
    {
        // UIA selection events are off, so the only trigger left is the keyboard hook seeing Shift being
        // released after a Shift+navigation key. FlaUI injects its keys (dwExtraInfo 0): before the hook
        // skipped only the app's own marked keys, it ignored every injected key and this test would fail.
        using var ui = new UiSession(Name(), uiaSelectionEvents: false);
        ui.Guard(() =>
        {
            ui.SelectWithShiftEnd();
            ui.WaitForCardText(Full);

            var chat = Assert.Single(ui.Mock.ChatRequests);
            Assert.Contains("The quick brown fox", chat.Body);
        });
    }

    [Fact]
    public void Selection_key_that_selects_nothing_leaves_a_log_line()
    {
        // Shift+Left with the caret at the start selects nothing, yet releasing Shift is a selection gesture.
        // The automatic probe then finds no selection; that used to leave no trace in the log.
        using var ui = new UiSession(Name(), uiaSelectionEvents: false);
        ui.Guard(() =>
        {
            ui.PressShiftLeftAtStart();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !ui.DiagnosticsLog().Contains(NoSelectionLine, StringComparison.Ordinal))
                Thread.Sleep(100);

            Assert.Contains(NoSelectionLine, ui.DiagnosticsLog(), StringComparison.Ordinal);
            Assert.False(ui.CardVisible());
            Assert.Empty(ui.Mock.ChatRequests);
        });
    }

    private const string NoSelectionLine = "no selection found after a selection key";

    [Fact]
    public void Hotkey_uses_clipboard_fallback_and_restores_the_previous_clipboard_text()
    {
        using var ui = new UiSession(Name(), hotkeyEnabled: true);
        ui.Guard(() =>
        {
            SetClipboardText(KnownClipboardText);

            ui.ClickNoTextPatternBox();
            ui.PressHotkey();
            ui.WaitForCardText(Full);

            var chat = Assert.Single(ui.Mock.ChatRequests);
            Assert.Contains(ControlText, chat.Body);

            string? last = null;
            var restored = FlaUI.Core.Tools.Retry.WhileFalse(
                () => (last = GetClipboardText()) == KnownClipboardText,
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            Assert.True(restored.Success, $"clipboard text was not restored; it is '{last}'");
        });
    }

    // ---- clipboard (needs an STA thread; the clipboard can be busy for a moment) -----------------

    private static void SetClipboardText(string text) => OnSta(() =>
    {
        for (var attempt = 0; ; attempt++)
        {
            try { System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 50); return null; }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 10) { Thread.Sleep(50); }
        }
    });

    private static string? GetClipboardText() => OnSta(() =>
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { return System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(50); }
        }
        return null;
    });

    private static string? OnSta(Func<string?> action)
    {
        string? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("clipboard access failed", error);
        return result;
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
