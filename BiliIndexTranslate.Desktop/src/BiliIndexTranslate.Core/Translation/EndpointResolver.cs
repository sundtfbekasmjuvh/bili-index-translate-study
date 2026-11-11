namespace BiliIndexTranslate.Core.Translation;

public enum RequestRoute { Direct, Relay }

/// <summary>Wire format spoken by the configured endpoint.</summary>
public enum ApiFormat
{
    /// <summary>OpenAI-compatible chat completions; "/chat/completions" is appended to the base URL.</summary>
    OpenAi,

    /// <summary>DeepLX-style translate endpoint; the URL is used as entered (see <see cref="DeeplxProtocol"/>).</summary>
    Deeplx,
}

/// <param name="ChatCompletionsUrl">The exact URL to POST. In the DeepLX format this is the translate endpoint.</param>
/// <param name="NormalizedBase">Stable identity of the endpoint for cache keys. Never contains the API key.</param>
/// <param name="Route">Direct, or through the relay.</param>
/// <param name="KeyInUrl">True when the API key was substituted into the URL, so it must not also be sent as a header.</param>
public sealed record ResolvedEndpoint(Uri ChatCompletionsUrl, string NormalizedBase, RequestRoute Route, bool KeyInUrl = false)
{
    /// <summary>Host shown in diagnostics (never the full URL with query strings).</summary>
    public string DisplayHost => ChatCompletionsUrl.Host;
}

/// <summary>
/// Turns the user's endpoint input into the exact URL to POST. Accepts a base URL
/// (".../v1") or a full ".../chat/completions" URL, trims whitespace and slashes, never duplicates
/// the suffix, and only applies the relay prefix when relay use is explicitly enabled.
/// </summary>
public static class EndpointResolver
{
    public const string ChatCompletionsSuffix = "/chat/completions";

    /// <summary>Format-aware entry point: the OpenAI rules, or the DeepLX rules of <see cref="TryResolveDeeplx"/>.</summary>
    public static bool TryResolve(ApiFormat format, string? baseUrl, bool useRelay, string? relayPrefix, string? apiKey, out ResolvedEndpoint? endpoint, out string? error)
    {
        if (format == ApiFormat.Deeplx)
            return TryResolveDeeplx(baseUrl, useRelay, relayPrefix, apiKey, out endpoint, out error);

        // Only the DeepLX format fills in "{apiKey}". Under the OpenAI rules the query string is dropped,
        // so the request would go out without the token; report the mismatch instead.
        if (((useRelay ? relayPrefix : baseUrl) ?? string.Empty).Contains("{apiKey}", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = null;
            error = "The endpoint contains {apiKey}, which only the DeepLX API format fills in. Choose DeepLX-compatible or remove the placeholder.";
            return false;
        }

        return TryResolve(baseUrl, useRelay, relayPrefix, out endpoint, out error);
    }

    public static bool TryResolve(string? baseUrl, bool useRelay, string? relayPrefix, out ResolvedEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        error = null;

        var normalized = NormalizeBase(baseUrl);
        if (normalized is null) { error = "Endpoint is empty."; return false; }

        var full = normalized + ChatCompletionsSuffix;
        var route = RequestRoute.Direct;

        if (useRelay)
        {
            var relay = NormalizeBase(relayPrefix);
            if (relay is null) { error = "Relay is enabled but the relay URL is empty."; return false; }
            full = relay + ChatCompletionsSuffix;
            route = RequestRoute.Relay;
        }

        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Endpoint must be an absolute http(s) URL.";
            return false;
        }

        endpoint = new ResolvedEndpoint(uri, route == RequestRoute.Relay ? NormalizeBase(relayPrefix)! : normalized, route);
        return true;
    }

    /// <summary>
    /// DeepLX endpoints are used as entered: path and query are kept and nothing is appended.
    /// "{apiKey}" (or "{{apiKey}}") in the URL is replaced with the escaped key, as in
    /// https://host/translate?token={apiKey}. With the relay enabled, the relay URL is the complete endpoint.
    /// </summary>
    public static bool TryResolveDeeplx(string? baseUrl, bool useRelay, string? relayPrefix, string? apiKey, out ResolvedEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        error = null;

        var template = (useRelay ? relayPrefix : baseUrl)?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(template))
        {
            error = useRelay ? "Relay is enabled but the relay URL is empty." : "Endpoint is empty.";
            return false;
        }

        var key = Uri.EscapeDataString((apiKey ?? string.Empty).Trim());
        var full = template
            .Replace("{{apiKey}}", key, StringComparison.OrdinalIgnoreCase)
            .Replace("{apiKey}", key, StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Endpoint must be an absolute http(s) URL.";
            return false;
        }

        var keyInUrl = template.Contains("{apiKey}", StringComparison.OrdinalIgnoreCase);
        endpoint = new ResolvedEndpoint(uri, template, useRelay ? RequestRoute.Relay : RequestRoute.Direct, keyInUrl);
        return true;
    }

    /// <summary>Returns the base without trailing slash and without a trailing /chat/completions suffix, or null if blank.</summary>
    public static string? NormalizeBase(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        // Keep query-less: drop fragments/queries silently (they would break the suffix logic).
        var q = s.IndexOfAny(['?', '#']);
        if (q >= 0) s = s[..q];
        s = s.TrimEnd('/');
        if (s.EndsWith(ChatCompletionsSuffix, StringComparison.OrdinalIgnoreCase))
            s = s[..^ChatCompletionsSuffix.Length].TrimEnd('/');
        return s.Length == 0 ? null : s;
    }
}
