using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobPipeline.Enricher;

/// <summary>
/// Just enough HTML handling for job pages, with no parser dependency: plain text from HTML, links with their
/// text, the page title and first heading, and schema.org JobPosting blocks.
/// </summary>
public static partial class Html
{
    /// <summary>
    /// Readable plain text: block elements and line breaks become new lines, list items get "- ", entities are
    /// decoded, runs of spaces collapse. Accepts HTML that was itself entity-encoded (Greenhouse's "content").
    /// </summary>
    public static string ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html;
        if (s.Contains("&lt;", StringComparison.Ordinal) && !s.Contains('<')) s = WebUtility.HtmlDecode(s);
        s = Unwanted().Replace(s, " ");
        s = Comments().Replace(s, " ");
        s = ListItem().Replace(s, "\n- ");
        s = BlockBreak().Replace(s, "\n");
        s = Tag().Replace(s, " ");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        var lines = s.Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim())
            .Select(l => l == "-" ? "" : l);
        return string.Join('\n', lines.Where(l => l.Length > 0));
    }

    public sealed record Link(string Url, string Text);

    /// <summary>Absolute http(s) links on the page with their visible text (or aria-label/title).</summary>
    public static IEnumerable<Link> Links(string html, string pageUrl)
    {
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var page);
        foreach (Match m in Anchor().Matches(html))
        {
            var attrs = m.Groups["attrs"].Value;
            var href = AttrValue(attrs, "href");
            if (href is null) continue;
            href = WebUtility.HtmlDecode(href).Trim();
            if (!Uri.TryCreate(page, href, out var u) || u.Scheme is not ("http" or "https")) continue;
            var text = ToText(m.Groups["text"].Value);
            if (text.Length == 0) text = WebUtility.HtmlDecode(AttrValue(attrs, "aria-label") ?? AttrValue(attrs, "title") ?? "");
            yield return new Link(u.ToString(), text.Trim());
        }
    }

    public static string? Title(string html) =>
        TitleTag().Match(html) is { Success: true } m ? Clean(m.Groups[1].Value) : null;

    public static string? FirstHeading(string html) =>
        H1().Match(html) is { Success: true } m ? Clean(m.Groups[1].Value) : null;

    public static string? MetaContent(string html, string property)
    {
        foreach (Match m in Meta().Matches(html))
        {
            var attrs = m.Groups[1].Value;
            var key = AttrValue(attrs, "property") ?? AttrValue(attrs, "name");
            if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase))
                return AttrValue(attrs, "content") is { } c ? Clean(c) : null;
        }
        return null;
    }

    /// <summary>Every JobPosting object in the page's JSON-LD, including inside @graph and arrays.</summary>
    public static IReadOnlyList<JsonElement> JobPostings(string html)
    {
        var found = new List<JsonElement>();
        foreach (Match m in JsonLd().Matches(html))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim(),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var e in Walk(doc.RootElement))
                    if (IsType(e, "JobPosting")) found.Add(e.Clone());
            }
            catch (JsonException)
            {
                // Broken JSON-LD is common; the page simply has no usable block.
            }
        }
        return found;
    }

    static IEnumerable<JsonElement> Walk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
            foreach (var y in Walk(x))
                yield return y;
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            yield return e;
            if (e.TryGetProperty("@graph", out var g))
                foreach (var y in Walk(g))
                    yield return y;
        }
    }

    static bool IsType(JsonElement e, string type) =>
        e.TryGetProperty("@type", out var t) &&
        (t.ValueKind == JsonValueKind.String ? t.GetString() == type
            : t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == type));

    static string? AttrValue(string attrs, string name)
    {
        var m = Regex.Match(attrs, $@"(?:^|\s){Regex.Escape(name)}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    static string? Clean(string s)
    {
        var t = Spaces().Replace(WebUtility.HtmlDecode(Tag().Replace(s, " ")), " ").Trim();
        return t.Length == 0 ? null : t;
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|template|iframe)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Unwanted();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();
    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();
    [GeneratedRegex(@"<br\s*/?>|</?(p|div|section|article|ul|ol|li|h[1-6]|tr|table|blockquote|header|footer)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();
    [GeneratedRegex(@"<title[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();
    [GeneratedRegex(@"<h1\b[^>]*>(.*?)</h1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex H1();
    [GeneratedRegex(@"<meta\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Meta();
    [GeneratedRegex(@"<script[^>]*application/ld\+json[^>]*>(.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLd();
}
