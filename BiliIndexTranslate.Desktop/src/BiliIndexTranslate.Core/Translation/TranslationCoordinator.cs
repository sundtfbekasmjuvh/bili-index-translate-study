using System.Diagnostics;
using BiliIndexTranslate.Core.Diagnostics;

namespace BiliIndexTranslate.Core.Translation;

public abstract record TranslationOutcome
{
    public sealed record Success(string Translation, RequestRoute Route, bool FromCache, double ElapsedMs) : TranslationOutcome;
    public sealed record Failure(TranslationError Error) : TranslationOutcome;
}

/// <summary>
/// Owns in-flight translation requests: bounded concurrency, per-request cancellation, cache
/// lookup/population and stale-result suppression. Completion callbacks are marshalled through
/// <c>post</c> (the UI dispatcher in the app, synchronous in tests) and are only delivered while the
/// request is still the caller's current one.
/// </summary>
public sealed class TranslationCoordinator : IDisposable
{
    private readonly OpenAiCompatibleTranslator _translator;
    private readonly TranslationCache _cache;
    private readonly Action<Action> _post;
    private readonly Action<Action> _postPartial;
    private readonly TimingStats? _timings;
    private readonly DiagnosticsLog? _log;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _gate = new();
    private readonly Dictionary<long, CancellationTokenSource> _active = new();
    private bool _disposed;

    public TranslationCoordinator(
        OpenAiCompatibleTranslator translator,
        TranslationCache cache,
        Action<Action> post,
        TimingStats? timings = null,
        DiagnosticsLog? log = null,
        int maxConcurrency = 2,
        Action<Action>? postPartial = null)
    {
        _translator = translator;
        _cache = cache;
        _post = post;
        _postPartial = postPartial ?? post;
        _timings = timings;
        _log = log;
        _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public int ActiveCount { get { lock (_gate) return _active.Count; } }

    /// <summary>Synchronous cache probe so the UI can skip the loading state entirely.</summary>
    public TranslationOutcome.Success? TryGetCached(TranslationRequest request)
    {
        var sw = Stopwatch.StartNew();
        var hit = _cache.TryGet(CacheKey.For(request), out var entry);
        _timings?.Record(TimingCategory.Cache, sw.Elapsed.TotalMilliseconds);
        return hit ? new TranslationOutcome.Success(entry.Translation, entry.Route, true, sw.Elapsed.TotalMilliseconds) : null;
    }

    /// <summary>Starts a network translation. The callback fires at most once, via <c>post</c>, unless cancelled first.</summary>
    /// <remarks>
    /// <paramref name="onPartial"/> receives the newest displayable text while the reply streams in; it
    /// is delivered via <c>postPartial</c>, only while this request is still the active one, and never
    /// after the final outcome has been handed over.
    /// </remarks>
    public void Start(long requestId, TranslationRequest request, Action<long, TranslationOutcome> onCompleted, Action<long, string>? onPartial = null)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed) { cts.Dispose(); return; }
            if (_active.Remove(requestId, out var old)) { old.Cancel(); old.Dispose(); }
            _active[requestId] = cts;
        }

        LatestValueRelay<string>? relay = null;
        Action<string>? partialSink = null;
        if (onPartial is not null)
        {
            relay = new LatestValueRelay<string>(_postPartial, text =>
            {
                bool valid;
                lock (_gate) valid = _active.TryGetValue(requestId, out var current) && ReferenceEquals(current, cts);
                if (valid) onPartial(requestId, text);
            });
            var r = relay;
            partialSink = text =>
            {
                if (!cts.IsCancellationRequested) r.Offer(text);
            };
        }
        _ = RunAsync(requestId, request, cts, onCompleted, relay, partialSink);
    }

    public void Cancel(long requestId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_active.Remove(requestId, out cts)) return;
        }
        cts.Cancel();
        cts.Dispose();
    }

    public void CancelAll()
    {
        List<CancellationTokenSource> all;
        lock (_gate)
        {
            all = _active.Values.ToList();
            _active.Clear();
        }
        foreach (var c in all) { c.Cancel(); c.Dispose(); }
    }

    private async Task RunAsync(
        long requestId,
        TranslationRequest request,
        CancellationTokenSource cts,
        Action<long, TranslationOutcome> onCompleted,
        LatestValueRelay<string>? relay,
        Action<string>? partialSink)
    {
        var token = cts.Token;
        TranslationOutcome outcome;
        var acquired = false;
        try
        {
            await _concurrency.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            var result = await _translator.TranslateAsync(request, token, partialSink).ConfigureAwait(false);
            _cache.Set(CacheKey.For(request), result.Translation, result.Route);
            _timings?.Record(TimingCategory.Http, result.ElapsedMs);
            _log?.Info("translate", $"ok #{requestId} via {result.Route} {result.ElapsedMs:0} ms, {request.Text.Length} chars → {result.Translation.Length} chars");
            outcome = new TranslationOutcome.Success(result.Translation, result.Route, false, result.ElapsedMs);
        }
        catch (TranslationException ex)
        {
            if (ex.Error.Kind != TranslationErrorKind.Cancelled)
                _log?.Warn("translate", $"fail #{requestId} {ex.Error}");
            outcome = new TranslationOutcome.Failure(ex.Error);
        }
        catch (OperationCanceledException)
        {
            outcome = new TranslationOutcome.Failure(new TranslationError(TranslationErrorKind.Cancelled, "Cancelled.", request.UseRelay ? RequestRoute.Relay : RequestRoute.Direct));
        }
        catch (Exception ex)
        {
            _log?.Error("translate", $"unexpected #{requestId} {ex.GetType().Name}");
            outcome = new TranslationOutcome.Failure(new TranslationError(TranslationErrorKind.Network, $"Unexpected error ({ex.GetType().Name}).", request.UseRelay ? RequestRoute.Relay : RequestRoute.Direct));
        }
        finally
        {
            if (acquired) _concurrency.Release();
        }

        bool stillActive;
        lock (_gate)
        {
            stillActive = _active.Remove(requestId, out var current) && ReferenceEquals(current, cts);
        }
        if (!stillActive) { relay?.Clear(); return; } // cancelled or superseded: never deliver
        cts.Dispose();
        relay?.Clear();
        // Cleared again when the final result is delivered, so a partial that was queued meanwhile cannot draw over it.
        _post(() =>
        {
            relay?.Clear();
            onCompleted(requestId, outcome);
        });
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        CancelAll();
        _concurrency.Dispose();
    }
}
