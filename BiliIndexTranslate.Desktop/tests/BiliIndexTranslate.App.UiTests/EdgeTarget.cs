using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>Describes a selection target that is an external program instead of the WPF test target window.</summary>
public sealed record ExternalTarget(Func<string, Process> Start, Func<UIA3Automation, Process, Window> FindWindow);

/// <summary>[Fact] that is reported as skipped (not failed) when Microsoft Edge cannot be found.</summary>
public sealed class EdgeFactAttribute : FactAttribute
{
    public EdgeFactAttribute()
    {
        if (EdgeTarget.Locate() is null)
            Skip = "Microsoft Edge not found (looked at %ProgramFiles(x86)% and %ProgramFiles% \\Microsoft\\Edge\\Application\\msedge.exe; BIT_EDGE_EXE overrides)";
    }
}

/// <summary>
/// Microsoft Edge showing a local page with one paragraph. Edge runs with its own throw-away profile and WITHOUT
/// --force-renderer-accessibility: the app is a UIA client itself and has to switch Chromium's accessibility on.
/// </summary>
public static class EdgeTarget
{
    public const string PageTitle = "BIT Edge Selection Test";
    public const string Sentence = "The quick brown fox jumps over the lazy dog while the sun sets behind the hills.";

    public static string? Locate()
    {
        var fromEnv = Environment.GetEnvironmentVariable("BIT_EDGE_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;

        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Target description for <see cref="UiSession"/>. The profile and the page live inside the session's data dir.</summary>
    public static ExternalTarget Target() => new(Start, FindWindow);

    private static Process Start(string dataDir)
    {
        var exe = Locate() ?? throw new FileNotFoundException("Microsoft Edge not found");
        var page = Path.Combine(dataDir, "edge-page.html");
        File.WriteAllText(page,
            "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>" + PageTitle + "</title></head>" +
            "<body style=\"margin:40px;font-size:28px;font-family:sans-serif\"><p>" + Sentence + "</p></body></html>");

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--user-data-dir=" + Path.Combine(dataDir, "edge-profile"));
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-sync");
        psi.ArgumentList.Add("--new-window");
        psi.ArgumentList.Add(new Uri(page).AbsoluteUri);
        return Process.Start(psi) ?? throw new InvalidOperationException("Edge did not start");
    }

    /// <summary>The launcher process may not be the one that owns the window, so the window is found by its title.</summary>
    private static Window FindWindow(UIA3Automation automation, Process _)
    {
        Window? found = null;
        var result = Retry.WhileFalse(() =>
        {
            try
            {
                foreach (var w in automation.GetDesktop().FindAllChildren())
                {
                    var name = w.Properties.Name.ValueOrDefault;
                    if (name is not null && name.Contains(PageTitle, StringComparison.Ordinal))
                    {
                        found = w.AsWindow();
                        return true;
                    }
                }
            }
            catch (Exception) { /* windows come and go while Edge starts */ }
            return false;
        }, TimeSpan.FromSeconds(40), TimeSpan.FromMilliseconds(300));
        if (!result.Success || found is null) throw new TimeoutException($"no Edge window titled '{PageTitle}' within 40 s");
        return found;
    }

    // ---- selecting ---------------------------------------------------------------------------

    /// <summary>The paragraph element, found by its text. Chromium builds its UIA tree lazily, so this polls.</summary>
    private static AutomationElement? FindParagraph(Window window, TimeSpan timeout)
    {
        AutomationElement? hit = null;
        Retry.WhileFalse(() =>
        {
            try
            {
                hit = window.FindAllDescendants()
                    .FirstOrDefault(e => (e.Properties.Name.ValueOrDefault ?? string.Empty)
                        .Contains("quick brown fox", StringComparison.Ordinal)
                        && e.ControlType != ControlType.Document && e.ControlType != ControlType.Window);
            }
            catch (Exception) { }
            return hit is not null;
        }, timeout, TimeSpan.FromMilliseconds(500));
        return hit;
    }

    private static void Foreground(Window window)
    {
        window.SetForeground();
        Thread.Sleep(300);
    }

    /// <summary>Triple-click in the middle of the paragraph (window client centre when the element is not found). Returns a note for the log.</summary>
    public static string SelectByTripleClick(Window window)
    {
        Foreground(window);
        var para = FindParagraph(window, TimeSpan.FromSeconds(20));
        var rect = para?.BoundingRectangle ?? default;
        var from = "paragraph element";
        if (para is null || rect.Width <= 0 || rect.Height <= 0)
        {
            rect = window.BoundingRectangle;
            from = "window centre (paragraph element not found)";
        }
        var point = rect.Center();
        Mouse.MoveTo(point);
        for (var i = 0; i < 3; i++)
        {
            Mouse.Click(FlaUI.Core.Input.MouseButton.Left);
            Thread.Sleep(60);
        }
        return $"triple click at ({point.X},{point.Y}) on {from}";
    }

    /// <summary>Fallback: collapse any selection with a click, then Ctrl+A in the document.</summary>
    public static string SelectByCtrlA(Window window)
    {
        Foreground(window);
        var rect = window.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height * 0.8)));
        Thread.Sleep(500);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        return "Ctrl+A after a click in the document";
    }

    // ---- evidence ----------------------------------------------------------------------------

    /// <summary>The first levels of the UIA tree of the Edge window (or the desktop's top-level windows when none was found).</summary>
    public static string DumpTree(UIA3Automation automation, Window? window)
    {
        var sb = new StringBuilder();
        var count = 0;
        void Walk(AutomationElement e, int depth)
        {
            if (count++ > 600 || depth > 7) return;
            string line;
            try
            {
                var p = e.Properties;
                var name = p.Name.ValueOrDefault ?? string.Empty;
                if (name.Length > 80) name = name[..80] + "...";
                line = $"{new string(' ', depth * 2)}{e.ControlType} class='{p.ClassName.ValueOrDefault}' id='{p.AutomationId.ValueOrDefault}' name='{name}' rect={e.BoundingRectangle}";
            }
            catch (Exception ex) { line = $"{new string(' ', depth * 2)}(unreadable: {ex.Message})"; }
            sb.AppendLine(line);
            try { foreach (var child in e.FindAllChildren()) Walk(child, depth + 1); }
            catch (Exception) { }
        }

        try
        {
            if (window is not null) Walk(window, 0);
            else
            {
                sb.AppendLine("Edge window was not found; top-level windows:");
                foreach (var w in automation.GetDesktop().FindAllChildren()) Walk(w, 0 /* each with its own limit */ + 7);
            }
        }
        catch (Exception ex) { sb.AppendLine("dump failed: " + ex.Message); }
        return sb.ToString();
    }
}
