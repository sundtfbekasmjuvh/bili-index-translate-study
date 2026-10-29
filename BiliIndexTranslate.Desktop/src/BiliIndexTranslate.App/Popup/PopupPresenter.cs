using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BiliIndexTranslate.App.Native;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Study;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.App.Popup;

/// <summary>
/// Owns the reading card window(s), the ephemeral companion Study Card window, and turns anchors
/// into screen positions. One window is reused for the live session; pinned ("keep open") cards
/// are detached copies, capped at three. All placement is done in physical pixels through SetWindowPos
/// so WPF's own DPI/position logic never fights with the anchor math.
/// </summary>
internal sealed class PopupPresenter : IDisposable
{
    private const int MaxPinned = 3;

    private readonly IMonitorLocator _monitors;
    private readonly DiagnosticsLog _log;
    private readonly TimingStats _timings;
    private readonly Func<string, string?, Action<StudyDeckResult>, CancellationToken, Task<StudyDeckResult>>? _studyProvider;
    private readonly Dispatcher _dispatcher;
    private readonly List<PopupWindow> _pinned = new();

    private PopupWindow? _window;
    private AnchorRect? _anchor;
    private PlacementSide? _lastSide;
    private PxRect? _lastWindowRect;
    private bool _visible;
    private bool _placing;
    private double _textScale = 1.0;
    private bool _showOriginal;

    private StudyCardWindow? _studyWindow;
    private PopupWindow? _studyOwner;
    private CancellationTokenSource? _studyCts;
    private long _studySeq;
    private bool _studyVisible;
    private bool _studyPlacing;

