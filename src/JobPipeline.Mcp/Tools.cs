using System.Text.Json;
using JobPipeline.Core;

namespace JobPipeline.Mcp;

public static class Tools
{
    public const string Instructions =
        "Job pipeline store. Statuses: New → Tailoring → ReadyToApply → Applying → Applied → Interviewing|Rejected, " +
        "with exits ActionNeeded, Skipped, Expired, Failed. The hunt only adds jobs (add_jobs). A separate .NET worker " +
        "tailors resumes and moves jobs to ReadyToApply. Applying: call begin_apply_run first, then start_apply right " +
        "before opening each form, then mark_applied or flag_action. Never edit jobs.db directly.";

    static readonly JsonSerializerOptions ArgOptions = new(JobStore.Json) { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ToolDef> Create(JobStore store) =>
    [
        new("get_config",
            "Read one of your pipeline config files. 'hunt' = target titles, similar titles, exclusions, sites to search " +
            "and apply rules. 'profile' = applicant answers for application forms (contact, authorization, salary, etc.). " +
            "'resume' = the user's master (untailored) resume, the second source for form answers such as work history " +
            "and years of experience. 'isSetUp' is false while a file still has [fill] fields or the resume placeholder. " +
            "Read 'hunt' before every hunt, and all three before every apply run.",
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
            "Save newly found postings (1-25 per call). Duplicates of jobs already stored (same company+title, same URL, " +
            "or same source+job id) are reported as 'duplicate' and not added, so it is safe to resend. jdText must be " +
            "the full job description as plain text; the worker tailors from it. Copy techStack terms exactly as written " +
            "in the posting (same spelling, capitalization, hyphens).",
            """
            {"type":"object","required":["jobs"],"properties":{"jobs":{"type":"array","minItems":1,"maxItems":25,"items":{
              "type":"object","required":["source","company","title","jdText"],
              "properties":{
                "source":{"type":"string","description":"Site it was found on: builtin, dice, wellfound, greenhouse, lever, ashby, linkedin, indeed, company"},
                "company":{"type":"string"},
                "title":{"type":"string","description":"Exactly as posted"},
                "matchedTitle":{"type":"string","description":"Which target or similar title from the hunt config it matched"},
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
                "jdText":{"type":"string"}
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
                return new { results, stats = store.Stats() };
            }),

        new("find_jobs",
            "Find stored jobs by company (and optionally title), best match first with a 0-1 score. Use it to match a " +
            "Gmail message to a job before record_email, or to check whether a posting is already tracked.",
            """
            {"type":"object","required":["company"],"properties":{"company":{"type":"string"},"title":{"type":"string"},"limit":{"type":"integer"}}}
            """,
            a => store.FindJobs(Str(a, "company")!, Str(a, "title"), Int(a, "limit") ?? 10)),

        new("get_job",
            "Full record for one job, including status history and emails. includeJd=true also returns the job description text.",
            """
            {"type":"object","required":["id"],"properties":{"id":{"type":"string"},"includeJd":{"type":"boolean"}}}
            """,
            a =>
            {
                var job = store.GetJob(Id(a)) ?? throw new PipelineException($"No job with id {Id(a)}");
                return Bool(a, "includeJd") == true ? new { job, jdText = store.ReadJd(job) } : new { job, jdText = (string?)null };
            }),

        new("list_jobs",
            "List jobs, most recently updated first, optionally filtered by status (e.g. ActionNeeded to see what is blocked on you).",
            """
            {"type":"object","properties":{"status":{"type":"string","enum":["New","Tailoring","ReadyToApply","Applying","Applied","Interviewing","Rejected","ActionNeeded","Skipped","Expired","Failed"]},"limit":{"type":"integer"},"offset":{"type":"integer"}}}
            """,
            a => store.ListJobs(Str(a, "status") is { } s ? Parse<JobStatus>(s) : null, Int(a, "limit") ?? 50, Int(a, "offset") ?? 0)),

        new("pipeline_stats", "Count of jobs in each status.",
            """{"type":"object","properties":{}}""",
            _ => store.Stats()),

        new("begin_apply_run",
            "Call once at the start of every apply run. Moves any job left in Applying by a crashed run to ActionNeeded " +
            "(it may already have been submitted, so it must not be retried automatically), then returns the jobs ready " +
            "to apply, how many were applied today, and per job how many other applications went to the same company in 30 days. " +
            "Jobs at companies on the hunt config's manual-apply list are moved to ActionNeeded and returned in manualApply: " +
            "never apply to those, by any URL.",
            """
            {"type":"object","properties":{"limit":{"type":"integer","description":"Max ready jobs to return (default 10)"}}}
            """,
            a => store.BeginApplyRun(Int(a, "limit") ?? 10, Configs.ManualApplyCompanies(store.DataDir))),

        new("start_apply",
            "Call immediately before opening a job's application. Moves it ReadyToApply → Applying and returns the URLs " +
            "and the absolute path of the tailored resume to upload. Fails if the job is not ReadyToApply or the company is on " +
            "the manual-apply list.",
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
            "Record a submitted application (Applying → Applied). Only call after the site showed a submission confirmation.",
            """
            {"type":"object","required":["id"],"properties":{"id":{"type":"string"},"note":{"type":"string","description":"e.g. confirmation text or what was submitted"}}}
            """,
            a => store.MarkApplied(Id(a), Str(a, "note"))),

        new("flag_action",
            "Hand a job to the user: CAPTCHA, account creation or password required, 2FA, assessment, a question the " +
            "profile cannot answer, upload blocked, or any other step needing a person. reason must say exactly what " +
            "the user has to do and where it stopped.",
            """
            {"type":"object","required":["id","reason"],"properties":{"id":{"type":"string"},"reason":{"type":"string"}}}
            """,
            a => store.FlagAction(Id(a), Str(a, "reason")!)),

        new("close_job",
            "Close a job without applying: 'expired' when the posting is gone or closed, 'skipped' when it turns out not " +
            "to fit (onsite, clearance, wrong stack, cap reached for that company).",
            """
            {"type":"object","required":["id","outcome","reason"],"properties":{"id":{"type":"string"},"outcome":{"type":"string","enum":["expired","skipped"]},"reason":{"type":"string"}}}
            """,
            a => store.CloseJob(Id(a), Parse<JobStatus>(Str(a, "outcome")!), Str(a, "reason")!, "apply")),

        new("resolve_action",
            "For the user clearing a blocked (ActionNeeded) or Failed job: 'retry' puts it back in the queue, 'applied' " +
            "records that they applied by hand, 'skip' drops it. Only call when the user says so.",
            """
            {"type":"object","required":["id","outcome"],"properties":{"id":{"type":"string"},"outcome":{"type":"string","enum":["retry","applied","skip"]},"note":{"type":"string"}}}
            """,
            a => store.ResolveAction(Id(a), Parse<ResolveOutcome>(Str(a, "outcome")!), Str(a, "note"))),

        new("record_email",
            "Attach a Gmail message to a job (use find_jobs first). confirmation → Applied, rejection → Rejected, " +
            "interview → Interviewing (pass interviewDate only when a specific time is confirmed). The same thread and " +
            "type is never recorded twice, so re-syncing is safe.",
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
