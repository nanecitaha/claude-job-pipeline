# AGENTS.md: context for AI coding assistants

Read this before changing anything. It's written for an AI assistant (Claude Code, Cursor, Copilot, etc.) working
in this repo. Humans should start with `README.md` and `docs/GETTING-STARTED.md`.
(Claude Code: copy or symlink this file to `CLAUDE.md` so it loads automatically.)

## What this project is

A job-search pipeline that runs on one Windows PC:

1. **Claude desktop** (subscription, no API key) runs three skills in `skills/`:
   - `job-hunt` finds postings (builtin.com first, then the other sites in `hunt-config.md`) and saves them with
     their tech stack.
   - `job-apply` fills in applications.
   - `job-email-sync` reads Gmail and updates statuses.
2. **`JobPipeline.Mcp`**: a local stdio MCP server. It is the only way Claude reads or writes jobs.
3. **`JobPipeline.Worker`**: a .NET BackgroundService that tailors a resume (Word → PDF) for each new job.
4. **`JobPipeline.Dashboard`**: a local web app (http://localhost:5080) for viewing and editing jobs by hand.
5. **Storage**: everything lives in one SQLite file, `jobs.db`, in the data folder (default `C:\JobPipeline\data`).

```
 job-hunt ──add_jobs──►┐                    ┌──begin_apply_run / start_apply──► job-apply
                       │  JobPipeline.Mcp   │◄─mark_applied / flag_action / close_job
 job-email-sync ───────►│                    │
                       └────► JobStore ◄────┘
                                 │  ▲
                 jobs.db ◄───────┘  ├── JobPipeline.Worker  (claim → tailor → complete)
                                    └── JobPipeline.Dashboard (view / edit / add / delete)
```

## Golden rules

1. **All reads and writes go through `JobStore`** (`src/JobPipeline.Core/JobStore.cs`). Never open `jobs.db` with
   raw SQL from another project. `JobStore`:
   - runs each change as one `BEGIN IMMEDIATE` transaction in WAL mode, so the MCP server, worker and dashboard
     can run at the same time;
   - enforces the status state machine and writes a history entry for every change.
2. **Status changes follow the state machine below.** Add a new transition as a `JobStore` method with a test;
   don't set `job.Status` directly anywhere else.
3. **Never auto-submit an application that might already have been sent.** A job left in `Applying` by a
   crashed run goes to `ActionNeeded`, never back to `ReadyToApply`.
4. **Manual-apply companies** (FAANG-type, listed in `hunt-config.md`) are never auto-applied. They go to
   `ActionNeeded` with the resume path. Keep this guard in `BeginApplyRun` and `StartApply`.
5. **Keywords are copied exactly as written in the posting**: same spelling, case and hyphens. Never paraphrase
   or invent terms. The user's extraction rules are in `skills/job-hunt/SKILL.md`; keep them.
6. **The repo is public. Never commit personal data**: resumes, `applicant-profile.md`, `resume.md`,
   `appsettings.Local.json`, real names, emails or phone numbers. `.gitignore` already covers the known files.
7. **Test resumes have red highlighted additions and unchecked keywords.** Don't wire anything that submits
   them as real applications without the owner agreeing.
8. Only one NuGet package is used: `Microsoft.Data.Sqlite`. Ask before adding more. The MCP protocol is
   hand-written on `System.Text.Json` on purpose.

## Status state machine (`JobStatus` in `Models.cs`)

```
New → Tailoring → ReadyToApply → Applying → Applied → Interviewing | Rejected
                                    ↘ ActionNeeded / Skipped / Expired / Failed
```

| Actor            | Moves                                                                     |
|------------------|---------------------------------------------------------------------------|
| job-hunt         | creates `New` (duplicates refused: company+title, URL, or source+job id)  |
| Worker           | `New → Tailoring → ReadyToApply`; retries, then `Failed` (`LastError`)    |
| job-apply        | `ReadyToApply → Applying → Applied / ActionNeeded / Expired / Skipped`    |
| job-email-sync   | confirmation → `Applied`, interview → `Interviewing`, rejection → `Rejected` |
| Owner (via MCP)  | `resolve_action`: retry / applied / skip                                  |
| Owner (dashboard)| `SetStatus` to anything except `Tailoring` and `Applying` (pipeline-only)  |

`SetStatus` rules: `New` clears the resume, error and attempt count, so the job is re-tailored. `ReadyToApply`
requires `ResumePath`. `Applied` sets `DateApplied`.

## Repo map

```
src/JobPipeline.Core       Models.cs (Job, JobStatus, JobEdit, JobComment…), JobStore.cs (all DB logic), Dedupe.cs
src/JobPipeline.Mcp        McpServer.cs (stdio JSON-RPC), Tools.cs (tool definitions), Configs.cs (hunt/profile/resume md files)
src/JobPipeline.Worker     TailoringWorker.cs (poll loop), Tailoring.cs (ITailor, PipelineOptions, CopyMasterResumeTailor),
                           TechStackTailor.cs (v1), DocxTemplate.cs (docx edits), ResumeLocation.cs, PdfConverters.cs (Word / LibreOffice)
src/JobPipeline.Dashboard  Program.cs (minimal API), wwwroot/ (index.html, app.css, app.js: no framework, no build step)
skills/                    job-hunt, job-apply, job-email-sync SKILL.md (Claude desktop skills)
tools/JobPipeline.Probe    one-off check: can plain HTTP on this machine read job pages / ATS APIs (no deps)
tests/JobPipeline.SelfTest dependency-free test runner (store, state machine, concurrency, tailor)
tests/e2e                  mcp_e2e.py (MCP + worker processes), dashboard_e2e.py (dashboard API)
docs/GETTING-STARTED.md    end-user setup from a fresh Windows PC
```

### MCP tools (`Tools.cs`)
`get_config`, `add_jobs`, `find_jobs`, `get_job`, `list_jobs`, `pipeline_stats`, `begin_apply_run`, `start_apply`,
`mark_applied`, `flag_action`, `close_job`, `resolve_action`, `update_tech_stack`, `retailor_jobs`, `record_email`.
When you change a tool's name or arguments, update the skill that calls it in `skills/` and `tests/e2e/mcp_e2e.py`.
Tool results go into Claude's context on every call, so keep them small: `McpServer.Wire` writes compact JSON
without null fields, state changes return `Tools.Brief`, and `get_job` returns `Tools.Detail` unless asked for
history. Keep tool descriptions to a line or two; the rules for when and how to call a tool belong in the skills.

### Dashboard API (`Dashboard/Program.cs`)
| Endpoint | What it does |
|---|---|
| `POST /api/login` `{username,password,remember}`, `POST /api/logout`, `GET /api/me` | sign-in |
| `GET /api/info`, `GET /api/stats` | data folder and database path; job counts per status |
| `GET /api/jobs?status=&q=` | list jobs, optionally filtered by status and search text |
| `GET /api/jobs/{id}` | one job plus its job description (`{job, jdText}`) |
| `POST /api/jobs` | add a job by hand; returns 409 `{error,id}` if it's a duplicate |
| `PUT /api/jobs/{id}` | edit the job's fields (`JobEdit`) |
| `POST /api/jobs/{id}/status` | `{status, note}` |
| `POST /api/jobs/{id}/comments`, `DELETE …/comments/{cid}` | add or delete a comment |
| `PUT /api/jobs/{id}/description` | replace the job description |
| `DELETE /api/jobs/{id}` | delete the job |
| `GET /api/jobs/{id}/resume` | the tailored resume; only files inside the data folder are served |

**Sign-in** (`Program.cs`, `UserCommands.cs`, `Core/Users.cs`): every page and `/api/*` route except
`/login.html`, `/login.js`, `/app.css` and `POST /api/login` needs a signed-in cookie session.
- Users live in the `users` table (schema v3), with PBKDF2-SHA256 hashes (`PasswordHashing`). They're managed
  only from the CLI (`JobPipeline.Dashboard.exe user add|passwd|remove|list`); there's no sign-up page.
- Each request re-checks the user: the cookie carries the password-changed time, so `passwd` and `remove`
  end sessions.
- Non-GET `/api` calls must send `X-Requested-With: JobPipeline` (CSRF guard; `app.js` and `login.js` add it).
  New endpoints get all of this automatically through the gate middleware and `RequireAuthorization()` on the
  `/api` group; keep it that way.
- `POST /api/login` is rate-limited per IP (`Dashboard:LoginAttemptsPerMinute`). Forwarded headers are trusted
  only from loopback proxies.
- Data-protection keys are in `<DataDir>/dashboard-keys` (DPAPI-protected on Windows).
- Edits and comments record the signed-in username as `by`.

Business-rule errors (`PipelineException`, `ArgumentException`) become HTTP 400 `{error}` with a readable
message. Keep messages in plain language: they're shown to the user as written.

### Worker / tailoring
- `ITailor.TailorAsync(TailorRequest, ct) → TailorResult`. Pick one with `Pipeline:Tailor`: `TechStack` (v1) or
  `CopyMaster`. **A new tailor (AI, relevance filtering) plugs in as a new `ITailor`**, registered in
  `Worker/Program.cs`.
- Template tags in the user's `.docx`:
  - `{{ADD_MISSING}}` at the end of a tech-stack line. The posting's terms missing from that line are appended,
    in red bold by default (`HighlightAddedTerms`, `HighlightColor`, `HighlightFontSize`; 0 means unchanged).
  - `{{LOCATION:City, ST}}`. Remote jobs keep the given text; on-site and hybrid jobs get the job's city
    (`UpdateLocation`, `LocationFormat`).
- Output goes to one folder per job: `resumes/<company>-<title>-<shortid>/` containing `<ResumeFileName>.pdf`
  and `techstack.txt`. The path is saved in the `resume_path` and `resume_dir` columns.
- PDF conversion: `Word` (COM, needs a logged-in session) or `LibreOffice` (headless).
- Settings: `Worker/appsettings.json` → `Pipeline` section, overridden by `appsettings.Local.json` (gitignored).

## Database

- Table `jobs`: the full job as JSON in a `doc` column, plus indexed columns (status, company, dates, URLs,
  resume_path, resume_dir). Job descriptions are in the `job_descriptions` table. Schema version 2;
  migrations are in `JobStore` (see `MigrateResumeColumns`).
- When you add a field: add it to `Job` in `Models.cs`. Only add an indexed column (with a migration) if you
  need to query by it.
- A legacy `jobs.json` is imported on first start and renamed `jobs.json.imported`.

## Build and test

Needs the .NET 8 SDK and Python 3.12.

```bash
dotnet build JobPipeline.sln
dotnet run --no-build --project tests/JobPipeline.SelfTest   # must end "N passed, 0 failed"
python tests/e2e/mcp_e2e.py                                  # MCP server + worker end to end
python tests/e2e/dashboard_e2e.py                            # dashboard API end to end
```

CI (`.github/workflows/ci.yml`) runs all of the above on `windows-latest` and `ubuntu-latest`; on Linux it also
installs LibreOffice for the real PDF test. **Every change needs green CI.** Add a SelfTest case for every new
`JobStore` method or tailor behavior, and an e2e assertion for every new tool or endpoint.

Run locally:
```bash
dotnet run --project src/JobPipeline.Dashboard -- --Dashboard:DataDir=/tmp/jp-data   # http://localhost:5080
dotnet run --project src/JobPipeline.Worker                                         # uses Pipeline:DataDir
```

## Conventions

- C# 12 / .NET 8, nullable enabled, file-scoped namespaces, primary constructors where they read well.
- Keep it dependency-light. The dashboard front end is plain HTML/CSS/JS: no npm, no bundler.
- User-facing text: plain language, sentence case, says what happened and what to do next.
- Log lines are ASCII only (the Windows console shows Unicode arrows as garbage).
- Git: branch from the latest feature branch, open a PR, keep commits small and descriptive. Don't push to
  `main` directly.

## Branches (as of this writing)

`main` ← `sqlite-store` (PR #1) ← `implement-tailor-worker` (PR #2) ← `dashboard-app` (PR #3) ← `dashboard-auth` (PR #4). Each branch is
stacked on the one before it, and the newest one has everything. Branch from the newest until these are merged.

## Known gaps / good next work

1. **Relevance-aware tailoring (v2)**: today `{{ADD_MISSING}}` adds every missing posting term. Next: add only
   terms the candidate can defend (checked against `resume.md`), and optionally rewrite bullets. This is the
   owner's area, so agree the approach with them first.
2. **Fit scoring before tailoring**: the hunt filter is deliberately loose ("C# or .NET mentioned anywhere"), so
   let the worker or a skill skip weak matches.
3. **Dashboard**: pagination beyond 500 rows, bulk status changes, a CSV export, roles (read-only users), and
   changing your own password from the page.
4. **Daily summary** of what was applied, what's blocked on the owner, and upcoming interviews.
5. Running the worker as a Windows Service (`AddWindowsService` is stubbed in `Worker/Program.cs`).
