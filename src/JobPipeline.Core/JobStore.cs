using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace JobPipeline.Core;

/// <summary>
/// The single owner of the pipeline database (jobs.db, SQLite). Every caller (MCP server, worker,
/// web API, tests) goes through here. Each write runs in one BEGIN IMMEDIATE transaction, so writers in
/// different processes are serialized and can't lose each other's updates; WAL mode lets readers
/// (e.g. a web app) read while a write is in progress.
///
/// Each row keeps the full job as a JSON document plus indexed columns (status, company, dates, URLs)
/// for querying. Job descriptions live in their own table.
/// </summary>
public sealed partial class JobStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    static readonly JsonSerializerOptions Compact = new(Json) { WriteIndented = false };

    readonly string _connectionString;
    readonly TimeProvider _clock;

    public string DataDir { get; }
    public string DatabasePath { get; }

    public JobStore(string dataDir, TimeProvider? clock = null)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(Path.Combine(DataDir, "resumes"));
        DatabasePath = Path.Combine(DataDir, "jobs.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
        _clock = clock ?? TimeProvider.System;
        Initialize();
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
        var company = Dedupe.Company(input.Company);
        return Mutate(db =>
        {
            var matches = db.Jobs.Where(j =>
                (j.DedupeKey.Length > 0 && j.DedupeKey == key)
                || (!string.IsNullOrWhiteSpace(input.ExternalJobId) && j.ExternalJobId == input.ExternalJobId
                    && string.Equals(j.Source, input.Source, StringComparison.OrdinalIgnoreCase)
                    && Dedupe.Company(j.Company) == company)
                || SameUrl(j.ApplyUrl, input.ApplyUrl) || SameUrl(j.ListingUrl, input.ListingUrl)
                || SameUrl(j.ApplyUrl, input.ListingUrl) || SameUrl(j.ListingUrl, input.ApplyUrl)).ToList();

            // A real job already tracked wins. A URL the enricher hasn't read yet (Discovered) or couldn't read
            // (EnrichFailed) is a placeholder: the details sent here fill it in instead of making a second row.
            if (matches.FirstOrDefault(j => !IsPlaceholder(j)) is { } dup)
                return (new AddResult(dup.Id, "duplicate", dup.Status), false);

            var job = matches.FirstOrDefault();
            var filled = job is not null;
            if (job is null)
            {
                job = new Job { Id = Guid.NewGuid(), CreatedAt = Now };
                db.Jobs.Add(job);
            }
            job.Source = Blank(input.Source) ?? job.Source;
            job.Company = input.Company.Trim();
            job.Title = input.Title.Trim();
            job.MatchedTitle = input.MatchedTitle;
            job.ListingUrl = Blank(input.ListingUrl) ?? job.ListingUrl;
            job.ApplyUrl = Blank(input.ApplyUrl) ?? job.ApplyUrl;
            job.ExternalJobId = Blank(input.ExternalJobId);
            job.Location = Blank(input.Location);
            job.IsRemote = input.IsRemote;
            job.SalaryMin = input.SalaryMin;
            job.SalaryMax = input.SalaryMax;
            job.SalaryCurrency = Blank(input.SalaryCurrency);
            job.SalaryText = Blank(input.SalaryText);
            job.TechStack = CleanStack(input.TechStack);
            job.DatePosted = Blank(input.DatePosted);
            job.IsEasyApply = input.IsEasyApply;
            job.DedupeKey = key;
            job.ActionDesc = null;
            job.LastError = null;
            job.UpdatedAt = Now;
            if (!string.IsNullOrWhiteSpace(input.JdText))
                db.PendingJds[job.Id] = input.JdText;   // saved in the same transaction as the job
            if (filled) Move(job, JobStatus.New, by, "details added by hand or by Claude");
            else
            {
                job.Status = JobStatus.New;
                job.History.Add(new StatusChange(null, JobStatus.New, Now, by, null));
            }
            return (new AddResult(job.Id, filled ? "filled" : "added", JobStatus.New), true);
        });
    }

    /// <summary>
    /// Saves bare posting URLs for the enricher to fill in (status Discovered). A URL already tracked, in any
    /// status, comes back as 'duplicate'; repeats within the batch too. Tracking parameters don't make a URL new.
    /// </summary>
    public IReadOnlyList<UrlAddResult> AddUrls(string source, IEnumerable<string?> urls, string by = "hunt")
    {
        Require(!string.IsNullOrWhiteSpace(source), "source is required");
        var list = urls.ToList();
        Require(list.Count is > 0 and <= 200, "send 1 to 200 urls per call");
        return Mutate(db =>
        {
            var results = new List<UrlAddResult>();
            foreach (var raw in list)
            {
                var url = raw?.Trim() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                {
                    results.Add(new UrlAddResult(url, "rejected", null, null, "not an http(s) URL"));
                    continue;
                }
                if (db.Jobs.FirstOrDefault(j => SameUrl(j.ListingUrl, url) || SameUrl(j.ApplyUrl, url)) is { } dup)
                {
                    results.Add(new UrlAddResult(url, "duplicate", dup.Id, dup.Status, null));
                    continue;
                }
                var job = new Job
                {
                    Id = Guid.NewGuid(),
                    Status = JobStatus.Discovered,
                    Source = source.Trim(),
                    ListingUrl = url,
                    CreatedAt = Now,
                    UpdatedAt = Now,
                };
                job.History.Add(new StatusChange(null, JobStatus.Discovered, Now, by, null));
                db.Jobs.Add(job);
                results.Add(new UrlAddResult(url, "added", job.Id, JobStatus.Discovered, null));
            }
            return (results, results.Any(r => r.Result == "added"));
        });
    }

    static bool IsPlaceholder(Job j) => j.Status is JobStatus.Discovered or JobStatus.EnrichFailed;

    static List<string> CleanStack(IEnumerable<string>? terms) =>
        terms?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList() ?? new();

    // ───────────────────────────── Enricher ─────────────────────────────

    /// <summary>
    /// Atomically claims the oldest Discovered job (or an Enriching job whose enricher died more than
    /// <paramref name="staleAfter"/> ago) and moves it to Enriching. A job that already failed waits
    /// <paramref name="retryDelay"/> × attempts before it is tried again.
    /// </summary>
    public Job? ClaimForEnrichment(TimeSpan staleAfter, TimeSpan retryDelay, string by = "enricher") => Mutate<Job?>(db =>
    {
        var now = Now;
        var job = db.Jobs
            .Where(j => (j.Status == JobStatus.Discovered && (j.EnrichAttempts == 0 || j.UpdatedAt <= now - retryDelay * j.EnrichAttempts))
                        || (j.Status == JobStatus.Enriching && j.UpdatedAt < now - staleAfter))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefault();
        if (job is null) return (null, false);
        job.EnrichAttempts++;
        Move(job, JobStatus.Enriching, by, job.Status == JobStatus.Enriching ? "reclaimed stale claim" : null);
        return (Clone(job), true);
    });

    /// <summary>
    /// Saves what the enricher read and applies its verdict: Keep → New (the tailoring worker picks it up),
    /// Review → NeedsReview, Exclude → Excluded. A posting that turns out to be a job already tracked (same
    /// company and title, or same apply URL) is closed as Skipped and points at the original.
    /// </summary>
    public Job CompleteEnrichment(Guid id, NewJob e, EnrichVerdict verdict, string? reason, string by = "enricher") => Mutate(db =>
    {
        Require(!string.IsNullOrWhiteSpace(e.Company), "company is required");
        Require(!string.IsNullOrWhiteSpace(e.Title), "title is required");
        Require(verdict != EnrichVerdict.Keep || !string.IsNullOrWhiteSpace(e.JdText),
            "a kept job needs its description: the tailoring worker reads it");
        var job = Get(db, id);
        Expect(job, JobStatus.Enriching);

        job.Company = e.Company.Trim();
        job.Title = e.Title.Trim();
        job.MatchedTitle = Blank(e.MatchedTitle);
        job.ApplyUrl = Blank(e.ApplyUrl) ?? job.ApplyUrl;
        job.ExternalJobId = Blank(e.ExternalJobId);
        job.Location = Blank(e.Location);
        job.IsRemote = e.IsRemote;
        job.SalaryMin = e.SalaryMin;
        job.SalaryMax = e.SalaryMax;
        job.SalaryCurrency = Blank(e.SalaryCurrency);
        job.SalaryText = Blank(e.SalaryText);
        job.TechStack = CleanStack(e.TechStack);
        job.DatePosted = Blank(e.DatePosted);
        job.IsEasyApply = e.IsEasyApply;
        job.DedupeKey = Dedupe.Key(job.Company, job.Title);
        job.LastError = null;

        var dup = db.Jobs.FirstOrDefault(o => o.Id != job.Id && !IsPlaceholder(o) && o.Status != JobStatus.Enriching
            && (SameUrl(o.ApplyUrl, job.ApplyUrl) || SameUrl(o.ListingUrl, job.ApplyUrl)
                || (o.DedupeKey == job.DedupeKey && !DifferentPostingsOnSameBoard(o.ApplyUrl, job.ApplyUrl))));
        if (dup is not null)
        {
            job.ActionDesc = $"Duplicate of {dup.Company} – {dup.Title} ({dup.Id}, {dup.Status})";
            Move(job, JobStatus.Skipped, by, "duplicate");
            return (Clone(job), true);
        }

        if (!string.IsNullOrWhiteSpace(e.JdText)) db.PendingJds[job.Id] = e.JdText;
        switch (verdict)
        {
            case EnrichVerdict.Keep:
                job.ActionDesc = null;
                Move(job, JobStatus.New, by, Blank(reason));
                break;
            case EnrichVerdict.Review:
                job.ActionDesc = Blank(reason) ?? "Check whether this posting fits.";
                Move(job, JobStatus.NeedsReview, by, job.ActionDesc);
                break;
            default:
                job.ActionDesc = Blank(reason) ?? "Excluded by a hunt rule.";
                Move(job, JobStatus.Excluded, by, job.ActionDesc);
                break;
        }
        return (Clone(job), true);
    });

    /// <summary>
    /// Records a failed enrichment. Gone (404/410, closed posting) → Expired. Permanent, or a transient failure on
    /// the last allowed attempt → EnrichFailed. Otherwise back to Discovered for a later retry.
    /// </summary>
    public Job FailEnrichment(Guid id, string error, EnrichFailure kind, int maxAttempts, string by = "enricher") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.Enriching);
        job.LastError = error;
        if (kind == EnrichFailure.Gone)
        {
            job.ActionDesc = error;
            Move(job, JobStatus.Expired, by, error);
        }
        else if (kind == EnrichFailure.Permanent || job.EnrichAttempts >= maxAttempts)
        {
            job.ActionDesc = error;
            Move(job, JobStatus.EnrichFailed, by, error);
        }
        else
        {
            Move(job, JobStatus.Discovered, by, error);
        }
        return (Clone(job), true);
    });

    /// <summary>
    /// Two apply URLs on the same host with different paths are two postings (e.g. the same title in two teams),
    /// even when company and title match.
    /// </summary>
    static bool DifferentPostingsOnSameBoard(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && !SameUrl(a, b);

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
    public ApplyRun BeginApplyRun(int limit, IReadOnlyCollection<string>? manualApplyCompanies = null, string by = "apply")
    {
        limit = Math.Clamp(limit, 1, 100);
        var manualList = manualApplyCompanies ?? Array.Empty<string>();
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

            // Companies the user applies to personally: never auto-applied, handed over with the tailored resume.
            var manual = new List<JobSummary>();
            foreach (var j in db.Jobs.Where(j => j.Status == JobStatus.ReadyToApply))
            {
                if (ManualApplyMatch(j.Company, manualList) is not { } listed) continue;
                j.ActionDesc = $"Manual apply: {j.Company} is on your manual-apply list ({listed}). Apply yourself" +
                               (j.ResumePath is null ? "." : $"; tailored resume: {j.ResumePath}");
                Move(j, JobStatus.ActionNeeded, by, "manual-apply company");
                manual.Add(Summary(j, db));
            }

            var ready = db.Jobs
                .Where(j => j.Status == JobStatus.ReadyToApply)
                .OrderByDescending(j => j.DatePosted ?? "")
                .ThenBy(j => j.CreatedAt)
                .Take(limit)
                .Select(j => Summary(j, db))
                .ToList();
            var appliedToday = db.Jobs.Count(j => j.DateApplied is { } d && d.UtcDateTime.Date == Now.UtcDateTime.Date);
            return (new ApplyRun(interrupted, manual, ready, appliedToday), interrupted.Count > 0 || manual.Count > 0);
        });
    }

    /// <summary>The list entry a company matches, or null. See <see cref="Dedupe.CompanyMatches"/>.</summary>
    public static string? ManualApplyMatch(string company, IEnumerable<string> manualApplyCompanies) =>
        manualApplyCompanies.FirstOrDefault(m => Dedupe.CompanyMatches(company, m));

    /// <summary>Call immediately before opening the application. Re-runs never resubmit a job left in Applying.</summary>
    public Job StartApply(Guid id, IReadOnlyCollection<string>? manualApplyCompanies = null, string by = "apply") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.ReadyToApply);
        if (ManualApplyMatch(job.Company, manualApplyCompanies ?? Array.Empty<string>()) is { } listed)
            throw new PipelineException(
                $"{job.Company} is on the manual-apply list ({listed}); the user applies there personally. Don't apply.");
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
        Expect(job, JobStatus.New, JobStatus.ReadyToApply, JobStatus.Applying, JobStatus.ActionNeeded, JobStatus.Failed,
            JobStatus.NeedsReview, JobStatus.EnrichFailed);
        job.ActionDesc = reason;
        Move(job, outcome, by, reason);
        return (Clone(job), true);
    });

    /// <summary>
    /// Replaces a job's tech stack (e.g. re-extracted with better rules). Jobs not yet applied to whose resume is
    /// already made (ReadyToApply, ActionNeeded, Failed) go back to New so the worker re-tailors with the new list.
    /// </summary>
    public (Job Job, bool Requeued) UpdateTechStack(Guid id, IEnumerable<string> techStack, string by = "you") => Mutate(db =>
    {
        var job = Get(db, id);
        job.TechStack = techStack.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList();
        job.UpdatedAt = Now;
        var requeue = job.Status is JobStatus.ReadyToApply or JobStatus.ActionNeeded or JobStatus.Failed;
        if (requeue)
        {
            job.ResumePath = null;
            job.ActionDesc = null;
            job.LastError = null;
            job.TailorAttempts = 0;
            Move(job, JobStatus.New, by, "tech stack updated; re-tailor");
        }
        return ((Clone(job), requeue), true);
    });

    /// <summary>
    /// Sends a job back to the worker for a fresh resume (e.g. after changing the template or tailor settings):
    /// ReadyToApply, ActionNeeded or Failed → New. The old resume path is dropped; the old file is left on disk.
    /// </summary>
    public Job RequeueForTailoring(Guid id, string by = "you") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.ReadyToApply, JobStatus.ActionNeeded, JobStatus.Failed);
        job.ResumePath = null;
        job.ActionDesc = null;
        job.LastError = null;
        job.TailorAttempts = 0;
        Move(job, JobStatus.New, by, "re-tailor requested");
        return (Clone(job), true);
    });

    /// <summary>
    /// You clearing a blocker: retry it, record that you applied by hand, or drop it. Retry on a NeedsReview or
    /// Excluded job means "keep it" (on to tailoring); on an EnrichFailed job it lets the enricher try again.
    /// </summary>
    public Job ResolveAction(Guid id, ResolveOutcome outcome, string? note, string by = "you") => Mutate(db =>
    {
        var job = Get(db, id);
        Expect(job, JobStatus.ActionNeeded, JobStatus.Failed, JobStatus.NeedsReview, JobStatus.Excluded, JobStatus.EnrichFailed);
        switch (outcome)
        {
            case ResolveOutcome.Retry when job.Status == JobStatus.EnrichFailed:
                job.ActionDesc = null;
                job.LastError = null;
                job.EnrichAttempts = 0;
                Move(job, JobStatus.Discovered, by, note ?? "retry enrichment");
                break;
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

    // ───────────────────────────── Dashboard (you, editing by hand) ─────────────────────────────

    /// <summary>Replaces the job's editable fields (see <see cref="JobEdit"/>). Doesn't change its status.</summary>
    public Job EditJob(Guid id, JobEdit e, string by = "you") => Mutate(db =>
    {
        Require(!string.IsNullOrWhiteSpace(e.Company), "company is required");
        Require(!string.IsNullOrWhiteSpace(e.Title), "title is required");
        Require(!string.IsNullOrWhiteSpace(e.ListingUrl) || !string.IsNullOrWhiteSpace(e.ApplyUrl),
            "listingUrl or applyUrl is required");
        Require(e.SalaryMin is null || e.SalaryMax is null || e.SalaryMin <= e.SalaryMax, "salaryMin is above salaryMax");
        var job = Get(db, id);
        job.Company = e.Company.Trim();
        job.Title = e.Title.Trim();
        job.Source = Blank(e.Source) ?? job.Source;
        job.MatchedTitle = Blank(e.MatchedTitle);
        job.ListingUrl = Blank(e.ListingUrl);
        job.ApplyUrl = Blank(e.ApplyUrl);
        job.ExternalJobId = Blank(e.ExternalJobId);
        job.Location = Blank(e.Location);
        job.IsRemote = e.IsRemote;
        job.SalaryMin = e.SalaryMin;
        job.SalaryMax = e.SalaryMax;
        job.SalaryCurrency = Blank(e.SalaryCurrency);
        job.SalaryText = Blank(e.SalaryText);
        job.TechStack = e.TechStack?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList() ?? new();
        job.DatePosted = Blank(e.DatePosted);
        job.IsEasyApply = e.IsEasyApply;
        job.ActionDesc = Blank(e.ActionDesc);
        job.DateApplied = e.DateApplied;
        job.InterviewDate = e.InterviewDate;
        job.DedupeKey = Dedupe.Key(job.Company, job.Title);
        job.UpdatedAt = Now;
        job.History.Add(new StatusChange(job.Status, job.Status, Now, by, "fields edited"));
        return (Clone(job), true);
    });

    /// <summary>
    /// Sets any status by hand, with an optional note in the history. New re-queues the job for a fresh resume;
    /// Applied stamps the applied date if missing. Tailoring and Applying are set by the pipeline only.
    /// </summary>
    public Job SetStatus(Guid id, JobStatus to, string? note, string by = "you") => Mutate(db =>
    {
        Require(to is not (JobStatus.Tailoring or JobStatus.Applying or JobStatus.Enriching),
            $"{to} is set by the pipeline itself; choose New to re-tailor, ReadyToApply to queue for applying, or Discovered to re-read the posting");
        var job = Get(db, id);
        note = Blank(note);
        switch (to)
        {
            case JobStatus.New:
                job.ResumePath = null;
                job.LastError = null;
                job.TailorAttempts = 0;
                job.ActionDesc = null;
                break;
            case JobStatus.ReadyToApply:
                Require(job.ResumePath is not null, "this job has no resume yet; set it to New so the worker makes one");
                job.ActionDesc = null;
                break;
            case JobStatus.Applied:
                job.DateApplied ??= Now;
                job.ActionDesc = null;
                break;
            case JobStatus.Discovered:
                Require(!string.IsNullOrWhiteSpace(job.ListingUrl) || !string.IsNullOrWhiteSpace(job.ApplyUrl),
                    "this job has no URL to read");
                job.EnrichAttempts = 0;
                job.LastError = null;
                job.ActionDesc = null;
                break;
            case JobStatus.ActionNeeded or JobStatus.Skipped or JobStatus.Expired
                or JobStatus.NeedsReview or JobStatus.Excluded or JobStatus.EnrichFailed:
                job.ActionDesc = note ?? job.ActionDesc ?? $"{to} (set by you)";
                break;
        }
        Move(job, to, by, note ?? "set by hand");
        return (Clone(job), true);
    });

    public Job AddComment(Guid id, string text, string by = "you") => Mutate(db =>
    {
        Require(!string.IsNullOrWhiteSpace(text), "comment text is required");
        var job = Get(db, id);
        job.Comments.Add(new JobComment { Id = Guid.NewGuid(), At = Now, By = by, Text = text.Trim() });
        job.UpdatedAt = Now;
        return (Clone(job), true);
    });

    public Job DeleteComment(Guid id, Guid commentId) => Mutate(db =>
    {
        var job = Get(db, id);
        Require(job.Comments.RemoveAll(c => c.Id == commentId) > 0, $"No comment with id {commentId}");
        job.UpdatedAt = Now;
        return (Clone(job), true);
    });

    /// <summary>Replaces the saved job description text.</summary>
    public Job SaveJobDescription(Guid id, string text, string by = "you") => Mutate(db =>
    {
        Require(!string.IsNullOrWhiteSpace(text), "job description text is required");
        var job = Get(db, id);
        job.JdPath = null;
        job.UpdatedAt = Now;
        db.PendingJds[job.Id] = text;
        return (Clone(job), true);
    });

    /// <summary>Removes a job and its description from the database. Resume files on disk are left alone.</summary>
    public bool DeleteJob(Guid id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        foreach (var sql in new[] { "DELETE FROM job_descriptions WHERE job_id = $id", "DELETE FROM jobs WHERE id = $id" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }
        var gone = Scalar(conn, tx, "SELECT changes()") > 0;
        tx.Commit();
        return gone;
    }

    /// <summary>Full job records, newest activity first, filtered by status and a free-text search.</summary>
    public IReadOnlyList<Job> QueryJobs(JobStatus? status, string? search, int limit = 500, int offset = 0) => Read(db =>
    {
        var q = Blank(search);
        return db.Jobs
            .Where(j => status is null || j.Status == status)
            .Where(j => q is null || Matches(j, q))
            .OrderByDescending(j => j.UpdatedAt)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 2000))
            .Select(Clone).ToList();
    });

    static bool Matches(Job j, string q) =>
        new[] { j.Company, j.Title, j.Location, j.Source, j.ActionDesc, j.SalaryText, j.ExternalJobId }
            .Any(f => f?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
        || j.TechStack.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
        || j.Comments.Any(c => c.Text.Contains(q, StringComparison.OrdinalIgnoreCase));

    // ───────────────────────────── Reads ─────────────────────────────

    public Job? GetJob(Guid id) => Read(db => db.Jobs.FirstOrDefault(j => j.Id == id) is { } j ? Clone(j) : null);

    public string? ReadJd(Job job)
    {
        if (job.JdPath is { } p && File.Exists(Resolve(p))) return File.ReadAllText(Resolve(p));   // legacy file
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT text FROM job_descriptions WHERE job_id = $id";
        cmd.Parameters.AddWithValue("$id", job.Id.ToString());
        return cmd.ExecuteScalar() as string;
    }

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

    static bool SameUrl(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && NormalizeUrl(a) == NormalizeUrl(b);

    /// <summary>Query parameters that only say where a click came from; they never identify a posting.</summary>
    static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "refid", "referrer", "src", "source", "trid", "oga", "gclid", "fbclid", "mc_cid", "mc_eid",
        "lever-source", "lever-origin", "gh_src", "trk", "trackingid", "from", "campaign", "iis",
    };

    /// <summary>
    /// Comparable form of a URL: https, lowercase host without "www.", no trailing slash or fragment, and only the
    /// query parameters that identify the page (Indeed's ?jk=, Greenhouse's ?gh_jid=, …), sorted.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return url.TrimEnd('/').ToLowerInvariant();
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        var query = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => !TrackingParams.Contains(p[0]) && !p[0].StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Length == 2 ? $"{p[0].ToLowerInvariant()}={p[1]}" : p[0].ToLowerInvariant())
            .OrderBy(p => p, StringComparer.Ordinal);
        var q = string.Join('&', query);
        return $"https://{host}{u.AbsolutePath.TrimEnd('/').ToLowerInvariant()}{(q.Length > 0 ? "?" + q : "")}";
    }

    static Job Clone(Job j) => JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(j, Json), Json)!;

    // Reads see one consistent snapshot (a single SELECT in WAL mode) and never block writers.
    T Read<T>(Func<JobFile, T> f)
    {
        using var conn = Open();
        return f(LoadAll(conn, null));
    }

    // One write = one BEGIN IMMEDIATE transaction: load, apply one guarded change, save only the rows that
    // changed. IMMEDIATE takes SQLite's write lock up front, so two processes can't both read the same state
    // and then overwrite each other.
    T Mutate<T>(Func<JobFile, (T result, bool dirty)> f)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var db = LoadAll(conn, tx);
        var before = db.Jobs.ToDictionary(j => j.Id, j => JsonSerializer.Serialize(j, Compact));
        var (result, dirty) = f(db);
        if (dirty)
        {
            foreach (var job in db.Jobs)
            {
                var doc = JsonSerializer.Serialize(job, Compact);
                if (!before.TryGetValue(job.Id, out var old) || old != doc) Upsert(conn, tx, job, doc);
            }
            foreach (var (id, text) in db.PendingJds) SaveJd(conn, tx, id, text);
            tx.Commit();
        }
        return result;
    }

    SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Exec(conn, null, "PRAGMA busy_timeout = 30000; PRAGMA foreign_keys = ON;");
        return conn;
    }

    void Initialize()
    {
        using var conn = Open();
        Exec(conn, null, "PRAGMA journal_mode = WAL;");
        using (var tx = conn.BeginTransaction(IsolationLevel.Serializable, deferred: false))
        {
            Exec(conn, tx, """
                CREATE TABLE IF NOT EXISTS jobs (
                    id              TEXT PRIMARY KEY,
                    status          TEXT NOT NULL,
                    company         TEXT NOT NULL,
                    title           TEXT NOT NULL,
                    source          TEXT,
                    dedupe_key      TEXT NOT NULL,
                    external_job_id TEXT,
                    listing_url     TEXT,
                    apply_url       TEXT,
                    is_remote       INTEGER,
                    is_easy_apply   INTEGER NOT NULL DEFAULT 0,
                    date_posted     TEXT,
                    date_applied    TEXT,
                    interview_date  TEXT,
                    resume_path     TEXT,
                    resume_dir      TEXT,
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL,
                    doc             TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_jobs_status     ON jobs(status);
                CREATE INDEX IF NOT EXISTS ix_jobs_dedupe     ON jobs(dedupe_key);
                CREATE INDEX IF NOT EXISTS ix_jobs_updated_at ON jobs(updated_at);
                CREATE TABLE IF NOT EXISTS job_descriptions (
                    job_id TEXT PRIMARY KEY REFERENCES jobs(id),
                    text   TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '2');
                """);
            MigrateResumeColumns(conn, tx);
            CreateUsersTable(conn, tx);
            ImportLegacyJson(conn, tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Schema v2: resume_path / resume_dir as real columns (previously only inside the JSON doc). Adds them to an
    /// older database and fills them in from each job's document.
    /// </summary>
    static void MigrateResumeColumns(SqliteConnection conn, SqliteTransaction tx)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA table_info(jobs)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }
        if (columns.Contains("resume_path") && columns.Contains("resume_dir")) return;
        if (!columns.Contains("resume_path")) Exec(conn, tx, "ALTER TABLE jobs ADD COLUMN resume_path TEXT");
        if (!columns.Contains("resume_dir")) Exec(conn, tx, "ALTER TABLE jobs ADD COLUMN resume_dir TEXT");
        foreach (var job in LoadAll(conn, tx).Jobs)
            Upsert(conn, tx, job, JsonSerializer.Serialize(job, Compact));
        Exec(conn, tx, "UPDATE meta SET value = '2' WHERE key = 'schema_version'");
    }

    /// <summary>
    /// One-time migration from the earlier jobs.json store: runs only when the database is empty and a
    /// jobs.json exists. Job descriptions are pulled in from jds/*.txt; jobs.json is renamed afterwards.
    /// </summary>
    void ImportLegacyJson(SqliteConnection conn, SqliteTransaction tx)
    {
        var legacy = Path.Combine(DataDir, "jobs.json");
        if (!File.Exists(legacy) || Scalar(conn, tx, "SELECT COUNT(*) FROM jobs") > 0) return;

        JobFile old;
        try
        {
            old = JsonSerializer.Deserialize<JobFile>(File.ReadAllText(legacy), Json) ?? new JobFile();
        }
        catch (JsonException ex)
        {
            throw new PipelineException($"Can't import jobs.json: it is not valid JSON ({ex.Message}). Fix or remove it, then restart.");
        }

        foreach (var job in old.Jobs)
        {
            if (job.JdPath is { } p && File.Exists(Resolve(p))) SaveJdLater(job, File.ReadAllText(Resolve(p)));
            job.JdPath = null;
            Upsert(conn, tx, job, JsonSerializer.Serialize(job, Compact));
        }
        foreach (var (id, text) in old.PendingJds) SaveJd(conn, tx, id, text);
        File.Move(legacy, legacy + ".imported", overwrite: true);

        void SaveJdLater(Job j, string text) => old.PendingJds[j.Id] = text;
    }

    static JobFile LoadAll(SqliteConnection conn, SqliteTransaction? tx)
    {
        var db = new JobFile();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT doc FROM jobs";
        using var r = cmd.ExecuteReader();
        while (r.Read()) db.Jobs.Add(JsonSerializer.Deserialize<Job>(r.GetString(0), Json)!);
        return db;
    }

    static void Upsert(SqliteConnection conn, SqliteTransaction? tx, Job j, string doc)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO jobs (id, status, company, title, source, dedupe_key, external_job_id, listing_url, apply_url,
                              is_remote, is_easy_apply, date_posted, date_applied, interview_date, resume_path, resume_dir,
                              created_at, updated_at, doc)
            VALUES ($id, $status, $company, $title, $source, $dedupe, $ext, $listing, $apply,
                    $remote, $easy, $posted, $applied, $interview, $resume, $resumeDir, $created, $updated, $doc)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status, company = excluded.company, title = excluded.title, source = excluded.source,
                dedupe_key = excluded.dedupe_key, external_job_id = excluded.external_job_id,
                listing_url = excluded.listing_url, apply_url = excluded.apply_url, is_remote = excluded.is_remote,
                is_easy_apply = excluded.is_easy_apply, date_posted = excluded.date_posted,
                date_applied = excluded.date_applied, interview_date = excluded.interview_date,
                resume_path = excluded.resume_path, resume_dir = excluded.resume_dir,
                updated_at = excluded.updated_at, doc = excluded.doc
            """;
        Add(cmd, "$id", j.Id.ToString());
        Add(cmd, "$status", j.Status.ToString());
        Add(cmd, "$company", j.Company);
        Add(cmd, "$title", j.Title);
        Add(cmd, "$source", j.Source);
        Add(cmd, "$dedupe", j.DedupeKey);
        Add(cmd, "$ext", j.ExternalJobId);
        Add(cmd, "$listing", j.ListingUrl);
        Add(cmd, "$apply", j.ApplyUrl);
        Add(cmd, "$remote", j.IsRemote is null ? null : (object)(j.IsRemote.Value ? 1 : 0));
        Add(cmd, "$easy", j.IsEasyApply ? 1 : 0);
        Add(cmd, "$posted", j.DatePosted);
        Add(cmd, "$applied", j.DateApplied?.ToString("O"));
        Add(cmd, "$interview", j.InterviewDate?.ToString("O"));
        Add(cmd, "$resume", j.ResumePath);
        Add(cmd, "$resumeDir", string.IsNullOrWhiteSpace(j.ResumePath) ? null : Path.GetDirectoryName(j.ResumePath));
        Add(cmd, "$created", j.CreatedAt.ToUniversalTime().ToString("O"));
        Add(cmd, "$updated", j.UpdatedAt.ToUniversalTime().ToString("O"));
        Add(cmd, "$doc", doc);
        cmd.ExecuteNonQuery();
    }

    static void SaveJd(SqliteConnection conn, SqliteTransaction? tx, Guid id, string text)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO job_descriptions (job_id, text) VALUES ($id, $text) " +
                          "ON CONFLICT(job_id) DO UPDATE SET text = excluded.text";
        Add(cmd, "$id", id.ToString());
        Add(cmd, "$text", text);
        cmd.ExecuteNonQuery();
    }

    static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static long Scalar(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static void Add(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}

public enum ResolveOutcome { Retry, Applied, Skip }

public sealed class PipelineException(string message) : Exception(message);

public sealed record AddResult(Guid Id, string Result, JobStatus Status);

public sealed record UrlAddResult(string Url, string Result, Guid? Id, JobStatus? Status, string? Error);

public sealed record ApplyRun(
    IReadOnlyList<JobSummary> Interrupted, IReadOnlyList<JobSummary> ManualApply, IReadOnlyList<JobSummary> Ready, int AppliedToday);

public sealed record JobSummary(
    Guid Id, JobStatus Status, string Company, string Title, string? Location, bool? IsRemote, string? Salary,
    string? Url, bool IsEasyApply, string? DatePosted, string? ResumePath, string? ActionDesc,
    DateTimeOffset? DateApplied, DateTimeOffset? InterviewDate, int OtherApplicationsAtCompanyLast30Days);

public sealed record JobMatch(double Score, JobSummary Job);

public sealed record EmailResult(Guid Id, JobStatus Before, JobStatus After, bool Duplicate);
