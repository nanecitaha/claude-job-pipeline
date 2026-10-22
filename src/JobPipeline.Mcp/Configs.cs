namespace JobPipeline.Mcp;

/// <summary>Editable config files in the data folder. Created with starter content on first run, never overwritten.</summary>
public static class Configs
{
    public static string FileFor(string name) => name.ToLowerInvariant() switch
    {
        "hunt" => "hunt-config.md",
        "profile" => "applicant-profile.md",
        _ => throw new ArgumentException("name must be 'hunt' or 'profile'"),
    };

    public static void EnsureDefaults(string dataDir)
    {
        Write(Path.Combine(dataDir, "hunt-config.md"), Hunt);
        Write(Path.Combine(dataDir, "applicant-profile.md"), Profile);
    }

    static void Write(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    const string Hunt = """
        # Hunt config
        Example values for a senior .NET search; replace with your own. Edit freely. Claude reads this at the start of every hunt and apply run.

        ## Target titles
        - Senior Backend Engineer (.NET / C#)
        - Senior .NET Developer
        - Senior Full Stack .NET Developer

        ## Similar titles (also accept)
        - Senior Software Engineer (when C#/.NET is the primary stack)
        - Lead .NET Developer
        - Staff Software Engineer (.NET)
        - Senior Back-End Developer
        - Senior Platform Engineer (.NET)
        - Senior C# Developer

        ## Exclude
        - Primary stack is not C#/.NET (Java, Go, Ruby, Python-only, Node-only, iOS, Android)
        - Junior, mid-level, intern, contract-to-hire under 6 months
        - Requires security clearance or US citizenship you can't meet
        - Onsite or hybrid outside your area

        ## Location
        - Remote, US

        ## Experience rule
        - Don't skip a posting only because it asks for 1-2 more years than you have.

        ## Sites (searched in this order)
        Paste each site's search URL with your filters already applied (remote, senior, posted recently).
        | name    | search url                                                         | pages |
        |---------|--------------------------------------------------------------------|-------|
        | builtin | https://builtin.com/jobs/remote/dev-engineering/net/senior         | 3     |

        ## Company boards (optional, read via their public JSON)
        - greenhouse:
        - lever:
        - ashby:

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

        ## Voluntary self-identification (EEO)
        - Answer: Decline to self-identify

        ## Never answer automatically (always flag_action)
        - SSN, date of birth, government ID, bank details
        - Background or reference check consent
        - Anything asking for a password
        """;
}
