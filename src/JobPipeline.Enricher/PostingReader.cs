using System.Text.Json;
using JobPipeline.Core;

namespace JobPipeline.Enricher;

/// <summary>A posting read from the web, and how it was read (for the job's history).</summary>
public sealed record ReadPosting(NewJob Job, string How);

/// <summary>
/// Turns any posting URL into job details, cheapest reliable source first:
/// 1. the URL is an ATS posting → its JSON API;
/// 2. the page (e.g. a builtin listing) links to an ATS posting → that API, keeping the page's company name;
/// 3. the page has a schema.org JobPosting block → that;
/// 4. the page's Apply link goes to another site → read that page the same way (one hop).
/// Anything else is EnrichFailed, for Claude or you to fill in with add_jobs.
/// </summary>
public sealed class PostingReader(IFetcher fetcher, EnrichOptions options)
{
    readonly AtsReader _ats = new(fetcher);

    /// <summary>Shortest description treated as the real thing; anything shorter is a teaser or a parse miss.</summary>
    public const int MinDescriptionChars = 200;

    public Task<ReadPosting> ReadAsync(string url, CancellationToken ct) => ReadAsync(url, null, 0, ct);

    async Task<ReadPosting> ReadAsync(string url, string? companyHint, int depth, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new EnrichException(EnrichFailure.Permanent, $"not an http(s) URL: {url}");
        if (IsSkippedHost(uri.Host))
            throw new EnrichException(EnrichFailure.Permanent,
                $"{uri.Host} isn't read automatically (it blocks automated reads or needs a sign-in); fill this job in with add_jobs or on the dashboard");

        // 1. The URL is an ATS posting.
        var direct = AtsRef.Parse(url);
        if (direct is not null)
        {
            if (await TryAtsAsync(direct, url, companyHint, null, ct) is { } read) return read;
        }

        var page = await fetcher.GetAsync(url, ct);
        ThrowIfUnreadable(page, uri.Host);
        var html = page.Body;

        // A link that redirected onto an ATS posting (tracking links often do): read that ATS.
        if (direct is null && AtsRef.Parse(page.FinalUrl) is { } landed)
        {
            if (await TryAtsAsync(landed, page.FinalUrl, companyHint, null, ct) is { } read)
                return read with { How = $"{read.How} via redirect from {uri.Host}" };
        }
        var hint = companyHint ?? CompanyFromTitle(Html.Title(html));
        var heading = Html.FirstHeading(html);
        var links = Html.Links(html, page.FinalUrl).ToList();

        // 2. Apply link to an ATS posting.
        if (PickAtsLink(links, page.FinalUrl) is { } ats
            && await TryAtsAsync(ats.Ref, ats.Url, hint, heading, ct) is { } viaLink)
            return viaLink with { How = $"{viaLink.How} via {uri.Host}" };

        // 3. schema.org JobPosting on the page.
        if (FromJsonLd(html, hint) is { } ld)
        {
            if (string.IsNullOrWhiteSpace(ld.Title)) ld.Title = heading ?? "";
            if (depth > 0) ld.ApplyUrl ??= url;
            return new ReadPosting(Checked(ld, url), $"JSON-LD on {uri.Host}");
        }

        // 4. One hop through an Apply link to the employer's own site.
        if (depth == 0 && PickApplyLink(links, page.FinalUrl) is { } apply)
        {
            try
            {
                var r = await ReadAsync(apply.Url, hint, depth + 1, ct);
                r.Job.ApplyUrl ??= apply.Url;
                return r with { How = $"{r.How} via {uri.Host}" };
            }
            catch (EnrichException ex) when (ex.Kind == EnrichFailure.Permanent)
            {
                // Fall through to the error below, which names the listing page.
            }
        }

        if (ClosedNotice(html) is { } notice)
            throw new EnrichException(EnrichFailure.Gone, $"the posting is closed ({uri.Host} says \"{notice}\")");
        throw new EnrichException(EnrichFailure.Permanent,
            $"no structured job data on {uri.Host} (no ATS link, no JobPosting data); fill this job in with add_jobs or on the dashboard");
    }

    /// <summary>Wording job sites use on a posting that has closed but still answers 200.</summary>
    static readonly string[] ClosedPhrases =
    [
        "no longer accepting applications", "this job is no longer available", "this job has expired",
        "this position has been filled", "job you are looking for is no longer", "this posting has closed",
        "position is no longer available", "job is closed",
    ];

