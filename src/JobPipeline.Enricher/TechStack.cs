using System.Text.RegularExpressions;

namespace JobPipeline.Enricher;

/// <summary>
/// Dictionary tech-stack extractor: finds known terms in a posting and returns each exactly as the posting writes
/// it (same spelling and capitalization), in order of first appearance, longest match first ("ASP.NET Core" beats
/// ".NET" at the same spot; ".NET" still counts where it appears on its own).
///
/// The term list is tech-terms.txt in the data folder, created with a starter list on first run and never
/// overwritten. One term per line. "# " starts a comment line. "=React" matches case-sensitively (so "react quickly"
/// doesn't count). "re:" starts a regular expression, e.g. re:\.NET\s?\d+ for ".NET 8".
/// </summary>
public sealed class TechStack
{
    public const string FileName = "tech-terms.txt";

    readonly List<Entry> _entries;

    sealed record Entry(string Term, bool CaseSensitive, Regex? Pattern);

    TechStack(List<Entry> entries) => _entries = entries;

    public int Count => _entries.Count;

    public static TechStack Parse(IEnumerable<string> lines)
    {
        var entries = new List<Entry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("# ") || line == "#") continue;
            if (line.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    entries.Add(new Entry(line, false, new Regex(line[3..], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(200))));
                }
                catch (ArgumentException)
                {
                    // A broken pattern in the user's file is skipped rather than stopping the enricher.
                }
            }
            else if (line.StartsWith('=') && line.Length > 1) entries.Add(new Entry(line[1..], true, null));
            else entries.Add(new Entry(line, false, null));
        }
        return new TechStack(entries);
    }

    /// <summary>Loads tech-terms.txt from the data folder, writing the starter list first if it's missing.</summary>
    public static TechStack Load(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) File.WriteAllText(path, Starter);
        return Parse(File.ReadAllLines(path));
    }

    public List<string> Extract(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var spans = new List<(int Start, int Length)>();
        foreach (var e in _entries)
        {
            if (e.Pattern is not null)
            {
                try
                {
                    foreach (Match m in e.Pattern.Matches(text))
                        if (m.Length > 0) spans.Add((m.Index, m.Length));
                }
                catch (RegexMatchTimeoutException)
                {
                    // Pathological pattern on this text: skip the term for this posting.
                }
                continue;
            }
            for (var i = Terms.Find(text, e.Term, 0, e.CaseSensitive); i >= 0; i = Terms.Find(text, e.Term, i + 1, e.CaseSensitive))
                spans.Add((i, e.Term.Length));
        }

        // Longest first, so "SQL Server" claims its spot before "SQL"; then keep spans that don't overlap.
        var taken = new List<(int Start, int Length)>();
        foreach (var s in spans.OrderByDescending(s => s.Length).ThenBy(s => s.Start))
            if (!taken.Any(t => s.Start < t.Start + t.Length && t.Start < s.Start + s.Length))
                taken.Add(s);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return taken.OrderBy(t => t.Start)
            .Select(t => text.Substring(t.Start, t.Length).Trim())
            .Where(t => t.Length > 0 && seen.Add(t))
            .ToList();
    }

    public const string Starter = """
        # Tech terms the enricher looks for in each posting (one per line). Matches keep the posting's own spelling.
        # "=Term" = case-sensitive. "re:" = regular expression. Edit freely; the enricher re-reads this file per job.

        # .NET
        C#
        C#.NET
        .NET
        .NET Core
        .NET Framework
        re:\.NET\s?(Core\s?)?\d+(\.\d+)?(?!\s*\+|\d|\s*years?)
        ASP.NET
        ASP.NET Core
        ASP.NET MVC
        ASP.NET Web API
        Web API
        MVC
        Blazor
        Razor
        Razor Pages
        WPF
        WinForms
        Windows Forms
        WCF
        Xamarin
        MAUI
        .NET MAUI
        Entity Framework
        Entity Framework Core
        EF Core
        Dapper
        LINQ
        ADO.NET
        SignalR
        gRPC
        MediatR
        AutoMapper
        xUnit
        NUnit
        MSTest
        Moq
        NuGet
        Visual Studio
        VB.NET
        F#
        Worker Services
        Hangfire
        Serilog
        Polly
        MassTransit
        NServiceBus
        IIS
        Windows Server
        PowerShell

        # Languages
        Java
        Python
        JavaScript
        TypeScript
        Node.js
        =Node
        Golang
        Rust
        Kotlin
        Scala
        Ruby
        PHP
        C++
        SQL
        T-SQL
        PL/SQL
        Bash

        # Front end
        =React
        React.js
        ReactJS
        Angular
        AngularJS
        Vue
        Vue.js
        Next.js
        Redux
        HTML
        HTML5
        CSS
        CSS3
        Sass
        Tailwind
        jQuery
        Bootstrap
        Webpack

        # Data
        SQL Server
        Microsoft SQL Server
        MS SQL
        Azure SQL
        PostgreSQL
        Postgres
        MySQL
        Oracle
        SQLite
        MongoDB
        Cosmos DB
        CosmosDB
        DynamoDB
        Redis
        Elasticsearch
        Cassandra
        Snowflake
        Databricks
        SSIS
        SSRS
        ETL
        NoSQL
        GraphQL

        # Messaging
        RabbitMQ
        Kafka
        Apache Kafka
        Azure Service Bus
        Service Bus
        Event Hubs
        Event Grid
        SQS
        SNS
        ActiveMQ

        # Cloud and infrastructure
        Azure
        Microsoft Azure
        AWS
        Amazon Web Services
        GCP
        Google Cloud
        Azure Functions
        Azure App Service
        Azure DevOps
        AKS
        EKS
        Lambda
        AWS Lambda
        EC2
        S3
        Docker
        Kubernetes
        Helm
        Terraform
        Bicep
        ARM templates
        Ansible
        CI/CD
        GitHub Actions
        Jenkins
        TeamCity
        Octopus Deploy
        Git
        GitHub
        GitLab
        Bitbucket
        Linux
        Nginx

        # Practices and architecture
        =REST
        =RESTful
        RESTful APIs
        REST APIs
        SOAP
        microservices
        event-driven
        event-driven architecture
        domain-driven design
        DDD
        CQRS
        =SOLID
        design patterns
        object-oriented
        OOP
        TDD
        unit testing
        integration testing
        Agile
        Scrum
        Kanban
        OAuth
        OAuth 2.0
        OpenID Connect
        JWT
        Identity Server
        Azure AD
        Entra ID
        OpenTelemetry
        Application Insights
        Datadog
        Splunk
        Grafana
        Prometheus
        New Relic
        Selenium
        Playwright
        Cypress
        Jira
        """;
}
