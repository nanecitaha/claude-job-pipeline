namespace JobPipeline.Worker;

/// <summary>
/// Tailor v1 (test version). Keeps your resume's tech-stack lines as they are and appends the job's tech-stack
/// terms (read from the database) that a line is missing, then saves the resume as a PDF.
///
/// Setup: in your resume .docx, type {{ADD_MISSING}} at the end of each tech-stack line, e.g.
///   C#, .NET, RabbitMQ, SQL Server {{ADD_MISSING}}
/// The line is read as a comma-separated list; missing posting terms go where the tag was and the tag is removed.
/// With Pipeline:HighlightAddedTerms (on by default) the added terms are bold and colored so they stand out.
///
/// It adds ALL missing posting terms, including ones you may not have. Treat these resumes as test output and
/// don't submit them on real applications until relevance filtering (v2) exists.
/// </summary>
public sealed class TechStackTailor(PipelineOptions options, IPdfConverter converter) : ITailor
{
    public async Task<TailorResult> TailorAsync(TailorRequest request, CancellationToken ct)
    {
        var template = options.ResumeTemplatePath;
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
            throw new FileNotFoundException(
                $"Resume template not found: '{template}'. Set Pipeline:ResumeTemplatePath to your resume .docx.");

        var job = request.Job;
        var dir = ResumeFiles.JobFolder(request.OutputDir, job);
        var fileName = ResumeFiles.FileName(options.ResumeFileName, job);
        var docx = Path.Combine(dir, fileName + ".docx");
        var pdf = Path.Combine(dir, fileName + ".pdf");
        var style = options.HighlightAddedTerms
            ? new AddedStyle(Bold: true, ColorHex: options.HighlightColor, FontSizePt: options.HighlightFontSize > 0 ? options.HighlightFontSize : null)
            : null;

        var lines = DocxTemplate.AddMissing(template, docx, existing => Merge(existing, job.TechStack).Added, style);
        try
        {
            if (lines.Count == 0)
                throw new InvalidOperationException(
                    "No {{ADD_MISSING}} tags found in the resume template. Type {{ADD_MISSING}} at the end of each " +
                    "tech-stack line.");

            await converter.ConvertAsync(docx, pdf, ct);
            if (!File.Exists(pdf)) throw new InvalidOperationException("PDF conversion reported success but produced no file.");
        }
        finally
        {
            if (!options.KeepDocx) File.Delete(docx);
        }

        // Next to the PDF: the job's tech stack as captured by the hunt, one comma-separated line.
        await File.WriteAllTextAsync(Path.Combine(dir, ResumeFiles.TechStackFile), string.Join(", ", job.TechStack), ct);

        var total = lines.SelectMany(l => l.Added).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new TailorResult(pdf, $"tailor v1: {lines.Count} tech-stack line(s), {total} term(s) added from the posting");
    }

    /// <summary>
    /// Your terms first, in your order, then each posting term you don't already list, in the posting's order and
    /// spelling. Matching ignores case only: "c#" equals "C#", but ".NET 8" and ".NET8" are different terms.
    /// </summary>
    public static (IReadOnlyList<string> All, IReadOnlyList<string> Added) Merge(IEnumerable<string> mine, IEnumerable<string> posting)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<string>();
        var added = new List<string>();
        foreach (var t in mine.Select(x => x.Trim()).Where(x => x.Length > 0))
            if (seen.Add(t)) all.Add(t);
        foreach (var t in posting.Select(x => x.Trim()).Where(x => x.Length > 0))
            if (seen.Add(t)) { all.Add(t); added.Add(t); }
        return (all, added);
    }
}

public static class ResumeFiles
{
    public const string TechStackFile = "techstack.txt";
    public const string DefaultFileName = "Resume";

    /// <summary>Folder name for a job's files: company-title-idprefix (unique per job).</summary>
    public static string BaseName(JobPipeline.Core.Job job) => $"{JobSlug(job)}-{job.Id.ToString()[..8]}";

    /// <summary>Creates (if needed) and returns the job's own folder under the resumes folder.</summary>
    public static string JobFolder(string resumesDir, JobPipeline.Core.Job job) =>
        Directory.CreateDirectory(Path.Combine(resumesDir, BaseName(job))).FullName;

    /// <summary>
    /// Resume file name (no extension) from Pipeline:ResumeFileName, e.g. "Jane_Doe_Resume(tailored for {job})";
    /// {job} becomes company-title. Characters Windows doesn't allow in file names are removed.
    /// </summary>
    public static string FileName(string? pattern, JobPipeline.Core.Job job)
    {
        var raw = (string.IsNullOrWhiteSpace(pattern) ? DefaultFileName : pattern)
            .Replace("{job}", JobSlug(job), StringComparison.OrdinalIgnoreCase);
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var clean = new string(raw.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Resume" : clean.Length > 150 ? clean[..150].TrimEnd() : clean;
    }

    /// <summary>company-title, lowercase with dashes, e.g. "draftkings-senior-platform-engineer".</summary>
    public static string JobSlug(JobPipeline.Core.Job job) => $"{Slug(job.Company)}-{Slug(job.Title)}";

    static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}
