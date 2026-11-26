using System.Text;
using BiliIndexTranslate.App.Input;
using BiliIndexTranslate.App.Localization;
using BiliIndexTranslate.App.Ocr;
using BiliIndexTranslate.App.Popup;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Session;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.App.Vision;

/// <summary>
/// Screen snip → translation of the picture (cloud vision or local OCR), and "recognize screen text and copy it".
/// Owns the state of one vision card (<c>_visionCts</c>, <c>_visionSeq</c>, <c>_visionActive</c>, last capture) and the
/// OCR-copy token. Everything here runs on the UI thread; results come back through the <c>post</c> delegates that
/// <see cref="AppHost"/> uses for all its own work, so threading and ordering are the same as when this code lived there.
/// </summary>
internal sealed class VisionController : IDisposable
{
    private readonly PopupPresenter _popup;
    private readonly DiagnosticsLog _log;
    private readonly Func<AppSettings> _settings;
    private readonly VisionTranslationService _visionService;
    private readonly OpenAiCompatibleTranslator _translator;
    private readonly Func<string, AppSettings, TranslationRequest> _buildRequest;
    private readonly Action<Action> _post;
    private readonly Action<Action> _postBackground;
    private readonly Action<SessionEvent> _dispatch;
    private readonly Action<string, string, bool> _showNotice;

    private CancellationTokenSource? _visionCts;
    private CancellationTokenSource? _ocrCopyCts;
    private long _visionSeq;
    private bool _visionActive;
    private ScreenSnipCapture? _lastVisionCapture;

    /// <param name="settings">The current settings (read at each use, as the host replaces its settings object).</param>
    /// <param name="buildRequest">Builds a text translation request from recognized text (local OCR path).</param>
    /// <param name="post">Runs an action on the UI thread at normal priority (dropped after the host is disposed).</param>
    /// <param name="postBackground">Same at background priority, for streamed partial text.</param>
    /// <param name="dispatch">Sends an event to the reading session.</param>
    /// <param name="showNotice">Tray notice: title, message, warning.</param>
    public VisionController(
        PopupPresenter popup,
        DiagnosticsLog log,
        Func<AppSettings> settings,
        VisionTranslationService visionService,
        OpenAiCompatibleTranslator translator,
        Func<string, AppSettings, TranslationRequest> buildRequest,
        Action<Action> post,
        Action<Action> postBackground,
        Action<SessionEvent> dispatch,
        Action<string, string, bool> showNotice)
    {
        _popup = popup;
        _log = log;
        _settings = settings;
        _visionService = visionService;
        _translator = translator;
        _buildRequest = buildRequest;
        _post = post;
        _postBackground = postBackground;
        _dispatch = dispatch;
        _showNotice = showNotice;
    }

    /// <summary>A vision card (cloud or local) is the current card.</summary>
    public bool IsActive => _visionActive;

    /// <summary>Card retry key: starts the last capture again. False when no vision card is active (the session retries then).</summary>
    public bool RetryLast()
    {
        if (_visionActive && _lastVisionCapture is { } cap) { Begin(cap); return true; }
        return false;
    }

    /// <summary>The card was pinned: it stops being a vision card and its request is cancelled (the capture is kept).</summary>
    public void Deactivate()
    {
        _visionActive = false;
        CancelVisionRequest();
    }

    /// <summary>A text card is about to show: forget the vision card and its capture and cancel its request.</summary>
    public void Reset()
    {
        _visionActive = false;
        _lastVisionCapture = null;
        CancelVisionRequest();
    }

    public void Dispose()
    {
        CancelVisionRequest();
        CancelOcrCopy();
    }

    // ---- Zero-OCR Vision Screen Snip --------------------------------------------------------------

    public void StartSnip()
    {
        if (ScreenSnipOverlay.IsActive) return;

        if (_popup.IsVisible) _dispatch(new SessionEvent.UserClosed());
        Dismiss();

        var capture = ScreenSnipOverlay.CaptureInteractive(message => _log.Info("snip", message));
        if (capture is not { } cap || string.IsNullOrWhiteSpace(cap.DataUrl))
            return;

        Begin(cap);
    }

