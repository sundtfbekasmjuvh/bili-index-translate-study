using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Xunit;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// Describes a selection target that is an external program instead of the WPF test target window.
/// Either <paramref name="FindWindow"/> (UIA, gives a <see cref="Window"/>) or <paramref name="FindWindowWin32"/>
/// (no UIA at all, gives a window handle) finds the window; the second is for sessions that must not query the target through UIA.
/// </summary>
public sealed record ExternalTarget(
    Func<string, Process> Start,
    Func<UIA3Automation, Process, Window>? FindWindow = null,
    Func<Process, IntPtr>? FindWindowWin32 = null);

/// <summary>[Fact] that is reported as skipped (not failed) when the named program cannot be found.</summary>
public sealed class AppFactAttribute : FactAttribute
{
    public AppFactAttribute(string appId)
    {
        var app = SelectionApps.ById(appId);
        if (app.Locate() is null) Skip = $"{app.DisplayName} not found ({app.LocateHint})";
    }
}

/// <summary>
/// Always skipped: the gesture does not exist in that program. Besides the skip, the constructor writes an "n/a" entry to the
/// app's matrix JSON (when the program is installed), so the CI table shows n/a instead of a gap. This runs at test
/// discovery, before any test of the run.
/// </summary>
public sealed class AppNotApplicableFactAttribute : FactAttribute
{
    public AppNotApplicableFactAttribute(string appId, string gestureName, string reason)
    {
        var app = SelectionApps.ById(appId);
        Skip = $"n/a: {reason}";
        if (app.Locate() is not null)
            MatrixResults.Record(app, new MatrixEntry(gestureName, MatrixEntry.NotApplicable, null, null, 0, null, reason, null));
    }
}

public enum Gesture { DoubleClick, TripleClick, Drag, Keyboard }

public static class SelectionApps
{
    /// <summary>
    /// Closes processes that own a visible window of the test pages (title starts with the page title) or of the Notepad test
    /// file. Needed because a browser's launcher process can hand over to another process and exit, so killing the started
    /// process (tree) does not close the browser. Only these windows' owners are killed, never programs by name.
    /// </summary>
    public static void CloseLeftovers()
        => Win32.KillWindowOwners([SelectionApp.PageTitle, EdgeCold.PageTitle, NotepadApp.FileName]);

    public static readonly SelectionApp Edge = new ChromiumApp("edge", "Microsoft Edge", "BIT_EDGE_EXE",
        "%ProgramFiles(x86)% and %ProgramFiles% \\Microsoft\\Edge\\Application\\msedge.exe",
        () => Candidates(["ProgramFiles(x86)", "ProgramFiles"], Path.Combine("Microsoft", "Edge", "Application", "msedge.exe")));

    public static readonly SelectionApp Chrome = new ChromiumApp("chrome", "Google Chrome", "BIT_CHROME_EXE",
        "%ProgramFiles%, %ProgramFiles(x86)% or %LocalAppData% \\Google\\Chrome\\Application\\chrome.exe",
        () => Candidates(["ProgramFiles", "ProgramFiles(x86)", "LocalAppData"], Path.Combine("Google", "Chrome", "Application", "chrome.exe")));

    public static readonly SelectionApp Firefox = new FirefoxApp();
    public static readonly SelectionApp Notepad = new NotepadApp();

    public static SelectionApp ById(string id) => id switch
    {
        "edge" => Edge,
        "chrome" => Chrome,
        "firefox" => Firefox,
        "notepad" => Notepad,
        _ => throw new ArgumentException("unknown app id " + id),
    };

    internal static string? Override(string envVar, out bool set)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        set = !string.IsNullOrWhiteSpace(fromEnv);
        return set && File.Exists(fromEnv) ? fromEnv : null;
    }

    internal static IEnumerable<string> Candidates(string[] roots, string relative)
    {
        foreach (var variable in roots)
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, relative);
        }
    }
}

/// <summary>The one line every app shows, and how a gesture finds its screen positions.</summary>
public abstract class SelectionApp
{
    public const string PageTitle = "BIT Selection Test";
    public const string Line = "Quick brown fox jumps over the lazy dog.";
    public const string Word = "brown";

    /// <summary>A second line, selected once before the measured gesture so a freshly started program has served one selection already.</summary>
    public const string WarmLine = "Warm up line for the reader.";

