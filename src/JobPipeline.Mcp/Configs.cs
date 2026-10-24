namespace JobPipeline.Mcp;

/// <summary>Editable config files in the data folder. Created with starter content on first run, never overwritten.</summary>
public static class Configs
{
    public static string FileFor(string name) => name.ToLowerInvariant() switch
    {
        "hunt" => "hunt-config.md",
        "profile" => "applicant-profile.md",
        "resume" => "resume.md",
        _ => throw new ArgumentException("name must be 'hunt', 'profile' or 'resume'"),
    };

    /// <summary>
    /// The "- Company" lines under the "## Manual-apply companies" heading of hunt-config.md (empty if absent).
    /// Read on every apply call, so edits take effect without restarting anything.
    /// </summary>
    public static IReadOnlyList<string> ManualApplyCompanies(string dataDir)
    {
        var path = Path.Combine(dataDir, "hunt-config.md");
        if (!File.Exists(path)) return Array.Empty<string>();
        var list = new List<string>();
        var inSection = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                var heading = line.TrimStart('#').Trim().ToLowerInvariant();
                inSection = heading.StartsWith("manual-apply") || heading.StartsWith("manual apply");
                continue;
            }
            if (!inSection || !line.StartsWith("- ")) continue;
            var name = line[2..];
            var comment = name.IndexOf('#');
            if (comment >= 0) name = name[..comment];
            name = name.Trim();
            if (name.Length > 0 && name != "[fill]") list.Add(name);
        }
        return list;
    }

    public static void EnsureDefaults(string dataDir)
    {
        Write(Path.Combine(dataDir, "hunt-config.md"), Hunt);
        Write(Path.Combine(dataDir, "applicant-profile.md"), Profile);
        Write(Path.Combine(dataDir, "resume.md"), Resume);
    }

    static void Write(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    const string Hunt = """
        # Hunt config
        Example values for a senior .NET search; replace with your own. Edit freely. Claude reads this at the start of every hunt and apply run.

        ## Keep
        The hunt keeps a posting when ALL of these hold, and applies no other filters of its own.
        - Role: any software engineering role (engineer, developer, lead, staff, architect, manager), any seniority
          or none stated
        - Stack: C# or .NET is mentioned anywhere in the job description (mixed-language roles are fine)
        - Location: anywhere in the US (remote, hybrid or onsite)

        ## Exclude
        - Internships, new-grad, junior or entry-level roles
        - Requires a security clearance you don't have

        ## Experience rule
        - Don't skip a posting only because it asks for 1-2 more years than you have.

        ## Sites (searched in this order)
        Paste each site's search URL with your filters already applied (e.g. keyword, posted recently).
        Test a URL in the browser first: some filters look applied but aren't (builtin's /net/ path segment, for
        one, returns general engineering jobs; its ?search= keyword works).
        | name    | search url                                                         | pages |
        |---------|--------------------------------------------------------------------|-------|
        | builtin | https://builtin.com/jobs/remote/dev-engineering/senior?search=C%23%20.NET | 3 |

        ## Company boards (optional, read via their public JSON)
        - greenhouse:
        - lever:
        - ashby:

        ## Manual-apply companies
        Never auto-applied. Their jobs are still found and tailored, then handed to you as ActionNeeded with the
        resume path. One company per line; "Amazon" also covers "Amazon Web Services", but not "Amazonia".
        - Meta
        - Facebook
        - Apple
        - Amazon
        - AWS
        - Netflix
        - Google
        - Alphabet
        - YouTube
        - Microsoft
        - Nvidia
        - OpenAI

        ## Apply rules
        - daily_cap: 10
        - per_company_cap_30_days: 2
        - linkedin_easy_apply: off      # automating LinkedIn risks account restriction
        - easy_apply_resume: upload_tailored   # fall back to the platform's saved resume only if upload isn't offered
        - cover_letter: only when required; 3-4 sentences from resume facts and the job description
        """;

    const string Profile = """
        # Applicant profile
        The ONLY source of answers for application forms. Anything not answered here → flag_action, never guess.
        Fill every [fill] before the first apply run.

        ## Contact
        - First name: [fill]
        - Last name: [fill]
        - Email: [fill]
        - Phone: [fill]
        - Location (city, state, ZIP): [fill]
        - LinkedIn: [fill]
        - GitHub / portfolio: [fill]

        ## Work
        - Current title: [fill]
        - Total years of experience: [fill]
        - Years of C#/.NET: [fill]
        - Earliest start date / notice period: [fill]

        ## Eligibility
        - Authorized to work in the US: [fill: yes/no]
        - Requires visa sponsorship now or in future: [fill: yes/no]
        - Willing to relocate: [fill: yes/no]

        ## Compensation
        - Desired base salary (USD): [fill]   # answer with this; if a form forces a single number use it

        ## Education
        - Degree, school, year: [fill]

        ## Common questions
        Answers to questions forms often ask. Add each new question Claude flags here, then "retry" the job.
        - How did you hear about us: Job board
        - Notice period: [fill]
        - Comfortable with on-call: [fill]
        - Willing to work Pacific time hours: [fill]
        - Open to contract roles: [fill]

        ## Voluntary self-identification (EEO)
        - Answer: Decline to self-identify

        ## Never answer automatically (always flag_action)
        - SSN, date of birth, government ID, bank details
        - Background or reference check consent
        - Anything asking for a password
        """;

    /// <summary>Marker the apply skill checks for: while it's present, resume.md is treated as not set up.</summary>
    public const string ResumePlaceholder = "[paste your master resume here]";

    const string Resume = """
        # Master resume
        Your real, untailored resume as plain text or Markdown: roles with dates, each role's tech stack, bullets,
        skills, education. Claude answers application questions from this (after applicant-profile.md), e.g.
        work history, "years of experience with X", "have you used X". It never reads the tailored PDFs.
        Only list what you can defend in an interview: anything here can become a "yes" on a form.

        [paste your master resume here]
        """;
}

