using System.Text;
using BiliIndexTranslate.App.Diagnostics;
using BiliIndexTranslate.App.Localization;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.App.Onboarding;
using BiliIndexTranslate.App.Settings;
using BiliIndexTranslate.App.Updates;
using BiliIndexTranslate.App.Vocabulary;
using BiliIndexTranslate.Core.Vocabulary;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Session;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Translation;

// The namespace is not "...App.Windows": that name would hide the WinRT root namespace "Windows" (Windows.Media.Ocr ...)
// for every file inside BiliIndexTranslate.App.
namespace BiliIndexTranslate.App.AppWindowing;

/// <summary>What the Welcome window and the diagnostics header read from the running app.</summary>
internal sealed record AppStatus(
    SessionState SessionState,
    bool HooksInstalled,
    bool UiaAlive,
    int CacheEntries,
    long CacheHits,
    long CacheMisses,
    int InFlight);

/// <summary>
/// The app's own windows: Settings, Diagnostics and Welcome (one of each at most). Opening a window that is already
/// open activates it. UI thread only.
/// </summary>
internal sealed class AppWindows
{
    private readonly DiagnosticsLog _log;
    private readonly TimingStats _timings;
    private readonly AppSupportRegistry _registry;
    private readonly Func<AppSettings> _settings;
    private readonly string _settingsFilePath;
    private readonly string _dataDir;
    private readonly Func<AppSettings, CancellationToken, Task<(bool Ok, string Message)>> _testConnection;
    private readonly Func<AppSettings, string?> _applySettings;
    private readonly Func<UpdateService> _updates;
    private readonly Func<AppStatus> _status;
    private readonly Action _save;
    private readonly VocabularyStore _vocabulary;

    private SettingsWindow? _settingsWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private VocabularyWindow? _vocabularyWindow;
    private WelcomeWindow? _welcomeWindow;
    private string? _hotkeyError;
    private string? _snipHotkeyError;

    /// <param name="settings">The current settings (read at each use).</param>
    /// <param name="settingsFilePath">Where the Settings window says the settings are stored.</param>
    /// <param name="dataDir">The data directory (settings, logs); the diagnostics bundle reads its log files.</param>
    /// <param name="testConnection">The Settings window's "Save and test" request.</param>
    /// <param name="applySettings">Applies and saves the settings the Settings window hands over; returns why it could not be saved, or null.</param>
    /// <param name="updates">The update service (created after this object, so it is read when Settings opens).</param>
    /// <param name="status">Live state of the app, read when the Welcome window opens and when the diagnostics header is built.</param>
    /// <param name="save">Writes the settings file now (used when the Welcome window finishes).</param>
    /// <param name="vocabulary">The vocabulary book shown by the Vocabulary window.</param>
    public AppWindows(
        DiagnosticsLog log,
        TimingStats timings,
        AppSupportRegistry registry,
        Func<AppSettings> settings,
        string settingsFilePath,
        string dataDir,
        Func<AppSettings, CancellationToken, Task<(bool Ok, string Message)>> testConnection,
        Func<AppSettings, string?> applySettings,
        Func<UpdateService> updates,
        Func<AppStatus> status,
        Action save,
        VocabularyStore vocabulary)
    {
        _vocabulary = vocabulary;
        _log = log;
        _timings = timings;
        _registry = registry;
        _settings = settings;
        _settingsFilePath = settingsFilePath;
        _dataDir = dataDir;
        _testConnection = testConnection;
        _applySettings = applySettings;
        _updates = updates;
        _status = status;
        _save = save;
    }

    /// <summary>Handle of the Welcome window, or zero when it is not open (the UIA reader ignores that window's text).</summary>
    public IntPtr WelcomeHwnd => _welcomeWindow?.Hwnd ?? IntPtr.Zero;

    /// <summary>Whether the explicit shortcut could be registered; shown in the Settings window (now and when it changes).</summary>
    public void SetHotkeyStatus(string? error)
    {
        _hotkeyError = error;
        _settingsWindow?.SetHotkeyStatus(error);
    }

    public void SetSnipHotkeyStatus(string? error)
    {
        _snipHotkeyError = error;
        _settingsWindow?.SetSnipHotkeyStatus(error);
    }

