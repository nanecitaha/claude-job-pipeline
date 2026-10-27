using System.Net;

namespace JobPipeline.Enricher;

/// <summary>One HTTP GET's outcome. Status 0 = the request itself failed (DNS, connection, timeout).</summary>
public sealed record FetchResult(int Status, string Body, string FinalUrl, string? ContentType, string? Error,
    IReadOnlyDictionary<string, string> Headers)
{
    public bool Ok => Status is >= 200 and < 300;
    public bool IsJson => ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>The only way the enricher reaches the network, so tests can swap in canned pages.</summary>
public interface IFetcher
{
    Task<FetchResult> GetAsync(string url, CancellationToken ct);
}

/// <summary>
/// HttpClient with a normal browser's headers, cookies, and a minimum gap between requests to the same host.
/// Nothing here works around a block: a challenge or 403 is reported as such and the posting is left to a person.
/// </summary>
public sealed class HttpFetcher : IFetcher, IDisposable
{
    readonly HttpClient _http;
    readonly TimeSpan _gap;
    readonly Dictionary<string, DateTimeOffset> _lastByHost = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _lock = new(1, 1);

    public HttpFetcher(EnrichOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds)),
            MaxResponseContentBufferSize = 16 * 1024 * 1024,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        _gap = TimeSpan.FromMilliseconds(Math.Max(0, options.RequestDelayMs));
    }

    public async Task<FetchResult> GetAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new FetchResult(0, "", url, null, "not an http(s) URL", new Dictionary<string, string>());

        await WaitForTurnAsync(uri.Host, ct);
        try
        {
            using var res = await _http.GetAsync(uri, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var headers = res.Headers.Concat(res.Content.Headers)
                .GroupBy(h => h.Key.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)));
            return new FetchResult((int)res.StatusCode, body, res.RequestMessage?.RequestUri?.ToString() ?? url,
                res.Content.Headers.ContentType?.MediaType, null, headers);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new FetchResult(0, "", url, null, ex is TaskCanceledException ? "timed out" : ex.Message,
                new Dictionary<string, string>());
        }
    }

    async Task WaitForTurnAsync(string host, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_lastByHost.TryGetValue(host, out var last))
            {
                var wait = last + _gap - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
            _lastByHost[host] = DateTimeOffset.UtcNow;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}
