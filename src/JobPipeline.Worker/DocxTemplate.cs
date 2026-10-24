using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JobPipeline.Worker;

/// <summary>How terms added to a line are styled (e.g. red + bold while testing).</summary>
public sealed record AddedStyle(bool Bold, string? ColorHex);

/// <summary>One tagged line: the terms it already had and the terms that were added.</summary>
public sealed record AddedLine(IReadOnlyList<string> Existing, IReadOnlyList<string> Added);

/// <summary>
/// Edits a Word (.docx) resume: every paragraph containing <c>{{ADD_MISSING}}</c> is read as a comma-separated
/// list of terms (your tech stack line), the posting terms missing from it are appended where the tag was, and the
/// tag is removed. Everything else in the file is untouched.
///
/// Uses only the built-in zip and XML libraries: a .docx is a zip whose body is word/document.xml. Word often
/// splits typed text into several "runs" (spell-check, formatting), so the tag may span several text nodes; it's
/// found on the paragraph's combined text. Added terms copy the formatting of the text where the tag was, plus
/// the optional highlight.
/// </summary>
public static partial class DocxTemplate
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string BodyPart = "word/document.xml";

    [GeneratedRegex(@"\{\{\s*ADD_MISSING\s*\}\}", RegexOptions.IgnoreCase)]
    public static partial Regex Tag();

    /// <summary>
    /// Copies <paramref name="templatePath"/> to <paramref name="outputPath"/> and fills every tagged line.
    /// <paramref name="missing"/> gets the line's existing terms and returns the terms to append.
    /// Returns one result per tagged line, in document order.
    /// </summary>
    public static IReadOnlyList<AddedLine> AddMissing(
        string templatePath, string outputPath, Func<IReadOnlyList<string>, IReadOnlyList<string>> missing, AddedStyle? style)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Resume template not found: '{templatePath}'.");

        File.Copy(templatePath, outputPath, overwrite: true);
        var results = new List<AddedLine>();
        try
        {
            using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Update);
            var entry = zip.GetEntry(BodyPart)
                        ?? throw new InvalidDataException($"'{templatePath}' is not a Word document (no {BodyPart}).");

            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s, LoadOptions.PreserveWhitespace);

            foreach (var p in doc.Descendants(W + "p").ToList())
                if (FillParagraph(p, missing, style) is { } line) results.Add(line);

            entry.Delete();
            using var o = zip.CreateEntry(BodyPart, CompressionLevel.Optimal).Open();
            doc.Save(o, SaveOptions.DisableFormatting);
        }
        catch
        {
            File.Delete(outputPath);   // never leave a half-written resume behind
            throw;
        }
        return results;
    }

    /// <summary>Plain text of the document body, one paragraph per line (for checks and tests).</summary>
    public static string ReadText(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        using var s = zip.GetEntry(BodyPart)!.Open();
        var doc = XDocument.Load(s, LoadOptions.PreserveWhitespace);
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(W + "p"))
            sb.AppendLine(string.Concat(TextNodes(p).Select(t => t.Value)));
        return sb.ToString();
    }

    /// <summary>Splits a tech-stack line into terms: by commas, trimmed, trailing "." or ";" dropped.</summary>
    public static IReadOnlyList<string> ParseTerms(string line) =>
        line.Split(',')
            .Select(t => t.Trim().TrimEnd('.', ';').Trim())
            .Where(t => t.Length > 0)
            .ToList();

    static AddedLine? FillParagraph(XElement p, Func<IReadOnlyList<string>, IReadOnlyList<string>> missing, AddedStyle? style)
    {
        var nodes = TextNodes(p).ToList();
        if (nodes.Count == 0) return null;
        var full = string.Concat(nodes.Select(n => n.Value));
        var matches = Tag().Matches(full);
        if (matches.Count == 0) return null;

        // Remove every tag (right to left so positions stay valid); remember where the first one was.
        var starts = Starts(nodes);
        (int Node, int Offset) insertAt = default;
        foreach (var m in matches.Reverse())
            insertAt = RemoveRange(nodes, starts, m.Index, m.Length);

        var existing = ParseTerms(string.Concat(nodes.Select(n => n.Value)));
        var added = missing(existing);
        if (added.Count == 0)
        {
            // Nothing to add: just drop the space that preceded the tag.
            var node = nodes[insertAt.Node];
            var rest = node.Value[insertAt.Offset..];
            Set(node, node.Value[..insertAt.Offset]);
            TrimEndBefore(nodes, insertAt.Node);
            Set(node, node.Value + rest);
            return new AddedLine(existing, added);
        }

        // Split the text node where the tag was: [head][added terms][tail].
        var at = nodes[insertAt.Node];
        var run = at.Parent!;   // w:r
        var tailText = at.Value[insertAt.Offset..];
        Set(at, at.Value[..insertAt.Offset]);
        TrimEndBefore(nodes, insertAt.Node);

        var before = string.Concat(nodes.Take(insertAt.Node + 1).Select(n => n.Value));
        var needsSeparator = before.Trim().Length > 0;
        var lead = !needsSeparator ? "" : before.TrimEnd().EndsWith(',') ? " " : ", ";

        var newRuns = new List<XElement>();
        for (var i = 0; i < added.Count; i++)
        {
            var sep = i == 0 ? lead : ", ";
            if (sep.Length > 0) newRuns.Add(CloneRun(run, sep, null));
            newRuns.Add(CloneRun(run, added[i], style));
        }
        if (tailText.Length > 0) newRuns.Add(CloneRun(run, tailText, null));
        run.AddAfterSelf(newRuns);

        return new AddedLine(existing, added);
    }

    // Text nodes that belong to this paragraph (not to a paragraph nested inside it, e.g. in a text box).
    static IEnumerable<XElement> TextNodes(XElement p) =>
        p.Descendants(W + "t").Where(t => t.Ancestors(W + "p").First() == p);

    static int[] Starts(List<XElement> nodes)
    {
        var starts = new int[nodes.Count];
        for (int i = 0, pos = 0; i < nodes.Count; i++) { starts[i] = pos; pos += nodes[i].Value.Length; }
        return starts;
    }

    /// <summary>Deletes [index, index+length) across text nodes; returns the node/offset where the range began.</summary>
    static (int Node, int Offset) RemoveRange(List<XElement> nodes, int[] starts, int index, int length)
    {
        var (first, firstOffset) = Locate(starts, nodes, index);
        var (last, lastOffset) = Locate(starts, nodes, index + length - 1);
        var head = nodes[first].Value[..firstOffset];
        var tail = nodes[last].Value[(lastOffset + 1)..];
        if (first == last)
        {
            Set(nodes[first], head + tail);
        }
        else
        {
            Set(nodes[first], head);
            for (var i = first + 1; i < last; i++) Set(nodes[i], "");
            Set(nodes[last], tail);
        }
        return (first, firstOffset);
    }

    static (int Node, int Offset) Locate(int[] starts, List<XElement> nodes, int index)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (index >= starts[i] && index < starts[i] + nodes[i].Value.Length) return (i, index - starts[i]);
        throw new InvalidOperationException("Text position outside the paragraph.");
    }

    // "...judgment {{ADD_MISSING}}" → drop the space before the tag so the result reads "judgment, Azure".
    static void TrimEndBefore(List<XElement> nodes, int upTo)
    {
        for (var i = upTo; i >= 0; i--)
        {
            var trimmed = nodes[i].Value.TrimEnd();
            Set(nodes[i], trimmed);
            if (trimmed.Length > 0) return;
        }
    }

    static XElement CloneRun(XElement run, string text, AddedStyle? style)
    {
        var rPr = run.Element(W + "rPr") is { } props ? new XElement(props) : new XElement(W + "rPr");
        if (style is not null)
        {
            if (style.Bold) SetProp(rPr, "b", null);
            if (!string.IsNullOrWhiteSpace(style.ColorHex)) SetProp(rPr, "color", style.ColorHex.TrimStart('#'));
        }
        var t = new XElement(W + "t", text);
        t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        return new XElement(W + "r", rPr.HasElements ? rPr : null, t);
    }

    // Word expects run properties in schema order; insert in the right place rather than appending.
    static readonly string[] RunPropOrder =
    {
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow",
        "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern",
        "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs",
        "em", "lang", "eastAsianLayout", "specVanish", "oMath",
    };

    static void SetProp(XElement rPr, string name, string? val)
    {
        rPr.Element(W + name)?.Remove();
        var el = new XElement(W + name);
        if (val is not null) el.SetAttributeValue(W + "val", val);
        var rank = Array.IndexOf(RunPropOrder, name);
        var next = rPr.Elements().FirstOrDefault(e => Array.IndexOf(RunPropOrder, e.Name.LocalName) > rank);
        if (next is null) rPr.Add(el); else next.AddBeforeSelf(el);
    }

    static void Set(XElement t, string value)
    {
        t.Value = value;
        t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
    }
}
