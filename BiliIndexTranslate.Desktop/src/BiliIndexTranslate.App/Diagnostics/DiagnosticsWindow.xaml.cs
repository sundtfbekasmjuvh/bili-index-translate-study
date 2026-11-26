using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.App.Localization;

namespace BiliIndexTranslate.App.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsLog _log;
    private readonly TimingStats _timings;
    private readonly AppSupportRegistry _registry;
    private readonly Func<string> _header;
    private readonly Func<AppSettings> _settings;
    private readonly string _dataDir;
    private readonly Func<string> _systemInfo;
    private readonly DispatcherTimer _refresh;
    private bool _dirty = true;

    public DiagnosticsWindow(DiagnosticsLog log, TimingStats timings, AppSupportRegistry registry, Func<string> header,
        Func<AppSettings> settings, string dataDir, Func<string> systemInfo)
    {
        InitializeComponent();
        _log = log;
        _timings = timings;
        _registry = registry;
        _header = header;
        _settings = settings;
        _dataDir = dataDir;
        _systemInfo = systemInfo;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refresh.Tick += (_, _) => { if (_dirty) Render(); };
        _log.EntryAdded += OnEntry;
        Loaded += (_, _) => { Render(); _refresh.Start(); };
        Closed += (_, _) => { _refresh.Stop(); _log.EntryAdded -= OnEntry; };
    }

    private void OnEntry(DiagnosticEntry entry) => _dirty = true;

    private void Render()
    {
        _dirty = false;
        Summary.Text = _header();
        TimingsBox.Text = RenderTimings();
        AppsBox.Text = RenderApps();
        var atEnd = LogBox.VerticalOffset >= LogBox.ExtentHeight - LogBox.ViewportHeight - 4;
        LogBox.Text = _log.Render();
        if (atEnd) LogBox.ScrollToEnd();
    }

    private string RenderTimings()
    {
        var all = _timings.All();
        if (all.Count == 0) return Loc.T("Diag_NoSamples");
        var sb = new StringBuilder();
        sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"{"category",-9} {"n",5} {"p50",8} {"p95",8} {"max",8} {"last",8}");
        foreach (var t in all)
            sb.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"{t.Category,-9} {t.Count,5} {t.P50,8:0.0} {t.P95,8:0.0} {t.Max,8:0.0} {t.Last,8:0.0}");
        return sb.ToString().TrimEnd();
    }

    private string RenderApps()
    {
        var apps = _registry.Snapshot();
        if (apps.Count == 0) return Loc.T("Diag_NoApps");
        var sb = new StringBuilder();
        foreach (var a in apps) sb.AppendLine(a.ToString());
        return sb.ToString().TrimEnd();
    }

    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_header());
        sb.AppendLine();
        sb.AppendLine("== Timings ==");
        sb.AppendLine(RenderTimings());
        sb.AppendLine();
        sb.AppendLine("== Applications ==");
        sb.AppendLine(RenderApps());
        sb.AppendLine();
        sb.AppendLine("== Log ==");
        sb.Append(_log.Render());
        return sb.ToString();
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetDataObject(BuildReport(), true); }
        catch (Exception) { /* clipboard busy */ }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        _log.Info("diag", "log view cleared (buffer keeps rolling)");
    }

    private const int DiagnosticsLogTailBytes = 2 * 1024 * 1024;
    private const int CrashLogTailBytes = 512 * 1024;

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("Diag_ExportTitle"),
                Filter = "ZIP (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "BiliIndexTranslate-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            if (dialog.ShowDialog(this) != true) return;

            ExportDiagnosticsButton.IsEnabled = false;
            // Everything that touches the app's state is read here on the UI thread; only the file work moves off it.
            var report = BuildReport();
            var system = _systemInfo();
            var settingsJson = SettingsRedactor.ToJson(_settings());
            var dataDir = _dataDir;
            var target = dialog.FileName;
            var scrubber = new PrivacyScrubber(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

            var (files, bytes) = await Task.Run(() => WriteBundle(target, dataDir, report, system, settingsJson, scrubber));
            _log.Info("diag", $"diagnostics bundle exported ({files} files, {bytes} bytes)");
            ShowExportStatus(Loc.T("Diag_ExportSaved", Path.GetFileName(target)));
        }
        catch (Exception ex)
        {
            ShowExportStatus(Loc.T("Diag_ExportFailed", ex.Message));
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private static (int Files, long Bytes) WriteBundle(string target, string dataDir, string report, string system, string settingsJson, PrivacyScrubber scrubber)
    {
        var entries = new List<(string Name, string Text)> { ("report.txt", report) };
        if (DiagnosticBundle.ReadTail(Path.Combine(dataDir, "diagnostics.log"), DiagnosticsLogTailBytes) is { } diagnosticsLog)
            entries.Add(("diagnostics.log", diagnosticsLog));
        if (DiagnosticBundle.ReadTail(Path.Combine(dataDir, "crash.log"), CrashLogTailBytes) is { } crashLog)
            entries.Add(("crash.log", crashLog));
        entries.Add(("system.txt", system));
        entries.Add(("settings.json", settingsJson));

        var tmp = target + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                DiagnosticBundle.Write(fs, entries, scrubber);
            var size = new FileInfo(tmp).Length;
            File.Move(tmp, target, overwrite: true);
            return (entries.Count, size);
        }
        catch (Exception)
        {
            try { File.Delete(tmp); } catch (Exception) { /* best effort */ }
            throw;
        }
    }

    private void ShowExportStatus(string text)
    {
        ExportStatus.Text = text;
        ExportStatus.Visibility = Visibility.Visible;
    }
}
