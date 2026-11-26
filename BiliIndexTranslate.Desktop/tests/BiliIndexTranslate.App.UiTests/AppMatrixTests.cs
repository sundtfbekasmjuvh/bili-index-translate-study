using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>One gesture on one app: what happened.</summary>
public sealed record MatrixEntry(string Gesture, bool Passed, string? SelectedText, double? TimeToCardMs, int ChatRequests, string? How, string? Error);

public sealed record MatrixEnv(string Os, int ProcessorCount);

public sealed record MatrixFile(string App, string DisplayName, List<MatrixEntry> Entries, MatrixEnv Environment);

/// <summary>TestResults/baseline/app-matrix-&lt;app&gt;.json: one file per app, one entry per gesture (a re-run of a gesture replaces its entry).</summary>
public static class MatrixResults
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void Record(SelectionApp app, MatrixEntry entry)
    {
        try
        {
            lock (Gate)
            {
                var root = ExeLocator.DesktopRoot() ?? AppContext.BaseDirectory;
                var dir = Path.Combine(root, "TestResults", "baseline");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"app-matrix-{app.Id}.json");
                var entries = new List<MatrixEntry>();
                if (File.Exists(path))
                {
                    try { entries = JsonSerializer.Deserialize<MatrixFile>(File.ReadAllText(path), Options)?.Entries ?? entries; }
                    catch (JsonException) { /* unreadable old file: start over */ }
                }
                entries.RemoveAll(e => e.Gesture == entry.Gesture);
                entries.Add(entry);
                var file = new MatrixFile(app.Id, app.DisplayName, entries,
                    new MatrixEnv(System.Environment.OSVersion.ToString(), System.Environment.ProcessorCount));
                File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not write the app matrix result: " + ex.Message);
        }
    }
}

/// <summary>
/// Which real applications and selection gestures produce the selection card. One session per app and gesture, all strict:
/// they fail when the card does not appear. Every outcome goes to the test output and to app-matrix-&lt;app&gt;.json.
/// Edge, Chrome, Firefox and classic Notepad are each skipped (not failed) when the program is not installed.
/// The time to card is measured from the end of the gesture and includes the mock's three segments (about 800 ms).
/// </summary>
public class AppMatrixTests
{
    private static readonly string Full = MockUpstream.FullText;
    private readonly ITestOutputHelper _out;

    public AppMatrixTests(ITestOutputHelper output) => _out = output;

    // ---- Edge ----
    [AppFact("edge")] public void Edge_double_click_selects_word() => Run(SelectionApps.Edge, Gesture.DoubleClick);
    [AppFact("edge")] public void Edge_triple_click_selects_line() => Run(SelectionApps.Edge, Gesture.TripleClick);
    [AppFact("edge")] public void Edge_drag_selects_start_of_line() => Run(SelectionApps.Edge, Gesture.Drag);
    [AppFact("edge")] public void Edge_ctrl_a_selects_page() => Run(SelectionApps.Edge, Gesture.Keyboard);

    // ---- Chrome ----
    [AppFact("chrome")] public void Chrome_double_click_selects_word() => Run(SelectionApps.Chrome, Gesture.DoubleClick);
    [AppFact("chrome")] public void Chrome_triple_click_selects_line() => Run(SelectionApps.Chrome, Gesture.TripleClick);
    [AppFact("chrome")] public void Chrome_drag_selects_start_of_line() => Run(SelectionApps.Chrome, Gesture.Drag);
    [AppFact("chrome")] public void Chrome_ctrl_a_selects_page() => Run(SelectionApps.Chrome, Gesture.Keyboard);

    // ---- Firefox ----
    [AppFact("firefox")] public void Firefox_double_click_selects_word() => Run(SelectionApps.Firefox, Gesture.DoubleClick);
    [AppFact("firefox")] public void Firefox_triple_click_selects_line() => Run(SelectionApps.Firefox, Gesture.TripleClick);
    [AppFact("firefox")] public void Firefox_drag_selects_start_of_line() => Run(SelectionApps.Firefox, Gesture.Drag);
    [AppFact("firefox")] public void Firefox_ctrl_a_selects_page() => Run(SelectionApps.Firefox, Gesture.Keyboard);

