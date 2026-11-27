using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>Polling helpers shared by the UI tests that drive the app's own windows.</summary>
internal static class UiWait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Polls until <paramref name="find"/> returns something; exceptions count as "not there yet" (the element can vanish or not exist yet).</summary>
    public static T WaitFor<T>(Func<T?> find, string what) where T : class
    {
        var result = Retry.WhileNull(() =>
        {
            try { return find(); }
            catch (Exception) { return null; }
        }, Timeout, TimeSpan.FromMilliseconds(200));
        return result.Result ?? throw new TimeoutException($"{what} not found within {Timeout.TotalSeconds:F0} s");
    }

    public static IntPtr WaitFor(Func<IntPtr> find, string what)
    {
        var found = IntPtr.Zero;
        var result = Retry.WhileFalse(() => (found = find()) != IntPtr.Zero, Timeout, TimeSpan.FromMilliseconds(200));
        return result.Success ? found : throw new TimeoutException($"{what} not found within {Timeout.TotalSeconds:F0} s");
    }

    /// <summary>Polls until <paramref name="condition"/> holds.</summary>
    public static void WaitUntil(Func<bool> condition, string what)
    {
        var result = Retry.WhileFalse(() =>
        {
            try { return condition(); }
            catch (Exception) { return false; }
        }, Timeout, TimeSpan.FromMilliseconds(200));
        if (!result.Success) throw new TimeoutException($"{what} did not happen within {Timeout.TotalSeconds:F0} s");
    }
}

/// <summary>
/// Drives the common Save dialog of the app (a visible top-level window of the app process, class <c>#32770</c>): the path is
/// typed with the keyboard like a user would (setting the value through UIA changed nothing the dialog used, it saved under
/// the default name), read back, and Save (AutomationId 1) is invoked.
/// </summary>
internal static class SaveDialog
{
    public static IntPtr Find(UiSession ui)
        => Win32.TopLevelWindows(ui.AppProcess.Id, visibleOnly: true).FirstOrDefault(h => Win32.ClassName(h) == "#32770");

    public static void SaveAs(UiSession ui, string path, ITestOutputHelper output)
    {
        var dialog = UiWait.WaitFor(() => Find(ui), "Save dialog (class #32770)");
        output.WriteLine("step 3: Save dialog found, title '" + Win32.Title(dialog) + "'");

        var dialogElement = ui.Automation.FromHandle(dialog);
        var cf = ui.Automation.ConditionFactory;
        var nameBox = UiWait.WaitFor(() => dialogElement.FindFirstDescendant(cf.ByAutomationId("1001")), "file name edit (AutomationId 1001)");
        output.WriteLine($"step 4a: file name box class '{nameBox.ClassName}', value '{EditValue(nameBox)}'");
        nameBox.Focus();
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(path);
        Thread.Sleep(300);
        output.WriteLine($"step 4: file name box now '{EditValue(nameBox)}'");
        var save = UiWait.WaitFor(() => dialogElement.FindFirstDescendant(cf.ByAutomationId("1")), "Save button (AutomationId 1)");
        save.AsButton().Invoke();
        output.WriteLine("step 5: invoked Save");
    }

    private static string EditValue(AutomationElement e)
    {
        try { return e.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault ?? e.Name; }
        catch (Exception) { return "?"; }
    }
}