    /// <summary>Monospace on purpose: with a fixed character width a position inside the line can be computed from the character index.</summary>
    public const string Font = "32px Consolas, 'Courier New', monospace";

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string LocateHint { get; }
    public abstract string? Locate();

    /// <summary>Substring of the visible title of the app's window.</summary>
    protected abstract string WindowTitle { get; }

    protected abstract Process Start(string exe, string dataDir);

    /// <summary>Screen rectangle of the line.</summary>
    protected abstract Rectangle LineRect(Window window);

    /// <summary>A point in the window's content that is not on the text, where a click gives it keyboard focus.</summary>
    protected abstract Point BlankPoint(Window window);

    protected abstract void KeyboardSelect(Window window);

    /// <summary>Where and how often to click to select something on the warm-up line (a text different from every measured one).</summary>
    protected abstract (Point Point, int Clicks) WarmUpClick(Window window);

    /// <summary>The warm-up selection. Returns a note for the log.</summary>
    public string WarmUp(Window window)
    {
        Foreground(window);
        var (p, clicks) = WarmUpClick(window);
        Clicks(p, clicks);
        return $"warm-up: {clicks} click(s) at ({p.X},{p.Y}) on the warm-up line";
    }

    public ExternalTarget Target() => new(
        Start: dataDir => Start(Locate() ?? throw new FileNotFoundException(DisplayName + " not found"), dataDir),
        FindWindow: (automation, _) => FromHandlePatiently(automation, Win32.WaitForTopLevelByTitle(WindowTitle, TimeSpan.FromSeconds(40))));

    /// <summary>
    /// <c>FromHandle</c> on a program that has just started: its first UIA answer can take longer than the client
    /// timeout (a fresh Chrome once failed with UIA_E_TIMEOUT 0x80131505 here), so a timeout is retried for up to 20 s.
    /// </summary>
    private static Window FromHandlePatiently(UIA3Automation automation, IntPtr hwnd)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try { return automation.FromHandle(hwnd).AsWindow(); }
            catch (Exception ex) when (DateTime.UtcNow < deadline && (ex is TimeoutException || ex.InnerException is COMException { HResult: unchecked((int)0x80131505) }))
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Position of the middle of <see cref="Word"/>. Notepad overrides it with a real text range.</summary>
    protected virtual Point WordPoint(Window window)
    {
        var r = LineRect(window);
        var index = Line.IndexOf(Word, StringComparison.Ordinal) + Word.Length / 2.0;
        return new Point(r.Left + (int)(r.Width * index / Line.Length), r.Top + r.Height / 2);
    }

    /// <summary>Performs the gesture and returns a note for the log.</summary>
    public string Perform(Window window, Gesture gesture)
    {
        Foreground(window);
        switch (gesture)
        {
            case Gesture.DoubleClick:
            {
                var p = WordPoint(window);
                Clicks(p, 2);
                return $"double click at ({p.X},{p.Y}) on '{Word}'";
            }
            case Gesture.TripleClick:
            {
                var p = WordPoint(window);
                Clicks(p, 3);
                return $"triple click at ({p.X},{p.Y}) on '{Word}'";
            }
            case Gesture.Drag:
            {
                var r = LineRect(window);
                var y = r.Top + r.Height / 2;
                var from = new Point(r.Left + 3, y);
                var to = new Point(r.Left + (int)(r.Width * 0.6), y);
                DragMouse(from, to);
                return $"drag from ({from.X},{from.Y}) to ({to.X},{to.Y}), line rect {r}";
            }
            case Gesture.Keyboard:
                KeyboardSelect(window);
                return "keyboard selection";
            default:
                throw new ArgumentOutOfRangeException(nameof(gesture));
        }
    }

    protected static void Foreground(Window window)
    {
        window.SetForeground();
        Thread.Sleep(300);
    }

    /// <summary>
    /// Raw down/up pairs about 50 ms apart. FlaUI's Mouse.Click waits out the double-click time on a second click at the
    /// same spot on purpose, so repeated calls would be separate single clicks.
    /// </summary>
    public static void Clicks(Point p, int count)
    {
        Mouse.MoveTo(p);
        Thread.Sleep(100);
        for (var i = 0; i < count; i++)
        {
            Mouse.Down(FlaUI.Core.Input.MouseButton.Left);
            Mouse.Up(FlaUI.Core.Input.MouseButton.Left);
            Thread.Sleep(50);
        }
    }

