# Getting started (Windows, from scratch)

This guide takes you from a fresh Windows PC to Claude finding jobs and the worker making a tailored resume for
each one. No prior knowledge of the project needed. Copy the commands exactly; run them in **PowerShell**
(Start menu → type "PowerShell").

**How it fits together**

```
You ask Claude ──► Claude (job-hunt skill) finds jobs on job sites ──► saves them in jobs.db
                                                                          │
                                         Worker (runs on your PC) ◄───────┘
                                         makes a tailored resume PDF for each job
                                                                          │
You ask Claude ──► Claude (job-apply skill) fills applications ◄──────────┘
                   and hands anything it can't do back to you
```

| Folder | What's in it |
|---|---|
| `C:\claude-job-pipeline` | The code (downloaded from GitHub) |
| `C:\JobPipeline\bin` | The two programs you build: `mcp` (connects Claude to your job list) and `worker` (makes resumes) |
| `C:\JobPipeline\data` | Your stuff: job list (`jobs.db`), settings files, resumes |
| `C:\JobPipeline\resume-template.docx` | Your resume in Word, with a few tags added |

---

## Part 1 — Install (once, ~10 minutes)

### 1.1 Programs

```powershell
winget install Microsoft.DotNet.SDK.8
winget install Git.Git
```

Then **close PowerShell and open a new one** so the new programs are found.

You also need:

- **Claude desktop** — download from claude.ai and sign in.
- **Google Chrome** with the **Claude extension** installed and signed in to the same Claude account.
- **Microsoft Word** (to turn resumes into PDFs). No Word? Use LibreOffice instead (free): see 6.3.

### 1.2 Get the code

```powershell
cd C:\
git clone https://github.com/nanecitaha/claude-job-pipeline.git
cd C:\claude-job-pipeline
```

### 1.3 Check it works

```powershell
dotnet run --project tests\JobPipeline.SelfTest
```

The last line should say `... passed, 0 failed`. One `SKIP` line is fine.

### 1.4 Build the programs

```powershell
dotnet publish src\JobPipeline.Mcp    -c Release -o C:\JobPipeline\bin\mcp
dotnet publish src\JobPipeline.Worker -c Release -o C:\JobPipeline\bin\worker
dotnet publish src\JobPipeline.Dashboard -c Release -o C:\JobPipeline\bin\dashboard
```

Each should end with **Build succeeded**.

### 1.5 Create your data files

```powershell
C:\JobPipeline\bin\mcp\JobPipeline.Mcp.exe --data C:\JobPipeline\data
```

Wait two seconds, then press **Ctrl+C**. `C:\JobPipeline\data` now holds `hunt-config.md`,
`applicant-profile.md`, `resume.md` and `jobs.db`.

---

## Part 2 — Tell it about you (once, ~20 minutes)

Open each file in Notepad (or any editor). Anything written `[fill]` is a blank: replace the whole `[fill]`,
brackets included, with your answer.

### 2.1 `C:\JobPipeline\data\applicant-profile.md`

Your answers for application forms: name, email, phone, work authorization, salary, notice period and common
questions. **Claude answers forms only from this file and `resume.md`**; anything else is handed back to you.
Applying won't start while any `[fill]` is left.

### 2.2 `C:\JobPipeline\data\resume.md`

Paste your resume as plain text. Claude uses it to answer questions like "years of experience with C#". Only list
what you'd defend in an interview: anything here can become a "yes" on a form.

### 2.3 `C:\JobPipeline\data\hunt-config.md`

What to look for:

- **Keep** / **Exclude**: the only filters the hunt uses. Edit them in plain words.
- **Sites**: one row per search. Get the URL by searching on the site, setting its filters, and copying the
  address bar. Test it in the browser first.
- **Manual-apply companies**: jobs there are found and get a resume, but Claude never applies; you do.
- **Apply rules**: daily cap, per-company cap.

### 2.4 Your Word resume template

Open your resume in Word and add two kinds of tags, then save it as `C:\JobPipeline\resume-template.docx`:

| Type this | Where | What happens |
|---|---|---|
| `{{ADD_MISSING}}` | At the **end** of each tech-stack line, e.g. `C#, .NET, SQL Server {{ADD_MISSING}}` | The job's keywords your line is missing are added there |
| `{{LOCATION:Your City, ST}}` | Where your location is, e.g. `{{LOCATION:Irvine, CA}}` | Remote jobs keep your city; on-site/hybrid jobs show the job's city |