    public void ShowUpdateDownloaded(string version) => _settingsWindow?.ShowUpdateDownloaded(version);

    public void ShowUpdateDownloadFailed(string reason) => _settingsWindow?.ShowUpdateDownloadFailed(reason);

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        var updates = _updates();
        _settingsWindow = new SettingsWindow(_settings(), _settingsFilePath, _testConnection, _applySettings,
            new SettingsUpdateInfo(updates.IsInstalled, updates.CurrentVersionText, updates.CheckNowAsync));
        _settingsWindow.SetHotkeyStatus(_hotkeyError);
        _settingsWindow.SetSnipHotkeyStatus(_snipHotkeyError);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void OpenDiagnostics()
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }
        _diagnosticsWindow = new DiagnosticsWindow(_log, _timings, _registry, DiagnosticsHeader, _settings, _dataDir, SystemInfo);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    public void OpenVocabulary()
    {
        if (_vocabularyWindow is { IsLoaded: true })
        {
            _vocabularyWindow.Activate();
            return;
        }
        _vocabularyWindow = new VocabularyWindow(_vocabulary, _log);
        _vocabularyWindow.Closed += (_, _) => _vocabularyWindow = null;
        _vocabularyWindow.Show();
        _vocabularyWindow.Activate();
    }

    public void OpenWelcome(bool force)
    {
        if (_welcomeWindow is { IsLoaded: true })
        {
            _welcomeWindow.Activate();
            return;
        }
        _welcomeWindow = new WelcomeWindow();
        if (!_status().HooksInstalled) _welcomeWindow.SetWarning(Loc.T("App_HooksWarning"));
        _welcomeWindow.SettingsRequested += OpenSettings;
        _welcomeWindow.Finished += dontShow =>
        {
            var settings = _settings();
            if (dontShow && !settings.OnboardingCompleted) { settings.OnboardingCompleted = true; _save(); }
        };
        _welcomeWindow.Closed += (_, _) => _welcomeWindow = null;
        _welcomeWindow.Show();
        if (force) _welcomeWindow.Activate();
    }

    /// <summary>Closes whichever of the windows is open (app shutdown).</summary>
    public void CloseAll()
    {
        try { _settingsWindow?.Close(); _diagnosticsWindow?.Close(); _vocabularyWindow?.Close(); _welcomeWindow?.Close(); } catch (Exception) { }
    }

    private string DiagnosticsHeader()
    {
        var status = _status();
        var settings = _settings();
        var sb = new StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"v{typeof(AppHost).Assembly.GetName().Version} · {Environment.OSVersion.Version} · .NET {Environment.Version} · ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"session {status.SessionState} · hooks {(status.HooksInstalled ? "ok" : "MISSING")} · uia {(status.UiaAlive ? "ok" : "stopped")} · ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"cache {status.CacheEntries} entries, {status.CacheHits} hits / {status.CacheMisses} misses · in-flight {status.InFlight} · ");
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"target {settings.TargetLanguage} · {ServiceLabels.ModelLabel(settings)} · {ServiceLabels.HostOf(settings)} · relay {(settings.UseRelay ? "on" : "off")} · auto {(settings.AutoTranslate ? "on" : "off")} · delay {settings.StabilizationDelayMs} ms");
        var monitors = MonitorSummaries();
        if (monitors.Count > 0) sb.Append(" · monitors: ").Append(string.Join(", ", monitors));
        return sb.ToString();
    }

    /// <summary>One "name WxH@scale%" text per monitor (empty when they cannot be listed).</summary>
    private List<string> MonitorSummaries()
    {
        var monitors = new List<string>();
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var m = Win32MonitorLocator.FromHandle(h, _log);
                monitors.Add($"{m.Name.TrimStart('\\', '.')} {m.Bounds.Width}x{m.Bounds.Height}@{m.Scale * 100:0}%");
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception) { }
        return monitors;
    }

    private string SystemInfo()
        => SystemInfoReport.Build(typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "?", _updates().IsInstalled, _settings(), _dataDir, MonitorSummaries());
}
