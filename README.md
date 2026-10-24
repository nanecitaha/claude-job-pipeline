# JobPipeline

A job-search pipeline for Claude desktop: Claude finds postings on the sites you choose and fills in
applications, a .NET worker prepares a resume per job, and one shared store keeps every job's status
consistent across them. Anything that needs a person (CAPTCHA, account creation, an unanswerable question)
is handed back to you with the reason instead of guessed.

Runs on a Claude subscription (Claude desktop + this local MCP server); no API key needed.

> **Use responsibly.** You are responsible for following each job site's terms of service. Automating some
> sites (LinkedIn in particular) can get accounts restricted; it's off by default. Start in review mode and
> read what gets submitted in your name.

```
 job-hunt skill ──add_jobs──►┐                       ┌──begin_apply_run / start_apply──► job-apply skill
                             │   job-pipeline MCP    │◄─mark_applied / flag_action / close_job
 job-email-sync ─record_email►│  (JobPipeline.Mcp)   │
                             └──────► jobs.db ◄──────┘
                                         ▲
                       claim / complete  │
                          JobPipeline.Worker (tailors resumes)
```

Everything lives in one SQLite database, `jobs.db`. Every write goes through `JobStore` as a single
`BEGIN IMMEDIATE` transaction that applies one allowed status change, so the MCP server, the worker and any
web app can run at the same time without losing updates; WAL mode lets readers query while a write runs.
Each row stores the full job as JSON plus indexed columns (status, company, dates, URLs) for querying.
The only package is `Microsoft.Data.Sqlite`: the MCP protocol is implemented on `System.Text.Json`, and the worker uses the ASP.NET Core
shared framework.

## Status flow

```
New → Tailoring → ReadyToApply → Applying → Applied → Interviewing | Rejected
                                     ↘ ActionNeeded / Skipped / Expired / Failed
```

| Flag-style field      | Now                                                        |
|-----------------------|------------------------------------------------------------|
| readyToApply          | `status == ReadyToApply`                                   |
| isApplied             | `status` in Applied, Interviewing, Rejected                |
| actionNeeded          | `status == ActionNeeded` (reason in `actionDesc`)          |
| isInterviewScheduled  | `status == Interviewing` (+ `interviewDate` once confirmed) |
| isEmailReceived       | `emails[]` (type, threadId, subject, receivedAt)           |

Who moves what:

| Actor              | Transitions                                                                   |
|--------------------|-------------------------------------------------------------------------------|
| job-hunt           | creates `New` (duplicates by company+title, URL or source+job id are refused) |
| Worker             | `New → Tailoring → ReadyToApply`; failures retry, then `Failed`               |
| job-apply          | `ReadyToApply → Applying → Applied / ActionNeeded / Expired / Skipped`        |
| job-email-sync     | confirmation → `Applied`, interview → `Interviewing`, rejection → `Rejected`  |
| You (via Claude)   | `ActionNeeded / Failed →` retry, applied, or skip (`resolve_action`)          |

A job left in `Applying` by a crashed run is moved to `ActionNeeded` at the start of the next run, never retried,
because it may already have been submitted.

## Setup (Windows)

1. **.NET 8 SDK**: `winget install Microsoft.DotNet.SDK.8`
2. **Build**, from the repo folder:
   ```powershell
   dotnet run --project tests\JobPipeline.SelfTest            # 24 checks (LibreOffice one skipped if not installed)
   python tests\e2e\mcp_e2e.py                                # MCP + worker end to end
   dotnet publish src\JobPipeline.Mcp    -c Release -o C:\JobPipeline\bin\mcp
   dotnet publish src\JobPipeline.Worker -c Release -o C:\JobPipeline\bin\worker
   ```
