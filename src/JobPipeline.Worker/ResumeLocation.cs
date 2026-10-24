using System.Text.RegularExpressions;
using JobPipeline.Core;

namespace JobPipeline.Worker;

/// <summary>
/// What the resume's location line says for a job: your home location for remote jobs (or when the posting's
/// location is unclear), otherwise the job's city and state, written through Pipeline:LocationFormat.
/// </summary>
public static partial class ResumeLocation
{
    /// <param name="home">Your location as written in the template tag, e.g. "Irvine, CA".</param>
    /// <param name="format">"{location}" (default) or e.g. "{home} · Relocating to {location}".</param>
    public static (string Text, bool Changed) For(Job job, string home, string? format)
    {
        var remote = job.IsRemote == true
                     || (job.IsRemote is null && (job.Location ?? "").Contains("remote", StringComparison.OrdinalIgnoreCase));
        if (remote) return (home, false);

        var city = Clean(job.Location);
        if (city is null || string.Equals(city, home.Trim(), StringComparison.OrdinalIgnoreCase)) return (home, false);

        var text = (string.IsNullOrWhiteSpace(format) ? "{location}" : format)
            .Replace("{location}", city, StringComparison.OrdinalIgnoreCase)
            .Replace("{home}", home, StringComparison.OrdinalIgnoreCase);
        return (text, true);
    }

    /// <summary>
    /// "Boston, MA (Hybrid)" → "Boston, MA"; "New York, NY; Jersey City, NJ" → "New York, NY";
    /// "Hybrid - Austin, TX, United States" → "Austin, TX". Null when there's no usable city (remote, "United States",
    /// "Multiple Locations", empty).
    /// </summary>
    public static string? Clean(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var s = Parenthesized().Replace(location, " ");
        s = Separators().Split(s)[0];
        s = LeadingMode().Replace(s, "");
        s = TrailingMode().Replace(s, "");
        s = TrailingCountry().Replace(s, "");
        s = TrailingZip().Replace(s, "");
        s = s.Trim().Trim(',', '-', '–', ' ').Trim();
        if (s.Length == 0) return null;
        if (s.Contains("remote", StringComparison.OrdinalIgnoreCase) || s.Contains("multiple", StringComparison.OrdinalIgnoreCase)
            || s.Contains("locations", StringComparison.OrdinalIgnoreCase) || CountryOnly().IsMatch(s))
            return null;
        return s;
    }

    [GeneratedRegex(@"\([^)]*\)")] private static partial Regex Parenthesized();
    [GeneratedRegex(@"\s*(?:;|\||•|\n|\s/\s|\bor\b|\band\b)\s*", RegexOptions.IgnoreCase)] private static partial Regex Separators();
    [GeneratedRegex(@"^\s*(?:hybrid|on-?site|in[- ]office)\s*[-:–,]?\s*", RegexOptions.IgnoreCase)] private static partial Regex LeadingMode();
    [GeneratedRegex(@"\s*[-:–,]?\s*(?:hybrid|on-?site|in[- ]office)\s*$", RegexOptions.IgnoreCase)] private static partial Regex TrailingMode();
    [GeneratedRegex(@",?\s*(?:united states(?: of america)?|u\.?s\.?a\.?|u\.?s\.?)\s*$", RegexOptions.IgnoreCase)] private static partial Regex TrailingCountry();
    [GeneratedRegex(@",?\s*\d{5}(?:-\d{4})?\s*$")] private static partial Regex TrailingZip();
    [GeneratedRegex(@"^(?:united states(?: of america)?|u\.?s\.?a\.?|u\.?s\.?)$", RegexOptions.IgnoreCase)] private static partial Regex CountryOnly();
}
