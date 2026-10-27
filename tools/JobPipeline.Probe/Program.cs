using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

// Probe: can a plain HttpClient on THIS machine read job pages, or do we need a browser?
// Answers the question the URL-only hunt depends on: Claude inserts URLs, a worker fills in the rest over HTTP.
//
//   dotnet run --project tools/JobPipeline.Probe -- <url> [<url> ...]
//   dotnet run --project tools/JobPipeline.Probe -- --file urls.txt
//
// Run it on the machine the worker will run on (the VPS): blocking depends on that machine's IP.
// Per URL it reports the HTTP status, signs of a bot challenge, a schema.org JobPosting block (the JSON-LD
// job boards publish for Google Jobs), links out to employer ATS pages, and, for ATS URLs, whether the ATS's
// public JSON API returns the posting. One request every few seconds; nothing is retried or worked around.

var urls = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--file" or "-f" && i + 1 < args.Length)
        urls.AddRange(File.ReadAllLines(args[++i]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
    else
        urls.Add(args[i]);
}
if (urls.Count == 0)
{
    Console.Error.WriteLine("Usage: JobPipeline.Probe <url> [<url> ...] | --file urls.txt");
    Console.Error.WriteLine("Mix search pages, listing pages and employer ATS links (Greenhouse, Lever, Ashby, Workday).");
    return 2;
}

using var http = Probe.CreateClient();
var verdicts = new List<(string Url, string Verdict)>();
foreach (var (url, i) in urls.Select((u, i) => (u, i)))
{
    if (i > 0) await Task.Delay(TimeSpan.FromSeconds(3));
    Console.WriteLine();
    Console.WriteLine($"── {url}");
    verdicts.Add((url, await Probe.RunAsync(http, url)));
}

Console.WriteLine();
Console.WriteLine("── Summary");
foreach (var (url, verdict) in verdicts) Console.WriteLine($"{verdict,-14} {url}");
return 0;

static class Probe
{
    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        // A normal browser's headers. This is ordinary politeness, not evasion: a site that challenges this
        // request gets reported as blocked.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return http;
    }

    public static async Task<string> RunAsync(HttpClient http, string url)
    {
        var page = await FetchAsync(http, url);
        if (page.Error is not null)
        {
            Console.WriteLine($"   request failed: {page.Error}");
            return "ERROR";
        }

        Console.WriteLine($"   HTTP {(int)page.Status} in {page.Ms} ms, {page.Body.Length:N0} chars, final URL {page.FinalUrl}");
        if (page.Server is not null) Console.WriteLine($"   server: {page.Server}");

        var challenge = Challenge(page);
        if (challenge is not null) Console.WriteLine($"   bot check: {challenge}");

        // An ATS URL is checked through its public JSON API; that is the path the worker would use.
        var api = AtsApi.For(page.FinalUrl) ?? AtsApi.For(url);
        string? apiResult = null;
        if (api is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            apiResult = await CheckApiAsync(http, api);
        }

        var postings = JsonLd.JobPostings(page.Body).ToList();
        foreach (var p in postings)
            Console.WriteLine($"   JSON-LD JobPosting: title=\"{p.Title}\" company=\"{p.Company}\" posted={p.DatePosted ?? "?"} " +
                              $"description={p.DescriptionChars:N0} chars, salary={(p.HasSalary ? "yes" : "no")}");
        if (postings.Count == 0 && page.IsHtml) Console.WriteLine("   JSON-LD JobPosting: none");

        var ats = AtsLinks(page.Body).ToList();
        if (ats.Count > 0) Console.WriteLine($"   employer ATS links: {string.Join(", ", ats.Take(5))}{(ats.Count > 5 ? $" (+{ats.Count - 5})" : "")}");

        var jobLinks = JobLinks(page.Body, page.FinalUrl).ToList();
        if (jobLinks.Count > 1) Console.WriteLine($"   job listing links on page: {jobLinks.Count} (e.g. {jobLinks[0]})");

        if (challenge is not null || page.Status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) return "BLOCKED";
        if (apiResult is not null) return apiResult;
        if ((int)page.Status >= 400) return $"HTTP {(int)page.Status}";
        if (postings.Any(p => p.DescriptionChars > 200)) return "OK json-ld";
        if (jobLinks.Count > 1) return "OK search";
        return page.Body.Length > 2000 ? "OK html-only" : "EMPTY";
    }

    static async Task<string> CheckApiAsync(HttpClient http, AtsApi api)
    {
        var res = await FetchAsync(http, api.Url);
        if (res.Error is not null || (int)res.Status >= 400)
        {
            Console.WriteLine($"   {api.Name} API: {(res.Error ?? $"HTTP {(int)res.Status}")} ({api.Url})");
            return $"API {api.Name} fail";
        }
        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            var chars = api.DescriptionChars(doc.RootElement);
            Console.WriteLine($"   {api.Name} API: HTTP {(int)res.Status}, description {chars:N0} chars ({api.Url})");
            return chars > 200 ? $"OK {api.Name}" : $"API {api.Name} thin";
        }
        catch (JsonException)
        {
            Console.WriteLine($"   {api.Name} API: not JSON ({api.Url})");
            return $"API {api.Name} fail";
        }
    }

    sealed record Page(HttpStatusCode Status, string Body, string FinalUrl, string? Server, bool IsHtml,
        IReadOnlyDictionary<string, string> Headers, long Ms, string? Error);

    static async Task<Page> FetchAsync(HttpClient http, string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await http.GetAsync(url);
            var body = await res.Content.ReadAsStringAsync();
            var headers = res.Headers.Concat(res.Content.Headers)
                .GroupBy(h => h.Key.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)));
            return new Page(res.StatusCode, body, res.RequestMessage?.RequestUri?.ToString() ?? url,
                headers.GetValueOrDefault("server"),
                res.Content.Headers.ContentType?.MediaType?.Contains("html") == true,
                headers, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new Page(0, "", url, null, false, new Dictionary<string, string>(), sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>Signs that the response is a bot check rather than the page.</summary>
    static string? Challenge(Page p)
    {
        if (p.Headers.TryGetValue("cf-mitigated", out var m)) return $"Cloudflare ({m})";
        var head = p.Body.Length > 20000 ? p.Body[..20000] : p.Body;
        (string Needle, string Name)[] signs =
        [
            ("cf-chl", "Cloudflare challenge"), ("Just a moment...", "Cloudflare challenge"),
            ("challenges.cloudflare.com", "Cloudflare Turnstile"), ("px-captcha", "PerimeterX"),
            ("_Incapsula_Resource", "Imperva"), ("datadome", "DataDome"), ("g-recaptcha", "reCAPTCHA"),
            ("hcaptcha.com", "hCaptcha"), ("Access Denied", "access denied page"),
        ];
        foreach (var (needle, name) in signs)
            if (head.Contains(needle, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }

    static readonly Regex Href = new("href\\s*=\\s*[\"']([^\"'#]+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static IEnumerable<string> AtsLinks(string html) =>
        Href.Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
            .Where(u => AtsApi.For(u) is not null)
            .Distinct();

    /// <summary>Links that look like individual postings on the same site (search result pages).</summary>
    static IEnumerable<string> JobLinks(string html, string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) yield break;
        var seen = new HashSet<string>();
        foreach (Match m in Href.Matches(html))
        {
            if (!Uri.TryCreate(page, WebUtility.HtmlDecode(m.Groups[1].Value), out var u)) continue;
            if (u.Host != page.Host) continue;
            var path = u.AbsolutePath;
            if (!Regex.IsMatch(path, @"/jobs?/[^/]+/.*\d|/jobs?/view/\d+|/job/[^/]+-\d+", RegexOptions.IgnoreCase)) continue;
            var clean = u.GetLeftPart(UriPartial.Path);
            if (seen.Add(clean)) yield return clean;
        }
    }
}

/// <summary>schema.org JobPosting blocks in a page's JSON-LD.</summary>
static class JsonLd
{
    public sealed record Posting(string? Title, string? Company, string? DatePosted, int DescriptionChars, bool HasSalary);

    static readonly Regex Block = new("<script[^>]*application/ld\\+json[^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static IEnumerable<Posting> JobPostings(string html)
    {
        foreach (Match m in Block.Matches(html))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(m.Groups[1].Value.Trim()); }
            catch (JsonException) { continue; }
            using (doc)
                foreach (var p in Walk(doc.RootElement).Where(IsJobPosting).Select(Read).ToList())
                    yield return p;
        }
    }

    static IEnumerable<JsonElement> Walk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) foreach (var y in Walk(x)) yield return y;
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            yield return e;
            if (e.TryGetProperty("@graph", out var g)) foreach (var y in Walk(g)) yield return y;
        }
    }

    static bool IsJobPosting(JsonElement e) =>
        e.TryGetProperty("@type", out var t) &&
        (t.ValueKind == JsonValueKind.String ? t.GetString() == "JobPosting"
            : t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.GetString() == "JobPosting"));

    static Posting Read(JsonElement e) => new(
        Str(e, "title"),
        e.TryGetProperty("hiringOrganization", out var org) ? (org.ValueKind == JsonValueKind.String ? org.GetString() : Str(org, "name")) : null,
        Str(e, "datePosted"),
        Text.PlainLength(Str(e, "description")),
        e.TryGetProperty("baseSalary", out _));

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Public JSON APIs of the common applicant tracking systems, keyed off a posting URL.</summary>
sealed record AtsApi(string Name, string Url, Func<JsonElement, int> DescriptionChars)
{
    public static AtsApi? For(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // boards.greenhouse.io/{board}/jobs/{id}, job-boards.greenhouse.io/{board}/jobs/{id}
        if (host.EndsWith("greenhouse.io") && seg.Length >= 3 && seg[1] == "jobs")
            return new("greenhouse", $"https://boards-api.greenhouse.io/v1/boards/{seg[0]}/jobs/{seg[2]}",
                e => Text.PlainLength(Prop(e, "content") is { } c ? WebUtility.HtmlDecode(c) : null));

        // jobs.lever.co/{company}/{posting id}
        if (host is "jobs.lever.co" or "jobs.eu.lever.co" && seg.Length >= 2)
            return new("lever", $"https://{(host.Contains(".eu.") ? "api.eu.lever.co" : "api.lever.co")}/v0/postings/{seg[0]}/{seg[1]}",
                e => (Prop(e, "descriptionPlain")?.Length ?? 0) + (e.TryGetProperty("lists", out var l) ? l.GetRawText().Length : 0));

        // jobs.ashbyhq.com/{board}/{posting id}: the API serves the whole board; find the posting in it.
        if (host == "jobs.ashbyhq.com" && seg.Length >= 2)
        {
            var id = seg[1];
            return new("ashby", $"https://api.ashbyhq.com/posting-api/job-board/{seg[0]}?includeCompensation=true",
                e => e.TryGetProperty("jobs", out var jobs)
                    ? jobs.EnumerateArray().Where(j => Prop(j, "id") == id).Select(j => Prop(j, "descriptionPlain")?.Length ?? 0).FirstOrDefault()
                    : 0);
        }

        // {tenant}.wd{n}.myworkdayjobs.com/[{locale}/]{site}/job/{...}
        if (host.EndsWith(".myworkdayjobs.com"))
        {
            var jobAt = Array.IndexOf(seg, "job");
            if (jobAt >= 1)
            {
                var site = seg[jobAt - 1];
                var tenant = host.Split('.')[0];
                var rest = string.Join('/', seg[(jobAt + 1)..]);
                return new("workday", $"https://{host}/wday/cxs/{tenant}/{site}/job/{rest}",
                    e => e.TryGetProperty("jobPostingInfo", out var info) ? Text.PlainLength(Prop(info, "jobDescription")) : 0);
            }
        }
        return null;
    }

    static string? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

static class Text
{
    static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    static readonly Regex Space = new("\\s+", RegexOptions.Compiled);

    /// <summary>Length of the description once HTML is stripped: a stand-in for "is the full JD here?".</summary>
    public static int PlainLength(string? html) =>
        html is null ? 0 : Space.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim().Length;
}
