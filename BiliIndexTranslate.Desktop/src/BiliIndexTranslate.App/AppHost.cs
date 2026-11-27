using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BiliIndexTranslate.App.AppWindowing;
using BiliIndexTranslate.App.Input;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.App.Popup;
using BiliIndexTranslate.App.Probing;
using BiliIndexTranslate.App.Settings;
using BiliIndexTranslate.App.Themes;
using BiliIndexTranslate.App.Tracking;
using BiliIndexTranslate.App.Tray;
using BiliIndexTranslate.App.Uia;
using BiliIndexTranslate.App.Updates;
using BiliIndexTranslate.App.Vision;
using BiliIndexTranslate.App.Vocabulary;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Localization;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Translation;
using BiliIndexTranslate.Core.Vocabulary;
using BiliIndexTranslate.App.Localization;

namespace BiliIndexTranslate.App;

/// <summary>
/// Composition root and effect executor. Everything here runs on the UI thread; the UIA observer,
/// input hooks and HTTP work post back through <see cref="Post"/>. The <see cref="ReadingSession"/>
/// decides, this class does.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly Application _app;
    private readonly Dispatcher _dispatcher;
    private readonly string _dataDir;
    private readonly bool _forceWelcome;
    private readonly bool _openDiagnostics;
    private readonly bool _openVocabulary;

    private readonly DiagnosticsLog _log = new(300);
    private readonly TimingStats _timings = new(128);
    private readonly AppSupportRegistry _registry = new();
    private readonly SettingsStore _store;
    private readonly VocabularyStore _vocabulary;
    private AppSettings _settings = new AppSettings().Normalize();

    private readonly TranslationCache _cache = new(500, TimeSpan.FromHours(24));
    private readonly HttpTranslationTransport _transport = new();
    private readonly OpenAiCompatibleTranslator _translator;
    private readonly StudyCardService _studyService;
    private readonly VisionTranslationService _visionService;
    private readonly TranslationCoordinator _coordinator;

    private readonly ReadingSession _session = new();
    private readonly Queue<SessionEvent> _queue = new();
    private bool _dispatching;

    private readonly Win32MonitorLocator _monitors = new();
    private readonly ThemeManager _theme;
    private PopupPresenter _popup = null!;
    private readonly SpeechPlayer _speech = new();
    private UiaObserver _observer = null!;
    private InputHookThread _hooks = null!;
    private SourceWindowTracker _tracker = null!;
    private TrayIcon _tray = null!;
    private UpdateService _updates = null!;

    private readonly DispatcherTimer _stabTimer = new(DispatcherPriority.Input);
    private readonly DispatcherTimer _graceTimer = new(DispatcherPriority.Background);

    private long _stabTimerId;
    private long _graceSelectionId;
    private long _triggerTimestamp;
    private long _requestStartTimestamp; // network requests only (not cache hits); cleared once the first text is on screen
    private VisionController _vision = null!;
    private ProbeController _probes = null!;
    private readonly AppWindows _windows;
    private readonly SettingsSaver _saver;

    private StreamWriter? _fileLog;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private bool _started;
    private bool _disposed;

    public AppHost(Application app, string dataDir, bool forceWelcome, bool openDiagnostics = false, bool openVocabulary = false)
    {
        _openVocabulary = openVocabulary;
        _app = app;
        _dispatcher = app.Dispatcher;
        _dataDir = dataDir;
        _forceWelcome = forceWelcome;
        _openDiagnostics = openDiagnostics;
        _store = new SettingsStore(Path.Combine(dataDir, "settings.json"), new DpapiSecretProtector());
        _vocabulary = new VocabularyStore(Path.Combine(dataDir, "vocabulary.json"), _log);
        _translator = new OpenAiCompatibleTranslator(_transport, TimeSpan.FromSeconds(20));
        _studyService = new StudyCardService(_transport, TimeSpan.FromSeconds(30));
        _visionService = new VisionTranslationService(_transport, TimeSpan.FromSeconds(35));
        _coordinator = new TranslationCoordinator(_translator, _cache, Post, _timings, _log, maxConcurrency: 2, postPartial: PostBackground);
        _theme = new ThemeManager(app) { Log = message => _log.Info("app", "theme: " + message) };
        _monitors.Log = _log;
        _speech.Log = _log;
        _saver = new SettingsSaver(_store, () => _settings, _log, () => _tray);
        _windows = new AppWindows(
            _log,
            _timings,
            _registry,
            () => _settings,
            _store.FilePath,
            dataDir,
            TestConnectionAsync,
            s => ApplySettings(s, notifySession: true, notifySaveFailure: false),
            () => _updates,
            () => new AppStatus(_session.State, _hooks.HooksInstalled, _observer.IsAlive, _cache.Count, _cache.Hits, _cache.Misses, _coordinator.ActiveCount),
            () => _saver.TrySave(),
            _vocabulary);
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    /// <summary>Streamed partial text yields to pending rendering and input, so it never starves the UI.</summary>
    private void PostBackground(Action action)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    // ---- startup ----------------------------------------------------------------------------------

    public void Start()
    {
        if (_started) return;
        _started = true;
        _log.Info("app", $"Bili Index Translate {typeof(AppHost).Assembly.GetName().Version} starting on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");

        var load = _store.Load();
        _settings = load.Settings;
        Loc.Instance.Apply(_settings.UiLanguage);
        if (load.Note is not null) _log.Info("settings", load.Note);
        if (load.WasMigrated) { _log.Info("settings", "migrated to schema v2"); _saver.TrySave(); }
        ConfigureFileLogging(_settings.DiagnosticsFileLogging);

        _theme.Apply(_settings.Theme);
        _session.Options = new SessionOptions(_settings.AutoTranslate, _settings.StabilizationDelayMs);

        _popup = new PopupPresenter(
            _monitors,
            _log,
            _timings,
            _speech,
            () => _settings.SourceLanguage,
            (text, modelOverride, onPartial, ct) =>
            {
                var req = BuildRequest(text, _settings);
                if (!string.IsNullOrWhiteSpace(modelOverride))
                    req = req with { Model = modelOverride };
                return _studyService.GenerateAsync(req, ct, onPartial);
            },
            new VocabularyCardActions(_vocabulary, () => _settings));
        _popup.Configure(_settings.TextScale, _settings.ShowOriginalByDefault);
        _vision = new VisionController(
            _popup,
            _log,
            () => _settings,
            _visionService,
            _translator,
            BuildRequest,
            Post,
            PostBackground,
            Dispatch,
            (title, message, warning) => _tray.ShowNotice(title, message, warning: warning));
        _popup.PinRequested += () =>
        {
            if (_vision.IsActive) { _vision.Deactivate(); _popup.Detach(); return; }
            Dispatch(new SessionEvent.UserPinned());
        };
        _popup.CloseRequested += () =>
        {
            if (_vision.IsActive) { _vision.Dismiss(); return; }
            Dispatch(new SessionEvent.UserClosed());
        };
        _popup.RetryRequested += () =>
        {
            if (_vision.RetryLast()) return;
            Dispatch(new SessionEvent.UserRetry());
        };
        _popup.SettingsRequested += _windows.OpenSettings;
        _popup.TextScaleChanged += scale => { _settings.TextScale = scale; _saver.ScheduleSave(); };
        _popup.ShowOriginalChanged += show => { _settings.ShowOriginalByDefault = show; _saver.ScheduleSave(); };
        _popup.VisibleRectsChanged += rects => _hooks.PopupWheelRegions = rects;

        _observer = new UiaObserver(Post, _log, _timings, _settings.UseUiaSelectionEvents);
        _probes = new ProbeController(_log, _registry, () => _settings, _observer, () => _tracker, Dispatch, () => _windows.WelcomeHwnd);
        _observer.ProbeCompleted += _probes.OnProbeCompleted;
        _observer.RefreshCompleted += _probes.OnRefreshCompleted;
        _observer.SelectionChangedByUia += OnUiaSelectionChanged;
        _observer.Start();

        _hooks = new InputHookThread(Post);
        _hooks.InputReceived += OnInput;
        _hooks.WheelOverPopup += (delta, ctrl) => _popup.HandleWheel(delta, ctrl);
        _hooks.HotkeyStatusChanged += OnHotkeyStatus;
        _hooks.SnipHotkeyStatusChanged += OnSnipHotkeyStatus;
        _hooks.Start();
        if (!_hooks.HooksInstalled) _log.Error("input", _hooks.HookError ?? "input hooks not installed");
        ApplyHotkey();
        ApplySnipHotkey();

        _tracker = new SourceWindowTracker();
        _tracker.ForegroundChanged += OnForegroundChanged;
        _tracker.SourceLost += reason => { _log.Info("track", reason); Dispatch(new SessionEvent.SourceWindowLost()); };
        _tracker.RefreshRequested += _probes.RequestRefresh;

        _tray = new TrayIcon();
        _tray.SetAutoTranslate(_settings.AutoTranslate);
        _tray.SetCurrentAppProvider(CurrentAppName);
        _tray.IsAppExcluded = name => _settings.IsProcessExcluded(name);
        _tray.AutoTranslateToggled += enabled => { if (_settings.AutoTranslate != enabled) { _settings.AutoTranslate = enabled; ApplySettings(_settings, notifySession: true); } };
        _tray.ExcludeAppToggled += (name, excluded) => { _settings.SetProcessExcluded(name, excluded); ApplySettings(_settings, notifySession: excluded); _log.Info("settings", $"{name}: {(excluded ? "excluded" : "included")}"); };
        _tray.SnipRegionRequested += _vision.StartSnip;
        _tray.OcrCopyRequested += _vision.StartOcrCopy;
        _tray.TranslateClipboardRequested += TranslateClipboard;
        _tray.SettingsRequested += _windows.OpenSettings;
        _tray.DiagnosticsRequested += _windows.OpenDiagnostics;
        _tray.VocabularyRequested += _windows.OpenVocabulary;
        _tray.WelcomeRequested +=() => _windows.OpenWelcome(force: true);
        _tray.ExitRequested += Exit;

        _stabTimer.Tick += (_, _) => { _stabTimer.Stop(); var id = _stabTimerId; _stabTimerId = 0; Dispatch(new SessionEvent.StabilizationElapsed(id)); };
        _graceTimer.Tick += (_, _) => { _graceTimer.Stop(); Dispatch(new SessionEvent.GeometryGraceElapsed(_graceSelectionId)); };

        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateName);
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Post(() => { _log.Info("app", "second instance signalled; opening settings"); _windows.OpenSettings(); }), null, -1, false);
        }
        catch (Exception ex) { _log.Warn("app", $"activation event unavailable: {ex.GetType().Name}"); }

        if (!_hooks.HooksInstalled)
            _tray.ShowNotice("Bili Index Translate", Loc.T("App_HooksFailed"), warning: true);

        // Updates: only an installed (Velopack) build ever contacts GitHub; for a portable or development run Configure does nothing.
        _updates = new UpdateService(Post, _log);
        _updates.UpdateReady += OnUpdateReady;
        _updates.DownloadFailed += (reason, manual) => { if (manual) _windows.ShowUpdateDownloadFailed(reason); };
        _tray.RestartUpdateRequested += RestartAndUpdate;
        if (_updates.ReadyVersion is not null) _tray.SetUpdateReady(true); // downloaded in an earlier run, not installed yet
        _updates.Configure(_settings.AutoCheckForUpdates);

        _log.Info("app", $"ready · auto={_settings.AutoTranslate} delay={_settings.StabilizationDelayMs}ms target={_settings.TargetLanguage} model={_settings.Model} relay={_settings.UseRelay}");
        if (_forceWelcome || !_settings.OnboardingCompleted) _windows.OpenWelcome(force: false);
        if (_openDiagnostics) _windows.OpenDiagnostics();
        if (_openVocabulary) _windows.OpenVocabulary();

        // Prepare the card window once the start-up work is done, so the first selection does not wait for it.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (_disposed) return;
            try { _popup.Prewarm(); }
            catch (Exception ex) { _log.Warn("render", $"card window prewarm failed ({ex.GetType().Name}); it is created on first use"); }
        });
    }

    // ---- session dispatch -------------------------------------------------------------------------

    private void Dispatch(SessionEvent e)
    {
        if (_disposed) return;
        _queue.Enqueue(e);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_queue.Count > 0)
            {
                var next = _queue.Dequeue();
                IReadOnlyList<SessionEffect> effects;
                try { effects = _session.Handle(next); }
                catch (Exception ex) { _log.Error("session", $"{next.GetType().Name}: {ex.GetType().Name}: {ex.Message}"); continue; }
                foreach (var effect in effects)
                {
                    try { Execute(effect); }
                    catch (Exception ex) { _log.Error("effect", $"{effect.GetType().Name}: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        }
        finally { _dispatching = false; }
    }

    private void Execute(SessionEffect effect)
    {
        switch (effect)
        {
            case SessionEffect.StartStabilizationTimer t:
                _stabTimer.Stop();
                _stabTimerId = t.TimerId;
                _stabTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, t.DelayMs));
                _stabTimer.Start();
                break;

            case SessionEffect.CancelStabilizationTimer t:
                if (_stabTimerId == t.TimerId) { _stabTimer.Stop(); _stabTimerId = 0; }
                break;

            case SessionEffect.RequestProbe p:
                _probes.StartProbe(p.ProbeId, p.Mode);
                break;

            case SessionEffect.ShowLoading s:
                if (_triggerTimestamp != 0)
                {
                    _timings.Record(TimingCategory.Detect, Stopwatch.GetElapsedTime(_triggerTimestamp).TotalMilliseconds);
                    _triggerTimestamp = 0;
                }
                _vision.Reset();
                _popup.ShowLoading(s.Snapshot, Caption(s.Snapshot, null, false));
                break;

            case SessionEffect.StartTranslation st:
                StartTranslation(st.RequestId, st.Snapshot);
                break;

            case SessionEffect.CancelTranslation c:
                _coordinator.Cancel(c.RequestId);
                break;

            case SessionEffect.ShowTranslation show:
                _popup.ShowTranslation(show.Snapshot, show.Result, Caption(show.Snapshot, show.Result.Route, show.Result.FromCache));
                LogFirstText("final");
                break;

            case SessionEffect.ShowPartialTranslation partial:
                _popup.ShowPartialTranslation(partial.Snapshot, partial.PartialText, Caption(partial.Snapshot, null, false, streaming: true));
                LogFirstText("streaming");
                break;

            case SessionEffect.ShowError err:
                _popup.ShowError(err.Snapshot, err.Error, err.Error.Route == RequestRoute.Relay ? "relay" : null);
                break;

            case SessionEffect.HidePopup:
                _popup.Hide();
                break;

            case SessionEffect.BeginTracking b:
                _probes.BeginTracking(b.SelectionId, b.Snapshot);
                break;

            case SessionEffect.EndTracking:
                _probes.EndTracking();
                break;

            case SessionEffect.UpdateAnchor u:
                _popup.UpdateAnchor(u.Anchor);
                break;

            case SessionEffect.StartGeometryGrace g:
                _graceSelectionId = g.SelectionId;
                _graceTimer.Interval = TimeSpan.FromMilliseconds(g.DelayMs);
                _graceTimer.Stop();
                _graceTimer.Start();
                break;

            case SessionEffect.CancelGeometryGrace:
                _graceTimer.Stop();
                break;

            case SessionEffect.DetachPopup:
                _popup.Detach();
                break;

            case SessionEffect.Report r:
                _log.Add(r.Level, r.Category, r.Message);
                break;
        }
    }

    // ---- translation --------------------------------------------------------------------------------

    private static TranslationRequest BuildRequest(string text, AppSettings s)
    {
        var lang = s.ResolvedTargetLanguage;
        return new TranslationRequest(text.Trim(), lang.Code, lang.PromptName, s.SourceLanguage, s.Model, s.BaseUrl, s.UseRelay, s.RelayPrefix, s.ApiKey, s.GlossaryText, s.ApiFormat, s.CustomTemplate);
    }

    private void StartTranslation(long requestId, SelectionSnapshot snapshot)
    {
        var request = BuildRequest(snapshot.Text, _settings);
        if (_coordinator.TryGetCached(request) is { } cached)
        {
            _requestStartTimestamp = 0;
            Dispatch(new SessionEvent.TranslationCompleted(requestId, cached));
            return;
        }
        _requestStartTimestamp = Stopwatch.GetTimestamp();
        _coordinator.Start(
            requestId,
            request,
            (id, outcome) => Dispatch(new SessionEvent.TranslationCompleted(id, outcome)),
            (id, text) => Dispatch(new SessionEvent.TranslationProgress(id, text)));
    }

    /// <summary>Logs, once per network request, how long it took from starting the request to text on the card.</summary>
    private void LogFirstText(string kind)
    {
        if (_requestStartTimestamp == 0) return;
        _log.Info("translate", $"first text on the card {Stopwatch.GetElapsedTime(_requestStartTimestamp).TotalMilliseconds:0} ms after the request started ({kind})");
        _requestStartTimestamp = 0;
    }

    private string? Caption(SelectionSnapshot snapshot, RequestRoute? route, bool fromCache, bool streaming = false)
        => CardCaption.Build(Loc.Instance.Language, snapshot, route, fromCache, _settings.UseRelay, streaming);

    private async Task<(bool Ok, string Message)> TestConnectionAsync(AppSettings s, CancellationToken ct)
    {
        var request = BuildRequest("Hello, world.", s);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _translator.TranslateAsync(request, ct);
            return (true, Loc.T("App_TestOk", $"{result.ElapsedMs:0}", result.Route.ToString().ToLowerInvariant(), Truncate(result.Translation, 40)));
        }
        catch (TranslationException ex)
        {
            var msg = $"{ex.Error.Message}";
            if (ex.Error.Hint is { } hint) msg += " " + hint;
            _log.Warn("test", $"{ex.Error} ({sw.ElapsedMilliseconds} ms)");
            return (false, msg);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---- input & tracking events ------------------------------------------------------------------

    private void OnInput(InputEvent e)
    {
        if (ScreenSnipOverlay.IsActive) return;

        switch (e.Signal)
        {
            case InputSignal.MouseDown:
                if (!_popup.HitTest(e.Point))
                {
                    if (_popup.IsStudyVisible) _popup.HideStudy();
                    if (_vision.IsActive) { _vision.Dismiss(); break; }
                    if (_popup.IsVisible) Dispatch(new SessionEvent.OutsideClick());
                }
                break;

            case InputSignal.MouseUp:
                if (_popup.HitTest(e.Point)) return;
                _probes.NotePointer(e.Point);
                _triggerTimestamp = Stopwatch.GetTimestamp();
                Dispatch(new SessionEvent.TriggerReceived(TriggerKind.MouseUp));
                break;

            case InputSignal.KeyUpSelection:
                _probes.NoteSelectionKey();
                _triggerTimestamp = Stopwatch.GetTimestamp();
                Dispatch(new SessionEvent.TriggerReceived(TriggerKind.KeyUp));
                break;

            case InputSignal.Escape:
                if (_popup.IsStudyVisible) _popup.HideStudy();
                if (_vision.IsActive) { _vision.Dismiss(); break; }
                Dispatch(new SessionEvent.EscapePressed());
                break;

            case InputSignal.Hotkey:
                if (!_settings.HotkeyEnabled) return;
                _probes.NotePointer(e.Point);
                _triggerTimestamp = Stopwatch.GetTimestamp();
                Dispatch(new SessionEvent.TriggerReceived(TriggerKind.Hotkey));
                break;

            case InputSignal.SnipHotkey:
                if (!_settings.SnipHotkeyEnabled) return;
                _vision.StartSnip();
                break;
        }
    }

    private void OnUiaSelectionChanged()
    {
        if (!_settings.UseUiaSelectionEvents || _hooks.IsLeftButtonDown) return; // mouse-up will probe
        _triggerTimestamp = Stopwatch.GetTimestamp();
        Dispatch(new SessionEvent.TriggerReceived(TriggerKind.UiaEvent));
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (!_popup.IsVisible && !_session.IsProbePending) return;
        var pid = NativeMethods.ProcessIdOf(hwnd);
        var isOurs = pid == Environment.ProcessId;
        var root = hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        var isSource = _tracker.IsTracking && (root == _tracker.Root || hwnd == _tracker.Root);
        Dispatch(new SessionEvent.ForegroundChanged(isOurs || isSource));
    }

    private void TranslateClipboard()
    {
        var text = ClipboardFallback.ReadText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            _probes.SetPendingClipboardText(text);
            _triggerTimestamp = Stopwatch.GetTimestamp();
            Dispatch(new SessionEvent.TriggerReceived(TriggerKind.Hotkey));
            return;
        }

        if (ScreenSnipOverlay.TryReadClipboardImageDataUrl(out var imgDataUrl))
        {
            NativeMethods.GetCursorPos(out var pt);
            var anchorBox = new PxRect(pt.X - 24, pt.Y - 12, pt.X + 24, pt.Y + 12);
            _vision.Begin(new ScreenSnipCapture(imgDataUrl, anchorBox));
            return;
        }

        _tray.ShowNotice("Bili Index Translate", Loc.T("App_ClipboardEmpty"));
    }

    private string? CurrentAppName()
    {
        var fg = NativeMethods.GetForegroundWindow();
        var pid = NativeMethods.ProcessIdOf(fg);
        if (pid != 0 && pid != Environment.ProcessId) return ProcessInfo.NameOf(pid);
        return _probes.LastProcessName;
    }

    // ---- settings --------------------------------------------------------------------------------------

    /// <summary>Applies the settings and writes them to disk. Returns why they could not be written, or null.</summary>
    private string? ApplySettings(AppSettings next, bool notifySession, bool notifySaveFailure = true)
    {
        var previous = _settings;
        _settings = next.Normalize();
        var saveError = _saver.TrySave(notifySaveFailure);
        Loc.Instance.Apply(_settings.UiLanguage);

        _session.Options = new SessionOptions(_settings.AutoTranslate, _settings.StabilizationDelayMs);
        _theme.Apply(_settings.Theme);
        _popup.Configure(_settings.TextScale, _settings.ShowOriginalByDefault);
        _tray.SetAutoTranslate(_settings.AutoTranslate);
        _updates?.Configure(_settings.AutoCheckForUpdates);
        if (previous.Hotkey != _settings.Hotkey || previous.HotkeyEnabled != _settings.HotkeyEnabled) ApplyHotkey();
        if (previous.SnipHotkey != _settings.SnipHotkey || previous.SnipHotkeyEnabled != _settings.SnipHotkeyEnabled) ApplySnipHotkey();
        if (previous.UseUiaSelectionEvents != _settings.UseUiaSelectionEvents) _observer.SetSelectionEvents(_settings.UseUiaSelectionEvents);
        if (previous.DiagnosticsFileLogging != _settings.DiagnosticsFileLogging) ConfigureFileLogging(_settings.DiagnosticsFileLogging);

        var serviceChanged = previous.BaseUrl != _settings.BaseUrl || previous.UseRelay != _settings.UseRelay || previous.RelayPrefix != _settings.RelayPrefix
                             || previous.Model != _settings.Model || previous.VisionModel != _settings.VisionModel || previous.GlossaryText != _settings.GlossaryText
                             || previous.TargetLanguage != _settings.TargetLanguage || previous.ApiKey != _settings.ApiKey
                             || previous.ApiFormat != _settings.ApiFormat || previous.CustomTemplate != _settings.CustomTemplate;
        if (serviceChanged)
        {
            _studyService.ClearVolatileCache();
            _visionService.ClearVolatileCache();
            _log.Info("settings", $"service: format={_settings.ApiFormat} target={_settings.TargetLanguage} model={_settings.Model} vision={_settings.VisionModel} relay={_settings.UseRelay} host={ServiceLabels.HostOf(_settings)}");
        }
        if (notifySession) Dispatch(new SessionEvent.SettingsChanged());
        return saveError;
    }

    private void ApplyHotkey()
    {
        if (_settings.HotkeyEnabled && HotkeyParser.TryParse(_settings.Hotkey, out var spec)) _hooks.SetHotkey(spec);
        else _hooks.SetHotkey(null);
    }

    private void ApplySnipHotkey()
    {
        if (_settings.SnipHotkeyEnabled && HotkeyParser.TryParse(_settings.SnipHotkey, out var spec)) _hooks.SetSnipHotkey(spec);
        else _hooks.SetSnipHotkey(null);
    }

    private void OnHotkeyStatus(string display, string? error)
    {
        if (error is null) _log.Info("hotkey", $"{display} registered");
        else
        {
            _log.Warn("hotkey", error + " Automatic translation is unaffected.");
            _tray.ShowNotice(Loc.T("App_ShortcutUnavailable"), Loc.T("App_ShortcutUnavailableHint", error), warning: true);
        }
        _windows.SetHotkeyStatus(error);
    }

    private void OnSnipHotkeyStatus(string display, string? error)
    {
        if (error is null) _log.Info("hotkey", $"vision snip {display} registered");
        else
        {
            _log.Warn("hotkey", error);
            _tray.ShowNotice(Loc.T("App_SnipShortcutUnavailable"), Loc.T("App_SnipShortcutUnavailableHint", error), warning: true);
        }
        _windows.SetSnipHotkeyStatus(error);
    }

    private void ConfigureFileLogging(bool enabled)
    {
        if (!enabled)
        {
            _log.SetSink(null);
            _fileLog?.Dispose();
            _fileLog = null;
            return;
        }
        try
        {
            var path = Path.Combine(_dataDir, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
            _fileLog = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            var writer = _fileLog;
            _log.SetSink(entry => { lock (writer) writer.WriteLine(entry.ToString()); });
            _log.Info("diag", "file logging enabled");
        }
        catch (Exception ex) { _log.Warn("diag", $"file logging unavailable: {ex.GetType().Name}: {ex.Message}"); }
    }

    public void ReportCrash(Exception ex) => _log.Error("app", $"unhandled: {ex.GetType().Name}: {ex.Message}");

    // ---- lifetime -------------------------------------------------------------------------------------

    private void Exit()
    {
        _log.Info("app", "exit requested");
        Dispose();
        // A downloaded update is installed by the updater once this process has ended; no restart.
        _updates?.ApplyOnExit();
        _app.Shutdown();
    }

    private void OnUpdateReady(string version)
    {
        _tray.SetUpdateReady(true);
        _tray.ShowNotice("Bili Index Translate", Loc.T("Update_Ready", version));
        _windows.ShowUpdateDownloaded(version);
    }

    /// <summary>Tray: "Restart and update". Everything is stopped and saved first, because Velopack ends the process at once.</summary>
    private void RestartAndUpdate()
    {
        _log.Info("app", "restart for update requested");
        Dispose();
        if (_updates.ApplyAndRestart()) return;
        _app.Shutdown(); // the updater could not be started: leave the app as a normal exit would
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Dispatch(new SessionEvent.Shutdown()); } catch (Exception) { }
        _disposed = true;

        _stabTimer.Stop();
        SafeDispose(_probes); // stops the probe and refresh watchdogs
        _graceTimer.Stop();
        _saver.Dispose(); // stops a pending scheduled save
        _saver.TrySave();

        SafeDispose(_vision);
        _studyService.ClearVolatileCache();
        _visionService.ClearVolatileCache();
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        SafeDispose(_hooks);
        SafeDispose(_tracker);
        SafeDispose(_observer);
        SafeDispose(_coordinator);
        SafeDispose(_popup);
        SafeDispose(_speech);
        _windows.CloseAll();
        SafeDispose(_tray);
        SafeDispose(_updates);
        SafeDispose(_transport);
        SafeDispose(_theme);
        _log.Info("app", "stopped");
        _fileLog?.Dispose();
        _fileLog = null;
    }

    private static void SafeDispose(IDisposable? d)
    {
        try { d?.Dispose(); } catch (Exception) { }
    }
}
