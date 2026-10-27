namespace JobPipeline.Enricher;

public sealed class EnrichOptions
{
    /// <summary>Same folder the MCP server and tailoring worker use. Empty = JOBPIPELINE_DATA or %LOCALAPPDATA%\JobPipeline.</summary>
    public string? DataDir { get; set; }
    public int PollSeconds { get; set; } = 30;
    /// <summary>Tries per posting before it goes to EnrichFailed (only failures worth retrying count).</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Wait this long × attempts before retrying a posting that failed.</summary>
    public int RetryDelayMinutes { get; set; } = 15;
    /// <summary>An Enriching claim older than this is assumed dead (crashed enricher) and reclaimed.</summary>
    public int StaleClaimMinutes { get; set; } = 15;
    /// <summary>Minimum gap between two requests to the same host.</summary>
    public int RequestDelayMs { get; set; } = 2000;
    public int RequestTimeoutSeconds { get; set; } = 30;
    /// <summary>
    /// Sites never read automatically (they block bots or need a sign-in). URLs there go straight to EnrichFailed,
    /// for Claude or you to fill in with add_jobs. Matches the host and its subdomains. Empty = the defaults below.
    /// (Not pre-filled here: configuration binding appends to a non-empty list instead of replacing it.)
    /// </summary>
    public List<string> SkipHosts { get; set; } = [];

    public static readonly string[] DefaultSkipHosts = ["linkedin.com", "indeed.com", "glassdoor.com", "ziprecruiter.com"];

    public IReadOnlyList<string> EffectiveSkipHosts => SkipHosts.Count > 0 ? SkipHosts : DefaultSkipHosts;
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
}