    static string? ClosedNotice(string html)
    {
        var text = Html.ToText(html);
        return ClosedPhrases.FirstOrDefault(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads an ATS posting. Returns null when the API couldn't help but the page might (unknown board, odd
    /// response): the caller carries on with the page. Throws Gone only when the posting is really gone, and
    /// Transient errors as they are.
    /// </summary>
    async Task<ReadPosting?> TryAtsAsync(AtsRef ats, string postingUrl, string? hint, string? heading, CancellationToken ct)
    {
        try
        {
            var job = await _ats.ReadAsync(ats, hint, ct);
            job.ApplyUrl ??= postingUrl;
            if (string.IsNullOrWhiteSpace(job.Title)) job.Title = heading ?? "";
            return new ReadPosting(Checked(job, postingUrl), $"{ats.Kind} API");
        }
        catch (EnrichException ex) when (ex.Kind == EnrichFailure.Permanent)
        {
            return null;
        }
        catch (EnrichException ex) when (ex.ApiNotFound)
        {
            if (await PostingPageGoneAsync(postingUrl, ats, ct)) throw;
            return null;
        }
    }

    /// <summary>
    /// After an ATS API 404: is the posting page gone too (404/410), or does it now send visitors back to the
    /// board, which is how closed postings behave? A live posting page means the API just didn't know the board.
    /// </summary>
    async Task<bool> PostingPageGoneAsync(string postingUrl, AtsRef ats, CancellationToken ct)
    {
        var page = await fetcher.GetAsync(postingUrl, ct);
        if (page.Status is 404 or 410) return true;
        if (!page.Ok) return false;
        var landed = AtsRef.Parse(page.FinalUrl);
        return landed is null || landed.PostingId != ats.PostingId;
    }

    bool IsSkippedHost(string host) =>
        options.EffectiveSkipHosts.Any(h => !string.IsNullOrWhiteSpace(h)
            && (host.Equals(h.Trim(), StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + h.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>A read that came back without a title or a real description isn't a usable job.</summary>
    static NewJob Checked(NewJob job, string url)
    {
        if (string.IsNullOrWhiteSpace(job.Title))
            throw new EnrichException(EnrichFailure.Permanent, $"no job title found for {url}");
        if ((job.JdText?.Length ?? 0) < MinDescriptionChars)
            throw new EnrichException(EnrichFailure.Permanent,
                $"job description at {url} is missing or too short ({job.JdText?.Length ?? 0} characters)");
        return job;
    }

    static void ThrowIfUnreadable(FetchResult page, string host)
    {
        if (page.Status == 0)
            throw new EnrichException(EnrichFailure.Transient, $"{host}: {page.Error}");
        if (page.Status is 404 or 410)
            throw new EnrichException(EnrichFailure.Gone, $"the posting is gone ({host} answered HTTP {page.Status})");
        if (page.Status is 429 or >= 500)
            throw new EnrichException(EnrichFailure.Transient, $"{host} answered HTTP {page.Status}");
        if (Challenge(page) is { } check)
            throw new EnrichException(EnrichFailure.Permanent, $"{host} showed a bot check ({check}); not worked around");
        if (!page.Ok)
            throw new EnrichException(EnrichFailure.Permanent, $"{host} refused the request (HTTP {page.Status})");
    }

    /// <summary>Markers of an interstitial bot check rather than the page. Deliberately narrow: a reCAPTCHA
    /// script on a normal job page (application forms often have one) is not a block.</summary>
    static string? Challenge(FetchResult p)
    {
        if (p.Headers.TryGetValue("cf-mitigated", out var m)) return $"Cloudflare {m}";
        var head = p.Body.Length > 30000 ? p.Body[..30000] : p.Body;
        (string Needle, string Name)[] signs =
        [
            ("<title>Just a moment...</title>", "Cloudflare"), ("cf-chl-", "Cloudflare"),
            ("px-captcha", "PerimeterX"), ("_Incapsula_Resource", "Imperva"), ("geo.captcha-delivery.com", "DataDome"),
        ];
        foreach (var (needle, name) in signs)
            if (head.Contains(needle, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }

    sealed record AtsLink(string Url, AtsRef Ref);

    /// <summary>
    /// The ATS posting this page sends applicants to: a link labeled "apply" wins; otherwise only an unambiguous
    /// single ATS posting on the page counts (a page linking to several is a list, not one job).
    /// </summary>
    static AtsLink? PickAtsLink(IReadOnlyList<Html.Link> links, string pageUrl)
    {
        var page = AtsRef.Parse(pageUrl);
        var candidates = links
            .Select(l => (l, r: AtsRef.Parse(l.Url)))
            .Where(x => x.r is not null && x.r != page)
            .ToList();
        if (candidates.Count == 0) return null;
        var labeled = candidates.FirstOrDefault(x => IsApplyText(x.l.Text));
        if (labeled.r is not null) return new AtsLink(labeled.l.Url, labeled.r);
        var distinct = candidates.DistinctBy(x => (x.r!.Kind, x.r.Board, x.r.PostingId)).ToList();
        return distinct.Count == 1 ? new AtsLink(distinct[0].l.Url, distinct[0].r!) : null;
    }

    /// <summary>An "Apply" link that leaves this site (to the employer's careers page).</summary>
    Html.Link? PickApplyLink(IReadOnlyList<Html.Link> links, string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return null;
        return links.FirstOrDefault(l => IsApplyText(l.Text)
            && Uri.TryCreate(l.Url, UriKind.Absolute, out var u)
            && !SameSite(u.Host, page.Host) && !IsSkippedHost(u.Host));
    }

    static bool IsApplyText(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t.Length <= 40 && (t == "apply" || t.StartsWith("apply ") || t.StartsWith("apply now") || t.Contains("apply on"));
    }

    static bool SameSite(string a, string b)
    {
        static string Root(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
        return Root(a).Equals(Root(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Senior Engineer - Acme Corp | Built In" → "Acme Corp". Only for titles in that shape.</summary>
    public static string? CompanyFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        const string suffix = "| Built In";
        var cut = title.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (cut <= 0) return null;
        var head = title[..cut].Trim();
        var dash = head.LastIndexOf(" - ", StringComparison.Ordinal);
        var company = dash > 0 ? head[(dash + 3)..].Trim() : null;
        return string.IsNullOrWhiteSpace(company) ? null : company;
    }

    /// <summary>The page's schema.org JobPosting with a real description, as a NewJob; null if there isn't one.</summary>
    public static NewJob? FromJsonLd(string html, string? companyHint)
    {
        foreach (var e in Html.JobPostings(html))
        {
            var jd = Html.ToText(AtsReader.Str(e, "description"));
            if (jd.Length < MinDescriptionChars) continue;

            var job = new NewJob
            {
                Title = AtsReader.Str(e, "title") ?? "",
                Company = companyHint ?? Organization(e) ?? "",
                DatePosted = AtsReader.Day(AtsReader.Str(e, "datePosted")),
                JdText = jd,
                ExternalJobId = e.TryGetProperty("identifier", out var id)
                    ? id.ValueKind == JsonValueKind.String ? id.GetString() : AtsReader.Str(id, "value")
                    : null,
            };
            job.Location = Location(e);
            var remoteType = AtsReader.Str(e, "jobLocationType");
            job.IsRemote = remoteType is not null
                ? remoteType.Contains("TELECOMMUTE", StringComparison.OrdinalIgnoreCase)
                : AtsReader.RemoteFrom(job.Location);
            if (e.TryGetProperty("baseSalary", out var pay) && pay.ValueKind == JsonValueKind.Object)
            {
                var currency = AtsReader.Str(pay, "currency");
                if (pay.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object)
                    AtsReader.SetSalary(job, AtsReader.Num(v, "minValue") ?? AtsReader.Num(v, "value"),
                        AtsReader.Num(v, "maxValue") ?? AtsReader.Num(v, "value"), currency, AtsReader.Str(v, "unitText"));
            }
            return job;
        }
        return null;
    }

    static string? Organization(JsonElement e) =>
        e.TryGetProperty("hiringOrganization", out var org)
            ? org.ValueKind == JsonValueKind.String ? org.GetString() : AtsReader.Str(org, "name")
            : null;

    static string? Location(JsonElement e)
    {
        if (!e.TryGetProperty("jobLocation", out var loc)) return null;
        var places = loc.ValueKind == JsonValueKind.Array ? loc.EnumerateArray().ToList() : new List<JsonElement> { loc };
        var names = places.Select(p =>
        {
            if (!p.TryGetProperty("address", out var a)) return null;
            if (a.ValueKind == JsonValueKind.String) return a.GetString();
            var country = AtsReader.Str(a, "addressCountry") ?? AtsReader.Str(a, "addressCountry", "name");
            var parts = new[] { AtsReader.Str(a, "addressLocality"), AtsReader.Str(a, "addressRegion"), country }
                .Where(x => !string.IsNullOrWhiteSpace(x));
            var s = string.Join(", ", parts);
            return s.Length == 0 ? null : s;
        }).Where(x => x is not null).Distinct().ToList();
        return names.Count == 0 ? null : string.Join("; ", names);
    }
}
