using System.Collections.Concurrent;
using JobPipeline.Core;

// Dependency-free test runner: `dotnet run --project tests/JobPipeline.SelfTest`. Exit code 0 = all passed.

var failures = 0;
var passed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"  PASS  {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}"); }
}
void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} expected <{expected}> but got <{actual}>");
}
void Throws<TEx>(Action a, string contains = "") where TEx : Exception
{
    try { a(); }
    catch (TEx ex) when (ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
    throw new Exception($"expected {typeof(TEx).Name} containing '{contains}'");
}

string NewDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "jp-test-" + Guid.NewGuid().ToString("N")[..8])).FullName;

NewJob Posting(string company = "Acme Inc.", string title = "Senior Backend Engineer (.NET)", string url = "https://builtin.com/job/1") => new()
{
    Source = "builtin", Company = company, Title = title, ListingUrl = url,
    ApplyUrl = $"https://job-boards.greenhouse.io/acme/jobs/{Math.Abs(url.GetHashCode())}",
    TechStack = ["C#", ".NET 8", "RabbitMQ", "C#"], JdText = "We need C# and .NET 8.", IsRemote = true,
};

Console.WriteLine("JobPipeline self-test");

Test("add stores job, JD file and starts in New", () =>
{
    var s = new JobStore(NewDir());
    var r = s.AddJob(Posting());
    Eq("added", r.Result);
    var j = s.GetJob(r.Id)!;
    Eq(JobStatus.New, j.Status);
    Eq(3, j.TechStack.Count, "techStack deduped");
    Eq("We need C# and .NET 8.", s.ReadJd(j));
});

Test("duplicates caught across sources: company suffix, Sr./Senior, tracking params", () =>
{
    var s = new JobStore(NewDir());
    var first = s.AddJob(Posting());
    Eq("duplicate", s.AddJob(Posting("ACME, Inc", "Sr. Back-End Engineer (.NET)", "https://www.linkedin.com/jobs/view/9")).Result, "same role other site");
    var sameUrl = Posting("Other Co", "Different Title", "https://builtin.com/job/1/?utm_source=x");
    Eq("duplicate", s.AddJob(sameUrl).Result, "same listing url");
    Eq(first.Id, s.AddJob(Posting()).Id);
    Eq("added", s.AddJob(Posting("Acme Inc.", "Senior Full Stack Engineer", "https://builtin.com/job/2")).Result, "different role");
});

Test("add rejects missing company/title/url", () =>
{
    var s = new JobStore(NewDir());
    Throws<PipelineException>(() => s.AddJob(new NewJob { Company = "A", Title = "B" }), "url");
    Throws<PipelineException>(() => s.AddJob(new NewJob { Title = "B", ListingUrl = "https://x" }), "company");
});

Test("worker: claim → complete → ReadyToApply with resume", () =>
{
    var s = new JobStore(NewDir());
    var id = s.AddJob(Posting()).Id;
    var claimed = s.ClaimForTailoring(TimeSpan.FromMinutes(30))!;
    Eq(id, claimed.Id);
    Eq(JobStatus.Tailoring, s.GetJob(id)!.Status);
    Eq<Job?>(null, s.ClaimForTailoring(TimeSpan.FromMinutes(30)), "nothing else to claim");
    var done = s.CompleteTailoring(id, @"C:\resumes\a.pdf");
    Eq(JobStatus.ReadyToApply, done.Status);
    Eq(@"C:\resumes\a.pdf", done.ResumePath);
});

Test("worker: failures retry, then Failed after max attempts", () =>
{
    var s = new JobStore(NewDir());
    var id = s.AddJob(Posting()).Id;
    for (var i = 1; i <= 3; i++)
    {
        s.ClaimForTailoring(TimeSpan.FromMinutes(30));
        var after = s.FailTailoring(id, $"boom {i}", maxAttempts: 3);
        Eq(i < 3 ? JobStatus.New : JobStatus.Failed, after.Status, $"after attempt {i}");
    }
    Eq("boom 3", s.GetJob(id)!.LastError);
    var retried = s.ResolveAction(id, ResolveOutcome.Retry, null);
    Eq(JobStatus.New, retried.Status, "retry from Failed with no resume goes back to New");
    Eq(0, retried.TailorAttempts);
});

Test("worker: a dead worker's stale claim is reclaimed, a fresh one is not", () =>
{
    var clock = new ManualClock(DateTimeOffset.Parse("2026-10-22T10:00:00Z"));
    var s = new JobStore(NewDir(), clock);
    var id = s.AddJob(Posting()).Id;
    s.ClaimForTailoring(TimeSpan.FromMinutes(30));
    clock.Advance(TimeSpan.FromMinutes(10));
    Eq<Job?>(null, s.ClaimForTailoring(TimeSpan.FromMinutes(30)), "fresh claim kept");
    clock.Advance(TimeSpan.FromMinutes(25));
    var re = s.ClaimForTailoring(TimeSpan.FromMinutes(30))!;
    Eq(id, re.Id);
    Eq(2, re.TailorAttempts);
});

