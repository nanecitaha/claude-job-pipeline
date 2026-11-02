using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobPipeline.Core;

/// <summary>One labelled job for the tech stack model: the text and where each extracted term appears in it.</summary>
public sealed record TrainingExample(
    Guid Id, string Company, string Title, string Text, IReadOnlyList<string> TechStack,
    IReadOnlyList<TermSpan> Spans, IReadOnlyList<string> NotFound);

/// <summary>Characters [Start, End) of <see cref="TrainingExample.Text"/> hold <see cref="Term"/>, exactly as written.</summary>
public sealed record TermSpan(int Start, int End, string Term);

public sealed record ExportReport(
    int Jobs, int NoDescription, int NoTechStack, int LowMatch, int Exported, int Terms, int TermsFound,
    IReadOnlyList<(string Term, int Count)> MostMissed)
{
    public double MatchRate => Terms == 0 ? 0 : (double)TermsFound / Terms;
}

/// <summary>
/// Turns the jobs Claude has extracted into training data. Each job becomes "title, blank line, job description";
/// every occurrence of each tech stack term is marked where it appears word for word (same case, not inside a
/// longer word). Jobs where too few terms appear word for word (Claude reworded them, or the description is a
/// summary) are left out, because their labels would teach the model the wrong thing.
/// </summary>
public static class TrainingExport
{
    static readonly JsonSerializerOptions Jsonl = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static ExportReport Write(JobStore store, TextWriter output, double minMatch = 0.8)
    {
        int jobs = 0, noJd = 0, noStack = 0, low = 0, exported = 0, terms = 0, found = 0;
        var missed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var job in store.QueryJobs(null, null, limit: int.MaxValue).OrderBy(j => j.CreatedAt))
        {
            jobs++;
            var jd = store.ReadJd(job);
            if (string.IsNullOrWhiteSpace(jd)) { noJd++; continue; }
            if (job.TechStack.Count == 0) { noStack++; continue; }

            var example = Build(job.Id, job.Company, job.Title, jd, job.TechStack);
            var hits = example.TechStack.Count - example.NotFound.Count;
            foreach (var t in example.NotFound) missed[t] = missed.GetValueOrDefault(t) + 1;
            terms += example.TechStack.Count;
            found += hits;
            if ((double)hits / example.TechStack.Count < minMatch) { low++; continue; }

            output.WriteLine(JsonSerializer.Serialize(example, Jsonl));
            exported++;
        }
        var top = missed.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(15).Select(kv => (kv.Key, kv.Value)).ToList();
        return new ExportReport(jobs, noJd, noStack, low, exported, terms, found, top);
    }

    public static TrainingExample Build(Guid id, string company, string title, string jd, IEnumerable<string> techStack)
    {
        var text = $"{title.Trim()}\n\n{jd.Trim().Replace("\r\n", "\n")}";
        var stack = techStack.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var taken = new bool[text.Length];
        var spans = new List<TermSpan>();
        var notFound = new List<string>();

        // Longest terms first, so "ASP.NET Core" claims its text before "ASP.NET" or ".NET" can.
        foreach (var term in stack.OrderByDescending(t => t.Length))
        {
            var any = false;
            for (var at = text.IndexOf(term, StringComparison.Ordinal); at >= 0; at = text.IndexOf(term, at + 1, StringComparison.Ordinal))
            {
                var end = at + term.Length;
                if (!Boundary(text, at - 1) || !Boundary(text, end)) continue;   // "Java" inside "JavaScript"
                any = true;                                                      // counts as found even if a longer term owns it
                if (Enumerable.Range(at, term.Length).Any(i => taken[i])) continue;
                for (var i = at; i < end; i++) taken[i] = true;
                spans.Add(new TermSpan(at, end, term));
            }
            if (!any) notFound.Add(term);
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new TrainingExample(id, company, title, text, stack, spans, stack.Where(notFound.Contains).ToList());
    }

    // A term must not run into a neighbouring letter or digit ("Go" isn't in "Google"); punctuation is fine ("C#,").
    static bool Boundary(string text, int i) => i < 0 || i >= text.Length || !char.IsLetterOrDigit(text[i]);
}
