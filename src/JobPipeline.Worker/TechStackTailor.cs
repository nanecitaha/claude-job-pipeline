using System.Text;

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
        var name = ResumeFiles.BaseName(job);
        var docx = Path.Combine(request.OutputDir, name + ".docx");
        var pdf = Path.Combine(request.OutputDir, name + ".pdf");
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

        // Next to the PDF: exactly what was added from the posting, so test output is easy to review.
        var report = new StringBuilder($"{job.Company} – {job.Title}\nPosting tech stack: {string.Join(", ", job.TechStack)}\n");
        for (var i = 0; i < lines.Count; i++)
            report.Append($"Line {i + 1}: added {(lines[i].Added.Count == 0 ? "nothing" : string.Join(", ", lines[i].Added))}\n");
        await File.WriteAllTextAsync(Path.Combine(request.OutputDir, name + ".tech-added.txt"), report.ToString(), ct);

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
    /// <summary>File name (no extension) for a job's resume: company-title-idprefix.</summary>
    public static string BaseName(JobPipeline.Core.Job job) =>
        $"{Slug(job.Company)}-{Slug(job.Title)}-{job.Id.ToString()[..8]}";

    static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}
