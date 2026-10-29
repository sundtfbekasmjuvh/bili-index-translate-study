using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.Core.Study;

/// <summary>
/// Ephemeral, on-demand study card client with progressive SSE streaming
/// and a strictly volatile in-RAM LRU micro-cache (never persisted to disk or settings).
/// </summary>
public sealed class StudyCardService
{
    private const int MaxVolatileEntries = 16;

    private readonly ITranslationTransport _transport;
    private readonly TimeSpan _timeout;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, StudyDeckResult Value)>> _cacheMap = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, StudyDeckResult Value)> _cacheLru = new();

    public StudyCardService(ITranslationTransport transport, TimeSpan? timeout = null)
    {
        _transport = transport;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public void ClearVolatileCache()
    {
        lock (_cacheGate)
        {
            _cacheMap.Clear();
            _cacheLru.Clear();
        }
    }

    public async Task<StudyDeckResult> GenerateAsync(
        TranslationRequest request,
        CancellationToken ct,
        Action<StudyDeckResult>? onPartial = null)
    {
        if (!EndpointResolver.TryResolve(request.BaseUrl, request.UseRelay, request.RelayPrefix, out var endpoint, out var cfgError))
            throw new TranslationException(new TranslationError(TranslationErrorKind.Configuration, cfgError ?? "Invalid endpoint.", request.UseRelay ? RequestRoute.Relay : RequestRoute.Direct));

        var route = endpoint!.Route;
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new TranslationException(new TranslationError(TranslationErrorKind.Configuration, "No model selected.", route));

        var effectiveModel = StudyPromptBuilder.ResolveStudyModel(request.Model);
        var effectiveReq = request with { Model = effectiveModel };

        var cacheKey = BuildCacheKey(effectiveReq);
        if (TryGetCached(cacheKey, out var cached) && cached is not null)
            return cached with { ElapsedMs = 0, IsCached = true };

        var canStream = onPartial is not null && _transport is ISseTranslationTransport;
        var body = StudyPromptBuilder.BuildRequestJson(
            effectiveModel,
            effectiveReq.Text,
            effectiveReq.TargetLanguagePromptName,
            effectiveReq.SourceLanguage,
            effectiveReq.TargetLanguageCode,
            stream: canStream,
            baseUrl: effectiveReq.BaseUrl);

        var sw = Stopwatch.StartNew();
        TransportResponse response;
        var streamedContent = new StringBuilder();
        var lastEmitKey = string.Empty;

        try
        {
            if (canStream && _transport is ISseTranslationTransport sse)
            {
                response = await sse.PostSseAsync(
                    endpoint.ChatCompletionsUrl,
                    effectiveReq.ApiKey,
                    body,
                    _timeout,
                    dataJson =>
                    {
                        var delta = ExtractDeltaContent(dataJson);
                        if (string.IsNullOrEmpty(delta)) return;
                        streamedContent.Append(delta);
                        var partial = StudyCardParser.ExtractPartialDeck(streamedContent.ToString(), effectiveReq.Text);
                        if (partial is null) return;
                        var key = $"{partial.GrammarNote.Length}:{partial.Cards.Count}";
                        if (key == lastEmitKey) return;
                        lastEmitKey = key;
                        onPartial!(new StudyDeckResult(partial, route, sw.Elapsed.TotalMilliseconds, effectiveModel));
                    },
                    ct).ConfigureAwait(false);
            }
            else
            {
                response = await _transport.PostJsonAsync(endpoint.ChatCompletionsUrl, effectiveReq.ApiKey, body, _timeout, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new TranslationException(new TranslationError(TranslationErrorKind.Cancelled, "Cancelled.", route));
        }
        catch (OperationCanceledException)
        {
            throw new TranslationException(new TranslationError(TranslationErrorKind.Timeout, $"No response within {_timeout.TotalSeconds:0} s.", route));
        }
        catch (TimeoutException)
        {
            throw new TranslationException(new TranslationError(TranslationErrorKind.Timeout, $"No response within {_timeout.TotalSeconds:0} s.", route));
        }
        catch (HttpRequestException ex)
        {
            var msg = ex.StatusCode is { } sc ? $"Request failed (HTTP {(int)sc})." : "Connection failed.";
            throw new TranslationException(new TranslationError(TranslationErrorKind.Network, msg, route));
        }
        catch (SocketException ex)
        {
            throw new TranslationException(new TranslationError(TranslationErrorKind.Network, $"Connection failed ({ex.SocketErrorCode}).", route));
        }
        catch (Exception ex) when (ex is not TranslationException)
        {
            throw new TranslationException(new TranslationError(TranslationErrorKind.Network, $"Request failed ({ex.GetType().Name}).", route));
        }
        sw.Stop();

        if (response.StatusCode is < 200 or >= 300)
        {
            var msg = response.StatusCode switch
            {
                412 => "Request rejected by the service (HTTP 412).",
                401 => "Unauthorized (HTTP 401).",
                403 => "Forbidden (HTTP 403).",
                404 => "Endpoint not found (HTTP 404).",
                429 => "Too many requests (HTTP 429).",
                >= 500 => $"Service error (HTTP {response.StatusCode}).",
                _ => $"Request failed (HTTP {response.StatusCode}).",
            };
            throw new TranslationException(new TranslationError(TranslationErrorKind.Http, msg, route, response.StatusCode));
        }

        if (canStream && string.Equals(response.ContentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var finalDeck = StudyCardParser.ParseContent(streamedContent.ToString(), effectiveReq.Text);
            if (finalDeck is null)
                throw new TranslationException(new TranslationError(TranslationErrorKind.EmptyResult, "The service returned an empty study card.", route, response.StatusCode));
            var sseRes = new StudyDeckResult(finalDeck, route, sw.Elapsed.TotalMilliseconds, effectiveModel);
            PutCached(cacheKey, sseRes);
            return sseRes;
        }

        if (!StudyCardParser.TryParseCompletion(response.Body, effectiveReq.Text, out var deck, out var parseError) || deck is null)
        {
            var kind = parseError is not null && parseError.Contains("empty", StringComparison.OrdinalIgnoreCase)
                ? TranslationErrorKind.EmptyResult
                : TranslationErrorKind.InvalidResponse;
            throw new TranslationException(new TranslationError(kind, parseError ?? "Unreadable response.", route, response.StatusCode));
        }

        var result = new StudyDeckResult(deck, route, sw.Elapsed.TotalMilliseconds, effectiveModel);
        PutCached(cacheKey, result);
        return result;
    }

    private static string BuildCacheKey(TranslationRequest r)
        => $"{r.Model}|{r.TargetLanguageCode}|{r.BaseUrl}|{r.UseRelay}|{r.Text.Trim()}";

    private bool TryGetCached(string key, out StudyDeckResult? value)
    {
        lock (_cacheGate)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _cacheLru.Remove(node);
                _cacheLru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private void PutCached(string key, StudyDeckResult value)
    {
        lock (_cacheGate)
        {
            if (_cacheMap.TryGetValue(key, out var existing))
            {
                _cacheLru.Remove(existing);
                _cacheMap.Remove(key);
            }
            var node = new LinkedListNode<(string Key, StudyDeckResult Value)>((key, value));
            _cacheLru.AddFirst(node);
            _cacheMap[key] = node;

            while (_cacheMap.Count > MaxVolatileEntries && _cacheLru.Last is { } oldest)
            {
                _cacheLru.RemoveLast();
                _cacheMap.Remove(oldest.Value.Key);
            }
        }
    }

    private static string? ExtractDeltaContent(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