Test("apply: start → mark applied; illegal transitions are refused", () =>
{
    var s = new JobStore(NewDir());
    var id = ReadyJob(s);
    Throws<PipelineException>(() => s.MarkApplied(id), "needs Applying");
    var started = s.StartApply(id);
    Eq(JobStatus.Applying, started.Status);
    Eq(1, started.ApplyAttempts);
    Throws<PipelineException>(() => s.StartApply(id), "needs ReadyToApply");
    var applied = s.MarkApplied(id, "confirmation page shown");
    Eq(JobStatus.Applied, applied.Status);
    if (applied.DateApplied is null) throw new Exception("dateApplied not set");
});

Test("apply: a run that died mid-form is flagged, never resubmitted", () =>
{
    var s = new JobStore(NewDir());
    var stuck = ReadyJob(s);
    s.StartApply(stuck);                                   // run crashes here
    var waiting = ReadyJob(s, "Beta LLC", "https://builtin.com/job/b");
    var run = s.BeginApplyRun(10);
    Eq(1, run.Interrupted.Count);
    Eq(stuck, run.Interrupted[0].Id);
    Eq(JobStatus.ActionNeeded, s.GetJob(stuck)!.Status);
    Eq(1, run.Ready.Count);
    Eq(waiting, run.Ready[0].Id);
});

Test("apply: blockers need a reason; user resolves retry / applied / skip", () =>
{
    var s = new JobStore(NewDir());
    var id = ReadyJob(s);
    s.StartApply(id);
    Throws<PipelineException>(() => s.FlagAction(id, " "), "reason");
    var flagged = s.FlagAction(id, "CAPTCHA on the Workday login page");
    Eq(JobStatus.ActionNeeded, flagged.Status);
    Eq("CAPTCHA on the Workday login page", flagged.ActionDesc);
    Eq(JobStatus.ReadyToApply, s.ResolveAction(id, ResolveOutcome.Retry, null).Status, "retry keeps the resume");
    s.StartApply(id); s.FlagAction(id, "needs account");
    Eq(JobStatus.Applied, s.ResolveAction(id, ResolveOutcome.Applied, "did it by hand").Status);
});

Test("close: expired and skipped", () =>
{
    var s = new JobStore(NewDir());
    var id = ReadyJob(s);
    Eq(JobStatus.Expired, s.CloseJob(id, JobStatus.Expired, "posting closed", "apply").Status);
    Throws<PipelineException>(() => s.CloseJob(id, JobStatus.Applied, "x", "apply"), "Expired or Skipped");
});

Test("email: confirmation, duplicate thread, interview, rejection", () =>
{
    var s = new JobStore(NewDir());
    var id = ReadyJob(s);
    s.StartApply(id); s.FlagAction(id, "needs account");           // you then applied by hand
    var at = DateTimeOffset.Parse("2026-10-22T18:00:00Z");
    var r1 = s.RecordEmail(id, EmailType.Confirmation, "t1", "Thanks for applying", at, null);
    Eq(JobStatus.Applied, r1.After);
    Eq(at, s.GetJob(id)!.DateApplied);
    Eq(true, s.RecordEmail(id, EmailType.Confirmation, "t1", "Thanks for applying", at, null).Duplicate);
    var when = DateTimeOffset.Parse("2026-10-29T17:00:00-07:00");
    Eq(JobStatus.Interviewing, s.RecordEmail(id, EmailType.Interview, "t2", "Interview", at, when).After);
    Eq(when, s.GetJob(id)!.InterviewDate);
    Eq(JobStatus.Interviewing, s.RecordEmail(id, EmailType.Confirmation, "t3", "late confirmation", at, null).After, "no regression");
    Eq(JobStatus.Rejected, s.RecordEmail(id, EmailType.Rejection, "t4", "Unfortunately", at, null).After);
    Eq(4, s.GetJob(id)!.Emails.Count);
});

Test("find_jobs matches email sender company to the right role", () =>
{
    var s = new JobStore(NewDir());
    var backend = s.AddJob(Posting("Acme Corporation", "Senior Backend Engineer", "https://x/1")).Id;
    s.AddJob(Posting("Acme Corporation", "Senior iOS Engineer", "https://x/2"));
    s.AddJob(Posting("Globex", "Senior Backend Engineer", "https://x/3"));
    var hits = s.FindJobs("Acme", "Sr. Back End Engineer");
    Eq(2, hits.Count, "only Acme jobs");
    Eq(backend, hits[0].Job.Id, "best match first");
});