    protected static void DragMouse(Point from, Point to)
    {
        Mouse.MoveTo(from);
        Thread.Sleep(100);
        Mouse.Down(FlaUI.Core.Input.MouseButton.Left);
        const int steps = 8;
        for (var i = 1; i <= steps; i++)
        {
            Mouse.MoveTo(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            Thread.Sleep(30);
        }
        Mouse.Up(FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(50);
    }

    protected static void ClickBlank(Point p)
    {
        Mouse.MoveTo(p);
        Mouse.Click(p);
        Thread.Sleep(300);
    }

    /// <summary>Shared page: the measured line at a predictable place, the warm-up line below it. Returns the file path.</summary>
    public static string WritePage(string dataDir)
    {
        var page = Path.Combine(dataDir, "bit-page.html");
        var style = "display:inline-block;margin:0;white-space:nowrap;font:" + Font;
        File.WriteAllText(page,
            "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>" + PageTitle + "</title></head>" +
            "<body style=\"margin:40px\"><p style=\"" + style + "\">" + Line + "</p><br>" +
            "<p style=\"" + style + ";margin-top:30px\">" + WarmLine + "</p></body></html>");
        return page;
    }

    protected static Point Mid(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);
}

/// <summary>Edge or Chrome showing the shared page, with an own throw-away profile and WITHOUT --force-renderer-accessibility.</summary>
public abstract class BrowserApp : SelectionApp
{
    protected override string WindowTitle => PageTitle;

    /// <summary>The line's element, found by its text. Chromium and Firefox build their UIA tree lazily, so this polls.</summary>
    protected override Rectangle LineRect(Window window) => ElementRect(window, Line[..15]);

    protected override (Point Point, int Clicks) WarmUpClick(Window window) => (Mid(ElementRect(window, WarmLine[..12])), 3);

    private Rectangle ElementRect(Window window, string namePart)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            try
            {
                var hit = window.FindAllDescendants().FirstOrDefault(e =>
                    (e.Properties.Name.ValueOrDefault ?? string.Empty).Contains(namePart, StringComparison.OrdinalIgnoreCase)
                    && e.ControlType != ControlType.Document && e.ControlType != ControlType.Window);
                if (hit is not null && hit.BoundingRectangle is { Width: > 0, Height: > 0 } r) return r;
            }
            catch (Exception) { /* the tree changes while the page loads */ }
            Thread.Sleep(500);
        } while (DateTime.UtcNow < end);
        throw new TimeoutException($"{DisplayName}: no UIA element containing '{namePart}' with a rectangle within 20 s");
    }

    protected override Point BlankPoint(Window window)
    {
        var r = window.BoundingRectangle;
        return new Point(r.Left + r.Width / 2, r.Top + r.Height * 6 / 10);
    }

    /// <summary>The page has no caret, so Shift+arrows select nothing: click into the page for keyboard focus, then Ctrl+A.</summary>
    protected override void KeyboardSelect(Window window)
    {
        ClickBlank(BlankPoint(window));
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
    }
}

public sealed class ChromiumApp : BrowserApp
{
    private readonly string _envVar;
    private readonly Func<IEnumerable<string>> _candidates;

    public ChromiumApp(string id, string displayName, string envVar, string hint, Func<IEnumerable<string>> candidates)
    {
        Id = id;
        DisplayName = displayName;
        LocateHint = hint + "; " + envVar + " overrides";
        _envVar = envVar;
        _candidates = candidates;
    }

    public override string Id { get; }
    public override string DisplayName { get; }
    public override string LocateHint { get; }

    public override string? Locate()
    {
        var env = SelectionApps.Override(_envVar, out var set);
        if (set) return env;
        return _candidates().FirstOrDefault(File.Exists);
    }

    protected override Process Start(string exe, string dataDir)
        => StartChromium(exe, dataDir, Id + "-profile", new Uri(WritePage(dataDir)).AbsoluteUri);

    public static Process StartChromium(string exe, string dataDir, string profileDir, string url)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--user-data-dir=" + Path.Combine(dataDir, profileDir));
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-sync");
        psi.ArgumentList.Add("--new-window");
        psi.ArgumentList.Add(url);
        return Process.Start(psi) ?? throw new InvalidOperationException("browser did not start");
    }
}

