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
                             └──────► jobs.json ◄────┘
                                         ▲
                       claim / complete  │
                          JobPipeline.Worker (tailors resumes)
```

Claude never edits `jobs.json` directly. Every write goes through `JobStore`, which takes a cross-process file
lock, applies one allowed status change and writes atomically (`jobs.json.bak` keeps the previous version).
No NuGet packages: the MCP protocol is implemented on `System.Text.Json`, and the worker uses the ASP.NET Core
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
   dotnet run --project tests\JobPipeline.SelfTest            # 16 checks, should all pass
   python tests\e2e\mcp_e2e.py                                # MCP + worker end to end
   dotnet publish src\JobPipeline.Mcp    -c Release -o C:\JobPipeline\bin\mcp
   dotnet publish src\JobPipeline.Worker -c Release -o C:\JobPipeline\bin\worker
   ```
3. **Master resume**: save it as `C:\JobPipeline\master-resume.pdf` (or change the path in
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
5. **Fill the configs**: every `[fill]` in `applicant-profile.md` (apply refuses to run until they're done), and
   your site search URLs in `hunt-config.md`. builtin is already listed first.
6. **Run the worker** so it survives reboots: Task Scheduler → Create Task → trigger "At startup" →
   action `C:\JobPipeline\bin\worker\JobPipeline.Worker.exe`, "Run whether user is logged on or not".
   (Or as a real Windows Service: add `Microsoft.Extensions.Hosting.WindowsServices`, uncomment
   `AddWindowsService` in `Program.cs`, then `sc create`.)
7. **Skills**: save `skills/job-hunt`, `skills/job-apply`, `skills/job-email-sync` in Claude.
8. **Schedule** in Claude desktop on the VPS (keep the VM from sleeping and the app open):
   hunt 7:00, apply 9:00, email sync 18:00, weekdays. Run apply by hand the first week and watch it.

Checking on things from chat: "what's blocked on me?" (`list_jobs` ActionNeeded), "I applied to Acme myself"
(`resolve_action` applied), "retry the Globex one" (`resolve_action` retry), "pipeline stats".

## Bring your own tailoring

`CopyMasterResumeTailor` is a stand-in that copies the master resume per job, so the pipeline runs end to end
today. Replace it with your own `ITailor`:

```csharp
public interface ITailor
{
    // Write the resume under request.OutputDir, return its path. Throw to fail the attempt (retried, then Failed).
    Task<TailorResult> TailorAsync(TailorRequest request, CancellationToken ct);   // request.Job, request.JdText
}
```

and swap the registration in `src/JobPipeline.Worker/Program.cs`. Recommended shape: a `resume.json` bullet bank
with ids and skill tags → model returns bullet ids, order and wording → a plain-code validator rejects any tech
term or claim not in the bank → render PDF/DOCX.

## Layout

```
src/JobPipeline.Core     JobStore (lock, atomic writes, state machine), models, dedupe
src/JobPipeline.Mcp      stdio MCP server: 13 tools + config files
src/JobPipeline.Worker   tailoring BackgroundService + ITailor
tests/JobPipeline.SelfTest   dependency-free tests: transitions, crash recovery, dedupe, concurrency
tests/e2e                    MCP stdio + worker processes end to end (CI runs both on Windows and Linux)
skills/                  job-hunt, job-apply, job-email-sync
```

Data folder: `jobs.json`, `jobs.json.bak`, `jobs.lock`, `jds/{id}.txt`, `resumes/`, `hunt-config.md`,
`applicant-profile.md`. Open `jobs.json` to read any time; to hand-edit it, stop the worker and Claude first.

## Contributing

Issues and PRs welcome. Keep the store's rule: every write is one guarded status transition through
`JobStore`, with a self-test for it. `dotnet run --project tests/JobPipeline.SelfTest` and
`python tests/e2e/mcp_e2e.py` must pass (CI runs both on Windows and Linux).

## License

MIT
