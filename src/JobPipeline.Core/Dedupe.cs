using System.Text;
using System.Text.RegularExpressions;

namespace JobPipeline.Core;

/// <summary>
/// The same role shows up on builtin, LinkedIn and the company's own board, each with a different URL.
/// Normalized company + title is what catches those duplicates.
/// </summary>
public static partial class Dedupe
{
    static readonly string[] CompanySuffixes =
        { "inc", "incorporated", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "plc", "gmbh", "lp", "llp" };

    static readonly Dictionary<string, string> TitleAliases = new()
    {
        ["sr"] = "senior", ["snr"] = "senior", ["jr"] = "junior", ["eng"] = "engineer",
        ["engr"] = "engineer", ["dev"] = "developer", ["mgr"] = "manager", ["fullstack"] = "full stack",
        ["backend"] = "back end", ["frontend"] = "front end", ["swe"] = "software engineer",
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4",
    };

    public static string Key(string company, string title) => $"{Company(company)}|{Title(title)}";

    public static string Company(string company)
    {
        var words = Words(company).ToList();
        while (words.Count > 1 && CompanySuffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    public static string Title(string title)
    {
        // Drop parenthesized/bracketed qualifiers like "(Remote)" or "[US]" before tokenizing.
        var stripped = Bracketed().Replace(title, " ");
        var words = Words(stripped)
            .Select(w => TitleAliases.TryGetValue(w, out var a) ? a : w)
            .Where(w => w is not ("remote" or "us" or "usa" or "hybrid"));
        return string.Join(' ', string.Join(' ', words).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// True when a job's company is the listed company: same normalized name, or the listed name followed by more
    /// words ("Amazon" matches "Amazon Web Services", "Meta" matches "Meta Platforms, Inc."), but not a longer word
    /// ("Meta" does not match "Metabase").
    /// </summary>
    public static bool CompanyMatches(string jobCompany, string listed)
    {
        var j = Company(jobCompany);
        var e = Company(listed);
        return e.Length > 0 && (j == e || j.StartsWith(e + " ", StringComparison.Ordinal));
    }

    /// <summary>0..1 similarity of two titles by shared words; used to match emails to jobs.</summary>
    public static double TitleSimilarity(string a, string b)
    {
        var x = Title(a).Split(' ').ToHashSet();
        var y = Title(b).Split(' ').ToHashSet();
        if (x.Count == 0 || y.Count == 0) return 0;
        return (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    static IEnumerable<string> Words(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '#' or '+' ? ch : ' ');   // keep C#, C++
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    [GeneratedRegex(@"[\(\[].*?[\)\]]")]
    private static partial Regex Bracketed();
}
