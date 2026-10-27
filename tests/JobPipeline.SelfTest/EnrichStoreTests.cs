using JobPipeline.Core;
using JobPipeline.Enricher;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>URL intake and enrichment in the store, and the enricher end to end on canned pages.</summary>
public static class EnrichStoreTests
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

        var keep = new NewJob { Company = "Acme", Title = "Senior .NET Engineer", JdText = "C# all day", ApplyUrl = "https://job-boards.greenhouse.io/acme/jobs/1" };

        Sync("add_urls: added, duplicates (tracking params, same batch, already tracked), non-URLs rejected", () =>
        {
            var s = new JobStore(NewDir());
            s.AddJob(new NewJob { Source = "builtin", Company = "Old", Title = "Dev", ListingUrl = "https://builtin.com/job/old/9", JdText = "x" });
            var r = s.AddUrls("builtin", [
                "https://builtin.com/job/a/1", "https://www.builtin.com/job/a/1/?utm_source=x&trid=7", "https://builtin.com/job/old/9",
                "ftp://nope", "", null, "https://builtin.com/job/b/2"]);
            Eq("added duplicate duplicate rejected rejected rejected added", string.Join(" ", r.Select(x => x.Result)));
            Eq(JobStatus.Discovered, s.GetJob(r[0].Id!.Value)!.Status);
            Eq(r[0].Id, r[1].Id, "duplicate points at the first");
            Eq(2, s.Stats()["Discovered"]);
        });

        Sync("urls: query parameters that identify a posting keep two postings apart", () =>
        {
            var s = new JobStore(NewDir());
            var r = s.AddUrls("indeed", ["https://www.indeed.com/viewjob?jk=abc", "https://www.indeed.com/viewjob?jk=def",
                                         "https://www.indeed.com/viewjob?from=serp&jk=abc"]);
            Eq("added added duplicate", string.Join(" ", r.Select(x => x.Result)));
            Eq("https://indeed.com/viewjob?jk=abc", JobStore.NormalizeUrl("HTTP://www.Indeed.com/viewjob/?utm_campaign=x&jk=abc#top"));
        });

        Sync("enrich: claim → keep → New with details, JD and tech stack; tailoring worker can take it", () =>
        {
            var s = new JobStore(NewDir());
            var id = s.AddUrls("builtin", ["https://builtin.com/job/a/1"])[0].Id!.Value;
            var claimed = s.ClaimForEnrichment(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15))!;
            Eq((id, JobStatus.Enriching, 1), (claimed.Id, claimed.Status, claimed.EnrichAttempts));
            Eq<Job?>(null, s.ClaimForEnrichment(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)), "one claim at a time");
            var job = s.CompleteEnrichment(id, new NewJob
            {
                Company = "Acme", Title = "Senior .NET Engineer", JdText = "C# all day", TechStack = ["C#", "C#", " "],
                ApplyUrl = "https://job-boards.greenhouse.io/acme/jobs/1", MatchedTitle = "C# mentioned",
            }, EnrichVerdict.Keep, "read via greenhouse API");
            Eq((JobStatus.New, "https://builtin.com/job/a/1", "C#"), (job.Status, job.ListingUrl, string.Join(",", job.TechStack)));
            Eq("C# all day", s.ReadJd(job));
            Eq(id, s.ClaimForTailoring(TimeSpan.FromMinutes(30))!.Id);
        });

        Sync("enrich: review and exclude keep the reason; a repeat of a tracked job is skipped and points at it", () =>
        {
            var s = new JobStore(NewDir());
            var original = s.AddJob(new NewJob { Source = "linkedin", Company = "Acme, Inc.", Title = "Sr. .NET Engineer", ListingUrl = "https://linkedin.com/jobs/view/5", JdText = "x" });
            s.AddUrls("builtin", ["https://builtin.com/job/r/1", "https://builtin.com/job/e/2", "https://builtin.com/job/d/3"]);

            var review = Claim(s);
            Eq(JobStatus.NeedsReview, s.CompleteEnrichment(review, Job("Globex", "Lead Dev", "https://jobs.lever.co/g/1"),
                EnrichVerdict.Review, "mentions \"security clearance\"").Status);
            Eq("mentions \"security clearance\"", s.GetJob(review)!.ActionDesc);
            Eq(JobStatus.Excluded, s.CompleteEnrichment(Claim(s), Job("Initech", "Junior Dev", "https://jobs.lever.co/i/2"),
                EnrichVerdict.Exclude, "title says \"Junior\"").Status);
            var dup = s.CompleteEnrichment(Claim(s), Job("Acme", "Senior .NET Engineer", "https://job-boards.greenhouse.io/acme/jobs/1"),
                EnrichVerdict.Keep, null);
            Eq(JobStatus.Skipped, dup.Status);
            Eq(true, dup.ActionDesc!.Contains(original.Id.ToString()), dup.ActionDesc);
            Eq<string?>(null, s.ReadJd(dup), "a duplicate's description isn't stored");
        });

        Sync("enrich: same company and title but two postings on the same board are both kept", () =>
        {
            var s = new JobStore(NewDir());
            s.AddUrls("builtin", ["https://builtin.com/job/a/1", "https://builtin.com/job/a/2"]);
            s.CompleteEnrichment(Claim(s), Job("Acme", "Senior Engineer", "https://job-boards.greenhouse.io/acme/jobs/100"), EnrichVerdict.Keep, null);
            Eq(JobStatus.New, s.CompleteEnrichment(Claim(s), Job("Acme", "Senior Engineer", "https://job-boards.greenhouse.io/acme/jobs/200"),
                EnrichVerdict.Keep, null).Status);
        });

        Sync("enrich: a kept job must have a description; only an Enriching job can be completed", () =>
        {
            var s = new JobStore(NewDir());
            var id = s.AddUrls("builtin", ["https://builtin.com/job/a/1"])[0].Id!.Value;
            Throws<PipelineException>(() => s.CompleteEnrichment(id, keep, EnrichVerdict.Keep, null), "Enriching");
            Eq(id, Claim(s));
            Throws<PipelineException>(() => s.CompleteEnrichment(id, Job("A", "B", null, jd: ""), EnrichVerdict.Keep, null), "description");
        });

        Sync("enrich failures: transient retries after a delay, then EnrichFailed; permanent at once; gone = Expired", () =>
        {
            var clock = new ManualClock(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
            var s = new JobStore(NewDir(), clock);
            var ids = new List<Guid>();
            foreach (var url in new[] { "https://x.com/1", "https://x.com/2", "https://x.com/3" })
            {
                ids.Add(s.AddUrls("builtin", [url])[0].Id!.Value);
                clock.Advance(TimeSpan.FromSeconds(1));   // claims go oldest first
            }
            var delay = TimeSpan.FromMinutes(15);

            Eq(ids[0], s.ClaimForEnrichment(TimeSpan.FromMinutes(15), delay)!.Id);
            Eq(JobStatus.Discovered, s.FailEnrichment(ids[0], "timed out", EnrichFailure.Transient, 2).Status);
            Eq(ids[1], s.ClaimForEnrichment(TimeSpan.FromMinutes(15), delay)!.Id, "the failed one waits; the next is taken");
            Eq(JobStatus.EnrichFailed, s.FailEnrichment(ids[1], "bot check", EnrichFailure.Permanent, 2).Status);
            Eq(ids[2], s.ClaimForEnrichment(TimeSpan.FromMinutes(15), delay)!.Id);
            Eq(JobStatus.Expired, s.FailEnrichment(ids[2], "posting gone", EnrichFailure.Gone, 2).Status);
            Eq<Job?>(null, s.ClaimForEnrichment(TimeSpan.FromMinutes(15), delay), "retry not due yet");

            clock.Advance(TimeSpan.FromMinutes(16));
            var again = s.ClaimForEnrichment(TimeSpan.FromMinutes(15), delay)!;
            Eq((ids[0], 2), (again.Id, again.EnrichAttempts));
            var last = s.FailEnrichment(ids[0], "timed out", EnrichFailure.Transient, 2);
            Eq((JobStatus.EnrichFailed, "timed out"), (last.Status, last.ActionDesc));
        });

        Sync("enrich: a dead enricher's stale claim is reclaimed, a fresh one is not", () =>
        {
            var clock = new ManualClock(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
            var s = new JobStore(NewDir(), clock);
            var id = s.AddUrls("builtin", ["https://x.com/1"])[0].Id!.Value;
            s.ClaimForEnrichment(TimeSpan.FromMinutes(15), TimeSpan.Zero);
            clock.Advance(TimeSpan.FromMinutes(5));
            Eq<Job?>(null, s.ClaimForEnrichment(TimeSpan.FromMinutes(15), TimeSpan.Zero));
            clock.Advance(TimeSpan.FromMinutes(11));
            Eq((id, 2), (s.ClaimForEnrichment(TimeSpan.FromMinutes(15), TimeSpan.Zero)!.Id, 2));
        });

        Sync("add_jobs fills a URL the enricher couldn't read; a real job still counts as a duplicate", () =>
        {
            var s = new JobStore(NewDir());
            var id = s.AddUrls("builtin", ["https://builtin.com/job/a/1"])[0].Id!.Value;
            s.FailEnrichment(Claim(s), "no structured job data", EnrichFailure.Permanent, 3);
            var filled = s.AddJob(new NewJob { Source = "builtin", Company = "Acme", Title = "Senior Dev", ListingUrl = "https://builtin.com/job/a/1?utm_source=x", JdText = "C#" });
            Eq((id, "filled", JobStatus.New), (filled.Id, filled.Result, filled.Status));
            Eq(("Acme", "C#"), (s.GetJob(id)!.Company, s.ReadJd(s.GetJob(id)!)));
            Eq("duplicate", s.AddJob(new NewJob { Source = "x", Company = "Acme", Title = "Senior Dev", ListingUrl = "https://other.com/1", JdText = "y" }).Result);
            Eq(1, s.QueryJobs(null, null).Count);
        });

        Sync("resolve: retry an EnrichFailed job re-reads it; retry a NeedsReview job keeps it; skip drops it", () =>
        {
            var s = new JobStore(NewDir());
            s.AddUrls("builtin", ["https://x.com/1", "https://x.com/2", "https://x.com/3"]);
            var ids = new[] { Claim(s), Claim(s), Claim(s) };
            s.FailEnrichment(ids[0], "blocked", EnrichFailure.Permanent, 3);
            s.CompleteEnrichment(ids[1], Job("A", "Dev", "https://a.com/1"), EnrichVerdict.Review, "check");
            s.CompleteEnrichment(ids[2], Job("B", "Dev", "https://b.com/1"), EnrichVerdict.Exclude, "no C#");

            var retried = s.ResolveAction(ids[0], ResolveOutcome.Retry, null);
            Eq((JobStatus.Discovered, 0), (retried.Status, retried.EnrichAttempts));
            Eq(JobStatus.New, s.ResolveAction(ids[1], ResolveOutcome.Retry, "fits").Status);
            Eq(JobStatus.Skipped, s.ResolveAction(ids[2], ResolveOutcome.Skip, null).Status);
            Throws<PipelineException>(() => s.SetStatus(ids[0], JobStatus.Enriching, null), "pipeline");
        });

        Test("enricher end to end: URL in, tailoring-ready job out; rules from hunt-config.md; failures recorded", async () =>
        {
            var dir = NewDir();
            File.WriteAllText(Path.Combine(dir, "hunt-config.md"), "# Hunt config\n## Filters\n- require_any: C#, .NET\n");
            var s = new JobStore(dir);
            var ids = s.AddUrls("builtin", ["https://builtin.com/job/net/1", "https://builtin.com/job/java/2", "https://builtin.com/job/gone/3"])
                .Select(r => r.Id!.Value).ToList();
            var f = new FakeFetcher()
                .Page("https://builtin.com/job/net/1", EnrichLogicTests.Builtin("Senior .NET Engineer", "Acme",
                    "<a href=\"https://jobs.lever.co/acme/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2\">Apply now</a>"))
                .Json("https://api.lever.co/v0/postings/acme/10dfc8bc-99ad-4ca2-ab76-853cb90a92c2",
                    $$"""{"text":"Senior .NET Engineer","workplaceType":"remote","descriptionPlain":"{{EnrichLogicTests.Long("ASP.NET Core and Azure, C#.")}}"}""")
                .Page("https://builtin.com/job/java/2", EnrichLogicTests.Builtin("Senior Java Engineer", "Initech",
                    "<a href=\"https://jobs.lever.co/initech/20dfc8bc-99ad-4ca2-ab76-853cb90a92c2\">Apply</a>"))
                .Json("https://api.lever.co/v0/postings/initech/20dfc8bc-99ad-4ca2-ab76-853cb90a92c2",
                    $$"""{"text":"Senior Java Engineer","descriptionPlain":"{{EnrichLogicTests.Long("Java and Spring.")}}"}""");
            var options = new EnrichOptions { DataDir = dir };
            var worker = new EnrichWorker(s, new PostingReader(f, options), options, NullLogger<EnrichWorker>.Instance);

            while (await worker.ProcessOneAsync(default)) { }

            var net = s.GetJob(ids[0])!;
            Eq((JobStatus.New, "Acme", true, ".NET mentioned"), (net.Status, net.Company, net.IsRemote, net.MatchedTitle));
            Eq(".NET, ASP.NET Core, Azure, C#", string.Join(", ", net.TechStack), "title included, posting's order");
            Eq(true, s.ReadJd(net)!.StartsWith("ASP.NET Core"));
            Eq(true, File.Exists(Path.Combine(dir, TechStack.FileName)), "starter tech-terms.txt written");
            var java = s.GetJob(ids[1])!;
            Eq((JobStatus.Excluded, true), (java.Status, java.ActionDesc!.Contains("C#, .NET")));
            Eq(JobStatus.Expired, s.GetJob(ids[2])!.Status);
            Eq(false, await worker.ProcessOneAsync(default), "queue drained");
        });

        return (passed, failed);
    }

    static Guid Claim(JobStore s) =>
        s.ClaimForEnrichment(TimeSpan.FromMinutes(15), TimeSpan.Zero)?.Id ?? throw new Exception("nothing to claim");

    static NewJob Job(string company, string title, string? applyUrl, string jd = "C# and .NET") =>
        new() { Company = company, Title = title, ApplyUrl = applyUrl, JdText = jd };

    static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "jp-enrich-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }

    static void Throws<TEx>(Action a, string contains = "") where TEx : Exception
    {
        try { a(); }
        catch (TEx ex) when (ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception($"expected {typeof(TEx).Name} containing '{contains}'");
    }
}
