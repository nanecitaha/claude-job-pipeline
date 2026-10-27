using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobPipeline.Core;

namespace JobPipeline.Enricher;

/// <summary>A posting on an applicant tracking system whose public JSON API we can read.</summary>
public sealed record AtsRef(string Kind, string Board, string PostingId, string Host, string? Site = null, string? Path = null)
{
    /// <summary>Recognizes a posting URL on Greenhouse, Lever, Ashby, Workday or SmartRecruiters; null otherwise.</summary>
    public static AtsRef? Parse(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        var host = u.Host.ToLowerInvariant();
        var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        var query = QueryParams(u.Query);

        if (host.EndsWith("greenhouse.io"))
        {
            // boards.greenhouse.io/{board}/jobs/{id}, job-boards.greenhouse.io/{board}/jobs/{id}
            if (seg.Length >= 3 && seg[1] == "jobs" && IsDigits(seg[2]))
                return new AtsRef("greenhouse", seg[0], seg[2], host);
            // boards.greenhouse.io/embed/job_app?for={board}&token={id}
            if (seg.Length >= 2 && seg[0] == "embed" && query.TryGetValue("for", out var board)
                && query.TryGetValue("token", out var token) && IsDigits(token))
                return new AtsRef("greenhouse", board, token, host);
            return null;
        }

        // jobs.lever.co/{company}/{uuid}[/apply]
        if (host is "jobs.lever.co" or "jobs.eu.lever.co" && seg.Length >= 2 && Guid.TryParse(seg[1], out _))
            return new AtsRef("lever", seg[0], seg[1].ToLowerInvariant(), host);

        // jobs.ashbyhq.com/{board}/{uuid}[/application]
        if (host == "jobs.ashbyhq.com" && seg.Length >= 2 && Guid.TryParse(seg[1], out _))
            return new AtsRef("ashby", seg[0], seg[1].ToLowerInvariant(), host);

        // {tenant}.wd{n}.myworkdayjobs.com/[{locale}/]{site}/job/{location}/{title}_{req}
        if (host.EndsWith(".myworkdayjobs.com"))
        {
            var jobAt = Array.IndexOf(seg, "job");
            if (jobAt >= 1 && jobAt < seg.Length - 1)
                return new AtsRef("workday", host.Split('.')[0], seg[^1], host, Site: seg[jobAt - 1],
                    Path: string.Join('/', seg[(jobAt + 1)..].Select(Uri.EscapeDataString)));
            return null;
        }

        // jobs.smartrecruiters.com/{company}/{postingId}-{slug}
        if (host is "jobs.smartrecruiters.com" or "careers.smartrecruiters.com" && seg.Length >= 2
            && Regex.Match(seg[1], @"^(\d{6,})") is { Success: true } sr)
            return new AtsRef("smartrecruiters", seg[0], sr.Groups[1].Value, host);

        return null;
    }

    static Dictionary<string, string> QueryParams(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => Uri.UnescapeDataString(p[0]).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1]));

    static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}

/// <summary>
/// Reads one posting from its ATS's public JSON API into a <see cref="NewJob"/> (without tech stack or verdict).
/// Throws <see cref="EnrichException"/>: Gone when the API says the posting doesn't exist, Transient on network
/// or server errors, Permanent when the response can't be understood.
/// </summary>
public sealed class AtsReader(IFetcher fetcher)
{
    // Ashby serves a whole board per request: keep it briefly so a batch from one company costs one call.
    readonly Dictionary<string, (DateTimeOffset At, JsonDocument Doc)> _boards = new();
    static readonly TimeSpan BoardCacheFor = TimeSpan.FromMinutes(10);

    public async Task<NewJob> ReadAsync(AtsRef ats, string? companyHint, CancellationToken ct) => ats.Kind switch
    {
        "greenhouse" => await GreenhouseAsync(ats, companyHint, ct),
        "lever" => await LeverAsync(ats, companyHint, ct),
        "ashby" => await AshbyAsync(ats, companyHint, ct),
        "workday" => await WorkdayAsync(ats, companyHint, ct),
        "smartrecruiters" => await SmartRecruitersAsync(ats, companyHint, ct),
        _ => throw new EnrichException(EnrichFailure.Permanent, $"no reader for {ats.Kind}"),
    };

