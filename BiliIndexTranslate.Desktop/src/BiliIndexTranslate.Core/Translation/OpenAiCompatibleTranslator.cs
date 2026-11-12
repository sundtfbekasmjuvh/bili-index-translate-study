using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>
/// One request → one result or one <see cref="TranslationError"/>. No retries, no route switching:
/// if the direct endpoint answers 412 the error says so and points at the relay toggle.
/// Speaks the OpenAI chat-completions format, or the DeepLX translate format when the request's
/// <see cref="TranslationRequest.Format"/> asks for it; transport and error handling are shared.
/// </summary>
public sealed class OpenAiCompatibleTranslator
{
    private readonly ITranslationTransport _transport;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleTranslator(ITranslationTransport transport, TimeSpan? timeout = null)
    {
        _transport = transport;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        var deeplx = request.Format == ApiFormat.Deeplx;
        if (!EndpointResolver.TryResolve(request.Format, request.BaseUrl, request.UseRelay, request.RelayPrefix, request.ApiKey, out var endpoint, out var cfgError))
            throw new TranslationException(new TranslationError(TranslationErrorKind.Configuration, cfgError ?? "Invalid endpoint.", request.UseRelay ? RequestRoute.Relay : RequestRoute.Direct));

        var route = endpoint!.Route;
        if (!deeplx && string.IsNullOrWhiteSpace(request.Model))
            throw new TranslationException(new TranslationError(TranslationErrorKind.Configuration, "No model selected.", route));

        // DeepLX has no prompt that could carry glossary constraints, so it reports no hits.
        var hitCount = deeplx ? 0 : GlossaryEngine.MatchHits(request.GlossaryText, request.Text).Count;
        var body = deeplx
            ? DeeplxProtocol.BuildRequestJson(request.Text, request.SourceLanguage, request.TargetLanguageCode)
            : PromptBuilder.BuildRequestJson(
                request.Model,
                request.Text,
                request.TargetLanguagePromptName,
                request.SourceLanguage,
                stream: false,
                baseUrl: request.BaseUrl,
                glossaryText: request.GlossaryText);
        // A key that was substituted into the URL is not repeated as a Bearer header.
        var apiKey = endpoint.KeyInUrl ? string.Empty : request.ApiKey;
        var sw = Stopwatch.StartNew();
        TransportResponse response;
        try
        {
            response = await _transport.PostJsonAsync(endpoint.ChatCompletionsUrl, apiKey, body, _timeout, ct).ConfigureAwait(false);
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
            throw new TranslationException(new TranslationError(TranslationErrorKind.Network, DescribeNetwork(ex), route));
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
            var detail = deeplx ? DeeplxErrorDetail(response) : ExtractErrorDetail(response);
            var msg = HttpMessage(response.StatusCode);
            if (detail is not null) msg += " " + detail;
            throw new TranslationException(new TranslationError(TranslationErrorKind.Http, msg, route, response.StatusCode));
        }

        bool parsed;
        string translation;
        string? parseError;
        if (deeplx)
        {
            parsed = DeeplxProtocol.TryParse(response.Body, out translation, out parseError, out var serviceCode);
            if (!parsed && serviceCode is { } code)
            {
                // HTTP 200 carrying a non-200 "code": report it as the HTTP error it stands for.
                var msg = HttpMessage(code);
                if (parseError is not null) msg += " " + parseError;
                throw new TranslationException(new TranslationError(TranslationErrorKind.Http, msg, route, code));
            }
        }
        else
        {
            parsed = ChatCompletionParser.TryParse(response.Body, out translation, out parseError);
        }

        if (!parsed)
        {
            var kind = parseError is not null && parseError.Contains("empty translation", StringComparison.OrdinalIgnoreCase)
                ? TranslationErrorKind.EmptyResult
                : TranslationErrorKind.InvalidResponse;
            throw new TranslationException(new TranslationError(kind, parseError ?? "Unreadable response.", route, response.StatusCode));
        }

        return new TranslationResult(translation, route, sw.Elapsed.TotalMilliseconds, deeplx ? DeeplxProtocol.ModelLabel : request.Model, hitCount);
    }

    private static string HttpMessage(int status) => status switch
    {
        412 => "Request rejected by the service (HTTP 412).",
        401 => "Unauthorized (HTTP 401).",
        403 => "Forbidden (HTTP 403).",
        404 => "Endpoint not found (HTTP 404).",
        429 => "Too many requests (HTTP 429).",
        >= 500 => $"Service error (HTTP {status}).",
        _ => $"Request failed (HTTP {status}).",
    };

    private static string? ExtractErrorDetail(TransportResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Body)) return null;
        var body = response.Body.TrimStart();
        if (body.StartsWith('<')) return null; // never surface HTML
        if (ChatCompletionParser.TryParse(body, out _, out var err) || err is null) return null;
        // TryParse reports {"error":{"message":...}} as an error string; reuse it when it is informative.
        if (err.StartsWith("Response", StringComparison.Ordinal) || err.StartsWith("Empty", StringComparison.Ordinal) || err.StartsWith("The endpoint", StringComparison.Ordinal))
            return null;
        return err;
    }

    /// <summary>The service's own message from a DeepLX error body ({"code":401,"message":"..."}), else nothing.</summary>
    private static string? DeeplxErrorDetail(TransportResponse response)
        => !DeeplxProtocol.TryParse(response.Body, out _, out var err, out var serviceCode) && serviceCode is not null ? err : null;

    private static string DescribeNetwork(HttpRequestException ex)
    {
        var inner = ex.InnerException;
        if (inner is SocketException se) return $"Connection failed ({se.SocketErrorCode}).";
        if (inner is System.Security.Authentication.AuthenticationException) return "TLS handshake failed.";
        if (ex.StatusCode is { } sc) return $"Request failed (HTTP {(int)sc}).";
        return "Connection failed.";
    }
}
