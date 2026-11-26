using System.Windows.Threading;
using BiliIndexTranslate.App.Input;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.App.Tracking;
using BiliIndexTranslate.App.Uia;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using BiliIndexTranslate.Core.Settings;

namespace BiliIndexTranslate.App.Probing;

/// <summary>
/// Asks the UIA observer what is selected (the probe), falls back to the clipboard for explicit requests, turns the
/// answer into a <see cref="ProbeOutcome"/> for the session, and keeps the tracked selection's geometry up to date
/// (the refresh). Owns both watchdog timers and the state of the probe and refresh in flight. UI thread only; the
/// observer's callbacks reach it through <see cref="OnProbeCompleted"/> and <see cref="OnRefreshCompleted"/>.
/// </summary>
internal sealed class ProbeController : IDisposable
{
    private const int ProbeWatchdogMs = 2500;
    private const int ClipboardWatchdogMs = 1500;
    private const int RefreshWatchdogMs = 1000;

    private readonly DiagnosticsLog _log;
    private readonly AppSupportRegistry _registry;
    private readonly Func<AppSettings> _settings;
    private readonly UiaObserver _observer;
    private readonly Func<SourceWindowTracker> _tracker;
    private readonly Action<SessionEvent> _dispatch;
    private readonly Func<IntPtr> _welcomeHwnd;

    private readonly DispatcherTimer _probeWatchdog = new(DispatcherPriority.Background);
    private readonly DispatcherTimer _refreshWatchdog = new(DispatcherPriority.Background);

    private long _probeId;
    private ProbeMode _probeMode;
    private long _trackedSelectionId;
    private string? _pendingClipboardText;
    private PxPoint _lastPointer;
    private long _lastPointerTick;

    /// <param name="settings">The current settings (read at each use).</param>
    /// <param name="tracker">The source window tracker (created after the observer, so it is read at each use).</param>
    /// <param name="dispatch">Sends an event to the reading session.</param>
    /// <param name="welcomeHwnd">Handle of the Welcome window or zero; the UIA reader ignores that window.</param>
    public ProbeController(
        DiagnosticsLog log,
        AppSupportRegistry registry,
        Func<AppSettings> settings,
        UiaObserver observer,
        Func<SourceWindowTracker> tracker,
        Action<SessionEvent> dispatch,
        Func<IntPtr> welcomeHwnd)
    {
        _log = log;
        _registry = registry;
        _settings = settings;
        _observer = observer;
        _tracker = tracker;
        _dispatch = dispatch;
        _welcomeHwnd = welcomeHwnd;
        _probeWatchdog.Tick += (_, _) => OnProbeWatchdog();
        _refreshWatchdog.Tick += (_, _) => OnRefreshWatchdog();
    }

    /// <summary>Process name of the last app a probe read from (or found unsupported or failing); the tray's "current app" falls back to it.</summary>
    public string? LastProcessName { get; private set; }

    /// <summary>A mouse release or explicit shortcut at this point: a selection reported without bounds is anchored here for the next 1.5 s.</summary>
    public void NotePointer(PxPoint point)
    {
        _lastPointer = point;
        _lastPointerTick = Environment.TickCount64;
    }

    /// <summary>The next probe uses this text (tray "translate the clipboard") instead of asking UI Automation.</summary>
    public void SetPendingClipboardText(string text) => _pendingClipboardText = text;

    /// <summary>The session started tracking a card's selection: follow its window.</summary>
    public void BeginTracking(long selectionId, SelectionSnapshot snapshot)
    {
        _trackedSelectionId = selectionId;
        _tracker().Begin(new IntPtr(snapshot.SourceWindow), snapshot.ProcessId);
    }

    public void EndTracking()
    {
        _trackedSelectionId = 0;
        _tracker().End();
        _refreshWatchdog.Stop();
        _observer.ClearTracking();
    }

    /// <summary>Stops both watchdog timers (app shutdown).</summary>
    public void Dispose()
    {
        _probeWatchdog.Stop();
        _refreshWatchdog.Stop();
    }

    // ---- probes -----------------------------------------------------------------------------------

    private ReaderPolicy Policy() => new(_settings().MaxChars, Environment.ProcessId, _welcomeHwnd());

    public void StartProbe(long probeId, ProbeMode mode)
    {
        _probeId = probeId;
        _probeMode = mode;

        if (_pendingClipboardText is { } clip)
        {
            _pendingClipboardText = null;
            _probeId = 0;
            _dispatch(new SessionEvent.ProbeCompleted(probeId, OutcomeForText(clip, SelectionOrigin.Clipboard, explicitRequest: true)));
            return;
        }

        if (!_observer.IsAlive)
        {
            _probeId = 0;
            _dispatch(new SessionEvent.ProbeCompleted(probeId, new ProbeOutcome.Failed("UI Automation thread is not running")));
            return;
        }

        _observer.RequestProbe(probeId, mode, Policy());
        _probeWatchdog.Stop();
        _probeWatchdog.Interval = TimeSpan.FromMilliseconds(ProbeWatchdogMs);
        _probeWatchdog.Start();
    }

    private void OnProbeWatchdog()
    {
        _probeWatchdog.Stop();
        if (_probeId == 0) return;
        var id = _probeId;
        _probeId = 0;
        _log.Warn("probe", "stalled: UI Automation did not answer in time (the source app may be busy)");
        _dispatch(new SessionEvent.ProbeCompleted(id, new ProbeOutcome.Failed("probe stalled")));
    }