    async Task<NewJob> GreenhouseAsync(AtsRef a, string? hint, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://boards-api.greenhouse.io/v1/boards/{a.Board}/jobs/{a.PostingId}", "Greenhouse", ct);
        var e = doc.RootElement;
        var location = Str(e, "location", "name");
        return new NewJob
        {
            Company = hint ?? Str(e, "company_name") ?? Titleize(a.Board),
            Title = Str(e, "title") ?? "",
            ApplyUrl = Str(e, "absolute_url") ?? $"https://job-boards.greenhouse.io/{a.Board}/jobs/{a.PostingId}",
            ExternalJobId = a.PostingId,
            Location = location,
            IsRemote = RemoteFrom(location),
            DatePosted = Day(Str(e, "first_published") ?? Str(e, "updated_at")),
            JdText = Html.ToText(Str(e, "content")),
        };
    }

    async Task<NewJob> LeverAsync(AtsRef a, string? hint, CancellationToken ct)
    {
        var api = a.Host.Contains(".eu.") ? "api.eu.lever.co" : "api.lever.co";
        using var doc = await GetJsonAsync($"https://{api}/v0/postings/{a.Board}/{a.PostingId}", "Lever", ct);
        var e = doc.RootElement;

        var jd = new StringBuilder(Str(e, "descriptionPlain") ?? Html.ToText(Str(e, "description")));
        if (e.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Array)
            foreach (var list in lists.EnumerateArray())
                jd.Append("\n\n").Append(Str(list, "text")).Append('\n').Append(Html.ToText(Str(list, "content")));
        var more = Str(e, "additionalPlain") ?? Html.ToText(Str(e, "additional"));
        if (more.Length > 0) jd.Append("\n\n").Append(more);

        var location = Str(e, "categories", "location");
        var workplace = Str(e, "workplaceType");
        var job = new NewJob
        {
            Company = hint ?? Titleize(a.Board),
            Title = Str(e, "text") ?? "",
            ApplyUrl = Str(e, "hostedUrl") ?? $"https://{a.Host}/{a.Board}/{a.PostingId}",
            ExternalJobId = a.PostingId,
            Location = location,
            IsRemote = workplace is { } w ? w.Equals("remote", StringComparison.OrdinalIgnoreCase) : RemoteFrom(location),
            DatePosted = e.TryGetProperty("createdAt", out var c) && c.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd") : null,
            JdText = jd.ToString().Trim(),
        };
        if (e.TryGetProperty("salaryRange", out var s) && s.ValueKind == JsonValueKind.Object)
            SetSalary(job, Num(s, "min"), Num(s, "max"), Str(s, "currency"), Str(s, "interval"));
        job.SalaryText ??= Str(e, "salaryDescriptionPlain");
        return job;
    }

