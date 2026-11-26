using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Translation;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>What the card showed at one moment.</summary>
public sealed record CardSnapshot(string? Text, string? Caption);

/// <summary>
/// One isolated run: mock upstream, temporary data directory with its own settings.json, the selection
/// target window and the app (started with --data-dir). Disposing kills both processes and deletes the directory.
/// </summary>
public sealed class UiSession : IDisposable
{
    public static readonly TimeSpan CardTimeout = TimeSpan.FromSeconds(15);

    private readonly string _testName;
    private Process? _app;
    private Process? _target;

    public MockUpstream Mock { get; } = new();
    public string DataDir { get; }
    public UIA3Automation Automation { get; } = new();
    public Window TargetWindow { get; private set; } = null!;

    /// <summary>The app process under test (read-only use: counters, handles).</summary>
    public Process AppProcess => _app ?? throw new InvalidOperationException("app not started");

    /// <summary>Time from Process.Start of the app to the moment the 'ready' log line was seen (polled every 200 ms, so +0..200 ms).</summary>
    public TimeSpan StartupTime { get; private set; }

    private readonly ExternalTarget? _external;

    /// <param name="external">When given, this program is the selection target instead of the WPF test target window.</param>
    public UiSession(string testName, int? failWithStatus = null, ExternalTarget? external = null)
    {
        _testName = testName;
        _external = external;
        Mock.FailWithStatus = failWithStatus;
        DataDir = Path.Combine(Path.GetTempPath(), "bit-ui-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(DataDir);
        try
        {
            WriteSettings();
            StartTarget();
            StartApp();
        }
        catch (Exception)
        {
            // A constructor that throws is never disposed: keep the evidence and clean up here.
            SaveEvidence();
            Dispose();
            throw;
        }
    }

    // ---- setup --------------------------------------------------------------------------------

    private void WriteSettings()
    {
        var path = Path.Combine(DataDir, "settings.json");
        var settings = new AppSettings
        {
            BaseUrl = Mock.BaseUrl,
            ApiFormat = ApiFormat.OpenAi,
            Model = "Index-Translate-9B",
            AutoTranslate = true,
            OnboardingCompleted = true,
            UiLanguage = "en",
            HotkeyEnabled = false,
            SnipHotkeyEnabled = false,
            DiagnosticsFileLogging = true,
            ApiKey = string.Empty,
        };
        new SettingsStore(path, new PlainTextSecretProtector()).Save(settings);

        // SettingsStore.Save normalizes first, which turns the empty key into the default one and stores it with
        // the "plain" scheme; the app uses DPAPI and would report the key as undecryptable. An empty protected
        // value needs no scheme and makes the app fall back to its default key (see SettingsStore.Load).
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["apiKeyProtected"] = string.Empty;
        File.WriteAllText(path, node.ToJsonString());
    }

    private void StartTarget()
    {
        if (_external is not null)
        {
            _target = _external.Start(DataDir);
            TargetWindow = _external.FindWindow(Automation, _target);
            return;
        }
        _target = Process.Start(new ProcessStartInfo(ExeLocator.Target()) { UseShellExecute = false })
                  ?? throw new InvalidOperationException("target window did not start");
        var app = FlaUI.Core.Application.Attach(_target);
        TargetWindow = app.GetMainWindow(Automation, TimeSpan.FromSeconds(20))
                       ?? throw new InvalidOperationException("target window did not show up");
    }

    private void StartApp()
    {
        var exe = ExeLocator.App();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(DataDir);
        var startWatch = Stopwatch.StartNew();
        _app = Process.Start(psi) ?? throw new InvalidOperationException("app did not start");

        var ready = Retry.WhileFalse(() =>
        {
            if (_app.HasExited) throw new InvalidOperationException($"app exited early with code {_app.ExitCode}; see crash.log / diagnostics.log");
            return LogText().Contains("ready", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        if (!ready.Success) throw new TimeoutException("app did not log 'ready' within 30 s");
        StartupTime = startWatch.Elapsed;
    }

    /// <summary>The app's diagnostics.log so far (empty when it cannot be read).</summary>
    public string DiagnosticsLog() => LogText();

    private string LogText()
    {
        try
        {
            using var fs = new FileStream(Path.Combine(DataDir, "diagnostics.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (Exception) { return string.Empty; }
    }

    // ---- driving the target ------------------------------------------------------------------

    private AutomationElement SourceBox
        => TargetWindow.FindFirstDescendant(Automation.ConditionFactory.ByAutomationId("SourceText"))
           ?? throw new InvalidOperationException("SourceText not found in the target window");

    /// <summary>Focuses the text box and selects everything with Ctrl+A.</summary>
    public void SelectAll()
    {
        TargetWindow.SetForeground();
        var box = SourceBox;
        box.Focus();
        box.Click(); // caret only; also makes sure the window really is in the foreground
        Thread.Sleep(300);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
    }

    /// <summary>Clicking inside the text box collapses the selection.</summary>
    public void ClearSelection()
    {
        SourceBox.Click();
        Thread.Sleep(500);
    }

    public void PressEscape() => Keyboard.Press(VirtualKeyShort.ESCAPE);

    // ---- reading the card --------------------------------------------------------------------

    private AutomationElement? FindInApp(string automationId)
    {
        try
        {
            var cf = Automation.ConditionFactory;
            foreach (var window in Automation.GetDesktop().FindAllChildren(cf.ByProcessId(_app!.Id)))
            {
                var hit = window.FindFirstDescendant(cf.ByAutomationId(automationId));
                if (hit is not null) return hit;
            }
        }
        catch (Exception) { /* the element can vanish while it is being read */ }
        return null;
    }

    /// <summary>Translation text of the card, or null when the translation body is not shown (hidden, loading or error).</summary>
    public string? CardText()
    {
        try
        {
            var el = FindInApp("TranslationText");
            // PopupWindow exposes the text as HelpText: its Name is the fixed label "Translation".
            return el?.Properties.HelpText.ValueOrDefault ?? (el is null ? null : string.Empty);
        }
        catch (Exception) { return null; }
    }

    public string? CaptionText()
    {
        try { return FindInApp("Caption")?.Properties.Name.ValueOrDefault; }
        catch (Exception) { return null; }
    }

    public string? ErrorText()
    {
        try
        {
            var title = FindInApp("ErrorTitle")?.Properties.Name.ValueOrDefault;
            var msg = FindInApp("ErrorMessage")?.Properties.Name.ValueOrDefault;
            return title is null && msg is null ? null : $"{title} | {msg}";
        }
        catch (Exception) { return null; }
    }

    public bool CardVisible() => CardText() is not null || ErrorText() is not null;

    /// <summary>Polls until the card text equals <paramref name="expected"/>, recording every distinct state on the way.</summary>
    public List<CardSnapshot> WaitForCardText(string expected, TimeSpan? timeout = null)
    {
        var seen = new List<CardSnapshot>();
        var result = Retry.WhileFalse(() =>
        {
            var text = CardText();
            if (text is not null)
            {
                var snap = new CardSnapshot(text, CaptionText());
                if (seen.Count == 0 || seen[^1] != snap) seen.Add(snap);
            }
            return text == expected;
        }, timeout ?? CardTimeout, TimeSpan.FromMilliseconds(25));
        if (!result.Success)
            throw new TimeoutException($"card text never became '{expected}'; saw: " +
                (seen.Count == 0 ? "(no card)" : string.Join(" ; ", seen.Select(s => $"'{s.Text}' [{s.Caption}]"))));
        return seen;
    }

    public string WaitForError(TimeSpan? timeout = null)
    {
        string? text = null;
        var result = Retry.WhileFalse(() => (text = ErrorText()) is not null, timeout ?? CardTimeout, TimeSpan.FromMilliseconds(100));
        if (!result.Success) throw new TimeoutException($"no error card appeared; translation text: '{CardText()}'");
        return text!;
    }

    public bool WaitForCardGone(TimeSpan? timeout = null)
        => Retry.WhileTrue(CardVisible, timeout ?? TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(100)).Success;

    // ---- failure evidence and cleanup --------------------------------------------------------

    /// <summary>Runs a test body; on failure saves a full-screen screenshot and the app logs, then rethrows.</summary>
    public void Guard(Action body)
    {
        try { body(); }
        catch (Exception)
        {
            SaveEvidence();
            throw;
        }
    }

    private void SaveEvidence()
    {
        try
        {
            var root = ExeLocator.DesktopRoot() ?? AppContext.BaseDirectory;
            var dir = Path.Combine(root, "TestResults", "ui-screenshots");
            Directory.CreateDirectory(dir);
            Capture.Screen().ToFile(Path.Combine(dir, _testName + ".png"));
            if (_external is not null)
                File.WriteAllText(Path.Combine(dir, _testName + ".uia-tree.txt"), EdgeTarget.DumpTree(Automation, TargetWindow));
            foreach (var log in new[] { "diagnostics.log", "crash.log", "settings.json" })
            {
                var src = Path.Combine(DataDir, log);
                if (!File.Exists(src)) continue;
                using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = File.Create(Path.Combine(dir, $"{_testName}.{log}"));
                fs.CopyTo(dst);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not save UI test evidence: " + ex.Message);
        }
    }

    public void Dispose()
    {
        Kill(_app);
        Kill(_target);
        Mock.Dispose();
        Automation.Dispose();
        for (var i = 0; i < 10; i++)
        {
            try { Directory.Delete(DataDir, recursive: true); break; }
            catch (Exception ex)
            {
                // Edge's profile can stay locked for a moment after the kill; never fail a test for it.
                if (i == 9) Console.Error.WriteLine($"could not delete {DataDir}: {ex.Message}");
                else Thread.Sleep(300);
            }
        }
    }

    private static void Kill(Process? p)
    {
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
        }
        catch (Exception) { }
        finally { p.Dispose(); }
    }
}