3. **Resume**: set up the tailor (see [Tailoring](#tailoring)); for `CopyMaster`, save it as `C:\JobPipeline\master-resume.pdf` (or change the path in
   `C:\JobPipeline\bin\worker\appsettings.json`, which also sets the data folder `C:\JobPipeline\data`).
4. **Connect Claude desktop**: add this to `claude_desktop_config.json` (Claude desktop's developer settings
   open it), then fully quit and reopen the app:
   ```json
   {
     "mcpServers": {
       "job-pipeline": {
         "command": "C:\\JobPipeline\\bin\\mcp\\JobPipeline.Mcp.exe",
         "args": ["--data", "C:\\JobPipeline\\data"]
       }
     }
   }
   ```
   First start creates `C:\JobPipeline\data` with `hunt-config.md` and `applicant-profile.md`.
5. **Fill the configs** in the data folder:
   - `applicant-profile.md`: every `[fill]` (apply refuses to run until they're done).
   - `resume.md`: paste your **master** (untailored) resume as plain text or Markdown. After the profile, it's
     Claude's source for work history and "years of experience with X" questions. Only list what you'd defend
     in an interview. It stays in your data folder; don't commit it.
   - `hunt-config.md`: your site search URLs (builtin is already listed first), and the **Manual-apply
     companies** list (FAANG etc. by default). Jobs there are still found and tailored, but never auto-applied:
     they go to ActionNeeded with the resume path so you apply yourself. Edits take effect on the next apply run.
6. **Run the worker** so it survives reboots: Task Scheduler → Create Task → trigger "At startup" →
   action `C:\JobPipeline\bin\worker\JobPipeline.Worker.exe`. With `PdfConverter: Word`, choose
   "Run only when user is logged on" (Word needs a logged-in session); with `LibreOffice`, "Run whether user is
   logged on or not" works. For a quick test, just run the exe in a console.
   (Or as a real Windows Service: add `Microsoft.Extensions.Hosting.WindowsServices`, uncomment
   `AddWindowsService` in `Program.cs`, then `sc create`.)
7. **Skills**: save `skills/job-hunt`, `skills/job-apply`, `skills/job-email-sync` in Claude.
8. **Schedule** in Claude desktop on the VPS (keep the VM from sleeping and the app open):
   hunt 7:00, apply 9:00, email sync 18:00, weekdays. Run apply by hand the first week and watch it.

Checking on things from chat: "what's blocked on me?" (`list_jobs` ActionNeeded), "I applied to Acme myself"
(`resolve_action` applied), "retry the Globex one" (`resolve_action` retry), "pipeline stats".

## Tailoring

The worker runs one tailor, chosen with `Pipeline:Tailor` in `appsettings.json`:

| `Tailor`     | What it does                                                                               |
|--------------|--------------------------------------------------------------------------------------------|
| `TechStack`  | **v1 (test).** Adds the job's missing tech terms to your resume's tech lines, saves a PDF. |
| `CopyMaster` | Copies `MasterResumePath` unchanged (no tailoring).                                        |

### Tech-stack tailor (v1)

Keeps your resume's tech-stack lines as they are and appends the job's tech-stack terms (as stored by the hunt)
that each line is missing:

```
Your line:   C#, .NET, RabbitMQ, SQL Server {{ADD_MISSING}}
Job wants:   C#, Azure, Kubernetes
PDF shows:   C#, .NET, RabbitMQ, SQL Server, Azure, Kubernetes     (added terms in red bold)
```

- Your terms stay exactly as written; posting terms are appended in the posting's own spelling.
- Matching ignores case only: `c#` = `C#`, but `.NET 8` and `.NET8` are different terms.
- Added terms are **bold and red** while `Pipeline:HighlightAddedTerms` is `true`, so they're easy to spot in
  test PDFs. `HighlightColor` sets the color (hex, `FF0000` = red) and `HighlightFontSize` the size in points
  (e.g. `10.5`; `0` = same as the line). Set `HighlightAddedTerms` to `false` for normal-looking output.
- Each job gets its own folder under `data\resumes\`:
  ```
  resumes\draftkings-senior-platform-engineer-2c0453c6\
    Resume.pdf
    techstack.txt        <- the job's tech stack as captured by the hunt, one comma-separated line
  ```
  The file name comes from `Pipeline:ResumeFileName` (e.g. `Jane_Doe_Resume`; employers see it). The folder keeps
  jobs apart, so every job can use the same name. `{job}` in the name is replaced with company-title if you want it.
- **Location:** type `{{LOCATION:Irvine, CA}}` (your own city) where your location goes. Remote jobs, and jobs
  whose location is unclear, keep it; on-site and hybrid jobs show the job's city and state instead (e.g.
  `Boston, MA (Hybrid)` → `Boston, MA`), highlighted like added terms. `Pipeline:UpdateLocation: false` turns this
  off; `Pipeline:LocationFormat` sets the wording, e.g. `"{home} · Relocating to {location}"`.
- Changed the template or settings? Ask Claude to *"re-tailor all ready jobs"* (or name specific ones): they go
  back to the worker for a fresh resume.

> v1 adds **every** missing posting term, including ones you may not have. Use it to test the pipeline; don't
> send these resumes on real applications until relevance filtering exists.

Setup:

1. **Template**: open your resume in Word and type `{{ADD_MISSING}}` at the **end** of each tech-stack line
   (keep the line itself). Save it as `C:\JobPipeline\resume-template.docx`. No code changes needed.
2. **PDF converter**: `Pipeline:PdfConverter`
   - `Word`: Microsoft Word starts hidden for each resume, converts, and quits (a few seconds; no window).
     Needs a logged-in session, so run the worker from a console or a scheduled task set to
     *Run only when user is logged on*, not as a service. Open Word by hand once first.
   - `LibreOffice`: headless and free; works on a server with nobody logged in.
     Install it, or set `Pipeline:LibreOfficePath` to `soffice.exe`.
3. `Pipeline:KeepDocx: true` keeps the filled `.docx` next to the PDF while you tune the template.
4. **Personal settings** (your name in `ResumeFileName`, other paths): put them in
   `C:\JobPipeline\bin\worker\appsettings.Local.json`, which overrides `appsettings.json`, isn't in the repo,
   and survives re-publishing:
   ```json
   { "Pipeline": { "ResumeFileName": "Jane_Doe_Resume" } }
   ```

Errors (template missing, no `{{ADD_MISSING}}` tags, conversion failure) fail the attempt with the reason in the
job's `lastError`; after `MaxTailorAttempts` the job goes to `Failed`.

### Bring your own tailor

```csharp
public interface ITailor
{
    // Write the resume under request.OutputDir, return its path. Throw to fail the attempt (retried, then Failed).
    Task<TailorResult> TailorAsync(TailorRequest request, CancellationToken ct);   // request.Job, request.JdText
}
```

Add your class and a case for it in the `Pipeline:Tailor` switch in `src/JobPipeline.Worker/Program.cs`.

## Layout

```
src/JobPipeline.Core     JobStore (lock, atomic writes, state machine), models, dedupe
src/JobPipeline.Mcp      stdio MCP server: 13 tools + config files
src/JobPipeline.Worker   tailoring BackgroundService, ITailor, tech-stack tailor (v1), Word/LibreOffice PDF
tests/JobPipeline.SelfTest   dependency-free tests: transitions, crash recovery, dedupe, concurrency
tests/e2e                    MCP stdio + worker processes end to end (CI runs both on Windows and Linux)
skills/                  job-hunt, job-apply, job-email-sync
```

Data folder: `jobs.db` (+ its `-wal`/`-shm` files), `resumes/`, `hunt-config.md`, `applicant-profile.md`, `resume.md`.
Browse it with any SQLite tool (DB Browser for SQLite, `sqlite3`); for edits, stop the worker and Claude first or
go through the MCP tools. Back it up with `sqlite3 jobs.db ".backup jobs-backup.db"` (safe while running).
Upgrading from the earlier `jobs.json` store: the first start imports it and renames it `jobs.json.imported`.

## Contributing

Issues and PRs welcome. Keep the store's rule: every write is one guarded status transition through
`JobStore`, with a self-test for it. `dotnet run --project tests/JobPipeline.SelfTest` and
`python tests/e2e/mcp_e2e.py` must pass (CI runs both on Windows and Linux).

## License

MIT