    async Task<NewJob> AshbyAsync(AtsRef a, string? hint, CancellationToken ct)
    {
        var board = await AshbyBoardAsync(a.Board, ct);
        if (!board.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            throw new EnrichException(EnrichFailure.Permanent, $"Ashby board '{a.Board}' returned no job list");
        var e = jobs.EnumerateArray().FirstOrDefault(j => string.Equals(Str(j, "id"), a.PostingId, StringComparison.OrdinalIgnoreCase));
        if (e.ValueKind != JsonValueKind.Object)
            throw new EnrichException(EnrichFailure.Gone, $"posting {a.PostingId} is no longer on the {a.Board} Ashby board");

        var location = Str(e, "location");
        var job = new NewJob
        {
            Company = hint ?? Titleize(a.Board),
            Title = Str(e, "title") ?? "",
            ApplyUrl = Str(e, "jobUrl") ?? $"https://jobs.ashbyhq.com/{a.Board}/{a.PostingId}",
            ExternalJobId = a.PostingId,
            Location = location,
            IsRemote = e.TryGetProperty("isRemote", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? r.GetBoolean() : RemoteFrom(location),
            DatePosted = Day(Str(e, "publishedAt")),
            JdText = Str(e, "descriptionPlain") ?? Html.ToText(Str(e, "descriptionHtml")),
        };
        if (e.TryGetProperty("compensation", out var comp) && comp.ValueKind == JsonValueKind.Object)
        {
            job.SalaryText = Str(comp, "scrapeableCompensationSalarySummary") ?? Str(comp, "compensationTierSummary");
            if (comp.TryGetProperty("summaryComponents", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                var salary = parts.EnumerateArray().FirstOrDefault(p => Str(p, "compensationType") == "Salary");
                if (salary.ValueKind == JsonValueKind.Object)
                    SetSalary(job, Num(salary, "minValue"), Num(salary, "maxValue"), Str(salary, "currencyCode"), Str(salary, "interval"));
            }
        }
        return job;
    }

    async Task<JsonDocument> AshbyBoardAsync(string board, CancellationToken ct)
    {
        if (_boards.TryGetValue(board, out var hit) && DateTimeOffset.UtcNow - hit.At < BoardCacheFor) return hit.Doc;
        var doc = await GetJsonAsync($"https://api.ashbyhq.com/posting-api/job-board/{board}?includeCompensation=true", "Ashby", ct);
        if (_boards.Remove(board, out var old)) old.Doc.Dispose();
        _boards[board] = (DateTimeOffset.UtcNow, doc);
        return doc;
    }

    async Task<NewJob> WorkdayAsync(AtsRef a, string? hint, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://{a.Host}/wday/cxs/{a.Board}/{a.Site}/job/{a.Path}", "Workday", ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("jobPostingInfo", out var e) || e.ValueKind != JsonValueKind.Object)
            throw new EnrichException(EnrichFailure.Permanent, "Workday response has no jobPostingInfo");
        if (e.TryGetProperty("canApply", out var can) && can.ValueKind == JsonValueKind.False)
            throw new EnrichException(EnrichFailure.Gone, "Workday says this posting no longer takes applications");

        var location = Str(e, "location");
        var remoteType = Str(e, "remoteType");
        return new NewJob
        {
            Company = hint ?? Str(root, "hiringOrganization", "name") ?? Titleize(a.Board),
            Title = Str(e, "title") ?? "",
            ApplyUrl = Str(e, "externalUrl") ?? $"https://{a.Host}/{a.Site}/job/{a.Path}",
            ExternalJobId = Str(e, "jobReqId") ?? a.PostingId,
            Location = location,
            IsRemote = remoteType is { } rt ? rt.Contains("remote", StringComparison.OrdinalIgnoreCase) : RemoteFrom(location),
            DatePosted = Day(Str(e, "startDate")),
            JdText = Html.ToText(Str(e, "jobDescription")),
        };
    }

    async Task<NewJob> SmartRecruitersAsync(AtsRef a, string? hint, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"https://api.smartrecruiters.com/v1/companies/{a.Board}/postings/{a.PostingId}", "SmartRecruiters", ct);
        var e = doc.RootElement;
        if (e.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)
            throw new EnrichException(EnrichFailure.Gone, "SmartRecruiters says this posting is no longer active");

        var jd = new StringBuilder();
        if (e.TryGetProperty("jobAd", out var ad) && ad.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "companyDescription", "jobDescription", "qualifications", "additionalInformation" })
                if (sections.TryGetProperty(name, out var sec) && Html.ToText(Str(sec, "text")) is { Length: > 0 } text)
                {
                    if (jd.Length > 0) jd.Append("\n\n");
                    if (Str(sec, "title") is { Length: > 0 } heading) jd.Append(heading).Append('\n');
                    jd.Append(text);
                }

        string? location = null;
        bool? remote = null;
        if (e.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            location = Str(loc, "fullLocation")
                ?? string.Join(", ", new[] { Str(loc, "city"), Str(loc, "region"), Str(loc, "country")?.ToUpperInvariant() }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (loc.TryGetProperty("remote", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) remote = r.GetBoolean();
        }
        return new NewJob
        {
            Company = hint ?? Str(e, "company", "name") ?? Titleize(a.Board),
            Title = Str(e, "name") ?? "",
            ApplyUrl = Str(e, "postingUrl") ?? $"https://jobs.smartrecruiters.com/{a.Board}/{a.PostingId}",
            ExternalJobId = a.PostingId,
            Location = string.IsNullOrWhiteSpace(location) ? null : location,
            IsRemote = remote ?? RemoteFrom(location),
            DatePosted = Day(Str(e, "releasedDate")),
            JdText = jd.ToString(),
        };
    }

    async Task<JsonDocument> GetJsonAsync(string url, string ats, CancellationToken ct)
    {
        var res = await fetcher.GetAsync(url, ct);
        if (res.Status is 404 or 410)
            throw new EnrichException(EnrichFailure.Gone, $"{ats} says the posting doesn't exist (HTTP {res.Status})") { ApiNotFound = true };
        if (res.Status == 0 || res.Status is 429 or >= 500)
            throw new EnrichException(EnrichFailure.Transient, $"{ats} API: {res.Error ?? $"HTTP {res.Status}"}");
        if (!res.Ok)
            throw new EnrichException(EnrichFailure.Permanent, $"{ats} API refused the request (HTTP {res.Status})");
        try
        {
            return JsonDocument.Parse(res.Body);
        }
        catch (JsonException)
        {
            throw new EnrichException(EnrichFailure.Permanent, $"{ats} API returned something that isn't JSON");
        }
    }

    // ── small helpers shared with the page reader ──

    public static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String && e.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    }

    public static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
            : null
            : null;

    /// <summary>Sets numeric salary only for yearly pay; other intervals are kept as text so nothing is misread.</summary>
    public static void SetSalary(NewJob job, double? min, double? max, string? currency, string? interval)
    {
        if (min is null && max is null) return;
        var yearly = interval is null || interval.Contains("year", StringComparison.OrdinalIgnoreCase)
                     || interval.Equals("YEAR", StringComparison.OrdinalIgnoreCase);
        var cur = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        string Fmt(double? v) => v is null ? "?" : v.Value.ToString("#,0", CultureInfo.InvariantCulture);
        job.SalaryText ??= $"{(cur is null ? "" : cur + " ")}{Fmt(min)}-{Fmt(max)}{(interval is null ? "" : " " + interval)}";
        if (!yearly) return;
        job.SalaryMin = min is null ? null : (int)Math.Round(min.Value);
        job.SalaryMax = max is null ? null : (int)Math.Round(max.Value);
        job.SalaryCurrency = cur;
    }

    /// <summary>yyyy-MM-dd from an ISO date or date-time; null when it isn't one.</summary>
    public static string? Day(string? iso) =>
        iso is null ? null
        : DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime.ToString("yyyy-MM-dd")
        : null;

    public static bool? RemoteFrom(string? location) =>
        location is null ? null : location.Contains("remote", StringComparison.OrdinalIgnoreCase) ? true : null;

    /// <summary>"acme-labs" → "Acme Labs": a last-resort company name from an ATS board slug.</summary>
    public static string Titleize(string slug) =>
        string.Join(' ', Regex.Split(slug, @"[-_\s]+").Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

/// <summary>Why a posting couldn't be read; <see cref="Kind"/> decides retry, EnrichFailed or Expired.</summary>
public sealed class EnrichException(EnrichFailure kind, string message) : Exception(message)
{
    public EnrichFailure Kind { get; } = kind;
    /// <summary>
    /// The ATS API answered 404/410. Usually the posting is closed, but the API may simply not know the board
    /// (another region, a custom domain), so the reader checks the posting page before calling it gone.
    /// </summary>
    public bool ApiNotFound { get; init; }
}
