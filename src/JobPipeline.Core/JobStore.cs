using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobPipeline.Core;

/// <summary>
/// The single owner of jobs.json. Every caller (MCP server, worker, tests) goes through here, never
/// the file directly. Each operation takes a cross-process lock, reads, applies one guarded state
/// transition and writes atomically (temp file + replace, previous version kept as jobs.json.bak),
/// so concurrent writers can't lose each other's updates or leave a half-written file.
/// </summary>
public sealed class JobStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    readonly string _path, _lockPath;
    readonly TimeProvider _clock;

    public string DataDir { get; }

    public JobStore(string dataDir, TimeProvider? clock = null)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "jds"));
        Directory.CreateDirectory(Path.Combine(DataDir, "resumes"));
        _path = Path.Combine(DataDir, "jobs.json");
        _lockPath = Path.Combine(DataDir, "jobs.lock");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Default data folder shared by the MCP server and the worker when nothing is configured.</summary>
    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("JOBPIPELINE_DATA") is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JobPipeline");

    public string Resolve(string relativeOrAbsolute) =>
        Path.IsPathRooted(relativeOrAbsolute) ? relativeOrAbsolute : Path.Combine(DataDir, relativeOrAbsolute);

    DateTimeOffset Now => _clock.GetUtcNow();

    // ───────────────────────────── Hunt ─────────────────────────────

    public AddResult AddJob(NewJob input, string by = "hunt")
    {
        Require(!string.IsNullOrWhiteSpace(input.Company), "company is required");
        Require(!string.IsNullOrWhiteSpace(input.Title), "title is required");
        Require(!string.IsNullOrWhiteSpace(input.ListingUrl) || !string.IsNullOrWhiteSpace(input.ApplyUrl),
            "listingUrl or applyUrl is required");

        var key = Dedupe.Key(input.Company, input.Title);
        return Mutate(db =>
        {
            var dup = db.Jobs.FirstOrDefault(j =>
                j.DedupeKey == key
                || (!string.IsNullOrWhiteSpace(input.ExternalJobId) && j.ExternalJobId == input.ExternalJobId
                    && string.Equals(j.Source, input.Source, StringComparison.OrdinalIgnoreCase))
                || SameUrl(j.ApplyUrl, input.ApplyUrl) || SameUrl(j.ListingUrl, input.ListingUrl)
                || SameUrl(j.ApplyUrl, input.ListingUrl) || SameUrl(j.ListingUrl, input.ApplyUrl));
            if (dup is not null)
                return (new AddResult(dup.Id, "duplicate", dup.Status), false);

            var job = new Job
            {
                Id = Guid.NewGuid(),
                Source = input.Source.Trim(),
                Company = input.Company.Trim(),
                Title = input.Title.Trim(),
                MatchedTitle = input.MatchedTitle,
                ListingUrl = Blank(input.ListingUrl),
                ApplyUrl = Blank(input.ApplyUrl),
                ExternalJobId = Blank(input.ExternalJobId),
                Location = Blank(input.Location),
                IsRemote = input.IsRemote,
                SalaryMin = input.SalaryMin,
                SalaryMax = input.SalaryMax,
                SalaryCurrency = Blank(input.SalaryCurrency),
                SalaryText = Blank(input.SalaryText),
                TechStack = input.TechStack?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList() ?? new(),
                DatePosted = Blank(input.DatePosted),
                IsEasyApply = input.IsEasyApply,
                DedupeKey = key,
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            if (!string.IsNullOrWhiteSpace(input.JdText))
            {
                job.JdPath = Path.Combine("jds", $"{job.Id}.txt");
                File.WriteAllText(Resolve(job.JdPath), input.JdText);
            }
            job.History.Add(new StatusChange(null, JobStatus.New, Now, by, null));
            db.Jobs.Add(job);
            return (new AddResult(job.Id, "added", JobStatus.New), true);
        });
    }

    // ───────────────────────────── Worker ─────────────────────────────

    /// <summary>
    /// Atomically claims the oldest New job (or a Tailoring job whose worker died more than
    /// <paramref name="staleAfter"/> ago) and moves it to Tailoring.
    /// </summary>
    public Job? ClaimForTailoring(TimeSpan staleAfter, string by = "worker") => Mutate<Job?>(db =>
    {
        var cutoff = Now - staleAfter;
        var job = db.Jobs
            .Where(j => j.Status == JobStatus.New || (j.Status == JobStatus.Tailoring && j.UpdatedAt < cutoff))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefault();
        if (job is null) return (null, false);
        job.TailorAttempts++;
        Move(job, JobStatus.Tailoring, by, job.Status == JobStatus.Tailoring ? "reclaimed stale claim" : null);
        return (Clone(job), true);
    });

    public Job CompleteTailoring(Guid id, string resumePath, string? note = null, string by = "worker") => Mutate(db =>
    {
        Require(!string.IsNullOrWhiteSpace(resumePath), "resumePath is required");
        var job = Get(db, id);
        Expect(job, JobStatus.Tailoring);
        job.ResumePath = resumePath;
        job.LastError = null;
        Move(job, JobStatus.ReadyToApply, by, note);
        return (Clone(job), true);
    });

    public Job FailTailoring(Guid id, string error, int maxAttempts, string by = "worker") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.Tailoring);
        job.LastError = error;
        Move(job, job.TailorAttempts >= maxAttempts ? JobStatus.Failed : JobStatus.New, by, error);
        return (Clone(job), true);
    });

    // ───────────────────────────── Apply ─────────────────────────────

    /// <summary>
    /// Start of every apply run. Any job still in Applying means a previous run died mid-form and may or may
    /// not have submitted, so it goes to ActionNeeded instead of being retried. Then returns the ready queue.
    /// </summary>
    public ApplyRun BeginApplyRun(int limit, string by = "apply")
    {
        limit = Math.Clamp(limit, 1, 100);
        return Mutate(db =>
        {
            var interrupted = new List<JobSummary>();
            foreach (var j in db.Jobs.Where(j => j.Status == JobStatus.Applying))
            {
                j.ActionDesc = $"Interrupted mid-application (started {j.UpdatedAt:yyyy-MM-dd HH:mm} UTC). " +
                               "Check whether it was submitted, then resolve as applied or retry.";
                Move(j, JobStatus.ActionNeeded, by, "interrupted run");
                interrupted.Add(Summary(j, db));
            }

            var ready = db.Jobs
                .Where(j => j.Status == JobStatus.ReadyToApply)
                .OrderByDescending(j => j.DatePosted ?? "")
                .ThenBy(j => j.CreatedAt)
                .Take(limit)
                .Select(j => Summary(j, db))
                .ToList();
            var appliedToday = db.Jobs.Count(j => j.DateApplied is { } d && d.UtcDateTime.Date == Now.UtcDateTime.Date);
            return (new ApplyRun(interrupted, ready, appliedToday), interrupted.Count > 0);
        });
    }

    /// <summary>Call immediately before opening the application. Re-runs never resubmit a job left in Applying.</summary>
    public Job StartApply(Guid id, string by = "apply") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.ReadyToApply);
        job.ApplyAttempts++;
        Move(job, JobStatus.Applying, by, null);
        return (Clone(job), true);
    });

    public Job MarkApplied(Guid id, string? note = null, string by = "apply") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.Applying);
        job.DateApplied = Now;
        job.ActionDesc = null;
        Move(job, JobStatus.Applied, by, note);
        return (Clone(job), true);
    });

    public Job FlagAction(Guid id, string reason, string by = "apply") => Mutate(db =>
    {
        Require(!string.IsNullOrWhiteSpace(reason), "reason is required: say exactly what you need to do");
        var job = Get(db, id);
        Expect(job, JobStatus.New, JobStatus.Tailoring, JobStatus.ReadyToApply, JobStatus.Applying);
        job.ActionDesc = reason.Trim();
        Move(job, JobStatus.ActionNeeded, by, reason);
        return (Clone(job), true);
    });

    /// <summary>Close a job that will not be applied to: posting gone (Expired) or not a fit (Skipped).</summary>
    public Job CloseJob(Guid id, JobStatus outcome, string reason, string by) => Mutate(db =>
    {
        Require(outcome is JobStatus.Expired or JobStatus.Skipped, "outcome must be Expired or Skipped");
        var job = Get(db, id);
        Expect(job, JobStatus.New, JobStatus.ReadyToApply, JobStatus.Applying, JobStatus.ActionNeeded, JobStatus.Failed);
        job.ActionDesc = reason;
        Move(job, outcome, by, reason);
        return (Clone(job), true);
    });

    /// <summary>You clearing a blocker: retry it, record that you applied by hand, or drop it.</summary>
    public Job ResolveAction(Guid id, ResolveOutcome outcome, string? note, string by = "you") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.ActionNeeded, JobStatus.Failed);
        switch (outcome)
        {
            case ResolveOutcome.Retry:
                job.ActionDesc = null;
                job.LastError = null;
                if (job.Status == JobStatus.Failed) job.TailorAttempts = 0;
                Move(job, job.ResumePath is null ? JobStatus.New : JobStatus.ReadyToApply, by, note ?? "retry");
                break;
            case ResolveOutcome.Applied:
                job.ActionDesc = null;
                job.DateApplied ??= Now;
                Move(job, JobStatus.Applied, by, note ?? "applied manually");
                break;
            case ResolveOutcome.Skip:
                job.ActionDesc = note ?? job.ActionDesc;
                Move(job, JobStatus.Skipped, by, note ?? "skipped");
                break;
        }
        return (Clone(job), true);
    });

    // ───────────────────────────── Email sync ─────────────────────────────

    public EmailResult RecordEmail(Guid id, EmailType type, string? threadId, string? subject,
        DateTimeOffset receivedAt, DateTimeOffset? interviewDate, string by = "email-sync") => Mutate(db =>
    {
        var job = Get(db, id);
        if (job.Emails.Any(e => e.Type == type && threadId is not null && e.ThreadId == threadId))
            return (new EmailResult(job.Id, job.Status, job.Status, Duplicate: true), false);

        job.Emails.Add(new EmailEvent { Type = type, ThreadId = threadId, Subject = subject, ReceivedAt = receivedAt });
        var before = job.Status;
        switch (type)
        {
            case EmailType.Confirmation when before is JobStatus.New or JobStatus.Tailoring or JobStatus.ReadyToApply
                                                 or JobStatus.Applying or JobStatus.ActionNeeded or JobStatus.Failed:
                job.DateApplied ??= receivedAt;
                job.ActionDesc = null;
                Move(job, JobStatus.Applied, by, subject);
                break;
            case EmailType.Rejection when before != JobStatus.Rejected:
                Move(job, JobStatus.Rejected, by, subject);
                break;
            case EmailType.Interview:
                if (interviewDate is not null) job.InterviewDate = interviewDate;
                if (before != JobStatus.Interviewing) Move(job, JobStatus.Interviewing, by, subject);
                break;
        }
        job.UpdatedAt = Now;
        return (new EmailResult(job.Id, before, job.Status, Duplicate: false), true);
    });

    // ───────────────────────────── Reads ─────────────────────────────

    public Job? GetJob(Guid id) => Read(db => db.Jobs.FirstOrDefault(j => j.Id == id) is { } j ? Clone(j) : null);

    public string? ReadJd(Job job) => job.JdPath is { } p && File.Exists(Resolve(p)) ? File.ReadAllText(Resolve(p)) : null;

    public IReadOnlyList<JobSummary> ListJobs(JobStatus? status, int limit, int offset = 0) => Read(db =>
        db.Jobs.Where(j => status is null || j.Status == status)
            .OrderByDescending(j => j.UpdatedAt)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500))
            .Select(j => Summary(j, db)).ToList());

    /// <summary>Candidates for an email or a manual lookup, best match first.</summary>
    public IReadOnlyList<JobMatch> FindJobs(string company, string? title, int limit = 10) => Read(db =>
    {
        var c = Dedupe.Company(company);
        Require(c.Length > 0, "company is required");
        return db.Jobs
            .Select(j =>
            {
                var jc = Dedupe.Company(j.Company);
                double companyScore = jc == c ? 1 : (jc.Contains(c) || c.Contains(jc)) && Math.Min(jc.Length, c.Length) >= 3 ? 0.8 : 0;
                double titleScore = string.IsNullOrWhiteSpace(title) ? 0.5 : Dedupe.TitleSimilarity(j.Title, title);
                return (j, score: companyScore == 0 ? 0 : 0.6 * companyScore + 0.4 * titleScore);
            })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.j.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(x => new JobMatch(Math.Round(x.score, 2), Summary(x.j, db)))
            .ToList();
    });

    public IReadOnlyDictionary<string, int> Stats() => Read(db =>
        Enum.GetValues<JobStatus>().ToDictionary(s => s.ToString(), s => db.Jobs.Count(j => j.Status == s)));

    // ───────────────────────────── Plumbing ─────────────────────────────

    static JobSummary Summary(Job j, JobFile db)
    {
        var company = Dedupe.Company(j.Company);
        var recentAtCompany = db.Jobs.Count(o => o.Id != j.Id && o.DateApplied is { } d
            && d > DateTimeOffset.UtcNow.AddDays(-30) && Dedupe.Company(o.Company) == company);
        return new JobSummary(j.Id, j.Status, j.Company, j.Title, j.Location, j.IsRemote, j.SalaryText,
            j.ApplyUrl ?? j.ListingUrl, j.IsEasyApply, j.DatePosted, j.ResumePath, j.ActionDesc, j.DateApplied,
            j.InterviewDate, recentAtCompany);
    }

    void Move(Job job, JobStatus to, string by, string? note)
    {
        job.History.Add(new StatusChange(job.Status, to, Now, by, note));
        job.Status = to;
        job.UpdatedAt = Now;
    }

    static Job Get(JobFile db, Guid id) =>
        db.Jobs.FirstOrDefault(j => j.Id == id) ?? throw new PipelineException($"No job with id {id}");

    static void Expect(Job job, params JobStatus[] allowed)
    {
        if (!allowed.Contains(job.Status))
            throw new PipelineException(
                $"Job {job.Id} ({job.Company} – {job.Title}) is {job.Status}; this action needs {string.Join(" or ", allowed)}.");
    }

    static void Require(bool ok, string message) { if (!ok) throw new PipelineException(message); }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static bool SameUrl(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string N(string u)
        {
            u = u.Trim();
            var cut = u.IndexOfAny(new[] { '?', '#' });     // tracking params differ between sources
            if (cut >= 0) u = u[..cut];
            return u.TrimEnd('/').Replace("http://", "https://").ToLowerInvariant();
        }
        return N(a) == N(b);
    }

    static Job Clone(Job j) => JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(j, Json), Json)!;

    T Read<T>(Func<JobFile, T> f) => Mutate(db => (f(db), false));

    T Mutate<T>(Func<JobFile, (T result, bool dirty)> f)
    {
        using var _ = AcquireLock();
        var db = Load();
        var (result, dirty) = f(db);
        if (dirty) Save(db);
        return result;
    }

    FileStream AcquireLock()
    {
        // An exclusively-opened lock file works across processes and across Windows sessions
        // (a Windows service and the desktop app's MCP server), and the OS releases it if a process dies.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(Random.Shared.Next(20, 80));
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(Random.Shared.Next(20, 80));   // Windows reports a pending delete/lock this way sometimes
            }
        }
    }

    JobFile Load()
    {
        if (!File.Exists(_path)) return new JobFile();
        var text = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text)) return new JobFile();
        try
        {
            return JsonSerializer.Deserialize<JobFile>(text, Json) ?? new JobFile();
        }
        catch (JsonException ex)
        {
            // Never overwrite a file we can't parse; that would silently wipe the pipeline.
            throw new PipelineException(
                $"jobs.json is not valid JSON ({ex.Message}). Fix it by hand or restore jobs.json.bak, then retry.");
        }
    }

    void Save(JobFile db)
    {
        var tmp = _path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, db, Json);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, _path);
    }
}

public enum ResolveOutcome { Retry, Applied, Skip }

public sealed class PipelineException(string message) : Exception(message);

public sealed record AddResult(Guid Id, string Result, JobStatus Status);

public sealed record ApplyRun(IReadOnlyList<JobSummary> Interrupted, IReadOnlyList<JobSummary> Ready, int AppliedToday);

public sealed record JobSummary(
    Guid Id, JobStatus Status, string Company, string Title, string? Location, bool? IsRemote, string? Salary,
    string? Url, bool IsEasyApply, string? DatePosted, string? ResumePath, string? ActionDesc,
    DateTimeOffset? DateApplied, DateTimeOffset? InterviewDate, int OtherApplicationsAtCompanyLast30Days);

public sealed record JobMatch(double Score, JobSummary Job);

public sealed record EmailResult(Guid Id, JobStatus Before, JobStatus After, bool Duplicate);