    // ---- Notepad ----
    [AppFact("notepad")] public void Notepad_double_click_selects_word() => Run(SelectionApps.Notepad, Gesture.DoubleClick);
    [AppFact("notepad")] public void Notepad_triple_click_selects_something() => Run(SelectionApps.Notepad, Gesture.TripleClick);
    [AppFact("notepad")] public void Notepad_drag_selects_start_of_line() => Run(SelectionApps.Notepad, Gesture.Drag);
    [AppFact("notepad")] public void Notepad_shift_end_selects_line() => Run(SelectionApps.Notepad, Gesture.Keyboard);

    // ---- cold Chromium ----

    /// <summary>
    /// Rule: from this test process no UI Automation request goes to Edge, so the app alone has to get Chromium to expose the
    /// selection. Edge's window and its page area are found with Win32 (EnumWindows, EnumChildWindows), the line sits at a fixed
    /// CSS position, and the click point is computed from the page area's rectangle and the window's DPI. Reading the card only
    /// asks the app's own windows (FindInApp). On failure no UIA tree or focus dump of Edge is taken either.
    /// </summary>
    [AppFact("edge")]
    public void Edge_cold_chromium_triple_click_without_uia_from_the_test()
    {
        const string gesture = "cold-triple-click";
        var app = SelectionApps.Edge;
        var name = "matrix-edge-cold";
        UiSession ui;
        try { ui = new UiSession(name, external: EdgeCold.Target()); }
        catch (Exception ex) { Fail(app, gesture, "setup: " + ex.Message); throw; }

        using (ui)
        {
            ui.Guard(() =>
            {
                string? how = null, selected = null;
                double? ms = null;
                try
                {
                    var top = ui.TargetHandle;
                    Win32.SetForeground(top);
                    Thread.Sleep(800); // the page has just been titled; let it paint

                    var point = EdgeCold.ClickPoint(top, out var note);
                    how = "first triple click: " + note;
                    _out.WriteLine(how);
                    SelectionApp.Clicks(point, 3);
                    var sw = Stopwatch.StartNew();
                    var firstWorked = TryCard(ui, TimeSpan.FromSeconds(10));
                    if (!firstWorked)
                    {
                        _out.WriteLine("no card after the first triple click; clearing the selection and trying once more");
                        SelectionApp.Clicks(EdgeCold.BlankPoint(top), 1);
                        Thread.Sleep(500);
                        point = EdgeCold.ClickPoint(top, out note);
                        SelectionApp.Clicks(point, 3);
                        sw.Restart();
                        ui.WaitForCardText(Full, TimeSpan.FromSeconds(15));
                    }
                    ms = sw.Elapsed.TotalMilliseconds;
                    _out.WriteLine($"cold Chromium: card on the first triple click = {firstWorked}");
                    how += $"; worked on first triple click: {firstWorked}";

                    var requests = ui.Mock.ChatRequests;
                    Assert.NotEmpty(requests);
                    var body = requests[^1].Body;
                    selected = SelectedText(body);
                    Assert.Contains(SelectionApp.Line, body);
                    _out.WriteLine($"selected text sent: '{selected}'; time to card {ms:F0} ms");
                    MatrixResults.Record(app, new MatrixEntry(gesture, true, selected, ms, requests.Count, how, null));
                }
                catch (Exception ex)
                {
                    Fail(app, gesture, ex.Message, how, selected, ui);
                    _out.WriteLine("app log:" + Environment.NewLine + ui.DiagnosticsLog());
                    throw;
                }
            });
        }
    }

    // ---- shared flow ----

