using System.IO.Compression;
using System.Runtime.CompilerServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// The Diagnostics window's "Export diagnostics…" button: the app is started with <c>--diagnostics</c> and settings that
/// hold sentinel strings; the common Save dialog is driven through UIA; the ZIP must not contain any sentinel or the user name.
/// English UI (see <see cref="UiSession"/>).
/// </summary>
public class DiagnosticsExportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ITestOutputHelper _output;

    public DiagnosticsExportTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Export_writes_a_scrubbed_zip_without_secrets_or_the_user_name()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "bit-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        try
        {
            using var ui = new UiSession(Name(),
                customizeSettings: s =>
                {
                    s.BaseUrl = "https://diag-host.example/v1/SENTINEL1-path?token=SENTINEL2";
                    s.CustomHeaders = "X-Test-Secret: SENTINEL3";
                    s.GlossaryText = "SENTINEL4 => x";
                    s.DiagnosticsFileLogging = true;
                },
                plainApiKey: "SENTINEL5",
                appArgs: ["--diagnostics"]);
            ui.Guard(() =>
            {
                var target = Path.Combine(outDir, "diag-test.zip");

                var export = WaitFor(() => ui.FindInApp("ExportDiagnosticsButton"), "ExportDiagnosticsButton (Diagnostics window opened by --diagnostics)");
                _output.WriteLine("step 1: Diagnostics window is open");
                export.AsButton().Invoke();
                _output.WriteLine("step 2: invoked Export diagnostics");

                var dialog = WaitFor(() => FindSaveDialog(ui), "Save dialog (class #32770)");
                _output.WriteLine("step 3: Save dialog found, title '" + Win32.Title(dialog) + "'");

                var dialogElement = ui.Automation.FromHandle(dialog);
                var cf = ui.Automation.ConditionFactory;
                var nameBox = WaitFor(() => dialogElement.FindFirstDescendant(cf.ByAutomationId("1001")), "file name edit (AutomationId 1001)");
                nameBox.AsTextBox().Text = target;
                _output.WriteLine("step 4: file name set");
                var save = WaitFor(() => dialogElement.FindFirstDescendant(cf.ByAutomationId("1")), "Save button (AutomationId 1)");
                save.AsButton().Invoke();
                _output.WriteLine("step 5: invoked Save");

                var status = WaitFor(() =>
                {
                    var e = ui.FindInApp("ExportStatus");
                    return e is not null && !string.IsNullOrEmpty(e.Name) ? e : null;
                }, "ExportStatus text");
                _output.WriteLine("step 6: status '" + status.Name + "'");
                Assert.StartsWith("Saved: ", status.Name, StringComparison.Ordinal);
                Assert.Contains("diag-test.zip", status.Name, StringComparison.Ordinal);
                Assert.False(File.Exists(target + ".tmp"), "temporary file left behind");

                var entries = ReadZip(target);
                _output.WriteLine("step 7: entries " + string.Join(", ", entries.Keys));
                Assert.Equal(["diagnostics.log", "report.txt", "settings.json", "system.txt"], entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

                foreach (var (name, text) in entries)
                {
                    Assert.DoesNotContain("SENTINEL", text, StringComparison.Ordinal);
                    if (Environment.UserName.Length >= 3)
                        Assert.False(text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), $"{name} contains the user name");
                    // Also the profile's 8.3 short name: %TEMP% on the runner is C:\Users\RUNNER~1\..., and the test data dir is under it.
                    var userFolder = System.Text.RegularExpressions.Regex.Match(text, @"[A-Za-z]:[\\/]Users[\\/](?!%USERNAME%|Public|Default)[^\\/\s]+",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    Assert.False(userFolder.Success, $"{name} contains a user folder: {userFolder.Value}");
                }
                Assert.Contains("https://diag-host.example", entries["settings.json"], StringComparison.Ordinal);

                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (ui.DataDir.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                    Assert.Contains("%USERPROFILE%", entries["system.txt"], StringComparison.Ordinal);
                _output.WriteLine("step 8: no sentinel, no user name, origin kept");
            });
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static IntPtr FindSaveDialog(UiSession ui)
        => Win32.TopLevelWindows(ui.AppProcess.Id, visibleOnly: true).FirstOrDefault(h => Win32.ClassName(h) == "#32770");

    private static T WaitFor<T>(Func<T?> find, string what) where T : class
    {
        var result = Retry.WhileNull(() =>
        {
            try { return find(); }
            catch (Exception) { return null; } // the element can vanish or not exist yet
        }, Timeout, TimeSpan.FromMilliseconds(200));
        return result.Result ?? throw new TimeoutException($"{what} not found within {Timeout.TotalSeconds:F0} s");
    }

    private static IntPtr WaitFor(Func<IntPtr> find, string what)
    {
        var found = IntPtr.Zero;
        var result = Retry.WhileFalse(() => (found = find()) != IntPtr.Zero, Timeout, TimeSpan.FromMilliseconds(200));
        return result.Success ? found : throw new TimeoutException($"{what} not found within {Timeout.TotalSeconds:F0} s");
    }

    private static Dictionary<string, string> ReadZip(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = reader.ReadToEnd();
        }
        return result;
    }

    private static string Name([CallerMemberName] string name = "") => name;
}
