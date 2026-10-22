using JobPipeline.Core;

namespace JobPipeline.Worker;

public sealed class PipelineOptions
{
    /// <summary>Same folder the MCP server uses. Empty = JOBPIPELINE_DATA or %LOCALAPPDATA%\JobPipeline.</summary>
    public string? DataDir { get; set; }
    /// <summary>Your master resume (PDF or DOCX). The placeholder tailor copies it; a real tailor reads from it.</summary>
    public string MasterResumePath { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
    public int MaxTailorAttempts { get; set; } = 3;
    /// <summary>A Tailoring claim older than this is assumed dead (crashed worker) and reclaimed.</summary>
    public int StaleClaimMinutes { get; set; } = 30;
}

public sealed record TailorRequest(Job Job, string JdText, string OutputDir);

public sealed record TailorResult(string ResumePath, string? Note = null);

/// <summary>
/// The one piece to replace with real tailoring (resume bank → Claude → validator → render).
/// Contract: write the resume file under OutputDir and return its absolute path; throw to fail the attempt.
/// </summary>
public interface ITailor
{
    Task<TailorResult> TailorAsync(TailorRequest request, CancellationToken ct);
}

/// <summary>
/// Stand-in so the whole pipeline runs end to end today: copies the master resume under a per-job name and
/// writes the posting's tech stack (exactly as captured) next to it for reference.
/// </summary>
public sealed class CopyMasterResumeTailor(PipelineOptions options) : ITailor
{
    public async Task<TailorResult> TailorAsync(TailorRequest request, CancellationToken ct)
    {
        var master = options.MasterResumePath;
        if (string.IsNullOrWhiteSpace(master) || !File.Exists(master))
            throw new FileNotFoundException($"Master resume not found: '{master}'. Set Pipeline:MasterResumePath in appsettings.json.");

        var job = request.Job;
        var name = $"{Slug(job.Company)}-{Slug(job.Title)}-{job.Id.ToString()[..8]}";
        var resume = Path.Combine(request.OutputDir, name + Path.GetExtension(master));
        File.Copy(master, resume, overwrite: true);

        if (job.TechStack.Count > 0)
            await File.WriteAllTextAsync(Path.Combine(request.OutputDir, name + ".keywords.txt"),
                string.Join(", ", job.TechStack), ct);

        return new TailorResult(resume, "placeholder: master resume copied, not tailored");
    }

    static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}