    private void Run(SelectionApp app, Gesture gesture)
    {
        var gestureName = GestureName(gesture);
        var testName = $"matrix-{app.Id}-{gestureName}";
        UiSession ui;
        try { ui = new UiSession(testName, external: app.Target()); }
        catch (Exception ex) { Fail(app, gestureName, "setup: " + ex.Message); throw; }

        using (ui)
        {
            ui.Guard(() =>
            {
                string? how = null, selected = null;
                try
                {
                    how = app.Perform(ui.TargetWindow, gesture);
                    _out.WriteLine($"{app.DisplayName} / {gestureName}: {how}");
                    var sw = Stopwatch.StartNew();
                    ui.WaitForCardText(Full, TimeSpan.FromSeconds(20));
                    var ms = sw.Elapsed.TotalMilliseconds;

                    var requests = ui.Mock.ChatRequests;
                    Assert.NotEmpty(requests);
                    var body = requests[^1].Body;
                    selected = SelectedText(body);
                    CheckBody(app, gesture, body);
                    _out.WriteLine($"PASS {app.DisplayName} / {gestureName}: selected '{selected}', card after {ms:F0} ms, {requests.Count} request(s)");
                    MatrixResults.Record(app, new MatrixEntry(gestureName, true, selected, ms, requests.Count, how, null));
                }
                catch (Exception ex)
                {
                    Fail(app, gestureName, ex.Message, how, selected, ui);
                    var nl = Environment.NewLine;
                    _out.WriteLine($"FAIL {app.DisplayName} / {gestureName}: {ex.Message}");
                    _out.WriteLine("error card: " + (ui.ErrorText() ?? "none") + "; chat requests: " + ui.Mock.ChatRequests.Count);
                    _out.WriteLine("UIA seen from the test:" + nl + UiaDiagnostics.DescribeFocus(ui.Automation));
                    _out.WriteLine("app log:" + nl + ui.DiagnosticsLog());
                    throw;
                }
            });
        }
    }

    private static void CheckBody(SelectionApp app, Gesture gesture, string body)
    {
        switch (gesture)
        {
            case Gesture.DoubleClick:
                Assert.Contains(SelectionApp.Word, body);
                Assert.DoesNotContain(SelectionApp.Line, body);
                break;
            case Gesture.TripleClick:
                // Notepad's Edit control has no triple click (it may repeat the word selection): there only a
                // non-empty selection that contains the clicked word is required.
                if (app is NotepadApp) Assert.Contains(SelectionApp.Word, body);
                else Assert.Contains(SelectionApp.Line, body);
                break;
            case Gesture.Drag:
                Assert.Contains("Quick", body);
                break;
            case Gesture.Keyboard:
                Assert.Contains(SelectionApp.Line, body);
                break;
        }
    }

    private static string GestureName(Gesture g) => g switch
    {
        Gesture.DoubleClick => "double-click",
        Gesture.TripleClick => "triple-click",
        Gesture.Drag => "drag",
        _ => "keyboard",
    };

    private static bool TryCard(UiSession ui, TimeSpan timeout)
    {
        try { ui.WaitForCardText(Full, timeout); return true; }
        catch (TimeoutException) { return false; }
    }

    private static void Fail(SelectionApp app, string gesture, string error, string? how = null, string? selected = null, UiSession? ui = null)
        => MatrixResults.Record(app, new MatrixEntry(gesture, false, selected, null, ui?.Mock.ChatRequests.Count ?? 0, how, error));

    /// <summary>The user message of the chat request (the selected text, possibly inside the prompt); the raw body when it cannot be parsed.</summary>
    private static string SelectedText(string body)
    {
        try
        {
            var messages = JsonNode.Parse(body)?["messages"]?.AsArray();
            var last = messages?.LastOrDefault(m => m?["role"]?.GetValue<string>() == "user") ?? messages?.LastOrDefault();
            var content = last?["content"];
            if (content is not null) return content is JsonValue v && v.TryGetValue<string>(out var s) ? s : content.ToJsonString();
        }
        catch (Exception) { /* fall through */ }
        return body.Length > 300 ? body[..300] : body;
    }
}
