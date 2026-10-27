using System.Text.Json;
using JobPipeline.Core;

namespace JobPipeline.Mcp;

public static class Tools
{
    public const string Instructions =
        "Job pipeline store. Statuses: New → Tailoring → ReadyToApply → Applying → Applied → Interviewing|Rejected; " +
        "exits ActionNeeded, Skipped, Expired, Failed. The job-hunt, job-apply and job-email-sync skills say when to " +
        "call each tool. Fields left out of a result are null.";

    static readonly JsonSerializerOptions ArgOptions = new(JobStore.Json) { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ToolDef> Create(JobStore store) =>
    [
        new("get_config",
            "Read a config file: 'hunt' (filters, sites, apply rules), 'profile' (form answers) or 'resume' (master " +
            "resume). isSetUp is false while it still has [fill] fields or the resume placeholder.",
            """
            {"type":"object","properties":{"name":{"type":"string","enum":["hunt","profile","resume"]}},"required":["name"]}
            """,
            a =>
            {
                var file = Configs.FileFor(Str(a, "name")!);
                var path = Path.Combine(store.DataDir, file);
                var content = File.Exists(path) ? File.ReadAllText(path) : "";
                var isSetUp = content.Trim().Length > 0 && !content.Contains("[fill]") && !content.Contains(Configs.ResumePlaceholder);
                return new { path, isSetUp, content };
            }),

        new("add_jobs",
            "Save postings with full details, 1-25 per call. Already-stored jobs come back as 'duplicate'; a URL the " +
            "enricher couldn't read comes back 'filled'. Prefer add_urls: it needs no description.",
            """
            {"type":"object","required":["jobs"],"properties":{"jobs":{"type":"array","minItems":1,"maxItems":25,"items":{
              "type":"object","required":["source","company","title","jdText"],
              "properties":{
                "source":{"type":"string","description":"Site it was found on: builtin, dice, wellfound, greenhouse, lever, ashby, linkedin, indeed, company"},
                "company":{"type":"string"},
                "title":{"type":"string","description":"Exactly as posted"},
                "matchedTitle":{"type":"string","description":"Which Keep rule from the hunt config it matched, in a few words"},
                "listingUrl":{"type":"string","description":"URL of the listing where it was found"},
                "applyUrl":{"type":"string","description":"Employer's own application page, when the listing links out"},
                "externalJobId":{"type":"string","description":"Job/requisition id shown in the posting or URL"},
                "location":{"type":"string"},
                "isRemote":{"type":"boolean"},
                "salaryMin":{"type":"integer"},"salaryMax":{"type":"integer"},
                "salaryCurrency":{"type":"string"},
                "salaryText":{"type":"string","description":"Pay exactly as written"},
                "techStack":{"type":"array","items":{"type":"string"}},
                "datePosted":{"type":"string","description":"yyyy-MM-dd"},
                "isEasyApply":{"type":"boolean"},
                "jdText":{"type":"string","description":"Full job description, plain text"}
              }}}}}
            """,
            a =>
            {
                var jobs = a.GetProperty("jobs").Deserialize<List<NewJob>>(ArgOptions) ?? [];
                if (jobs.Count is 0 or > 25) throw new ArgumentException("Send 1 to 25 jobs per call.");
                var results = jobs.Select(j =>
                {
                    try { var r = store.AddJob(j); return (object)new { j.Company, j.Title, r.Id, r.Result, r.Status }; }
                    catch (PipelineException ex) { return new { j.Company, j.Title, Result = "rejected", Error = ex.Message }; }
                }).ToList();
                return new { results };
            }),

        new("add_urls",
            "Save posting URLs (1-200 per call) for the enricher, which reads each posting and fills in the job. " +
            "Returns counts; already-tracked URLs count as duplicates.",
            """
            {"type":"object","required":["source","urls"],"properties":{
              "source":{"type":"string","description":"Site they were found on, e.g. builtin"},
              "urls":{"type":"array","minItems":1,"maxItems":200,"items":{"type":"string"}}}}
            """,
            a =>
            {
                var urls = a.GetProperty("urls").Deserialize<List<string?>>(ArgOptions) ?? [];
                var results = store.AddUrls(Str(a, "source") ?? "", urls);
                return new
                {
                    added = results.Count(r => r.Result == "added"),
                    duplicate = results.Count(r => r.Result == "duplicate"),
                    rejected = results.Where(r => r.Result == "rejected").Select(r => new { r.Url, r.Error }).ToList(),
                };
            }),

        new("find_jobs",
            "Stored jobs matching a company (and optional title), best first, with a 0-1 score.",
            """
            {"type":"object","required":["company"],"properties":{"company":{"type":"string"},"title":{"type":"string"},"limit":{"type":"integer"}}}
            """,
            a => store.FindJobs(Str(a, "company")!, Str(a, "title"), Int(a, "limit") ?? 10)),

        new("get_job",
            "One job's details. includeJd adds the job description text; includeHistory adds status history and comments.",
            """
            {"type":"object","required":["id"],"properties":{"id":{"type":"string"},"includeJd":{"type":"boolean"},"includeHistory":{"type":"boolean"}}}
            """,
            a =>
            {
                var job = store.GetJob(Id(a)) ?? throw new PipelineException($"No job with id {Id(a)}");
                object view = Bool(a, "includeHistory") == true ? job : Detail(job);
                return new { job = view, jdText = Bool(a, "includeJd") == true ? store.ReadJd(job) : null };
            }),

        new("list_jobs",
            "Jobs, most recently updated first, optionally filtered by status.",
            """
            {"type":"object","properties":{"status":{"type":"string","enum":["Discovered","Enriching","New","Tailoring","ReadyToApply","Applying","Applied","Interviewing","Rejected","NeedsReview","ActionNeeded","EnrichFailed","Excluded","Skipped","Expired","Failed"]},"limit":{"type":"integer"},"offset":{"type":"integer"}}}
            """,
            a => store.ListJobs(Str(a, "status") is { } s ? Parse<JobStatus>(s) : null, Int(a, "limit") ?? 50, Int(a, "offset") ?? 0)),

        new("pipeline_stats", "Count of jobs in each status.",
            """{"type":"object","properties":{}}""",
            _ => store.Stats()),

        new("begin_apply_run",
            "Start an apply run. Returns interrupted (left mid-form by a crashed run, now ActionNeeded; never retry), " +
            "manualApply (manual-apply companies, now ActionNeeded; never apply to these by any URL), ready, appliedToday.",
            """
            {"type":"object","properties":{"limit":{"type":"integer","description":"Max ready jobs to return (default 10)"}}}
            """,
            a => store.BeginApplyRun(Int(a, "limit") ?? 10, Configs.ManualApplyCompanies(store.DataDir))),

        new("start_apply",
            "Call right before opening a job's application: ReadyToApply → Applying. Returns the URLs and tailored resume path.",
            """
            {"type":"object","required":["id"],"properties":{"id":{"type":"string"}}}
            """,
            a =>
            {
                var job = store.StartApply(Id(a), Configs.ManualApplyCompanies(store.DataDir));
                return new
                {
                    job.Id, job.Company, job.Title, job.ApplyUrl, job.ListingUrl, job.IsEasyApply, job.ResumePath,
                    resumeFileExists = job.ResumePath is not null && File.Exists(job.ResumePath),
                };
            }),

        new("mark_applied",
            "Applying → Applied. Only after the site showed a submission confirmation.",
            """
            {"type":"object","required":["id"],"properties":{"id":{"type":"string"},"note":{"type":"string","description":"e.g. confirmation text or what was submitted"}}}
            """,
            a => Brief(store.MarkApplied(Id(a), Str(a, "note")))),

        new("flag_action",
            "Hand a job to the user (→ ActionNeeded). reason says exactly what they must do and where it stopped.",
            """
            {"type":"object","required":["id","reason"],"properties":{"id":{"type":"string"},"reason":{"type":"string"}}}
            """,
            a => Brief(store.FlagAction(Id(a), Str(a, "reason")!))),

        new("close_job",
            "Close without applying: 'expired' (posting gone) or 'skipped' (doesn't fit, or company cap reached).",
            """
            {"type":"object","required":["id","outcome","reason"],"properties":{"id":{"type":"string"},"outcome":{"type":"string","enum":["expired","skipped"]},"reason":{"type":"string"}}}
            """,
            a => Brief(store.CloseJob(Id(a), Parse<JobStatus>(Str(a, "outcome")!), Str(a, "reason")!, "apply"))),

        new("resolve_action",
            "Only when the user says so, for an ActionNeeded, Failed, NeedsReview, Excluded or EnrichFailed job: 'retry' " +
            "requeues (keeps a reviewed job; re-reads an EnrichFailed one), 'applied' records a manual application, 'skip' drops it.",
            """
            {"type":"object","required":["id","outcome"],"properties":{"id":{"type":"string"},"outcome":{"type":"string","enum":["retry","applied","skip"]},"note":{"type":"string"}}}
            """,
            a => Brief(store.ResolveAction(Id(a), Parse<ResolveOutcome>(Str(a, "outcome")!), Str(a, "note")))),

        new("update_tech_stack",
            "Replace one job's tech stack. A job not yet applied to is re-tailored with the new list.",
            """
            {"type":"object","required":["id","techStack"],"properties":{"id":{"type":"string"},"techStack":{"type":"array","items":{"type":"string"}}}}
            """,
            a =>
            {
                var terms = a.GetProperty("techStack").Deserialize<List<string>>(ArgOptions) ?? [];
                var (job, requeued) = store.UpdateTechStack(Id(a), terms);
                return new { job.Id, job.Company, job.Title, job.Status, job.TechStack, requeuedForTailoring = requeued };
            }),

        new("retailor_jobs",
            "Only when the user asks: send ReadyToApply, ActionNeeded or Failed jobs back to New for a fresh tailored " +
            "resume. Pass ids, or allReady=true.",
            """
            {"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"}},"allReady":{"type":"boolean"}}}
            """,
            a =>
            {
                var ids = new List<Guid>();
                if (a.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                        ids.Add(Guid.TryParse(e.GetString(), out var g) ? g : throw new ArgumentException($"'{e}' is not a job id"));
                if (Bool(a, "allReady") == true)
                    ids.AddRange(store.ListJobs(JobStatus.ReadyToApply, 500).Select(j => j.Id));
                if (ids.Count == 0) throw new ArgumentException("Pass ids or allReady=true.");
                var results = ids.Distinct().Select(id =>
                {
                    try { var j = store.RequeueForTailoring(id); return (object)new { j.Id, j.Company, j.Title, Result = "requeued" }; }
                    catch (PipelineException ex) { return new { Id = id, Company = "", Title = "", Result = ex.Message }; }
                }).ToList();
                return new { results, stats = store.Stats() };
            }),

        new("record_email",
            "Attach a Gmail message to a job: confirmation → Applied, rejection → Rejected, interview → Interviewing. " +
            "The same thread and type is recorded once, so re-syncing is safe.",
            """
            {"type":"object","required":["id","type"],"properties":{"id":{"type":"string"},
              "type":{"type":"string","enum":["confirmation","rejection","interview","assessment","other"]},
              "threadId":{"type":"string"},"subject":{"type":"string"},
              "receivedAt":{"type":"string","description":"ISO 8601"},
              "interviewDate":{"type":"string","description":"ISO 8601 with offset, only when confirmed"}}}
            """,
            a => store.RecordEmail(Id(a), Parse<EmailType>(Str(a, "type")!), Str(a, "threadId"), Str(a, "subject"),
                Date(a, "receivedAt") ?? DateTimeOffset.UtcNow, Date(a, "interviewDate"))),
    ];

    // ── result shapes ──
    // Tool results land in Claude's context, so each one carries only what the caller acts on next.

    /// <summary>After a state change: enough to confirm it and report it.</summary>
    static object Brief(Job j) => new { j.Id, j.Company, j.Title, j.Status, j.ActionDesc, j.DateApplied };

    /// <summary>get_job without the dashboard-only parts (history, comments, bookkeeping).</summary>
    static object Detail(Job j) => new
    {
        j.Id, j.Status, j.Source, j.Company, j.Title, j.MatchedTitle, j.ListingUrl, j.ApplyUrl, j.ExternalJobId,
        j.Location, j.IsRemote, j.SalaryText, j.TechStack, j.DatePosted, j.IsEasyApply, j.ResumePath, j.ActionDesc,
        j.LastError, j.DateApplied, j.InterviewDate, j.Emails,
    };

    // ── argument helpers ──

    static Guid Id(JsonElement a) =>
        Guid.TryParse(Str(a, "id"), out var g) ? g : throw new ArgumentException("id must be a job GUID");

    static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static int? Int(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    static bool? Bool(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    static DateTimeOffset? Date(JsonElement a, string name) =>
        Str(a, name) is { Length: > 0 } s
            ? DateTimeOffset.TryParse(s, out var d) ? d : throw new FormatException($"{name} must be an ISO 8601 date-time")
            : null;

    static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? v : throw new ArgumentException($"'{value}' is not one of: {string.Join(", ", Enum.GetNames<T>())}");
}
