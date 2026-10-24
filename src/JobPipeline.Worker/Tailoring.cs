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

    /// <summary>Which tailor runs: "TechStack" (v1: fills tech-stack lines, saves PDF) or "CopyMaster" (copies the master resume).</summary>
    public string Tailor { get; set; } = "CopyMaster";
    /// <summary>
    /// File name (no extension) for each tailored resume, e.g. "Jane_Doe_Resume". Each job has its own folder, so the
    /// same name is fine. Optional {job} placeholder becomes company-title. Employers see this name.
    /// </summary>
    public string ResumeFileName { get; set; } = ResumeFiles.DefaultFileName;
    /// <summary>Your resume as a Word .docx with {{ADD_MISSING}} at the end of each tech-stack line (TechStack tailor).</summary>
    public string ResumeTemplatePath { get; set; } = "";
    /// <summary>"Word" (Microsoft Word, needs a logged-in session) or "LibreOffice" (headless, works on servers).</summary>
    public string PdfConverter { get; set; } = "Word";
    /// <summary>Full path to soffice(.exe). Empty = default install location.</summary>
    public string LibreOfficePath { get; set; } = "";
    public int ConversionTimeoutSeconds { get; set; } = 120;
    /// <summary>Keep the filled .docx next to the PDF (handy while testing the template).</summary>
    public bool KeepDocx { get; set; }
    /// <summary>Make terms added from the posting bold and colored, so they're easy to spot while testing.</summary>
    public bool HighlightAddedTerms { get; set; } = true;
    /// <summary>Color for added terms as hex RGB (FF0000 = red).</summary>
    public string HighlightColor { get; set; } = "FF0000";
    /// <summary>Font size in points for added terms (e.g. 10 or 10.5). 0 = same size as the line.</summary>
    public double HighlightFontSize { get; set; }
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
/// Stand-in so the whole pipeline runs end to end: copies the master resume into the job's folder and writes the
/// posting's tech stack (exactly as captured) next to it for reference.
/// </summary>
public sealed class CopyMasterResumeTailor(PipelineOptions options) : ITailor
{
    public async Task<TailorResult> TailorAsync(TailorRequest request, CancellationToken ct)
    {
        var master = options.MasterResumePath;
        if (string.IsNullOrWhiteSpace(master) || !File.Exists(master))
            throw new FileNotFoundException($"Master resume not found: '{master}'. Set Pipeline:MasterResumePath in appsettings.json.");

        var job = request.Job;
        var dir = ResumeFiles.JobFolder(request.OutputDir, job);
        var resume = Path.Combine(dir, ResumeFiles.FileName(options.ResumeFileName, job) + Path.GetExtension(master));
        File.Copy(master, resume, overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(dir, ResumeFiles.TechStackFile), string.Join(", ", job.TechStack), ct);

        return new TailorResult(resume, "placeholder: master resume copied, not tailored");
    }
}