    public PopupPresenter(
        IMonitorLocator monitors,
        DiagnosticsLog log,
        TimingStats timings,
        Func<string, string?, Action<StudyDeckResult>, CancellationToken, Task<StudyDeckResult>>? studyProvider = null)
    {
        _monitors = monitors;
        _log = log;
        _timings = timings;
        _studyProvider = studyProvider;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public event Action? PinRequested;
    public event Action? CloseRequested;
    public event Action? RetryRequested;
    public event Action? SettingsRequested;
    public event Action<double>? TextScaleChanged;
    public event Action<bool>? ShowOriginalChanged;
    /// <summary>Fires whenever the set of visible card rectangles (live + pinned + study, physical px) changes.</summary>
    public event Action<PxRect[]>? VisibleRectsChanged;

    public bool IsVisible => _visible;
    public bool IsStudyVisible => _studyVisible;

    public void Configure(double textScale, bool showOriginal)
    {
        _textScale = Math.Clamp(textScale, 0.8, 1.6);
        _showOriginal = showOriginal;
        if (_window is not null)
        {
            _window.ApplyTextScale(_textScale);
            _window.SetShowOriginal(_showOriginal);
        }
        foreach (var p in _pinned) p.ApplyTextScale(_textScale);
        _studyWindow?.ApplyTextScale(_textScale);
    }

    private PopupWindow Window
    {
        get
        {
            if (_window is not null) return _window;
            _window = CreateWindow();
            return _window;
        }
    }

    private StudyCardWindow StudyWindow
    {
        get
        {
            if (_studyWindow is not null) return _studyWindow;
            var sw = new StudyCardWindow();
            sw.EnsureHandle();
            sw.ApplyTextScale(_textScale);
            sw.CloseRequested += HideStudy;
            sw.RetryRequested += () => ReloadStudy(preserveDrag: true);
            sw.SpeedToggleRequested += () => ReloadStudy(preserveDrag: true);
            sw.SizeChanged += (_, _) => { if (_studyVisible && !_studyPlacing && !sw.HasBeenDragged) PlaceStudy(animate: false); };
            sw.Moved += PublishRects;
            _studyWindow = sw;
            return sw;
        }
    }

    private PopupWindow CreateWindow()
    {
        var w = new PopupWindow();
        w.EnsureHandle();
        w.ApplyTextScale(_textScale);
        w.SetShowOriginal(_showOriginal);
        w.PinRequested += () => PinRequested?.Invoke();
        w.CloseRequested += () => { if (ReferenceEquals(w, _window)) CloseRequested?.Invoke(); else ClosePinned(w); };
        w.RetryRequested += () => RetryRequested?.Invoke();
        w.SettingsRequested += () => SettingsRequested?.Invoke();
        w.StudyRequested += () => ToggleStudy(w);
        w.OriginalToggled += show => { _showOriginal = show; ShowOriginalChanged?.Invoke(show); Replace(); };
        w.SizeChanged += (_, _) =>
        {
            if (ReferenceEquals(w, _window) && _visible && !_placing) Replace();
            if (_studyVisible && ReferenceEquals(w, _studyOwner) && _studyWindow is { HasBeenDragged: false }) PlaceStudy(animate: false);
        };
        w.Moved += () =>
        {
            if (_studyVisible && ReferenceEquals(w, _studyOwner) && _studyWindow is { HasBeenDragged: false }) PlaceStudy(animate: false);
            PublishRects();
        };
        return w;
    }

    // ---- state entry points (UI thread) ----------------------------------------------------------

    public void ShowLoading(SelectionSnapshot snapshot, string? caption)
    {
        if (snapshot.Anchor is null) return;
        var w = Window;
        if (_studyVisible && ReferenceEquals(_studyOwner, w)) HideStudy();
        w.ShowLoading(snapshot, caption);
        Present(snapshot.Anchor, firstShow: !_visible);
    }

    public void ShowTranslation(SelectionSnapshot snapshot, TranslationOutcome.Success result, string? caption)
    {
        if (snapshot.Anchor is null) return;
        var sw = Stopwatch.StartNew();
        var w = Window;
        w.ShowTranslation(snapshot, result.Translation, caption);
        Present(snapshot.Anchor, firstShow: !_visible);
        _timings.Record(TimingCategory.Render, sw.Elapsed.TotalMilliseconds);
    }

    public void ShowError(SelectionSnapshot snapshot, TranslationError error, string? caption)
    {
        if (snapshot.Anchor is null) return;
        var w = Window;
        if (_studyVisible && ReferenceEquals(_studyOwner, w)) HideStudy();
        w.ShowError(snapshot, error, caption);
        Present(snapshot.Anchor, firstShow: !_visible);
    }

    public void UpdateAnchor(AnchorRect anchor)
    {
        if (!_visible || _window is null) return;
        if (_anchor is { } old && old.FirstLine == anchor.FirstLine && old.Union == anchor.Union) return;
        _anchor = anchor;
        Place(animate: false);
    }

    public void Hide()
    {
        if (_window is null) return;
        if (_studyVisible && ReferenceEquals(_studyOwner, _window)) HideStudy();
        _visible = false;
        _anchor = null;
        _lastSide = null;
        _lastWindowRect = null;
        if (_window.IsVisible) _window.Hide();
        _window.ResetOpacity();
        PublishRects();
    }

    /// <summary>Converts the live card into a pinned window and starts a fresh live window for the next selection.</summary>
    public void Detach()
    {
        if (_window is null || !_visible) return;
        var pinned = _window;
        _window = null;
        _visible = false;
        _anchor = null;
        _lastSide = null;
        _lastWindowRect = null;
        pinned.SetPinned(true);
        _pinned.Add(pinned);
        while (_pinned.Count > MaxPinned) ClosePinned(_pinned[0]);
        PublishRects();
        _log.Info("popup", $"pinned ({_pinned.Count} open)");
    }

    // ---- Companion Study Card lifecycle ----------------------------------------------------------

    private void ToggleStudy(PopupWindow owner)
    {
        var source = owner.CurrentSource?.Trim();
        if (string.IsNullOrEmpty(source) || _studyProvider is null) return;

        if (_studyVisible && ReferenceEquals(_studyOwner, owner) && string.Equals(_studyWindow?.CurrentSource, source, StringComparison.Ordinal))
        {
            HideStudy();
            return;
        }

        OpenStudy(owner, source);
    }

    private void ReloadStudy(bool preserveDrag)
    {
        if (_studyOwner is not { } owner) return;
        var source = owner.CurrentSource?.Trim();
        if (string.IsNullOrEmpty(source)) return;
        OpenStudy(owner, source, preserveDrag);
    }

    private void OpenStudy(PopupWindow owner, string sourceText, bool preserveDrag = false)
    {
        if (_studyProvider is null) return;

        CancelStudyRequest();
        if (_studyOwner is not null && !ReferenceEquals(_studyOwner, owner))
            _studyOwner.SetStudyOpen(false);

        _studyOwner = owner;
        owner.SetStudyOpen(true);

        var sw = StudyWindow;
        sw.ShowLoading(sourceText, preserveDrag);

        var firstShow = !_studyVisible || !sw.IsVisible;
        if (firstShow)
        {
            var ownerRect = owner.CardPhysicalRect() ?? new PxRect(100, 100, 420, 220);
            var monitor = _monitors.FromRect(ownerRect);
            ApplyStudySizeLimits(sw, monitor);
            sw.ResetOpacity();
            NativeMethods.SetWindowPos(sw.Hwnd, NativeMethods.HWND_TOPMOST, ownerRect.Right, ownerRect.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            _studyPlacing = true;
            try { sw.Show(); }
            finally { _studyPlacing = false; }
        }

        _studyVisible = true;
        PlaceStudy(animate: firstShow);

        var seq = ++_studySeq;
        var modelOverride = sw.PreferFlagshipModel ? "Index-Translate-35B-A3B" : null;
        var cts = new CancellationTokenSource();
        _studyCts = cts;
        _ = RunStudyRequestAsync(seq, owner, sourceText, modelOverride, cts);
    }

    private async Task RunStudyRequestAsync(
        long seq,
        PopupWindow owner,
        string sourceText,
        string? modelOverride,
        CancellationTokenSource cts)
    {
        StudyDeckResult? ok = null;
        TranslationError? error = null;

        try
        {
            ok = await _studyProvider!(
                sourceText,
                modelOverride,
                partial =>
                {
                    if (cts.IsCancellationRequested) return;
                    _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        if (seq != _studySeq || !_studyVisible || !ReferenceEquals(_studyOwner, owner) || _studyWindow is null)
                            return;
                        _studyWindow.ShowPartialDeck(sourceText, partial);
                        PlaceStudy(animate: false);
                    }));
                },
                cts.Token).ConfigureAwait(false);
        }
        catch (TranslationException ex)
        {
            error = ex.Error;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            error = new TranslationError(TranslationErrorKind.Network, $"Unexpected error ({ex.GetType().Name}).", RequestRoute.Direct);
        }