Keep the line's normal font and style on the tags; added text copies it. Then **open Word once by hand and
close it**, so no first-run pop-up can block the worker later.

### 2.5 Your personal worker settings

Create `C:\JobPipeline\bin\worker\appsettings.Local.json` in Notepad:

```json
{ "Pipeline": { "ResumeFileName": "Firstname_Lastname_Resume" } }
```

This file is yours: it isn't in the repo and rebuilding never overwrites it. Other settings you can add here are
in 6.3.

---

## Part 3 — Connect Claude (once, ~10 minutes)

### 3.1 Connect the job list to Claude desktop

1. **Quit Claude completely**: right-click its icon in the system tray (bottom right) → **Quit**.
2. Open `%APPDATA%\Claude\claude_desktop_config.json` in Notepad (paste that path into File Explorer's address
   bar; create the file if it doesn't exist).
3. If the file is empty or only `{}`, replace everything with:
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
   If it already has other settings, add the `"mcpServers": {...}` block next to them, with a comma between.
   Backslashes must be **doubled**, and nothing may come after the final `}`.
4. Check the file is valid:
   ```powershell
   Get-Content "$env:APPDATA\Claude\claude_desktop_config.json" -Raw | ConvertFrom-Json
   ```
   No red error = good.
5. Open Claude → **Settings → Developer**. `job-pipeline` should show **Running**.
6. Start a **new chat** and type: `show job-pipeline stats`. All zeros = connected.

### 3.2 Add the three skills

The skills tell Claude how to hunt, apply and read your email. In Claude, go to **Settings → Customize →
Skills** and add each folder from `C:\claude-job-pipeline\skills\`: `job-hunt`, `job-apply`, `job-email-sync`.
Type `/` in a chat to check they're listed.

### 3.3 Browser and email

- In Chrome (signed in to the Google account you use for job applications), open each job site once and allow
  it when the Claude extension asks. Sign in to sites that hide details behind a login (builtin.com does).
- In Claude, **Settings → Connectors**: connect **Gmail** (and **Google Calendar** for interviews) with the same
  Google account.

---

## Part 4 — Use it

### 4.1 Start the worker

Open PowerShell and run:

```powershell
C:\JobPipeline\bin\worker\JobPipeline.Worker.exe
```

**Leave this window open**: the worker only runs while it's open (Ctrl+C stops it). The first line should be:

```
Tailoring worker started. Data folder: C:\JobPipeline\data. Tailor: TechStackTailor (PDF via Word).
```

Different folder, or `CopyMasterResumeTailor`? See Part 5.

### 4.2 See and edit your jobs (dashboard)

The dashboard needs a sign-in. Create your user once (it asks for the password twice; at least 10 characters):

```powershell
C:\JobPipeline\bin\dashboard\JobPipeline.Dashboard.exe user add taha
```

Then start it in a second PowerShell window:

```powershell
C:\JobPipeline\bin\dashboard\JobPipeline.Dashboard.exe
```

Open **http://localhost:5080** and sign in. Leave the window open while you use it (Ctrl+C stops it).
It can run at the same time as the worker and Claude.

- The coloured bar at the top counts jobs per status; click one to show only those jobs, click again for all.
- The search box looks through company, title, location, tech stack and your comments.
- Click a job to open it: change its status (with a note), add comments, edit any field, fix the job
  description, see its history, open the tailored resume, or delete it.
- **Add a job** puts in one you found yourself. Leave it as New and the worker tailors a resume for it.
- Setting a job back to **New** makes the worker tailor a fresh resume. Tailoring and Applying can't be set
  by hand: the worker and Claude set those.

Users (run these with the dashboard running or not):

| To | Run |
|---|---|
| Add someone | `JobPipeline.Dashboard.exe user add <name>` |
| Change a password | `JobPipeline.Dashboard.exe user passwd <name>` (signs them out everywhere) |
| Remove someone | `JobPipeline.Dashboard.exe user remove <name>` (signs them out right away) |
| See who has access | `JobPipeline.Dashboard.exe user list` |

Users are stored in the `users` table of `jobs.db`. Passwords are never stored, only a salted, slow hash
(PBKDF2-SHA256, 600,000 rounds). Every user can see and change every job; there are no roles yet.

To open it from other devices, see 6.5.

Different data folder or port? Create `C:\JobPipeline\bin\dashboard\appsettings.Local.json`:

```json
{ "Urls": "http://localhost:5090", "Dashboard": { "DataDir": "D:\\jobs\\data" } }
```

### 4.3 What to say to Claude

Always in a **new chat** in Claude desktop.

| You want to | Say |
|---|---|
| Find jobs (first time: keep it small) | `run the job hunt, 1 page, max 5 jobs` |
| Find jobs | `run the job hunt` |
| See your jobs | `list my jobs` · `show job-pipeline stats` |
| See a job's links | `show the listing and apply URLs for the Acme job` |
| See a job's keywords | `show the tech stack for the Acme job` |
| Re-read keywords with new rules | `re-extract the tech stack for all jobs` |
| Rebuild resumes (after changing the template) | `re-tailor all ready jobs` |
| Try an application without sending it | `open the Acme application and fill it in, but stop before submitting` |
| Apply to ready jobs | `apply to the ready jobs` |
| See what needs you | `what's blocked on me?` |
| You applied yourself | `I applied to Acme myself` |
| Try a blocked job again | `retry Acme` |
| Drop a job | `skip Acme` |
| Update from your email | `sync job emails` |

### 4.4 Where things end up

```
C:\JobPipeline\data\resumes\acme-senior-backend-engineer-1a2b3c4d\
    Firstname_Lastname_Resume.pdf     ← tailored resume
    techstack.txt                     ← the job's keywords
```

To browse everything, install DB Browser for SQLite (`winget install DBBrowserForSQLite.DBBrowserForSQLite`),
open `C:\JobPipeline\data\jobs.db` **read-only**, and run:

```sql
SELECT company, title, status, location, listing_url, apply_url, resume_path FROM jobs;
```

### 4.5 Job statuses

| Status | Meaning |
|---|---|
| New | Found, waiting for the worker |
| ReadyToApply | Resume made |
| Applied | Application sent |
| ActionNeeded | Needs you: CAPTCHA, account, a question it couldn't answer, or a manual-apply company. `what's blocked on me?` shows why |
| Interviewing / Rejected | From your email |
| Failed | The worker couldn't make a resume 3 times; the reason is on the job |
| Skipped / Expired | Not a fit / posting gone |

### 4.6 Before real applications

Version 1 of the tailor adds **every** keyword from the posting that your line is missing, in **red bold** so
you can see them. That's for testing. Before sending real applications:

- turn off the highlight: add `"HighlightAddedTerms": false` to `appsettings.Local.json`, restart the worker,
  and say `re-tailor all ready jobs`;
- make sure every added keyword is true for you (or wait for keyword filtering).

---

## Part 5 — Updating and fixing things

### 5.1 Getting a new version

```powershell
# 1. Quit Claude (tray icon → Quit), stop the worker and the dashboard (Ctrl+C in their windows)
cd C:\claude-job-pipeline
git pull
dotnet publish src\JobPipeline.Worker -c Release -o C:\JobPipeline\bin\worker
dotnet publish src\JobPipeline.Mcp    -c Release -o C:\JobPipeline\bin\mcp
dotnet publish src\JobPipeline.Dashboard -c Release -o C:\JobPipeline\bin\dashboard
# 2. Reopen Claude, start the worker (and dashboard) again
```

Your data folder, `appsettings.Local.json` and settings files are never overwritten. New sections added to the
config templates don't appear in files you already have; the release notes say what to paste in.

### 5.2 Common problems

| Problem | Fix |
|---|---|
| `publish` says a file is locked / "being used by another process" | Claude or the worker is still running. Quit Claude from the tray, stop the worker, then `Get-Process JobPipeline.Mcp -ErrorAction SilentlyContinue \| Stop-Process` and publish again |
| Claude says it has no job-pipeline tools | Start a **new** chat. Still missing? Settings → Developer: is `job-pipeline` Running? If not, check the config file (3.1, step 4) and **View logs** |
| Your `job-pipeline` entry disappeared from the config | The file had a typo, so Claude reset it. Quit Claude, fix the file, check it (3.1 step 4), reopen |
| Worker shows a different data folder or `CopyMasterResumeTailor` | Old build. Run the worker publish command again |
| `Resume template not found` | The template isn't at `C:\JobPipeline\resume-template.docx` |
| `No {{ADD_MISSING}} tags found` | Add the tags to the template and save it |
| Word timeout / "Microsoft Word is not installed" | Open Word by hand once, close it, restart the worker. Or switch to LibreOffice (6.3) |
| The hunt finds nothing useful | Open the search URL from `hunt-config.md` in a browser; if the results look wrong there, fix the URL |
| A job is Failed | `show the Acme job` shows the reason; fix it, then `retry Acme` |

---

## Part 6 — Extras

### 6.1 Run it on a schedule

In Claude desktop, create scheduled tasks such as "run the job hunt" every morning and "sync job emails" in the
evening. The PC must be on, awake and running Claude, and the worker must be running.

### 6.2 Start the worker automatically

Task Scheduler → **Create Task** → Triggers: *At log on* → Actions: *Start a program*,
`C:\JobPipeline\bin\worker\JobPipeline.Worker.exe` → General: **Run only when user is logged on** (Word needs
it).

### 6.3 Worker settings

Put any of these in `C:\JobPipeline\bin\worker\appsettings.Local.json`, then restart the worker:

```json
{
  "Pipeline": {
    "ResumeFileName": "Firstname_Lastname_Resume",
    "HighlightAddedTerms": true,
    "HighlightColor": "FF0000",
    "HighlightFontSize": 0,
    "UpdateLocation": true,
    "LocationFormat": "{location}",
    "PdfConverter": "Word",
    "PollSeconds": 60
  }
}
```

| Setting | What it does |
|---|---|
| `ResumeFileName` | PDF name (employers see it). `{job}` inserts company-title |
| `HighlightAddedTerms` | `true` = added keywords and changed location in color + bold |
| `HighlightColor` | Hex color, `FF0000` = red |
| `HighlightFontSize` | Size of added text in points; `0` = same as the line |
| `UpdateLocation` | `false` = always keep your own city |
| `LocationFormat` | `{location}` = job's city; e.g. `{home} · Relocating to {location}` |
| `PdfConverter` | `Word` or `LibreOffice` (install with `winget install TheDocumentFoundation.LibreOffice`) |
| `PollSeconds` | How often the worker checks for new jobs (minimum 5) |

### 6.4 Safety

- Claude never types passwords, solves CAPTCHAs, or answers anything not in your profile or resume; those come
  back to you as ActionNeeded.
- Automating LinkedIn can get accounts restricted, so it's off by default.
- Your files (`applicant-profile.md`, `resume.md`, `appsettings.Local.json`) stay on your PC and are never part
  of the repo.

### 6.5 Host the dashboard (open it from your phone or laptop)

**Never open port 5080 to the internet as plain `http://`.** Passwords and your job data would travel
unencrypted. Keep the dashboard on `http://localhost:5080` and put one of these in front of it:

**Option A: Tailscale (simplest, private).** Only your own devices can reach it; nothing is public.
1. Install Tailscale on the VPS and on your phone/laptop, signed in to the same account.
2. On the VPS: `tailscale serve --bg 5080`
3. Open the `https://<vps-name>.<tailnet>.ts.net` address it prints, on any of your devices.

**Option B: your own domain with Caddy (public, automatic HTTPS).**
1. Point a DNS record (e.g. `jobs.yourdomain.com`, type A) at the VPS's public IP.
2. Open ports 80 and 443: in your VPS provider's firewall, and in Windows (PowerShell as admin):
   ```powershell
   New-NetFirewallRule -DisplayName "Caddy web" -Direction Inbound -Protocol TCP -LocalPort 80,443 -Action Allow
   ```
3. Install Caddy (`winget install CaddyServer.Caddy`), then create `C:\JobPipeline\Caddyfile`:
   ```
   jobs.yourdomain.com {
       reverse_proxy localhost:5080
   }
   ```
4. Run `caddy run --config C:\JobPipeline\Caddyfile`. It gets a Let's Encrypt certificate by itself. Open
   `https://jobs.yourdomain.com` and sign in.

**Option C: Cloudflare Tunnel** (public, no open ports; needs a domain on Cloudflare): run `cloudflared` pointed
at `http://localhost:5080`.

All three work without changing the dashboard: it reads the visitor's real address and HTTPS from the proxy
(only proxies on the same machine are trusted), marks the sign-in cookie Secure, and limits sign-in attempts
to 5 a minute per address.

**Start it with Windows**: Task Scheduler → Create Task → trigger "At startup" → action
`C:\JobPipeline\bin\dashboard\JobPipeline.Dashboard.exe`, "Run whether user is logged on or not". Do the same
for `caddy run --config C:\JobPipeline\Caddyfile` if you use Option B.

Sign-in settings (in `appsettings.Local.json`, under `"Dashboard"`): `SessionHours` (default 12: how long a
sign-in lasts without "Keep me signed in"), `LoginAttemptsPerMinute` (default 5).

