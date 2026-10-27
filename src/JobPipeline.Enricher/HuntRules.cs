using JobPipeline.Core;

namespace JobPipeline.Enricher;

/// <summary>
/// The hunt's Keep/Exclude rules in a form code can apply, read from the "## Filters" section of hunt-config.md
/// (the file Claude reads for the hunt). Borderline postings go to NeedsReview, never silently dropped.
/// Order: excluded title → description too short → no required term → review phrase → not an engineering title.
/// </summary>
public sealed class HuntRules
{
    public IReadOnlyList<string> RequireAny { get; init; } = Defaults.RequireAny;
    public IReadOnlyList<string> ExcludeTitle { get; init; } = Defaults.ExcludeTitle;
    public IReadOnlyList<string> ReviewIf { get; init; } = Defaults.ReviewIf;
    public IReadOnlyList<string> RoleWords { get; init; } = Defaults.RoleWords;
    /// <summary>True when hunt-config.md has no Filters section and the built-in defaults are in use.</summary>
    public bool FromDefaults { get; init; }

    public static class Defaults
    {
        public static readonly string[] RequireAny = ["C#", ".NET", "dotnet", "ASP.NET"];
        public static readonly string[] ExcludeTitle =
            ["intern", "internship", "new grad", "new graduate", "junior", "jr", "entry level", "entry-level", "apprentice"];
        public static readonly string[] ReviewIf =
            ["security clearance", "clearance required", "active clearance", "TS/SCI", "top secret", "polygraph"];
        public static readonly string[] RoleWords =
            ["engineer", "engineering", "developer", "development", "programmer", "architect", "software", "devops", "sre", "swe", "lead"];
    }

    /// <summary>The section Configs writes into a new hunt-config.md; existing files can paste it in.</summary>
    public const string SectionHeading = "## Filters";

    public static HuntRules Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "hunt-config.md");
        return File.Exists(path) ? Parse(File.ReadAllLines(path)) : new HuntRules { FromDefaults = true };
    }

    /// <summary>
    /// "- name: a, b, c" lines under a heading starting with "## Filters". A " #" starts a comment ("C#" is fine).
    /// A missing line keeps that rule's default; an empty one ("- review_if:") turns the rule off.
    /// </summary>
    public static HuntRules Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        var sawSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                inSection = line.StartsWith(SectionHeading, StringComparison.OrdinalIgnoreCase);
                sawSection |= inSection;
                continue;
            }
            if (!inSection || !line.StartsWith("- ")) continue;
            var comment = line.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[2..colon].Trim();
            values[name] = line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0 && t != "[fill]").ToArray();
        }
        return new HuntRules
        {
            RequireAny = values.GetValueOrDefault("require_any") ?? Defaults.RequireAny,
            ExcludeTitle = values.GetValueOrDefault("exclude_title") ?? Defaults.ExcludeTitle,
            ReviewIf = values.GetValueOrDefault("review_if") ?? Defaults.ReviewIf,
            RoleWords = values.GetValueOrDefault("role_words") ?? Defaults.RoleWords,
            FromDefaults = !sawSection,
        };
    }

    /// <summary>Keep, Review or Exclude, with the reason shown on the job and the Keep rule it matched.</summary>
    public (EnrichVerdict Verdict, string Reason) Evaluate(string title, string jdText)
    {
        if (ExcludeTitle.Select(t => (t, i: Terms.Find(title, t))).FirstOrDefault(x => x.i >= 0) is { t: not null } ex)
            return (EnrichVerdict.Exclude, $"title says \"{title.Substring(ex.i, ex.t.Length)}\"");

        if (jdText.Length < PostingReader.MinDescriptionChars)
            return (EnrichVerdict.Review, $"description too short to check ({jdText.Length} characters)");

        var text = title + "\n" + jdText;
        if (RequireAny.Count > 0)
        {
            var hit = RequireAny.Select(t => (t, i: Terms.Find(text, t))).Where(x => x.i >= 0).OrderBy(x => x.i).FirstOrDefault();
            if (hit.t is null)
                return (EnrichVerdict.Exclude, $"doesn't mention {string.Join(", ", RequireAny)}");
        }

        if (ReviewIf.Select(t => (t, i: Terms.Find(jdText, t))).FirstOrDefault(x => x.i >= 0) is { t: not null } rv)
            return (EnrichVerdict.Review, $"mentions \"{Context(jdText, rv.i, rv.t.Length)}\"");

        if (RoleWords.Count > 0 && !RoleWords.Any(w => Terms.Find(title, w) >= 0))
            return (EnrichVerdict.Review, "title doesn't look like an engineering role");

        return (EnrichVerdict.Keep, MatchedRule(text));
    }

    /// <summary>"C# mentioned, remote" style note for the job's MatchedTitle.</summary>
    public string MatchedRule(string text)
    {
        var hit = RequireAny.Select(t => (t, i: Terms.Find(text, t))).Where(x => x.i >= 0).OrderBy(x => x.i).FirstOrDefault();
        return hit.t is null ? "kept" : $"{text.Substring(hit.i, hit.t.Length)} mentioned";
    }

    /// <summary>The matched phrase with a few words around it, so the reason reads on its own.</summary>
    static string Context(string text, int at, int length)
    {
        var start = Math.Max(0, text.LastIndexOfAny([' ', '\n'], Math.Max(0, at - 25)) + 1);
        var end = Math.Min(text.Length, at + length + 25);
        var spaceAfter = text.IndexOfAny([' ', '\n'], end < text.Length ? end : text.Length - 1);
        if (spaceAfter > 0) end = spaceAfter;
        return text[start..end].Replace('\n', ' ').Trim();
    }
}

/// <summary>
/// Case-insensitive term search that respects word edges: "intern" doesn't match "internal", ".NET" matches in
/// "ASP.NET" (its edge is a dot), "jr" doesn't match "jrnl". Edges are only checked on a side where the term
/// itself starts or ends with a letter or digit.
/// </summary>
public static class Terms
{
    public static int Find(string text, string term, int from = 0, bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text)) return -1;
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var i = from;
        while ((i = text.IndexOf(term, i, cmp)) >= 0)
        {
            var leftOk = !char.IsLetterOrDigit(term[0]) || i == 0 || !char.IsLetterOrDigit(text[i - 1]);
            var end = i + term.Length;
            var rightOk = !IsWordEnd(term[^1]) || end >= text.Length || !IsWordChar(text[end]);
            if (leftOk && rightOk && !LooksLikeDomain(text, i, term.Length)) return i;
            i++;
        }
        return -1;
    }

    /// <summary>
    /// ".net" in "careers@acme.net" or "acme.net/jobs" is a domain, not .NET: a term starting with a dot, written
    /// all lowercase right after a lowercase letter or digit. "ASP.NET", ".Net 8" and "asp.Net" still match.
    /// </summary>
    static bool LooksLikeDomain(string text, int at, int length) =>
        text[at] == '.' && at > 0 && (char.IsLower(text[at - 1]) || char.IsDigit(text[at - 1]))
        && text.Substring(at, length).All(c => !char.IsLetter(c) || char.IsLower(c));

    // "C#" ends in '#': no right edge needed. "Node" ends in a letter: "Nodes" mustn't match, "Node.js" may.
    static bool IsWordEnd(char c) => char.IsLetterOrDigit(c);
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '#' || c == '+';
}