        if (cts.IsCancellationRequested) return;

        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (seq != _studySeq || !_studyVisible || !ReferenceEquals(_studyOwner, owner) || _studyWindow is null)
                return;

            if (ok is not null)
            {
                _log.Info("study", $"ok via {ok.Route} {ok.ElapsedMs:0} ms, {ok.Deck.Cards.Count} card(s)");
                _studyWindow.ShowDeck(sourceText, ok);
                PlaceStudy(animate: false);
            }
            else if (error is not null && error.Kind != TranslationErrorKind.Cancelled)
            {
                _log.Warn("study", $"fail {error}");
                _studyWindow.ShowError(sourceText, error);
                PlaceStudy(animate: false);
            }
        }));
    }

    public void HideStudy()
    {
        CancelStudyRequest();
        _studySeq++;
        if (_studyOwner is not null)
        {
            _studyOwner.SetStudyOpen(false);
            _studyOwner = null;
        }
        _studyVisible = false;
        if (_studyWindow is not null)
        {
            _studyWindow.ClearState();
            if (_studyWindow.IsVisible) _studyWindow.Hide();
            _studyWindow.ResetOpacity();
        }
        PublishRects();
    }

    private void CancelStudyRequest()
    {
        if (_studyCts is null) return;
        try { _studyCts.Cancel(); } catch (Exception) { }
        _studyCts.Dispose();
        _studyCts = null;
    }

    private void PlaceStudy(bool animate)
    {
        if (!_studyVisible || _studyWindow is null || _studyOwner is null) return;
        if (_studyOwner.CardPhysicalRect() is not { } ownerCard) return;

        _studyPlacing = true;
        try
        {
            var monitor = _monitors.FromRect(ownerCard);
            ApplyStudySizeLimits(_studyWindow, monitor);
            _studyWindow.UpdateLayout();

            if (!NativeMethods.GetWindowRect(_studyWindow.Hwnd, out var wr)) return;
            var scale = _studyWindow.DpiScale();
            var inset = (int)Math.Round(StudyCardWindow.ShadowMarginLogical * scale);
            var windowW = wr.Right - wr.Left;
            var windowH = wr.Bottom - wr.Top;
            var cardW = Math.Max(1, windowW - 2 * inset);
            var cardH = Math.Max(1, windowH - 2 * inset);

            if (!_studyWindow.HasBeenDragged)
            {
                var placement = CompanionPopupPlacer.Place(ownerCard, cardW, cardH, PxInsets.Uniform(inset), monitor);
                var rect = placement.WindowRect;
                NativeMethods.SetWindowPos(_studyWindow.Hwnd, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }

            if (animate || _studyWindow.Opacity < 1) _studyWindow.FadeIn();
            PublishRects();
        }
        finally
        {
            _studyPlacing = false;
        }
    }

    private static void ApplyStudySizeLimits(StudyCardWindow w, MonitorArea monitor)
    {
        var (maxW, maxH) = CompanionPopupPlacer.MaxCardSize(monitor);
        var scale = monitor.Scale;
        w.SetSizeLimits(maxW / scale, Math.Max(140, maxH / scale - 76));
    }

    // ---- Wheel, HitTest & Surfaces ---------------------------------------------------------------

    public void HandleWheel(int delta, bool ctrl)
    {
        if (ctrl)
        {
            var next = Math.Clamp(Math.Round((_textScale + (delta > 0 ? 0.1 : -0.1)) * 10) / 10, 0.8, 1.6);
            if (Math.Abs(next - _textScale) < 0.01) return;
            _textScale = next;
            _window?.ApplyTextScale(next);
            foreach (var p in _pinned) p.ApplyTextScale(next);
            _studyWindow?.ApplyTextScale(next);
            TextScaleChanged?.Invoke(next);
            return;
        }
        // Scroll whichever of our windows (translation, pinned, or study card) is under the cursor.
        NativeMethods.GetCursorPos(out var pt);
        var target = AllSurfaces().FirstOrDefault(w => w.CardPhysicalRect() is { } r && r.Contains(pt.X, pt.Y));
        target?.ScrollBy(delta);
    }

    /// <summary>True when the point lies inside any visible card (live, pinned, or study card).</summary>
    public bool HitTest(PxPoint p) => AllSurfaces().Any(w => w.CardPhysicalRect() is { } r && r.Contains(p));

    private void PublishRects()
    {
        var rects = AllSurfaces().Select(w => w.CardPhysicalRect()).Where(r => r.HasValue).Select(r => r!.Value).ToArray();
        VisibleRectsChanged?.Invoke(rects);
    }

    public bool IsOurWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && AllSurfaces().Any(w => w.Hwnd == hwnd);

    public PxRect? LiveRect() => _visible ? _window?.PhysicalRect() : null;

    private IEnumerable<IPopupSurface> AllSurfaces()
    {
        if (_window is not null && _visible) yield return _window;
        foreach (var p in _pinned) yield return p;
        if (_studyWindow is not null && _studyVisible) yield return _studyWindow;
    }

    private void ClosePinned(PopupWindow w)
    {
        if (_studyVisible && ReferenceEquals(_studyOwner, w)) HideStudy();
        _pinned.Remove(w);
        try { w.Close(); } catch (Exception) { /* already closed */ }
        PublishRects();
    }

    // ---- Primary card placement ------------------------------------------------------------------

    private void Present(AnchorRect anchor, bool firstShow)
    {
        _anchor = anchor;
        var w = Window;
        var monitor = _monitors.FromRect(anchor.Union);
        ApplySizeLimits(w, monitor);

        if (firstShow || !w.IsVisible)
        {
            // Park the (still fully transparent) window on the target monitor so WPF lays it out at that DPI.
            w.ResetOpacity();
            NativeMethods.SetWindowPos(w.Hwnd, NativeMethods.HWND_TOPMOST, anchor.FirstLine.Left, anchor.FirstLine.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            _placing = true;
            try { w.Show(); }
            finally { _placing = false; }
            _visible = true;
            Place(animate: true);
        }
        else
        {
            _visible = true;
            Place(animate: false);
        }
    }

    private void Replace()
    {
        if (_anchor is { } && _visible) { Place(animate: false); }
    }

    private void Place(bool animate)
    {
        if (_window is null || _anchor is not { } anchor) return;
        _placing = true;
        try
        {
            _window.UpdateLayout();
            if (!NativeMethods.GetWindowRect(_window.Hwnd, out var wr)) return;
            var scale = _window.DpiScale();
            var inset = (int)Math.Round(PopupWindow.ShadowMarginLogical * scale);
            var windowW = wr.Right - wr.Left;
            var windowH = wr.Bottom - wr.Top;
            var cardW = Math.Max(1, windowW - 2 * inset);
            var cardH = Math.Max(1, windowH - 2 * inset);
            var monitor = _monitors.FromRect(anchor.Union);
            var placement = PopupPlacer.Place(anchor, cardW, cardH, PxInsets.Uniform(inset), monitor, _lastSide);
            _lastSide = placement.Side;

            var rect = placement.WindowRect;
            if (_lastWindowRect is { } last && last == rect && _window.Opacity > 0) return;
            _lastWindowRect = rect;
            NativeMethods.SetWindowPos(_window.Hwnd, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            var monitorAfter = _monitors.FromRect(rect);
            if (Math.Abs(monitorAfter.Scale - monitor.Scale) > 0.01) ApplySizeLimits(_window, monitorAfter);

            if (animate || _window.Opacity < 1) _window.FadeIn();
            if (_studyVisible && ReferenceEquals(_studyOwner, _window) && _studyWindow is { HasBeenDragged: false })
                PlaceStudy(animate: false);
            PublishRects();
        }
        finally { _placing = false; }
    }

    private static void ApplySizeLimits(PopupWindow w, MonitorArea monitor)
    {
        var (maxW, maxH) = PopupPlacer.MaxCardSize(monitor);
        var scale = monitor.Scale;
        w.SetSizeLimits(maxW / scale, Math.Max(48, maxH / scale - 56));
    }

    public void Dispose()
    {
        HideStudy();
        if (_studyWindow is not null)
        {
            try { _studyWindow.Close(); } catch (Exception) { }
            _studyWindow = null;
        }
        foreach (var p in _pinned.ToList()) ClosePinned(p);
        if (_window is not null)
        {
            try { _window.Close(); } catch (Exception) { }
            _window = null;
        }
    }
}
