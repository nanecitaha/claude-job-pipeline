using System.Net;
using System.Net.Sockets;

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
///
/// URLs come from web pages, so they're untrusted: every connection, including each redirect, is checked at the
/// socket and refused when the address is on this machine or a private network (localhost, 10.x, 192.168.x,
/// 169.254.x cloud metadata, …). A posting page can't make the enricher call the dashboard or the VPS's own services.
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
            ConnectCallback = ConnectPublicOnlyAsync,
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

    static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublic).ToList();
        if (allowed.Count == 0)
            throw new HttpRequestException($"{host} resolves to a local or private address; not fetched");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>False for loopback, private, link-local, carrier-grade NAT, multicast and unspecified addresses.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)      // carrier-grade NAT
                     || (b[0] == 169 && b[1] == 254)                     // link-local, cloud metadata
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                     || (b[0] & 0xFE) == 0xFC);                         // unique local fc00::/7
        }
        return false;
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
