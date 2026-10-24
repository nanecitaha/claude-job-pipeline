using System.Text;

namespace JobPipeline.Worker;

/// <summary>
/// Tailor v1 (test version): fills each role's tech-stack line in your Word resume with your own stack for that
/// role plus every term from the job's tech stack (read from the database) that the role doesn't already list,
/// then saves the resume as a PDF.
///
/// It adds ALL missing posting terms, including ones you may not have. Treat these resumes as test output and
/// don't submit them on real applications until relevance filtering (v2) exists.
///
/// Setup: in your resume .docx, put a placeholder where each role's tech stack goes, e.g.
///   Tech stack: {{TECH_STACK:role1}}
/// and add a matching key to <see cref="MyTechStack"/>. Set Pipeline:ResumeTemplatePath to that .docx.
/// </summary>
public sealed class TechStackTailor : ITailor
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    // EDIT THIS: your tech stack per role, in the order you want it shown. Each key must match a
    // {{TECH_STACK:key}} placeholder in your resume template (keys are case-insensitive).
    // This repo is public: if you'd rather not commit your real roles, keep the keys generic (role1, role2, ...).
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    public static readonly IReadOnlyDictionary<string, string[]> MyTechStack =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["role1"] = ["C#", ".NET 8", "ASP.NET Core", "SQL Server", "RabbitMQ"],
            ["role2"] = ["C#", "ASP.NET MVC", ".NET Framework 4.8", "SQL Server"],
            ["role3"] = ["Python", "SQL"],
        };

    readonly PipelineOptions _options;
    readonly IPdfConverter _converter;
    readonly IReadOnlyDictionary<string, string[]> _stack;

    public TechStackTailor(PipelineOptions options, IPdfConverter converter) : this(options, converter, MyTechStack) { }

    public TechStackTailor(PipelineOptions options, IPdfConverter converter, IReadOnlyDictionary<string, string[]> stack)
    {
        _options = options;
        _converter = converter;
        _stack = new Dictionary<string, string[]>(stack, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<TailorResult> TailorAsync(TailorRequest request, CancellationToken ct)
    {
        var template = _options.ResumeTemplatePath;
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
            throw new FileNotFoundException(
                $"Resume template not found: '{template}'. Set Pipeline:ResumeTemplatePath to your resume .docx.");

        var job = request.Job;
        var name = ResumeFiles.BaseName(job);
        var docx = Path.Combine(request.OutputDir, name + ".docx");
        var pdf = Path.Combine(request.OutputDir, name + ".pdf");

        var added = new List<(string Role, IReadOnlyList<string> Terms)>();
        var roles = DocxTemplate.Fill(template, docx, role =>
        {
            if (!_stack.TryGetValue(role, out var mine))
                throw new InvalidOperationException(
                    $"The template has {{{{TECH_STACK:{role}}}}} but TechStackTailor.MyTechStack has no '{role}' entry.");
            var merged = Merge(mine, job.TechStack);
            added.Add((role, merged.Added));
            return string.Join(", ", merged.All);
        });

        try
        {
            if (roles.Count == 0)
                throw new InvalidOperationException(
                    "No {{TECH_STACK:role}} placeholders found in the resume template; nothing to tailor.");

            await _converter.ConvertAsync(docx, pdf, ct);
            if (!File.Exists(pdf)) throw new InvalidOperationException("PDF conversion reported success but produced no file.");
        }
        finally
        {
            if (!_options.KeepDocx) File.Delete(docx);
        }

        // Next to the PDF: exactly what was added from the posting, so test output is easy to review.
        var report = new StringBuilder($"{job.Company} – {job.Title}\nPosting tech stack: {string.Join(", ", job.TechStack)}\n");
        foreach (var (role, terms) in added)
            report.Append($"{role}: added {(terms.Count == 0 ? "nothing" : string.Join(", ", terms))}\n");
        await File.WriteAllTextAsync(Path.Combine(request.OutputDir, name + ".tech-added.txt"), report.ToString(), ct);

        var total = added.SelectMany(a => a.Terms).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new TailorResult(pdf, $"tailor v1: tech stack on {roles.Count} role(s), {total} term(s) added from the posting");
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
