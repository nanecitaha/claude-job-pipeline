using JobPipeline.Core;
using JobPipeline.Enricher;

/// <summary>Enricher logic with no database: URL recognition, HTML, rules, tech stack, and the reader on canned pages.</summary>
public static class EnrichLogicTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed = 0, failed = 0;
        void Test(string name, Func<Task> body)
        {
            try { body().GetAwaiter().GetResult(); passed++; Console.WriteLine($"  PASS  {name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}"); }
        }
        void Sync(string name, Action body) => Test(name, () => { body(); return Task.CompletedTask; });

        Sync("ats: recognizes posting URLs on the five ATSs, not board pages or other sites", () =>
        {
            Eq(new AtsRef("greenhouse", "current", "8687284002", "job-boards.greenhouse.io"),
                AtsRef.Parse("https://job-boards.greenhouse.io/current/jobs/8687284002?gh_src=abc"));
            Eq(new AtsRef("greenhouse", "acme", "4001", "boards.greenhouse.io"),
                AtsRef.Parse("https://boards.greenhouse.io/embed/job_app?for=acme&token=4001"));
            Eq<AtsRef?>(null, AtsRef.Parse("https://boards.greenhouse.io/acme"), "board page");
            Eq("lever|palantir|10dfc8bc-99ad-4ca2-ab76-853cb90a92c2",
                Key(AtsRef.Parse("https://jobs.lever.co/palantir/10DFC8BC-99ad-4ca2-ab76-853cb90a92c2/apply")));
            Eq("ashby|ramp|7bfa613e-151c-469b-9973-c89ee3d14838",
                Key(AtsRef.Parse("https://jobs.ashbyhq.com/ramp/7bfa613e-151c-469b-9973-c89ee3d14838/application")));
            var wd = AtsRef.Parse("https://travelers.wd5.myworkdayjobs.com/en-US/External/job/CT---Hartford/Senior-Engineer_R-51546")!;
            Eq("workday|travelers|External|CT---Hartford/Senior-Engineer_R-51546", $"{wd.Kind}|{wd.Board}|{wd.Site}|{wd.Path}");
            Eq("smartrecruiters|TreehouseStrategy|743999873587821",
                Key(AtsRef.Parse("https://jobs.smartrecruiters.com/TreehouseStrategy/743999873587821-senior-c-net-backend-developer?oga=true")));
            Eq<AtsRef?>(null, AtsRef.Parse("https://builtin.com/job/senior-c-net-backend-developer/10494669"));
            Eq<AtsRef?>(null, AtsRef.Parse("not a url"));
        });

        Sync("fetcher: only public addresses are fetched (no localhost, private networks or cloud metadata)", () =>
        {
            foreach (var ip in new[] { "127.0.0.1", "10.1.2.3", "172.20.0.1", "192.168.1.10", "169.254.169.254", "100.64.0.1",
                                       "0.0.0.0", "::1", "fe80::1", "fd00::1", "::ffff:127.0.0.1" })
                Eq(false, HttpFetcher.IsPublic(System.Net.IPAddress.Parse(ip)), ip);
            foreach (var ip in new[] { "8.8.8.8", "104.18.0.1", "172.32.0.1", "2606:4700::1" })
                Eq(true, HttpFetcher.IsPublic(System.Net.IPAddress.Parse(ip)), ip);
        });

        Sync("html: text keeps structure, drops scripts, decodes entity-encoded HTML", () =>
        {
            var text = Html.ToText("<div><h2>About</h2><p>We use C# &amp; .NET.</p><script>var x = 1;</script>" +
                                   "<ul><li>RabbitMQ</li><li>SQL&nbsp;Server</li></ul></div>");
            Eq("About\nWe use C# & .NET.\n- RabbitMQ\n- SQL Server", text);
            Eq("Hello\nworld", Html.ToText("&lt;p&gt;Hello&lt;/p&gt;&lt;p&gt;world&lt;/p&gt;"), "Greenhouse-style encoded content");
            Eq("", Html.ToText(null));
        });

        Sync("html: links with text, title, first heading, JSON-LD inside @graph, broken JSON-LD ignored", () =>
        {
            const string page = """
                <html><head><title>Senior C# Dev - Acme Corp | Built In</title>
                <script type="application/ld+json">{ broken </script>
                <script type="application/ld+json">{"@graph":[{"@type":"Organization"},{"@type":["JobPosting"],"title":"X"}]}</script></head>
                <body><h1 class="t">Senior <b>C#</b> Dev</h1>
                <a href="/job/other/2">Other job</a><a class="btn" href='https://jobs.lever.co/acme/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2'><span>Apply</span></a>
                <a href="mailto:x@y.z">mail</a></body></html>
                """;
            var links = Html.Links(page, "https://builtin.com/job/x/1").ToList();
            Eq(2, links.Count, "mailto skipped");
            Eq("https://builtin.com/job/other/2", links[0].Url, "relative resolved");
            Eq("Apply", links[1].Text);
            Eq("Senior C# Dev - Acme Corp | Built In", Html.Title(page));
            Eq("Senior C# Dev", Html.FirstHeading(page));
            Eq(1, Html.JobPostings(page).Count);
            Eq("Acme Corp", PostingReader.CompanyFromTitle(Html.Title(page)));
            Eq<string?>(null, PostingReader.CompanyFromTitle("Careers at Acme"));
        });

        Sync("terms: word edges respected; symbols at a term's edge need no edge", () =>
        {
            Eq(-1, Terms.Find("Internal Tools Engineer", "intern"));
            Eq(0, Terms.Find("Intern, Software", "intern"));
            Eq(3, Terms.Find("ASP.NET Core", ".NET"));
            Eq(0, Terms.Find("C#/.NET developer", "C#"));
            Eq(-1, Terms.Find("JavaScript", "Java"));
            Eq(4, Terms.Find("Sr. Jr. Dev", "jr"));
            Eq(-1, Terms.Find("C++ and C#", "C"));
            Eq(-1, Terms.Find("react quickly", "React", caseSensitive: true));
            Eq(-1, Terms.Find("Email jobs@acme.net or visit careers.acme.net/apply", ".NET"), "a domain isn't .NET");
            Eq(4, Terms.Find("Use .Net 8", ".NET"));
            Eq(3, Terms.Find("asp.Net core", ".NET"));
        });

        Sync("rules: Filters section parsed; C# isn't a comment; empty line turns a rule off; no section = defaults", () =>
        {
            var r = HuntRules.Parse("""
                # Hunt config
                - require_any: nothing   # outside the section: ignored
                ## Filters (used by the enricher)
                - require_any: C#, .NET   # comment
                - review_if:
                ## Apply rules
                - exclude_title: ignored
                """.Split('\n'));
            Eq("C#|.NET", string.Join("|", r.RequireAny));
            Eq(0, r.ReviewIf.Count, "empty = off");
            Eq(string.Join("|", HuntRules.Defaults.ExcludeTitle), string.Join("|", r.ExcludeTitle), "missing line = default");
            Eq(false, r.FromDefaults);
            Eq(true, HuntRules.Parse(["# Hunt config", "## Keep", "- Stack: C#"]).FromDefaults);
        });

        Sync("rules: exclude by title, review thin or clearance or non-engineering, keep with the matched term", () =>
        {
            var r = new HuntRules();
            var jd = "We build services in C# on .NET 8 with RabbitMQ. " + new string('x', 300);
            Eq(EnrichVerdict.Exclude, r.Evaluate("Junior .NET Developer", jd).Verdict);
            Eq(EnrichVerdict.Keep, r.Evaluate("Senior .NET Developer", jd + " You will mentor junior engineers.").Verdict,
                "junior in the description is not a junior role");
            Eq(EnrichVerdict.Review, r.Evaluate("Senior .NET Developer", "C# only").Verdict, "thin description");
            Eq(EnrichVerdict.Exclude, r.Evaluate("Senior Java Developer", "Java and Spring. " + new string('x', 300)).Verdict);
            var clearance = r.Evaluate("Senior .NET Developer", jd + " Must be able to obtain a Security Clearance.");
            Eq(EnrichVerdict.Review, clearance.Verdict);
            Eq(true, clearance.Reason.Contains("Security Clearance"), clearance.Reason);
            Eq(EnrichVerdict.Review, r.Evaluate("Product Designer", jd).Verdict, "not an engineering title");
            var keep = r.Evaluate("Senior Backend Engineer", jd);
            Eq((EnrichVerdict.Keep, "C# mentioned"), keep);
        });

        Sync("tech stack: exact spelling, first-appearance order, longest match wins, case-sensitive and regex terms", () =>
        {
            var t = TechStack.Parse(TechStack.Starter.Split('\n'));
            var found = t.Extract("Senior Engineer\nWe use ASP.NET Core and .NET 8 on Azure. Experience with sql server, " +
                                  "React and the rest of the stack. You will react quickly. Also .NET elsewhere and C#.NET.");
            Eq("ASP.NET Core, .NET 8, Azure, sql server, React, .NET, C#.NET", string.Join(", ", found));
            Eq(0, t.Extract("").Count);
            Eq("C#, .NET", string.Join(", ", t.Extract("C# 5+ years, .NET 6+ years")), "years and '+' aren't versions");
            var small = TechStack.Parse(["# comment", "re:[", "Kafka"]);
            Eq(1, small.Count, "a broken pattern is skipped, not fatal");
            Eq("kafka", string.Join(", ", small.Extract("kafka? Kafka!")), "first appearance's spelling, listed once");
        });

        Test("reader: builtin page → its Apply link's Greenhouse API, company from the page title", async () =>
        {
            var f = new FakeFetcher()
                .Page("https://builtin.com/job/senior-c-net/1", Builtin("Senior C# .NET Developer", "Acme Corp",
                    "<a href=\"https://job-boards.greenhouse.io/acme/jobs/4001?gh_src=builtin\">Apply</a>"))
                .Json("https://boards-api.greenhouse.io/v1/boards/acme/jobs/4001", $$$"""
                    {"id":4001,"title":"Senior C# .NET Developer","absolute_url":"https://job-boards.greenhouse.io/acme/jobs/4001",
                     "location":{"name":"Remote - US"},"first_published":"2026-10-20T10:00:00-04:00","company_name":"Acme Corporation Inc",
                     "content":"{{{System.Net.WebUtility.HtmlEncode("<p>" + Long("C# and .NET 8") + "</p>")}}}"}
                    """);
            var r = await Reader(f).ReadAsync("https://builtin.com/job/senior-c-net/1", default);
            Eq("Acme Corp", r.Job.Company, "the listing's name beats the ATS's legal name");
            Eq("Senior C# .NET Developer", r.Job.Title);
            Eq("https://job-boards.greenhouse.io/acme/jobs/4001", r.Job.ApplyUrl);
            Eq("4001", r.Job.ExternalJobId);
            Eq(true, r.Job.IsRemote);
            Eq("2026-10-20", r.Job.DatePosted);
            Eq(true, r.Job.JdText!.StartsWith("C# and .NET 8"), r.Job.JdText);
            Eq("greenhouse API via builtin.com", r.How);
        });

        Test("reader: Apply link to the employer's own site → its JSON-LD, one hop", async () =>
        {
            var f = new FakeFetcher()
                .Page("https://builtin.com/job/x/2", Builtin("Platform Engineer", "Globex",
                    "<a href=\"https://careers.globex.com/jobs/77\">Apply</a>"))
                .Page("https://careers.globex.com/jobs/77", JsonLdPage("""
                    {"@context":"https://schema.org","@type":"JobPosting","title":"Platform Engineer",
                     "hiringOrganization":{"@type":"Organization","name":"Globex Inc"},"datePosted":"2026-10-01",
                     "jobLocationType":"TELECOMMUTE","identifier":{"@type":"PropertyValue","value":"REQ-77"},
                     "baseSalary":{"@type":"MonetaryAmount","currency":"USD","value":{"@type":"QuantitativeValue","minValue":150000,"maxValue":180000,"unitText":"YEAR"}},
                     "description":"DESC"}
                    """.Replace("DESC", Long("Platform work in C#"))));
            var r = await Reader(f).ReadAsync("https://builtin.com/job/x/2", default);
            Eq("Globex", r.Job.Company);
            Eq("https://careers.globex.com/jobs/77", r.Job.ApplyUrl);
            Eq("REQ-77", r.Job.ExternalJobId);
            Eq(true, r.Job.IsRemote);
            Eq((150000, 180000, "USD"), (r.Job.SalaryMin, r.Job.SalaryMax, r.Job.SalaryCurrency));
            Eq("JSON-LD on careers.globex.com via builtin.com", r.How);
        });

        Test("reader: failures are classified (gone, blocked, server error, no data, skipped site)", async () =>
        {
            var f = new FakeFetcher()
                .Status("https://builtin.com/job/gone/3", 404)
                .Status("https://builtin.com/job/busy/4", 503)
                .Page("https://example.com/blocked", "<html><title>Just a moment...</title></html>", headers: new() { ["cf-mitigated"] = "challenge" })
                .Page("https://example.com/nothing", "<html><body><h1>Job</h1><p>Short.</p></body></html>")
                .Page("https://example.com/closed", "<html><body><h1>Job</h1><p>Sorry, this job is no longer available.</p></body></html>")
                .Page("https://example.com/many", "<a href=\"https://jobs.lever.co/a/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2\">Job A</a>" +
                                                  "<a href=\"https://jobs.lever.co/a/20dfc8bc-99ad-4ca2-ab76-853cb90a92c2\">Job B</a>");
            var reader = Reader(f);
            await Fails(reader, "https://builtin.com/job/gone/3", EnrichFailure.Gone);
            await Fails(reader, "https://builtin.com/job/busy/4", EnrichFailure.Transient);
            await Fails(reader, "https://example.com/blocked", EnrichFailure.Permanent, "bot check");
            await Fails(reader, "https://example.com/nothing", EnrichFailure.Permanent, "no structured job data");
            await Fails(reader, "https://example.com/closed", EnrichFailure.Gone, "no longer available");
            await Fails(reader, "https://example.com/many", EnrichFailure.Permanent, "no structured job data");
            Eq(false, f.Requested.Any(u => u.Contains("api.lever.co")), "a page listing several ATS jobs isn't treated as one job");
            await Fails(reader, "https://www.linkedin.com/jobs/view/123", EnrichFailure.Permanent, "isn't read automatically");
            Eq(false, f.Requested.Any(u => u.Contains("linkedin")), "skipped sites are never requested");
        });

        Test("reader: Lever, Ashby (board cached, missing posting = gone), Workday (closed = gone), SmartRecruiters", async () =>
        {
            const string lever = "https://jobs.lever.co/palantir/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2";
            const string ashby = "https://jobs.ashbyhq.com/ramp/7bfa613e-151c-469b-9973-c89ee3d14838";
            const string wd = "https://travelers.wd5.myworkdayjobs.com/External/job/CT---Hartford/Senior-Engineer_R-51546";
            const string sr = "https://jobs.smartrecruiters.com/Treehouse/743999873587821-senior-dev";
            var f = new FakeFetcher()
                .Json("https://api.lever.co/v0/postings/palantir/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2", $$$"""
                    {"text":"Backend Engineer","categories":{"location":"New York, NY"},"workplaceType":"onsite","createdAt":1710115200000,
                     "descriptionPlain":"{{{Long("We write C#.")}}}","lists":[{"text":"Requirements","content":"<li>Kafka</li>"}],
                     "salaryRange":{"min":140000,"max":200000,"currency":"USD","interval":"per-year-salary"},"hostedUrl":"{{{lever}}}"}
                    """)
                .Json("https://api.ashbyhq.com/posting-api/job-board/ramp?includeCompensation=true", $$$"""
                    {"jobs":[{"id":"7bfa613e-151c-469b-9973-c89ee3d14838","title":"Backend Engineer, Ops","location":"New York",
                      "isRemote":false,"publishedAt":"2026-03-04T00:00:00Z","descriptionPlain":"{{{Long("C# backend")}}}",
                      "jobUrl":"{{{ashby}}}","compensation":{"compensationTierSummary":"$150K – $200K",
                      "summaryComponents":[{"compensationType":"Salary","interval":"1 YEAR","currencyCode":"USD","minValue":150000,"maxValue":200000}]}}]}
                    """)
                .Json("https://travelers.wd5.myworkdayjobs.com/wday/cxs/travelers/External/job/CT---Hartford/Senior-Engineer_R-51546", $$$"""
                    {"jobPostingInfo":{"title":"Senior Engineer","jobDescription":"<p>{{{Long(".NET and C#")}}}</p>","location":"Hartford, CT",
                     "startDate":"2026-10-01","jobReqId":"R-51546","canApply":true,"externalUrl":"{{{wd}}}"},
                     "hiringOrganization":{"name":"Travelers"}}
                    """)
                .Json("https://api.smartrecruiters.com/v1/companies/Treehouse/postings/743999873587821", $$$"""
                    {"name":"Senior C# Developer","company":{"name":"Treehouse"},"releasedDate":"2026-09-30T12:00:00.000Z",
                     "location":{"city":"White Plains","region":"NY","country":"us","remote":true},
                     "jobAd":{"sections":{"jobDescription":{"title":"Job Description","text":"<p>{{{Long("C# services")}}}</p>" } } } }
                    """);
            var reader = Reader(f);

            var l = (await reader.ReadAsync(lever, default)).Job;
            Eq(("Palantir", "Backend Engineer", false, "2024-03-11"), (l.Company, l.Title, l.IsRemote, l.DatePosted));
            Eq((140000, 200000), (l.SalaryMin, l.SalaryMax));
            Eq(true, l.JdText!.Contains("Requirements\n- Kafka"), l.JdText);

            var a = (await reader.ReadAsync(ashby, default)).Job;
            Eq(("Ramp", "Backend Engineer, Ops", "2026-03-04", 150000), (a.Company, a.Title, a.DatePosted, a.SalaryMin));
            await Fails(reader, "https://jobs.ashbyhq.com/ramp/00000000-151c-469b-9973-c89ee3d14838", EnrichFailure.Gone);
            Eq(1, f.Requested.Count(u => u.Contains("api.ashbyhq.com")), "board fetched once for both postings");

            var w = (await reader.ReadAsync(wd, default)).Job;
            Eq(("Travelers", "R-51546", "2026-10-01"), (w.Company, w.ExternalJobId, w.DatePosted));

            var s = (await reader.ReadAsync(sr, default)).Job;
            Eq(("Treehouse", "Senior C# Developer", true, "White Plains, NY, US"), (s.Company, s.Title, s.IsRemote, s.Location));

            f.Json("https://travelers.wd5.myworkdayjobs.com/wday/cxs/travelers/External/job/CT---Hartford/Closed_R-1",
                """{"jobPostingInfo":{"title":"Closed","canApply":false}}""");
            await Fails(reader, "https://travelers.wd5.myworkdayjobs.com/External/job/CT---Hartford/Closed_R-1", EnrichFailure.Gone);
        });

        Test("reader: an ATS API 404 means gone only if the posting page agrees; redirects onto ATS postings are followed", async () =>
        {
            const string eu = "https://job-boards.eu.greenhouse.io/acme/jobs/555";
            const string closed = "https://job-boards.greenhouse.io/acme/jobs/556";
            const string removed = "https://job-boards.greenhouse.io/acme/jobs/557";
            const string lever = "https://jobs.lever.co/acme/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2";
            var f = new FakeFetcher()
                .Page(eu, "<html><body><h1>Senior Dev</h1><p>Live page, but the API doesn't know this board.</p></body></html>")
                .Redirect(closed, "https://job-boards.greenhouse.io/acme?error=true")
                .Status(removed, 404)
                .Page("https://builtin.com/job/x/9", Builtin("Senior Dev", "Acme", "<a href=\"https://click.example.com/t?id=1\">Apply</a>"))
                .Redirect("https://click.example.com/t?id=1", lever)
                .Json("https://api.lever.co/v0/postings/acme/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2",
                    $$$"""{"text":"Senior Dev","descriptionPlain":"{{{Long("C# work")}}}"}""");
            var reader = Reader(f);
            await Fails(reader, eu, EnrichFailure.Permanent, "no structured job data");
            await Fails(reader, closed, EnrichFailure.Gone);
            await Fails(reader, removed, EnrichFailure.Gone);
            var r = await reader.ReadAsync("https://builtin.com/job/x/9", default);
            Eq(("Acme", lever), (r.Job.Company, r.Job.ApplyUrl));
            Eq("lever API via redirect from click.example.com via builtin.com", r.How);
        });

        return (passed, failed);
    }

    // ── helpers ──

    public static PostingReader Reader(IFetcher f) => new(f, new EnrichOptions());

    public static string Long(string start) => start + " " + string.Join(" ", Enumerable.Repeat("Build reliable services with the team.", 8));

    public static string Builtin(string title, string company, string body) =>
        $"<html><head><title>{title} - {company} | Built In</title></head><body><h1>{title}</h1>{body}</body></html>";

    static string JsonLdPage(string json) =>
        $"<html><head><script type=\"application/ld+json\">{json}</script></head><body></body></html>";

    static string Key(AtsRef? r) => r is null ? "null" : $"{r.Kind}|{r.Board}|{r.PostingId}";

    static async Task Fails(PostingReader reader, string url, EnrichFailure kind, string contains = "")
    {
        try
        {
            await reader.ReadAsync(url, default);
        }
        catch (EnrichException ex) when (ex.Kind == kind && ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        catch (EnrichException ex)
        {
            throw new Exception($"{url}: expected {kind} '{contains}' but got {ex.Kind}: {ex.Message}");
        }
        throw new Exception($"{url}: expected {kind} failure, but it was read");
    }

    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }
}