    public void OnProbeCompleted(long probeId, ProbeOutcome outcome)
    {
        if (probeId != _probeId) { _log.Info("probe", $"late result #{probeId} ignored"); return; }
        var explicitRequest = _probeMode == ProbeMode.Explicit;

        if (outcome is ProbeOutcome.Selected sel)
        {
            _probeWatchdog.Stop();
            _probeId = 0;
            _dispatch(new SessionEvent.ProbeCompleted(probeId, FinishSelected(sel.Snapshot, explicitRequest)));
            return;
        }

        if (explicitRequest && outcome is ProbeOutcome.NoSelection or ProbeOutcome.Unsupported or ProbeOutcome.Failed)
        {
            // UIA could not read it: the explicit fallback copies once via Ctrl+C.
            _log.Info("probe", $"explicit request, UIA gave {outcome.GetType().Name}; trying clipboard fallback");
            _probeWatchdog.Stop();
            _probeWatchdog.Interval = TimeSpan.FromMilliseconds(ClipboardWatchdogMs);
            _probeWatchdog.Start();
            _ = RunClipboardFallbackAsync(probeId);
            return;
        }

        _probeWatchdog.Stop();
        _probeId = 0;
        RecordOutcome(outcome);
        _dispatch(new SessionEvent.ProbeCompleted(probeId, outcome));
    }

    private async Task RunClipboardFallbackAsync(long probeId)
    {
        string? text = null;
        try { text = await ClipboardFallback.CaptureSelectionAsync(); }
        catch (Exception ex) { _log.Warn("clipboard", $"fallback failed: {ex.GetType().Name}"); }
        if (probeId != _probeId) return; // watchdog or a newer probe took over
        _probeWatchdog.Stop();
        _probeId = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Info("clipboard", "nothing captured");
            _dispatch(new SessionEvent.ProbeCompleted(probeId, new ProbeOutcome.NoSelection()));
            return;
        }
        _dispatch(new SessionEvent.ProbeCompleted(probeId, OutcomeForText(text, SelectionOrigin.Clipboard, explicitRequest: true)));
    }

    private ProbeOutcome FinishSelected(SelectionSnapshot snapshot, bool explicitRequest)
    {
        LastProcessName = snapshot.ProcessName;

        var decision = SelectionFinish.Decide(snapshot, _settings(), explicitRequest, _lastPointer, Environment.TickCount64 - _lastPointerTick);
        if (decision.AnchoredAtPointer)
            _log.Info("probe", $"{snapshot.ProcessName}: selection has no bounds; anchoring near the pointer");
        _registry.RecordSuccess(snapshot.ProcessName, decision.HadGeometry);
        if (decision.Outcome is ProbeOutcome.Selected selected)
        {
            var s = selected.Snapshot;
            _log.Info("probe", $"{s.ProcessName}: {s.Text.Length} chars, {s.Anchor!.LineCount} line(s), {s.ProbeDurationMs:0.0} ms, fp={DiagnosticsLog.Fingerprint(s.Text)}");
        }
        return decision.Outcome;
    }

    private ProbeOutcome OutcomeForText(string text, SelectionOrigin origin, bool explicitRequest)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var pid = NativeMethods.ProcessIdOf(foreground);
        NativeMethods.GetCursorPos(out var pt);
        _lastPointer = new PxPoint(pt.X, pt.Y);
        return SelectionFinish.ForText(text, origin, foreground.ToInt64(), pid, ProcessInfo.NameOf(pid), _lastPointer, _settings(), explicitRequest);
    }

    private void RecordOutcome(ProbeOutcome outcome)
    {
        var fg = NativeMethods.GetForegroundWindow();
        var name = ProcessInfo.NameOf(NativeMethods.ProcessIdOf(fg));
        if (NativeMethods.ProcessIdOf(fg) == Environment.ProcessId) return;
        switch (outcome)
        {
            case ProbeOutcome.Unsupported: _registry.RecordUnsupported(name); LastProcessName = name; break;
            case ProbeOutcome.Failed: _registry.RecordFailure(name); LastProcessName = name; break;
        }
    }

    // ---- geometry refresh ------------------------------------------------------------------------

    public void RequestRefresh()
    {
        if (_trackedSelectionId == 0) return;
        _observer.RequestRefresh(_trackedSelectionId, Policy());
        _refreshWatchdog.Stop();
        _refreshWatchdog.Interval = TimeSpan.FromMilliseconds(RefreshWatchdogMs);
        _refreshWatchdog.Start();
    }

    public void OnRefreshCompleted(long selectionId, GeometryUpdate update)
    {
        _refreshWatchdog.Stop();
        _tracker().RefreshCompleted();
        if (selectionId != _trackedSelectionId) return;
        _dispatch(new SessionEvent.GeometryRefreshed(selectionId, update));
    }

    private void OnRefreshWatchdog()
    {
        _refreshWatchdog.Stop();
        _tracker().RefreshCompleted();
        if (_trackedSelectionId == 0) return;
        _dispatch(new SessionEvent.GeometryRefreshed(_trackedSelectionId, new GeometryUpdate.Unavailable("refresh timed out")));
    }
}
