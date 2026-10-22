---
name: job-hunt
description: Find new job postings on the sites in the job pipeline's hunt config and save them to the pipeline with add_jobs. Never applies. Use when the user says "run the hunt", "find jobs", "look for new postings", or a scheduled hunt fires.
---

# Job hunt

Discovery only. Finds postings and saves them through the `job-pipeline` tools. The .NET worker tailors a resume
for each one later; the `job-apply` skill applies. Never open an application form, sign up, or apply here.

## 1. Load the rules
Call `get_config` with `name: "hunt"`. It holds target titles, similar titles, exclusions, location, the
experience rule, and the sites to search, in order. If the `job-pipeline` tools are missing, stop and say the
MCP server isn't connected.

## 2. Search each site, in the listed order
For each row in the Sites table:
1. Open the search URL in the browser. Read pages as text (not screenshots). Follow up to `pages` result pages.
2. From the result list, keep a listing only if its title matches a target or similar title and doesn't hit an
   exclusion. Judge on the title alone at this step.
3. Call `find_jobs` with the company and title; if a match scores 1.0, it's already tracked, skip it.
4. Open the listing and capture:
   - `jdText`: the full job description as plain text. Required; the worker tailors from it.
   - `applyUrl`: follow the apply link to the employer's own page (Greenhouse, Lever, Ashby, Workday, careers
     site) and record that URL. Keep the site's page as `listingUrl`.
   - `externalJobId`: the job/requisition id from the posting or the URL (e.g. `/jobs/4001`), if shown.
   - `location`, `isRemote`, `datePosted` (yyyy-MM-dd), `isEasyApply` (the site's own one-click apply).
   - Salary: `salaryText` exactly as written; `salaryMin`/`salaryMax`/`salaryCurrency` only when stated as numbers.
   - `techStack`: skills and knowledge a candidate can learn (languages, frameworks, tools, platforms, methods).
     Copy each term exactly as written in the posting: same spelling, hyphens, capitalization. Don't paraphrase,
     normalize or invent terms, and leave out activities, deliverables and company-specific phrases.
   - `matchedTitle`: which target or similar title it matched.
5. Re-check exclusions against the full description (primary stack not C#/.NET, clearance, onsite, wrong level).
   Apply the experience rule: don't exclude for asking more years than the user has when the config says so.
6. Collect kept postings and send them with `add_jobs` in batches of up to 25. Duplicates come back as
   `duplicate` and are fine.

If a site shows a CAPTCHA, a login wall the user hasn't set up, or blocks the page, skip that site for this run
and report it. Never try to get past it.

## 3. Company boards (optional)
For each slug under "Company boards", read the public JSON feed and treat each matching job like a listing:
- Greenhouse: `https://boards-api.greenhouse.io/v1/boards/{slug}/jobs?content=true`
- Lever: `https://api.lever.co/v0/postings/{slug}?mode=json` (EU boards: `api.eu.lever.co`)
- Ashby: `https://api.ashbyhq.com/posting-api/job-board/{slug}?includeCompensation=true`

## 4. Report
Call `pipeline_stats`, then give the user a short summary: added per site, duplicates, excluded with the main
reasons, and any site skipped and why.