    public void Begin(ScreenSnipCapture capture)
    {
        CancelVisionRequest();
        if (_popup.IsVisible && !_visionActive)
            _dispatch(new SessionEvent.UserClosed());

        _visionActive = true;
        _lastVisionCapture = capture;

        var anchor = new AnchorRect(capture.ScreenRect, capture.ScreenRect, 1, Math.Max(20, capture.ScreenRect.Height));
        var snapshot = new SelectionSnapshot(
            string.Empty,
            anchor,
            0,
            Environment.ProcessId,
            "ScreenSnip",
            EditableKind.NotEditable,
            SelectionOrigin.ScreenSnip,
            0);

        var settings = _settings();
        var route = VisionRouting.Decide(settings.VisionMode, settings.ApiFormat);
        if (route == VisionRoute.Unsupported)
        {
            // Cloud-only mode with a DeepLX or custom endpoint: nothing is sent; say what to change.
            _visionSeq++;
            var unsupported = DeeplxProtocol.Unsupported("Image translation", settings.UseRelay ? RequestRoute.Relay : RequestRoute.Direct, settings.ApiFormat);
            _log.Warn("vision", $"unsupported: mode={settings.VisionMode} format={settings.ApiFormat}");
            _popup.ShowError(snapshot, unsupported with { Message = unsupported.Message + " " + Loc.T("Vision_CloudUnsupportedHint") }, null);
            return;
        }

        if (route == VisionRoute.Local)
        {
            var localSeq = ++_visionSeq;
            var localCts = new CancellationTokenSource();
            _visionCts = localCts;
            _popup.ShowLoading(snapshot, Loc.T("Vision_LoadingLocal"));
            _ = RunLocalVisionAsync(localSeq, snapshot, capture.DataUrl, settings.Clone(), localCts);
            return;
        }

        var target = settings.ResolvedTargetLanguage;
        var visionModel = VisionPromptBuilder.ResolveVisionModel(settings.VisionModel);
        _popup.ShowLoading(snapshot, Loc.T("Vision_Loading", visionModel));

        var req = new ImageTranslationRequest(
            capture.DataUrl,
            target.Code,
            target.PromptName,
            visionModel,
            settings.BaseUrl,
            settings.UseRelay,
            settings.RelayPrefix,
            settings.ApiKey,
            settings.GlossaryText,
            settings.ApiFormat);

        var seq = ++_visionSeq;
        var cts = new CancellationTokenSource();
        _visionCts = cts;
        _ = RunVisionRequestAsync(seq, snapshot, req, cts);
    }

    private async Task RunVisionRequestAsync(
        long seq,
        SelectionSnapshot baseSnapshot,
        ImageTranslationRequest req,
        CancellationTokenSource cts)
    {
        ImageTranslationResult? ok = null;
        TranslationError? error = null;

        // Deltas arrive faster than the popup can be laid out and placed. The popup is given the newest
        // one after the pending rendering and input have been handled, and the ones in between are skipped.
        var live = new LatestValueRelay<ImageTranslationDelta>(
            deliver => _postBackground(deliver),
            delta =>
            {
                if (seq != _visionSeq || !_visionActive) return;
                var liveSnap = string.IsNullOrWhiteSpace(delta.ExtractedSourceText)
                    ? baseSnapshot
                    : baseSnapshot with { Text = delta.ExtractedSourceText };
                _popup.ShowVisionTranslation(liveSnap, delta.Translation, Loc.T("Vision_Streaming", delta.Model));
            });

        try
        {
            ok = await _visionService.TranslateImageAsync(
                req,
                cts.Token,
                delta =>
                {
                    if (!cts.IsCancellationRequested) live.Offer(delta);
                }).ConfigureAwait(false);
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
            error = new TranslationError(TranslationErrorKind.Network, Loc.T("App_UnexpectedVisionError", ex.GetType().Name), RequestRoute.Direct);
        }

        if (cts.IsCancellationRequested) return;

        _post(() =>
        {
            // The result below is final; a delta that is still waiting must not be drawn over it.
            live.Clear();
            if (seq != _visionSeq || !_visionActive) return;

            if (ok is not null)
            {
                _log.Info("vision", $"ok via {ok.Route} {ok.ElapsedMs:0} ms ({ok.Model}), srcChars={ok.ExtractedSourceText.Length}");
                var finalSnap = baseSnapshot with { Text = ok.ExtractedSourceText };
                var glossaryPart = ok.GlossaryHitCount > 0 ? " · " + Loc.T("Caption_Glossary", ok.GlossaryHitCount) : string.Empty;
                var speedPart = ok.IsCached ? " · " + Loc.T("Caption_Instant") : $" · {ok.Route.ToString().ToLowerInvariant()}";
                _popup.ShowVisionTranslation(finalSnap, ok.Translation, $"{ok.Model} · {Loc.T("Vision_Tag")}{glossaryPart}{speedPart}");
            }
            else if (error is not null && error.Kind != TranslationErrorKind.Cancelled)
            {
                _log.Warn("vision", $"fail {error}");
                _popup.ShowError(baseSnapshot, error, error.Route == RequestRoute.Relay ? "relay" : null);
            }
        });
    }