public sealed class FirefoxApp : BrowserApp
{
    public override string Id => "firefox";
    public override string DisplayName => "Mozilla Firefox";
    public override string LocateHint => "%ProgramFiles% or %ProgramFiles(x86)% \\Mozilla Firefox\\firefox.exe; BIT_FIREFOX_EXE overrides";

    public override string? Locate()
    {
        var env = SelectionApps.Override("BIT_FIREFOX_EXE", out var set);
        if (set) return env;
        return SelectionApps.Candidates(["ProgramFiles", "ProgramFiles(x86)"], Path.Combine("Mozilla Firefox", "firefox.exe")).FirstOrDefault(File.Exists);
    }

    protected override Process Start(string exe, string dataDir)
    {
        var profile = Path.Combine(dataDir, "firefox-profile");
        Directory.CreateDirectory(profile);
        // user.js is read at start-up: no first-run page, no welcome, no default-browser or data-reporting prompts, no updates.
        File.WriteAllText(Path.Combine(profile, "user.js"), string.Join('\n',
            "user_pref(\"browser.shell.checkDefaultBrowser\", false);",
            "user_pref(\"browser.shell.didSkipDefaultBrowserCheckOnFirstRun\", true);",
            "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");",
            "user_pref(\"startup.homepage_welcome_url\", \"\");",
            "user_pref(\"startup.homepage_welcome_url.additional\", \"\");",
            "user_pref(\"browser.aboutwelcome.enabled\", false);",
            "user_pref(\"trailhead.firstrun.didSeeAboutWelcome\", true);",
            "user_pref(\"browser.rights.3.shown\", true);",
            "user_pref(\"browser.startup.page\", 0);",
            "user_pref(\"browser.disableResetPrompt\", true);",
            "user_pref(\"browser.tabs.warnOnClose\", false);",
            "user_pref(\"browser.aboutConfig.showWarning\", false);",
            "user_pref(\"datareporting.policy.dataSubmissionEnabled\", false);",
            "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);",
            "user_pref(\"datareporting.healthreport.uploadEnabled\", false);",
            "user_pref(\"toolkit.telemetry.reportingpolicy.firstRun\", false);",
            "user_pref(\"app.update.auto\", false);",
            "user_pref(\"app.update.enabled\", false);",
            "user_pref(\"app.shield.optoutstudies.enabled\", false);",
            "") + "\n");

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("-profile");
        psi.ArgumentList.Add(profile);
        psi.ArgumentList.Add("-no-remote");
        psi.ArgumentList.Add("-new-instance");
        // Keeps the launcher process alive as the parent of the browser, so killing the started process tree closes the browser.
        psi.ArgumentList.Add("-wait-for-browser");
        psi.ArgumentList.Add(new Uri(WritePage(dataDir)).AbsoluteUri);
        return Process.Start(psi) ?? throw new InvalidOperationException("Firefox did not start");
    }
}

/// <summary>Classic Notepad (Edit control) with a .txt file that holds the line.</summary>
public sealed class NotepadApp : SelectionApp
{
    /// <summary>File name of the test text; Notepad's window title starts with it.</summary>
    public const string FileName = "bit-line.txt";

    public override string Id => "notepad";
    public override string DisplayName => "Notepad";
    public override string LocateHint => "%WINDIR%\\System32\\notepad.exe";
    protected override string WindowTitle => FileName;

    public override string? Locate()
    {
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (string.IsNullOrEmpty(windir)) return null;
        var exe = Path.Combine(windir, "System32", "notepad.exe");
        return File.Exists(exe) ? exe : null;
    }

    protected override Process Start(string exe, string dataDir)
    {
        var file = Path.Combine(dataDir, FileName);
        File.WriteAllText(file, Line + "\r\n" + WarmLine);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add(file);
        return Process.Start(psi) ?? throw new InvalidOperationException("Notepad did not start");
    }