/// <summary>Canned responses by exact URL; anything else is a 404. Records every request.</summary>
public sealed class FakeFetcher : IFetcher
{
    readonly Dictionary<string, FetchResult> _pages = new();
    public List<string> Requested { get; } = new();

    public FakeFetcher Page(string url, string html, int status = 200, Dictionary<string, string>? headers = null)
    {
        _pages[url] = new FetchResult(status, html, url, "text/html", null, headers ?? new());
        return this;
    }

    /// <summary>A URL that redirects: the response comes from <paramref name="finalUrl"/>.</summary>
    public FakeFetcher Redirect(string url, string finalUrl, string html = "<html></html>")
    {
        _pages[url] = new FetchResult(200, html, finalUrl, "text/html", null, new Dictionary<string, string>());
        return this;
    }

    public FakeFetcher Json(string url, string json)
    {
        _pages[url] = new FetchResult(200, json, url, "application/json", null, new Dictionary<string, string>());
        return this;
    }

    public FakeFetcher Status(string url, int status) => Page(url, "", status);

    public Task<FetchResult> GetAsync(string url, CancellationToken ct)
    {
        Requested.Add(url);
        return Task.FromResult(_pages.TryGetValue(url, out var r)
            ? r
            : new FetchResult(404, "not found", url, "text/html", null, new Dictionary<string, string>()));
    }
}