Test("history records every transition with who did it", () =>
{
    var s = new JobStore(NewDir());
    var id = ReadyJob(s);
    s.StartApply(id); s.MarkApplied(id);
    var h = s.GetJob(id)!.History;
    Eq("New,Tailoring,ReadyToApply,Applying,Applied", string.Join(",", h.Select(x => x.To)));
    Eq("hunt,worker,worker,apply,apply", string.Join(",", h.Select(x => x.By)));
});

Test("concurrency: 8 writers × 40 adds lose nothing and corrupt nothing", () =>
{
    var dir = NewDir();
    Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, w =>
    {
        var s = new JobStore(dir);                              // separate instance = separate file handles
        for (var i = 0; i < 40; i++)
            s.AddJob(Posting($"Company {w}-{i}", "Senior .NET Developer", $"https://x/{w}/{i}"));
    });
    Eq(320, new JobStore(dir).Stats()["New"]);
});

Test("concurrency: parallel claimers never claim the same job twice", () =>
{
    var dir = NewDir();
    var seed = new JobStore(dir);
    for (var i = 0; i < 60; i++) seed.AddJob(Posting($"Co {i}", "Senior .NET Developer", $"https://y/{i}"));
    var claimed = new ConcurrentBag<Guid>();
    Parallel.For(0, 6, _ =>
    {
        var s = new JobStore(dir);
        while (s.ClaimForTailoring(TimeSpan.FromMinutes(30)) is { } j) claimed.Add(j.Id);
    });
    Eq(60, claimed.Count, "claims");
    Eq(60, claimed.Distinct().Count(), "distinct claims");
});

Test("an existing jobs.json (earlier version) is imported once, with its job descriptions", () =>
{
    var dir = NewDir();
    var id = Guid.NewGuid();
    Directory.CreateDirectory(Path.Combine(dir, "jds"));
    File.WriteAllText(Path.Combine(dir, "jds", $"{id}.txt"), "Legacy JD: C#, .NET 8");
    File.WriteAllText(Path.Combine(dir, "jobs.json"), $$"""
        {"version":1,"jobs":[{"id":"{{id}}","status":"Applied","source":"builtin","company":"Old Co",
          "title":"Senior .NET Developer","listingUrl":"https://old.example/1","dedupeKey":"old|senior net developer",
          "jdPath":"jds/{{id}}.txt","techStack":["C#"],"emails":[],"history":[],
          "createdAt":"2026-10-01T00:00:00+00:00","updatedAt":"2026-10-02T00:00:00+00:00"}]}
        """);
    var s = new JobStore(dir);
    var job = s.GetJob(id)!;
    Eq(JobStatus.Applied, job.Status);
    Eq<string?>(null, job.JdPath, "description moved into the database");
    Eq("Legacy JD: C#, .NET 8", s.ReadJd(job));
    if (File.Exists(Path.Combine(dir, "jobs.json")) || !File.Exists(Path.Combine(dir, "jobs.json.imported")))
        throw new Exception("jobs.json should be renamed to jobs.json.imported");
    Eq("duplicate", s.AddJob(Posting("Old Co", "Senior .NET Developer", "https://other")).Result);
    Eq(1, new JobStore(dir).Stats().Values.Sum(), "re-opening does not import twice");
});

Test("a failed write rolls back completely", () =>
{
    var s = new JobStore(NewDir());
    var id = s.AddJob(Posting()).Id;
    Throws<PipelineException>(() => s.MarkApplied(id), "needs Applying");
    var j = s.GetJob(id)!;
    Eq(JobStatus.New, j.Status);
    Eq(1, j.History.Count);
});

Test("database file and job descriptions live in the data folder", () =>
{
    var dir = NewDir();
    var s = new JobStore(dir);
    var id = s.AddJob(Posting()).Id;
    if (!File.Exists(Path.Combine(dir, "jobs.db"))) throw new Exception("jobs.db missing");
    Eq<string?>(null, s.GetJob(id)!.JdPath);
    Eq("We need C# and .NET 8.", s.ReadJd(s.GetJob(id)!));
});

var (tailorPassed, tailorFailed) = TailorTests.Run();
passed += tailorPassed;
failures += tailorFailed;

Console.WriteLine($"\n{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;

Guid ReadyJob(JobStore s, string company = "Acme Inc.", string url = "https://builtin.com/job/1")
{
    var id = s.AddJob(Posting(company, "Senior Backend Engineer", url)).Id;
    while (s.ClaimForTailoring(TimeSpan.FromMinutes(30)) is { } j)
        if (j.Id == id) break;
    s.CompleteTailoring(id, "/tmp/resume.pdf");
    return id;
}

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