    private static AutomationElement Editor(Window window)
    {
        var cf = window.ConditionFactory;
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            try
            {
                var edit = window.FindFirstDescendant(cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.Document)));
                if (edit is not null) return edit;
            }
            catch (Exception) { }
            Thread.Sleep(300);
        } while (DateTime.UtcNow < end);
        throw new TimeoutException("Notepad: no Edit/Document element within 20 s");
    }

    /// <summary>Bounding rectangle of the first occurrence of the text, from the Edit control's UIA text range.</summary>
    private static Rectangle RangeRect(Window window, string text)
    {
        var pattern = Editor(window).Patterns.Text.PatternOrDefault
                      ?? throw new InvalidOperationException("Notepad editor has no text pattern");
        var range = pattern.DocumentRange.FindText(text, false, false)
                    ?? throw new InvalidOperationException($"text '{text}' not found in the Notepad editor");
        var rects = range.GetBoundingRectangles();
        if (rects.Length == 0) throw new InvalidOperationException($"no bounding rectangles for '{text}'");
        var left = rects.Min(r => r.Left);
        var top = rects.Min(r => r.Top);
        return Rectangle.FromLTRB(left, top, rects.Max(r => r.Right), rects.Max(r => r.Bottom));
    }

    protected override Rectangle LineRect(Window window) => RangeRect(window, Line);

    protected override Point WordPoint(Window window) => Mid(RangeRect(window, Word));

    /// <summary>Double click on the last word of the second line.</summary>
    protected override (Point Point, int Clicks) WarmUpClick(Window window) => (Mid(RangeRect(window, "reader")), 2);

    protected override Point BlankPoint(Window window)
    {
        var r = Editor(window).BoundingRectangle;
        return new Point(r.Left + r.Width / 2, r.Top + r.Height * 7 / 10);
    }

    /// <summary>Caret into the editor, Ctrl+Home, then Shift+End selects the line.</summary>
    protected override void KeyboardSelect(Window window)
    {
        ClickBlank(BlankPoint(window));
        Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL, FlaUI.Core.WindowsAPI.VirtualKeyShort.HOME);
        Thread.Sleep(200);
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT);
        Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.END);
        Thread.Sleep(100);
        Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.SHIFT);
    }
}

/// <summary>
/// Edge for the cold Chromium test: the test process sends NO UI Automation request to Edge. The window and the page area
/// are found with Win32, and the page positions its line absolutely, so the click point follows from the page area alone.
/// </summary>
public static class EdgeCold
{
    public const string PageTitle = "BIT Cold Chromium Test";
    public const string RenderWidgetClass = "Chrome_RenderWidgetHostHWND";

    // Position of the line inside the page, in CSS pixels (see the page below).
    private const int LineLeftCss = 40, LineTopCss = 40, LineHeightCss = 40;

    public static ExternalTarget Target() => new(
        Start: dataDir =>
        {
            var exe = SelectionApps.Edge.Locate() ?? throw new FileNotFoundException("Microsoft Edge not found");
            var page = Path.Combine(dataDir, "bit-cold-page.html");
            File.WriteAllText(page,
                "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>" + PageTitle + "</title></head>" +
                "<body style=\"margin:0\"><p style=\"position:absolute;left:" + LineLeftCss + "px;top:" + LineTopCss + "px;margin:0;" +
                "white-space:nowrap;font:" + SelectionApp.Font.Replace("32px", "32px/" + LineHeightCss + "px") + "\">" + SelectionApp.Line + "</p></body></html>");
            return ChromiumApp.StartChromium(exe, dataDir, "edge-cold-profile", new Uri(page).AbsoluteUri);
        },
        FindWindowWin32: _ => Win32.WaitForTopLevelByTitle(PageTitle, TimeSpan.FromSeconds(40)));

    /// <summary>Screen point inside the line (about 6 characters in), from the page area's rectangle and the window's DPI scale.</summary>
    public static Point ClickPoint(IntPtr topLevel, out string note)
    {
        var content = Win32.WaitForChildByClass(topLevel, RenderWidgetClass, TimeSpan.FromSeconds(20));
        var rect = Win32.Bounds(content);
        var scale = Win32.Scale(content);
        var point = new Point(
            rect.Left + (int)Math.Round((LineLeftCss + 100) * scale),
            rect.Top + (int)Math.Round((LineTopCss + LineHeightCss / 2) * scale));
        note = $"page area {rect}, scale {scale:F2}, click point ({point.X},{point.Y})";
        return point;
    }

    /// <summary>A point in the page area below the line: a click there clears the selection.</summary>
    public static Point BlankPoint(IntPtr topLevel)
    {
        var rect = Win32.Bounds(Win32.WaitForChildByClass(topLevel, RenderWidgetClass, TimeSpan.FromSeconds(20)));
        return new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height * 7 / 10);
    }
}
