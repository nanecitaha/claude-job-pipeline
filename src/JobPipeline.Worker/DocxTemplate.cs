using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace JobPipeline.Worker;

/// <summary>
/// Fills <c>{{TECH_STACK:key}}</c> placeholders in a Word (.docx) template and saves a copy.
/// Uses only the built-in zip and XML libraries: a .docx is a zip whose body is word/document.xml.
///
/// Word often splits what you typed into several "runs" (spell-check, formatting, autocorrect), so a
/// placeholder may be spread over several text nodes. Replacement works on each paragraph's combined text and
/// keeps the formatting of the run where the placeholder starts. Everything else in the file is untouched.
/// </summary>
public static partial class DocxTemplate
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string BodyPart = "word/document.xml";

    [GeneratedRegex(@"\{\{\s*TECH_STACK\s*:\s*([A-Za-z0-9_\-]+)\s*\}\}")]
    public static partial Regex Placeholder();

    /// <summary>
    /// Copies <paramref name="templatePath"/> to <paramref name="outputPath"/>, replacing each placeholder with
    /// <paramref name="resolve"/>(key). Returns the keys found, in document order.
    /// </summary>
    public static IReadOnlyList<string> Fill(string templatePath, string outputPath, Func<string, string> resolve)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Resume template not found: '{templatePath}'.");

        File.Copy(templatePath, outputPath, overwrite: true);
        var found = new List<string>();
        try
        {
            using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Update);
            var entry = zip.GetEntry(BodyPart)
                        ?? throw new InvalidDataException($"'{templatePath}' is not a Word document (no {BodyPart}).");

            XDocument doc;
            using (var s = entry.Open()) doc = XDocument.Load(s, LoadOptions.PreserveWhitespace);

            foreach (var p in doc.Descendants(W + "p").ToList())
                ReplaceInParagraph(p, resolve, found);

            entry.Delete();
            var fresh = zip.CreateEntry(BodyPart, CompressionLevel.Optimal);
            using var o = fresh.Open();
            doc.Save(o, SaveOptions.DisableFormatting);
        }
        catch
        {
            File.Delete(outputPath);   // never leave a half-written resume behind
            throw;
        }
        return found;
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

    static void ReplaceInParagraph(XElement p, Func<string, string> resolve, List<string> found)
    {
        var nodes = TextNodes(p).ToList();
        if (nodes.Count == 0) return;

        var starts = new int[nodes.Count];
        var sb = new StringBuilder();
        for (var i = 0; i < nodes.Count; i++) { starts[i] = sb.Length; sb.Append(nodes[i].Value); }
        var text = sb.ToString();

        var matches = Placeholder().Matches(text);
        if (matches.Count == 0) return;
        foreach (Match m in matches) found.Add(m.Groups[1].Value);

        // Right to left, so earlier matches' positions stay valid while later ones are rewritten.
        foreach (var m in matches.Reverse())
        {
            var replacement = resolve(m.Groups[1].Value);
            var (first, firstOffset) = Locate(starts, m.Index);
            var (last, lastOffset) = Locate(starts, m.Index + m.Length - 1);

            var head = nodes[first].Value[..firstOffset];
            var tail = nodes[last].Value[(lastOffset + 1)..];
            if (first == last)
            {
                Set(nodes[first], head + replacement + tail);
            }
            else
            {
                Set(nodes[first], head + replacement);
                for (var i = first + 1; i < last; i++) Set(nodes[i], "");
                Set(nodes[last], tail);
            }
        }
    }

    // Text nodes that belong to this paragraph (not to a paragraph nested inside it, e.g. in a text box).
    static IEnumerable<XElement> TextNodes(XElement p) =>
        p.Descendants(W + "t").Where(t => t.Ancestors(W + "p").First() == p);

    static (int node, int offset) Locate(int[] starts, int index)
    {
        var i = Array.BinarySearch(starts, index);
        if (i < 0) i = ~i - 1;
        while (i + 1 < starts.Length && starts[i + 1] == starts[i] && starts[i + 1] <= index) i++;   // skip empty nodes
        return (i, index - starts[i]);
    }

    static void Set(XElement t, string value)
    {
        t.Value = value;
        t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
    }
}