    // ---- Local OCR: read the image on this PC, then translate the recognized text -------------------

    /// <summary>
    /// Same lifecycle as <see cref="RunVisionRequestAsync"/>: the token in <see cref="_visionCts"/> cancels it, and
    /// <see cref="_visionSeq"/> / <see cref="_visionActive"/> discard whatever finishes after the card was closed or replaced.
    /// The screenshot never leaves the process; only the recognized text goes to the translation service.
    /// </summary>
    private async Task RunLocalVisionAsync(
        long seq,
        SelectionSnapshot baseSnapshot,
        string dataUrl,
        AppSettings s,
        CancellationTokenSource cts)
    {
        OcrOutcome ocr;
        try
        {
            ocr = await Task.Run(() => WindowsOcr.RecognizeAsync(dataUrl, cts.Token), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ocr = new OcrOutcome(string.Empty, OcrFailure.Error, ex.GetType().Name);
        }

        if (cts.IsCancellationRequested) return;

        if (!ocr.IsSuccess)
        {
            _post(() =>
            {
                if (seq != _visionSeq || !_visionActive) return;
                _log.Warn("vision", $"local OCR failed: {ocr.Failure} {ocr.Detail}");
                _popup.ShowError(baseSnapshot, OcrError(ocr), null);
            });
            return;
        }

        var srcSnap = baseSnapshot with { Text = ocr.Text };
        var modelLabel = ServiceLabels.ModelLabel(s);
        var captionStreaming = Loc.T("Vision_LocalStreaming", modelLabel);

        _post(() =>
        {
            if (seq != _visionSeq || !_visionActive) return;
            _log.Info("vision", $"local OCR ok, chars={ocr.Text.Length}");
            _popup.ShowLoading(srcSnap, Loc.T("Vision_LocalTranslating", modelLabel));
        });

        var live = new LatestValueRelay<string>(
            deliver => _postBackground(deliver),
            partial =>
            {
                if (seq != _visionSeq || !_visionActive) return;
                _popup.ShowVisionTranslation(srcSnap, partial, captionStreaming);
            });

        TranslationResult? ok = null;
        TranslationError? error = null;
        try
        {
            ok = await _translator.TranslateAsync(
                _buildRequest(ocr.Text, s),
                cts.Token,
                partial =>
                {
                    if (!cts.IsCancellationRequested) live.Offer(partial);
                }).ConfigureAwait(false);
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
            error = new TranslationError(TranslationErrorKind.Network, Loc.T("App_UnexpectedVisionError", ex.GetType().Name), RequestRoute.Direct);
        }

        if (cts.IsCancellationRequested) return;

        _post(() =>
        {
            live.Clear();
            if (seq != _visionSeq || !_visionActive) return;

            if (ok is not null)
            {
                _log.Info("vision", $"local ok via {ok.Route} {ok.ElapsedMs:0} ms ({ok.Model}), srcChars={ocr.Text.Length}");
                var glossaryPart = ok.GlossaryHitCount > 0 ? " · " + Loc.T("Caption_Glossary", ok.GlossaryHitCount) : string.Empty;
                _popup.ShowVisionTranslation(srcSnap, ok.Translation, $"{ok.Model} · {Loc.T("Vision_LocalTag")}{glossaryPart} · {ok.Route.ToString().ToLowerInvariant()}");
            }
            else if (error is not null && error.Kind != TranslationErrorKind.Cancelled)
            {
                _log.Warn("vision", $"local fail {error}");
                _popup.ShowError(srcSnap, error, error.Route == RequestRoute.Relay ? "relay" : null);
            }
        });
    }

    private static string OcrFailureMessage(OcrOutcome ocr) => ocr.Failure switch
    {
        OcrFailure.EngineUnavailable => Loc.T("Ocr_EngineUnavailable"),
        OcrFailure.NoText => Loc.T("Ocr_NoText") + " " + Loc.T("Ocr_NoTextHint"),
        OcrFailure.InvalidImage => Loc.T("Ocr_ImageFailed", ocr.Detail ?? "?"),
        _ => Loc.T("Ocr_Failed", ocr.Detail ?? "?"),
    };

    private static TranslationError OcrError(OcrOutcome ocr)
        => new(TranslationErrorKind.EmptyResult, OcrFailureMessage(ocr), RequestRoute.Direct);

    // ---- Recognize screen text and copy it (local only, no network) ---------------------------------

    public void StartOcrCopy()
    {
        if (ScreenSnipOverlay.IsActive) return;

        if (_popup.IsVisible) _dispatch(new SessionEvent.UserClosed());
        Dismiss();

        var capture = ScreenSnipOverlay.CaptureInteractive(message => _log.Info("snip", message));
        if (capture is not { } cap || string.IsNullOrWhiteSpace(cap.DataUrl))
            return;

        CancelOcrCopy();
        var cts = new CancellationTokenSource();
        _ocrCopyCts = cts;
        _ = RunOcrCopyAsync(cap.DataUrl, cts);
    }

    private async Task RunOcrCopyAsync(string dataUrl, CancellationTokenSource cts)
    {
        OcrOutcome ocr;
        try
        {
            ocr = await Task.Run(() => WindowsOcr.RecognizeAsync(dataUrl, cts.Token), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ocr = new OcrOutcome(string.Empty, OcrFailure.Error, ex.GetType().Name);
        }

        if (cts.IsCancellationRequested) return;

        _post(() =>
        {
            if (!ReferenceEquals(_ocrCopyCts, cts)) return;
            var title = Loc.T("Ocr_CopyTitle");
            if (!ocr.IsSuccess)
            {
                _log.Warn("ocr", $"copy: recognition failed: {ocr.Failure} {ocr.Detail}");
                _showNotice(title, OcrFailureMessage(ocr), true);
                return;
            }

            var count = ocr.Text.EnumerateRunes().Count();
            if (ClipboardFallback.TryWriteText(ocr.Text))
            {
                _log.Info("ocr", $"copy: ok, chars={count}");
                _showNotice(title, Loc.T("Ocr_Copied", count), false);
            }
            else
            {
                _log.Warn("ocr", "copy: clipboard busy");
                _showNotice(title, Loc.T("Ocr_CopyFailed"), true);
            }
        });
    }

    private void CancelOcrCopy()
    {
        if (_ocrCopyCts is null) return;
        try { _ocrCopyCts.Cancel(); } catch (Exception) { }
        _ocrCopyCts.Dispose();
        _ocrCopyCts = null;
    }

    /// <summary>Closes the vision card: cancels its request, makes any late result stale and hides the popup.</summary>
    public void Dismiss()
    {
        CancelVisionRequest();
        _visionSeq++;
        _visionActive = false;
        _lastVisionCapture = null;
        _popup.Hide();
    }

    private void CancelVisionRequest()
    {
        if (_visionCts is null) return;
        try { _visionCts.Cancel(); } catch (Exception) { }
        _visionCts.Dispose();
        _visionCts = null;
    }
}
