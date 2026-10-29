using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace BiliIndexTranslate.Core.Translation;

/// <summary>
/// Single long-lived <see cref="HttpClient"/> with pooled connections. Timeouts are enforced per request
/// through a linked token so a stalled provider never ties up the client. Response bodies are capped.
/// </summary>
public sealed class HttpTranslationTransport : ISseTranslationTransport, IDisposable
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private readonly HttpClient _client;

    public HttpTranslationTransport(string userAgent = "Index-Translate-Client/1.0")
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false, // a redirect to an HTML login page must surface as an error, not as "content"
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<TransportResponse> PostJsonAsync(Uri url, string apiKey, string jsonBody, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            string body;
            await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            {
                body = await ReadCappedAsync(stream, cts.Token).ConfigureAwait(false);
            }
            return new TransportResponse((int)response.StatusCode, body, contentType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    public async Task<TransportResponse> PostSseAsync(
        Uri url,
        string apiKey,
        string jsonBody,
        TimeSpan timeout,
        Action<string> onDataLine,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType;

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            if (status is >= 200 and < 300 && string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var totalChars = 0;
                while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
                {
                    totalChars += line.Length;
                    if (totalChars > MaxBodyBytes) break;
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = trimmed[5..].Trim();
                    if (data.Length > 0 && data != "[DONE]")
                        onDataLine(data);
                }
                return new TransportResponse(status, string.Empty, contentType);
            }

            var body = await ReadCappedAsync(stream, cts.Token).ConfigureAwait(false);
            return new TransportResponse(status, body, contentType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static async Task<string> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxBodyBytes) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public void Dispose() => _client.Dispose();
}
