using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>
/// Zero-OCR multimodal vision translation client with progressive <c>〖译文〗</c> → <c>〖原文〗</c>
/// SSE streaming and a strictly volatile in-RAM LRU cache (never persisted to disk).
/// </summary>
public sealed class VisionTranslationService
{
    private const int MaxVolatileEntries = 16;

    private readonly ITranslationTransport _transport;
    private readonly TimeSpan _timeout;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageTranslationResult Value)>> _cacheMap = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, ImageTranslationResult Value)> _cacheLru = new();

    public VisionTranslationService(ITranslationTransport transport, TimeSpan? timeout = null)
    {
        _transport = transport;
        _timeout = timeout ?? TimeSpan.FromSeconds(35);
    }

    public void ClearVolatileCache()
    {
        lock (_cacheGate)
        {
            _cacheMap.Clear();
            _cacheLru.Clear();
        }
    }

    public async Task<ImageTranslationResult> TranslateImageAsync(
        ImageTranslationRequest request,
        CancellationToken ct,
        Action<ImageTranslationDelta>? onDelta = null)
    {
        if (!EndpointResolver.TryResolve(request.BaseUrl, request.UseRelay, request.RelayPrefix, out var endpoint, out var cfgError))
            throw new TranslationException(new TranslationError(TranslationErrorKind.Configuration, cfgError ?? "Invalid endpoint.", request.UseRelay ? RequestRoute.Relay : RequestRoute.Direct));

        var route = endpoint!.Route;
        if (string.IsNullOrWhiteSpace(request.ImageDataUrl) || !request.ImageDataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            throw new TranslationException(new TranslationError(TranslationErrorKind.Configuration, "Invalid image data payload.", route));

        var effectiveModel = VisionPromptBuilder.ResolveVisionModel(request.Model);
        var cacheKey = BuildCacheKey(request, effectiveModel);
        if (TryGetCached(cacheKey, out var cached) && cached is not null)
            return cached with { ElapsedMs = 0, IsCached = true };

        var canStream = onDelta is not null && _transport is ISseTranslationTransport;
        var body = VisionPromptBuilder.BuildRequestJson(
            effectiveModel,
            request.ImageDataUrl,
            request.TargetLanguagePromptName,
            stream: canStream,
            baseUrl: request.BaseUrl,
            glossaryText: request.GlossaryText);

        var sw = Stopwatch.StartNew();
        TransportResponse response;
        var streamedContent = new StringBuilder();
        var lastShownTranslation = string.Empty;

        try
        {
            if (canStream && _transport is ISseTranslationTransport sse)
            {
                response = await sse.PostSseAsync(
                    endpoint.ChatCompletionsUrl,
                    request.ApiKey,
                    body,
                    _timeout,
                    dataJson =>
                    {
                        var delta = ExtractDeltaContent(dataJson);
                        if (string.IsNullOrEmpty(delta)) return;
                        streamedContent.Append(delta);
                        var parsed = VisionPromptBuilder.ParseDualOutput(streamedContent.ToString());
                        if (parsed.Translation.Length == 0 || string.Equals(parsed.Translation, lastShownTranslation, StringComparison.Ordinal))
                            return;
                        lastShownTranslation = parsed.Translation;
                        onDelta!(new ImageTranslationDelta(parsed.Translation, parsed.SourceText, effectiveModel, route));
                    },
                    ct).ConfigureAwait(false);
            }
            else
            {
                response = await _transport.PostJsonAsync(endpoint.ChatCompletionsUrl, request.ApiKey, body, _timeout, ct).ConfigureAwait(false);
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

        string rawFinal;
        if (canStream && string.Equals(response.ContentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            rawFinal = streamedContent.ToString();
        }
        else
        {
            if (!ChatCompletionParser.TryParse(response.Body, out var rawText, out var parseErr))
            {
                var kind = parseErr is not null && parseErr.Contains("empty translation", StringComparison.OrdinalIgnoreCase)
                    ? TranslationErrorKind.EmptyResult
                    : TranslationErrorKind.InvalidResponse;
                throw new TranslationException(new TranslationError(kind, parseErr ?? "Unreadable vision response.", route, response.StatusCode));
            }
            rawFinal = rawText;
        }

        var (translation, extractedSource) = VisionPromptBuilder.ParseDualOutput(rawFinal);
        if (string.IsNullOrWhiteSpace(translation))
            throw new TranslationException(new TranslationError(TranslationErrorKind.EmptyResult, "No readable text found in the selected region.", route, response.StatusCode));

        var hitCount = extractedSource.Length > 0
            ? GlossaryEngine.MatchHits(request.GlossaryText, extractedSource).Count
            : 0;

        var result = new ImageTranslationResult(
            translation,
            extractedSource,
            route,
            sw.Elapsed.TotalMilliseconds,
            effectiveModel,
            hitCount);

        PutCached(cacheKey, result);
        return result;
    }

    private static string ExtractDeltaContent(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return string.Empty;
            var first = choices[0];
            if (!first.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                return string.Empty;
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString() ?? string.Empty;
        }
        catch (JsonException) { }
        return string.Empty;
    }

    private static string BuildCacheKey(ImageTranslationRequest req, string model)
    {
        var imgBytes = Encoding.UTF8.GetBytes(req.ImageDataUrl);
        var imgHash = Convert.ToHexString(SHA256.HashData(imgBytes));
        var gHash = string.IsNullOrEmpty(req.GlossaryText)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.GlossaryText)));
        return $"{model}|{req.TargetLanguageCode}|{req.BaseUrl}|{req.UseRelay}|{gHash}|{imgHash}";
    }

    private bool TryGetCached(string key, out ImageTranslationResult? value)
    {
        lock (_cacheGate)
        {
            if (!_cacheMap.TryGetValue(key, out var node))
            {
                value = null;
                return false;
            }
            _cacheLru.Remove(node);
            _cacheLru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    private void PutCached(string key, ImageTranslationResult value)
    {
        lock (_cacheGate)
        {
            if (_cacheMap.TryGetValue(key, out var existing))
            {
                _cacheLru.Remove(existing);
                _cacheMap.Remove(key);
            }
            var node = new LinkedListNode<(string Key, ImageTranslationResult Value)>((key, value));
            _cacheLru.AddFirst(node);
            _cacheMap[key] = node;
            while (_cacheMap.Count > MaxVolatileEntries && _cacheLru.Last is { } oldest)
            {
                _cacheLru.RemoveLast();
                _cacheMap.Remove(oldest.Value.Key);
            }
        }
    }
}
