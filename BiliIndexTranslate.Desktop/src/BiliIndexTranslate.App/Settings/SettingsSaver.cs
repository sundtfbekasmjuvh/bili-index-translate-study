using System.Windows.Threading;
using BiliIndexTranslate.App.Localization;
using BiliIndexTranslate.App.Tray;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Settings;

namespace BiliIndexTranslate.App.Settings;

/// <summary>
/// Writes the settings file: right away (<see cref="TrySave"/>) or about 1.5 s after the last change
/// (<see cref="ScheduleSave"/>, for the card's text size and original toggle). UI thread only.
/// </summary>
internal sealed class SettingsSaver : IDisposable
{
    private readonly SettingsStore _store;
    private readonly Func<AppSettings> _settings;
    private readonly DiagnosticsLog _log;
    private readonly Func<TrayIcon?> _tray;
    private readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background);
    private bool _saveFailureShown;

    /// <param name="settings">The current settings (the host replaces its settings object, so it is read at each save).</param>
    /// <param name="tray">The tray icon, or null while it does not exist yet (start-up): a failed save is then only logged.</param>
    public SettingsSaver(SettingsStore store, Func<AppSettings> settings, DiagnosticsLog log, Func<TrayIcon?> tray)
    {
        _store = store;
        _settings = settings;
        _log = log;
        _tray = tray;
        _saveTimer.Interval = TimeSpan.FromSeconds(1.5);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); TrySave(); };
    }

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes the settings file. Returns null when it was written, otherwise the reason it was not.</summary>
    public string? TrySave(bool notify = true)
    {
        try
        {
            _store.Save(_settings());
            _saveFailureShown = false;
            return null;
        }
        catch (Exception ex)
        {
            _log.Error("settings", $"save failed: {ex.GetType().Name}: {ex.Message}");
            var reason = Loc.T("App_SettingsNotSavedReason", ex.GetType().Name, ex.Message);
            // The Settings window shows the reason itself. Other saves run in the background, so the tray says it,
            // once per run of failures. The tray does not exist yet during start-up; the log entry covers that case.
            if (notify && !_saveFailureShown && _tray() is { } tray)
            {
                _saveFailureShown = true;
                tray.ShowNotice(Loc.T("App_SettingsNotSaved"), reason, warning: true);
            }
            return reason;
        }
    }

    /// <summary>Stops a pending scheduled save (the host then saves once itself).</summary>
    public void Dispose() => _saveTimer.Stop();
}
