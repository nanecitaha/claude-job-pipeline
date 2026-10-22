using System.Text.Json.Serialization;

namespace JobPipeline.Core;

/// <summary>
/// One status per job replaces the isApplied / readyToApply / actionNeeded / isInterviewScheduled flags,
/// so impossible combinations (applied AND blocked) can't exist.
///
///   New → Tailoring → ReadyToApply → Applying → Applied → Interviewing | Rejected
///                                       ↘ ActionNeeded / Skipped / Expired / Failed
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    New,           // found by the hunt, waiting for the worker
    Tailoring,     // worker has claimed it
    ReadyToApply,  // tailored resume exists (ResumePath)
    Applying,      // Claude has opened the application; if a run dies here, it is NOT retried blindly
    Applied,
    Interviewing,
    Rejected,
    ActionNeeded,  // blocked on you: see ActionDesc
    Skipped,
    Expired,
    Failed,        // tailoring failed MaxTailorAttempts times: see LastError
}

[JsonConverter(typeof(JsonStringEnumConverter<EmailType>))]
public enum EmailType { Confirmation, Rejection, Interview, Assessment, Other }

public sealed class Job
{
    public Guid Id { get; set; }
    public JobStatus Status { get; set; } = JobStatus.New;

    // What the hunt captured
    public string Source { get; set; } = "";             // builtin, dice, greenhouse, ...
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string? MatchedTitle { get; set; }            // which of your target titles it matched
    public string? ListingUrl { get; set; }              // where it was found (e.g. builtin page)
    public string? ApplyUrl { get; set; }                // the employer's own application page
    public string? ExternalJobId { get; set; }           // id from the posting/ATS, when visible
    public string? Location { get; set; }
    public bool? IsRemote { get; set; }
    public int? SalaryMin { get; set; }
    public int? SalaryMax { get; set; }
    public string? SalaryCurrency { get; set; }
    public string? SalaryText { get; set; }              // raw text as posted
    public List<string> TechStack { get; set; } = new(); // copied exactly as written in the posting
    public string? DatePosted { get; set; }              // yyyy-MM-dd when known
    public bool IsEasyApply { get; set; }
    public string? JdPath { get; set; }                  // relative to the data folder (jds/{id}.txt)
    public string DedupeKey { get; set; } = "";

    // Pipeline state
    public string? ResumePath { get; set; }              // set by the worker
    public string? ActionDesc { get; set; }              // why it's blocked (ActionNeeded) or closed
    public string? LastError { get; set; }
    public int TailorAttempts { get; set; }
    public int ApplyAttempts { get; set; }
    public DateTimeOffset? DateApplied { get; set; }
    public DateTimeOffset? InterviewDate { get; set; }
    public List<EmailEvent> Emails { get; set; } = new();
    public List<StatusChange> History { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EmailEvent
{
    public EmailType Type { get; set; }
    public string? ThreadId { get; set; }
    public string? Subject { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed record StatusChange(JobStatus? From, JobStatus To, DateTimeOffset At, string By, string? Note);

/// <summary>Input for the hunt. Everything except Company, Title and a URL is optional.</summary>
public sealed class NewJob
{
    public string Source { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string? MatchedTitle { get; set; }
    public string? ListingUrl { get; set; }
    public string? ApplyUrl { get; set; }
    public string? ExternalJobId { get; set; }
    public string? Location { get; set; }
    public bool? IsRemote { get; set; }
    public int? SalaryMin { get; set; }
    public int? SalaryMax { get; set; }
    public string? SalaryCurrency { get; set; }
    public string? SalaryText { get; set; }
    public List<string>? TechStack { get; set; }
    public string? DatePosted { get; set; }
    public bool IsEasyApply { get; set; }
    public string? JdText { get; set; }
}

internal sealed class JobFile
{
    public int Version { get; set; } = 1;
    public List<Job> Jobs { get; set; } = new();
}
