using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// The app's own windows (Settings, Welcome), opened and closed through their buttons. The windows are found with Win32
/// (visible top-level windows of the app process, by title) and read with <c>Automation.FromHandle</c>, like the card.
/// English UI (see <see cref="UiSession"/>), so titles and button names are the English <c>UiText</c> strings.
/// </summary>
public class WindowTests
{
    private const string SettingsTitle = "Settings";             // UiText Settings_Title: "Bili Index Translate — Settings"
    private const string StartReadingButton = "Start reading";   // UiText Welcome_Start (no AutomationId in WelcomeWindow.xaml)
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public WindowTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Open_settings_from_the_error_card_and_cancel_leaves_the_settings_file_unchanged()
    {
        using var ui = new UiSession(Name(), failWithStatus: 412);
        ui.Guard(() =>
        {
            var file = Path.Combine(ui.DataDir, "settings.json");

            ui.SelectAll();
            var error = ui.WaitForError();
            _output.WriteLine("step 1: error card shown: " + error);

            var before = File.ReadAllBytes(file);
            _output.WriteLine($"step 2: settings.json read before ({before.Length} bytes)");

            var open = ui.FindInApp("OpenSettingsButton") ?? throw new InvalidOperationException("OpenSettingsButton not found on the error card");
            open.AsButton().Invoke();
            _output.WriteLine("step 3: invoked 'Open settings'");

            var settings = WaitForWindow(ui, h => Win32.Title(h).Contains(SettingsTitle, StringComparison.Ordinal));
            _output.WriteLine("step 4: Settings window found, title '" + Win32.Title(settings) + "'");

            var cancel = ui.Automation.FromHandle(settings).FindFirstDescendant(ui.Automation.ConditionFactory.ByAutomationId("CancelButton"))
                         ?? throw new InvalidOperationException("CancelButton not found in the Settings window");
            cancel.AsButton().Invoke();
            _output.WriteLine("step 5: invoked Cancel");

            Assert.True(WaitForGone(ui, settings), "Settings window still open after Cancel");
            _output.WriteLine("step 6: Settings window is gone");

            var after = File.ReadAllBytes(file);
            Assert.True(before.AsSpan().SequenceEqual(after), $"settings.json changed ({before.Length} -> {after.Length} bytes)");
            _output.WriteLine("step 7: settings.json is byte for byte the same");
        });
    }

    [Fact]
    public void Welcome_window_opens_on_first_run_and_start_reading_saves_onboarding_completed()
    {
        using var ui = new UiSession(Name(), onboardingCompleted: false);
        ui.Guard(() =>
        {
            var file = Path.Combine(ui.DataDir, "settings.json");
            Assert.False(ReadOnboardingCompleted(file), "the session should have written onboardingCompleted = false");

            var welcome = WaitForWindow(ui, h => HasButton(ui, h, StartReadingButton));
            _output.WriteLine("step 1: Welcome window found (window with a '" + StartReadingButton + "' button), title '" + Win32.Title(welcome) + "'");

            var start = ui.Automation.FromHandle(welcome).FindFirstDescendant(ui.Automation.ConditionFactory.ByName(StartReadingButton))
                        ?? throw new InvalidOperationException("'" + StartReadingButton + "' button not found");
            start.AsButton().Invoke();
            _output.WriteLine("step 2: invoked '" + StartReadingButton + "'");

            Assert.True(WaitForGone(ui, welcome), "Welcome window still open after Start reading");
            _output.WriteLine("step 3: Welcome window is gone");

            var saved = Retry.WhileFalse(() => ReadOnboardingCompleted(file), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            Assert.True(saved.Success, "settings.json does not have onboardingCompleted = true within 5 s");
            _output.WriteLine("step 4: settings.json has onboardingCompleted = true");
        });
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static IntPtr WaitForWindow(UiSession ui, Func<IntPtr, bool> match)
    {
        var found = IntPtr.Zero;
        var result = Retry.WhileFalse(() =>
        {
            foreach (var h in Win32.TopLevelWindows(ui.AppProcess.Id, visibleOnly: true))
            {
                if (!Safe(() => match(h))) continue;
                found = h;
                return true;
            }
            return false;
        }, WindowTimeout, TimeSpan.FromMilliseconds(200));
        if (!result.Success) throw new TimeoutException($"no matching app window within {WindowTimeout.TotalSeconds:F0} s; visible app windows: "
            + string.Join(" | ", Win32.TopLevelWindows(ui.AppProcess.Id, visibleOnly: true).Select(Win32.Title)));
        return found;
    }

    private static bool WaitForGone(UiSession ui, IntPtr hwnd)
        => Retry.WhileTrue(() => Win32.TopLevelWindows(ui.AppProcess.Id, visibleOnly: true).Contains(hwnd), WindowTimeout, TimeSpan.FromMilliseconds(100)).Success;

    private static bool HasButton(UiSession ui, IntPtr hwnd, string name)
        => ui.Automation.FromHandle(hwnd).FindFirstDescendant(ui.Automation.ConditionFactory.ByName(name)) is not null;

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch (Exception) { return false; } // the window can vanish while it is read
    }

    /// <summary>Reads only the one property (the file also holds the encrypted key); false when the file is unreadable at that moment.</summary>
    private static bool ReadOnboardingCompleted(string file)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var kv in node)
                if (string.Equals(kv.Key, "onboardingCompleted", StringComparison.OrdinalIgnoreCase))
                    return kv.Value?.GetValue<bool>() ?? false;
            return false;
        }
        catch (Exception) { return false; }
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
