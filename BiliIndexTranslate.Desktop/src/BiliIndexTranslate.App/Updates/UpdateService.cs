using System.Reflection;
using System.Windows.Threading;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace BiliIndexTranslate.App.Updates;

/// <summary>
/// Updates through Velopack. Active only in a build that the Velopack installer put on the machine
/// (<see cref="IsInstalled"/>); a portable zip or a development run never contacts GitHub.
/// <para>
/// Everything except the Velopack calls themselves runs on the UI thread (the same rule as <c>AppHost</c>):
/// the check and the download run on the thread pool and report back through <c>post</c>. Log lines carry
/// versions and short error text only (see <see cref="UpdateErrorText"/>), never URLs or tokens.
/// </para>
/// </summary>
internal sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/sundtfbekasmjuvh/bili-index-translate-study";

    private readonly Action<Action> _post;
    private readonly DiagnosticsLog _log;
    private readonly UpdateManager? _manager;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly CancellationTokenSource _cts = new();

    private bool _enabled;
    private bool _configured;
    private bool _busy;
    private bool _disposed;
    private bool _downloadIsManual;
    private DateTimeOffset? _lastCheck;
    private VelopackAsset? _ready;

    public UpdateService(Action<Action> post, DiagnosticsLog log)
    {
        _post = post;
        _log = log;
        try
        {
            // No access token and no pre-releases: only published releases of the public repository are seen.
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            if (_manager.IsInstalled && _manager.UpdatePendingRestart is { } pending) _ready = pending;
        }
        catch (Exception ex)
        {
            _log.Warn("update", $"updater unavailable: {UpdateErrorText.Describe(ex)}");
            _manager = null;
        }
        _timer.Tick += (_, _) => { _timer.Stop(); _ = CheckCoreAsync(manual: false); };
        if (_ready is not null) _log.Info("update", $"version {_ready.Version} was downloaded earlier and waits for the next restart");
    }

    /// <summary>False for a portable build and for a development run.</summary>
    public bool IsInstalled => _manager is { IsInstalled: true };

    /// <summary>The installed version, or the version of this assembly when the app was not installed.</summary>
    public string CurrentVersionText
    {
        get
        {
            try
            {
                if (_manager?.CurrentVersion is { } v) return v.ToString();
            }
            catch (Exception) { /* fall through to the assembly version */ }
            var asm = Assembly.GetExecutingAssembly().GetName().Version;
            return asm is null ? "?" : $"{asm.Major}.{asm.Minor}.{asm.Build}";
        }
    }

    /// <summary>Version of the downloaded update that waits to be installed, or null.</summary>
    public string? ReadyVersion => _ready?.Version.ToString();

    /// <summary>A new version has been downloaded (raised once per download, on the UI thread).</summary>
    public event Action<string>? UpdateReady;

    /// <summary>A download that follows a check has failed; the bool tells whether the user started that check.</summary>
    public event Action<string, bool>? DownloadFailed;

    /// <summary>Turns the automatic checks on or off and plans the next one.</summary>
    public void Configure(bool autoCheck)
    {
        // Settings are applied after every change (tray toggles too); an unchanged value must not restart the timer.
        if (_configured && _enabled == autoCheck) return;
        _configured = true;
        _enabled = autoCheck;
        Reschedule();
    }

    /// <summary>The "Check for updates" button. Always reports a result; starts the download when there is a newer version.</summary>
    public Task<UpdateCheckOutcome> CheckNowAsync() => CheckCoreAsync(manual: true);

    private void Reschedule()
    {
        _timer.Stop();
        if (_disposed || _busy) return;
        var plan = UpdateSchedule.Next(IsInstalled, _enabled && _ready is null, _lastCheck, DateTimeOffset.UtcNow);
        switch (plan.Action)
        {
            case UpdateCheckAction.Now:
                _post(() => { if (!_disposed && !_busy) _ = CheckCoreAsync(manual: false); });
                break;
            case UpdateCheckAction.After:
                _timer.Interval = plan.Delay;
                _timer.Start();
                break;
        }
    }

    private async Task<UpdateCheckOutcome> CheckCoreAsync(bool manual)
    {
        if (_disposed || _manager is null || !IsInstalled) return new UpdateCheckOutcome(UpdateCheckStatus.NotInstalled);
        if (_ready is not null) return new UpdateCheckOutcome(UpdateCheckStatus.Downloaded, _ready.Version.ToString());
        if (_busy) return new UpdateCheckOutcome(UpdateCheckStatus.Busy);

        _busy = true;
        var ct = _cts.Token;
        UpdateInfo? info;
        try
        {
            _log.Info("update", $"checking (current {CurrentVersionText}, {(manual ? "manual" : "automatic")})");
            var manager = _manager;
            info = await Task.Run(() => manager.CheckForUpdatesAsync(), ct).WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            return new UpdateCheckOutcome(UpdateCheckStatus.Cancelled);
        }
        catch (Exception ex)
        {
            var reason = UpdateErrorText.Describe(ex);
            _log.Warn("update", $"check failed: {reason}");
            _lastCheck = DateTimeOffset.UtcNow;
            _busy = false;
            Reschedule();
            return new UpdateCheckOutcome(UpdateCheckStatus.Failed, Reason: reason);
        }

        _lastCheck = DateTimeOffset.UtcNow;
        if (info is null)
        {
            _log.Info("update", "no newer version");
            _busy = false;
            Reschedule();
            return new UpdateCheckOutcome(UpdateCheckStatus.UpToDate);
        }

        var version = info.TargetFullRelease.Version.ToString();
        _log.Info("update", $"version {version} found; downloading");
        _downloadIsManual = manual;
        _ = DownloadAsync(info, version, ct); // _busy stays set until the download ends
        return new UpdateCheckOutcome(UpdateCheckStatus.Downloading, version);
    }

    private async Task DownloadAsync(UpdateInfo info, string version, CancellationToken ct)
    {
        try
        {
            var manager = _manager!;
            var lastLogged = -1;
            await Task.Run(() => manager.DownloadUpdatesAsync(info, p =>
            {
                if (p / 25 == lastLogged) return;
                lastLogged = p / 25;
                _log.Info("update", $"download {p}%");
            }, ct), ct).WaitAsync(ct);

            _ready = info.TargetFullRelease;
            _busy = false;
            _log.Info("update", $"version {version} downloaded; installs when the app exits");
            _post(() => { if (!_disposed) UpdateReady?.Invoke(version); });
        }
        catch (OperationCanceledException)
        {
            _busy = false;
        }
        catch (Exception ex)
        {
            var reason = UpdateErrorText.Describe(ex);
            _log.Warn("update", $"download of {version} failed: {reason}");
            _busy = false;
            var manual = _downloadIsManual;
            _post(() => { if (!_disposed) DownloadFailed?.Invoke(reason, manual); });
            Reschedule();
        }
    }

    /// <summary>
    /// The user chose "Restart and update". Exits the process at once through Velopack, so the caller must
    /// have stopped and saved everything before. Returns only if the update could not be started.
    /// </summary>
    public bool ApplyAndRestart()
    {
        if (_manager is null || _ready is null) return false;
        try
        {
            _manager.ApplyUpdatesAndRestart(_ready);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("update", $"restart for update failed: {UpdateErrorText.Describe(ex)}");
            return false;
        }
    }

    /// <summary>The user exits normally: a downloaded update is installed after this process has ended, without a restart.</summary>
    public void ApplyOnExit()
    {
        if (_manager is null || _ready is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
            _log.Info("update", $"version {_ready.Version} will be installed after exit");
        }
        catch (Exception ex)
        {
            _log.Error("update", $"scheduling the update failed: {UpdateErrorText.Describe(ex)}");
        }
    }

    /// <summary>Stops the timer and cancels a running check or download. A downloaded update stays available to <see cref="ApplyOnExit"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { _cts.Cancel(); } catch (Exception) { }
    }
}
