using System.IO.Compression;
using JobPipeline.Core;
using JobPipeline.Worker;

/// <summary>Tests for tailor v1 ({{ADD_MISSING}} lines, highlight, PDF conversion).</summary>
public static class TailorTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed = 0, failed = 0;
        void Test(string name, Func<Task> body)
        {
            try { body().GetAwaiter().GetResult(); passed++; Console.WriteLine($"  PASS  {name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}"); }
        }

        Test("tailor: merge keeps your order, appends missing posting terms as written, ignores case only", () =>
        {
            var (all, added) = TechStackTailor.Merge(
                ["C#", ".NET 8", "SQL Server"],
                ["c#", "Azure", ".NET8", "SQL Server", "  ", "Azure", "Kubernetes"]);
            Eq("C#, .NET 8, SQL Server, Azure, .NET8, Kubernetes", string.Join(", ", all));
            Eq("Azure, .NET8, Kubernetes", string.Join(", ", added));
            return Task.CompletedTask;
        });

        Test("tailor: keeps each existing line, appends what's missing, removes the tag; other text untouched", () =>
        {
            var dir = NewDir();
            var template = Path.Combine(dir, "template.docx");
            WriteDocx(template,
                Para(Run("Jane Doe")),
                Para(Run("C#, .NET, RabbitMQ ", italic: true), Run("{{ADD_", italic: true), Run("MISSING}}", italic: true)),   // split like Word does
                Para(Run("Built APIs.")),
                Para(Run("Python, Flask, SQL{{ADD_MISSING}}", italic: true)));
            var output = Path.Combine(dir, "out.docx");
            var posting = new[] { "c#", "Azure", "sql", "Kubernetes" };
            var lines = DocxTemplate.AddMissing(template, output, e => TechStackTailor.Merge(e, posting).Added, null);
            Eq(2, lines.Count);
            Eq("C#, .NET, RabbitMQ", string.Join(", ", lines[0].Existing));
            Eq("Azure, sql, Kubernetes", string.Join(", ", lines[0].Added));
            Eq("c#, Azure, Kubernetes", string.Join(", ", lines[1].Added));   // "sql" already there as "SQL"
            Eq("Jane Doe\nC#, .NET, RabbitMQ, Azure, sql, Kubernetes\nBuilt APIs.\nPython, Flask, SQL, c#, Azure, Kubernetes\n",
                DocxTemplate.ReadText(output).Replace("\r\n", "\n"));
            return Task.CompletedTask;
        });

        Test("tailor: added terms are red + bold and keep the line's italics; existing terms aren't highlighted", () =>
        {
            var dir = NewDir();
            var template = Path.Combine(dir, "template.docx");
            WriteDocx(template, Para(Run("C#, SQL {{ADD_MISSING}}", italic: true)));
            var output = Path.Combine(dir, "out.docx");
            DocxTemplate.AddMissing(template, output, _ => ["Azure", "Kubernetes"], new AddedStyle(true, "FF0000"));
            var body = ReadBody(output);
            Eq(2, CountOf(body, "<w:b /><w:i /><w:color w:val=\"FF0000\" />"), "added terms: bold, italic kept, red, in Word's order");
            Contains(body, "<w:rPr><w:i /></w:rPr><w:t xml:space=\"preserve\">C#, SQL</w:t>");   // existing text unchanged
            Eq("C#, SQL, Azure, Kubernetes\n", DocxTemplate.ReadText(output).Replace("\r\n", "\n"));
            return Task.CompletedTask;
        });

        Test("tailor: nothing missing leaves the line as it was (tag removed)", () =>
        {
            var dir = NewDir();
            var template = Path.Combine(dir, "template.docx");
            WriteDocx(template, Para(Run("C#, Azure {{ADD_MISSING}}")));
            var output = Path.Combine(dir, "out.docx");
            var lines = DocxTemplate.AddMissing(template, output, e => TechStackTailor.Merge(e, ["azure"]).Added, new AddedStyle(true, "FF0000"));
            Eq(0, lines[0].Added.Count);
            Eq("C#, Azure\n", DocxTemplate.ReadText(output).Replace("\r\n", "\n"));
            Eq(false, ReadBody(output).Contains("FF0000"));
            return Task.CompletedTask;
        });

        Test("tailor: end to end with a fake converter writes the PDF and a report of what was added", async () =>
        {
            var (dir, opts, job) = Setup();
            var tailor = new TechStackTailor(opts, new CopyConverter());
            var result = await tailor.TailorAsync(new TailorRequest(job, "jd", dir), CancellationToken.None);
            Eq(true, File.Exists(result.ResumePath) && result.ResumePath.EndsWith(".pdf"), "pdf written");
            Eq(false, File.Exists(Path.ChangeExtension(result.ResumePath, ".docx")), "temp docx removed");
            var text = DocxTemplate.ReadText(result.ResumePath);   // fake converter copies the docx
            Contains(text, "C#, .NET 8, SQL Server, Azure, Kubernetes");
            Contains(text, "C#, ASP.NET MVC, SQL Server, Azure, Kubernetes");
            var report = File.ReadAllText(Path.Combine(dir, ResumeFiles.BaseName(job) + ".tech-added.txt"));
            Contains(report, "Line 1: added Azure, Kubernetes");
            Contains(result.Note ?? "", "2 tech-stack line(s), 2 term(s)");
        });

        Test("tailor: a template without {{ADD_MISSING}} fails clearly and leaves nothing behind", async () =>
        {
            var (dir, opts, job) = Setup();
            WriteDocx(opts.ResumeTemplatePath, Para(Run("No tags here")));
            var t = new TechStackTailor(opts, new CopyConverter());
            await Throws<InvalidOperationException>(() => t.TailorAsync(new TailorRequest(job, "jd", dir), default), "No {{ADD_MISSING}}");
            Eq(0, Directory.GetFiles(dir, "*.pdf").Length, "no pdf left behind");
        });

        Test("tailor: missing template is reported with the setting to fix", async () =>
        {
            var (dir, opts, job) = Setup();
            opts.ResumeTemplatePath = Path.Combine(dir, "nope.docx");
            var t = new TechStackTailor(opts, new CopyConverter());
            await Throws<FileNotFoundException>(() => t.TailorAsync(new TailorRequest(job, "jd", dir), default), "ResumeTemplatePath");
        });

        var soffice = FindSoffice();
        if (soffice is null)
        {
            Console.WriteLine("  SKIP  tailor: real PDF via LibreOffice (soffice not installed)");
        }
        else
        {
            Test("tailor: real PDF via LibreOffice", async () =>
            {
                var (dir, opts, job) = Setup();
                opts.LibreOfficePath = soffice;
                var t = new TechStackTailor(opts, new LibreOfficePdfConverter(opts));
                var result = await t.TailorAsync(new TailorRequest(job, "jd", dir), CancellationToken.None);
                var bytes = File.ReadAllBytes(result.ResumePath);
                Eq("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4), "pdf header");
                Eq(true, bytes.Length > 1000, "pdf has content");
            });
        }

        return (passed, failed);
    }

    // ── fixtures ──

    sealed class CopyConverter : IPdfConverter
    {
        public Task ConvertAsync(string docx, string pdf, CancellationToken ct) { File.Copy(docx, pdf, true); return Task.CompletedTask; }
    }

    static (string Dir, PipelineOptions Opts, Job Job) Setup()
    {
        var dir = NewDir();
        var template = Path.Combine(dir, "resume-template.docx");
        WriteDocx(template,
            Para(Run("Senior Software Engineer, Company A")),
            Para(Run("C#, .NET 8, SQL Server {{ADD_MISSING}}", italic: true)),
            Para(Run("Software Developer, Company B")),
            Para(Run("C#, ASP.NET MVC, SQL Server {{ADD_MISSING}}", italic: true)));
        var opts = new PipelineOptions { ResumeTemplatePath = template, ConversionTimeoutSeconds = 120 };
        var job = new Job
        {
            Id = Guid.NewGuid(), Company = "Acme Inc.", Title = "Senior Backend Engineer",
            TechStack = ["C#", "Azure", "sql server", "Kubernetes"],
        };
        return (dir, opts, job);
    }

    static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "jp-tailor-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    static string Run(string text, bool italic = false) =>
        $"<w:r>{(italic ? "<w:rPr><w:i/></w:rPr>" : "")}<w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</w:t></w:r>";

    static string Para(params string[] runs) => $"<w:p>{string.Concat(runs)}</w:p>";

    static void WriteDocx(string path, params string[] paragraphs)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        Add("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
            """);
        Add("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
            """);
        Add("word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            string.Concat(paragraphs) + "</w:body></w:document>");
    }

    static string ReadBody(string docx)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var r = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return r.ReadToEnd();
    }

    static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }

    static string? FindSoffice()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe") }
            : new[] { "/usr/bin/soffice", "/usr/local/bin/soffice", "/opt/homebrew/bin/soffice", "/Applications/LibreOffice.app/Contents/MacOS/soffice" };
        return candidates.FirstOrDefault(File.Exists);
    }

    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected <{expected}> but got <{actual}>");
    }

    static void Contains(string haystack, string needle)
    {
        if (!haystack.Contains(needle)) throw new Exception($"expected to find <{needle}> in <{haystack.Trim()}>");
    }

    static async Task Throws<TEx>(Func<Task> a, string contains) where TEx : Exception
    {
        try { await a(); }
        catch (TEx ex) when (ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception($"expected {typeof(TEx).Name} containing '{contains}'");
    }
}
