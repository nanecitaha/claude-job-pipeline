---
name: job-email-sync
description: Match recent Gmail messages about applications (confirmations, rejections, interviews, assessments) to job-pipeline jobs and record them with record_email. Use when the user says "sync job emails", "check my email for job updates", or a scheduled email sync fires.
---

# Job email sync

Reads Gmail, writes only through `record_email`. Never sends, replies to, archives or labels mail.

## 1. Search
Gmail `search_threads` for the last 3 days (or the window the user gives):
`newer_than:3d (application OR applied OR "thank you for applying" OR "your application" OR interview OR
"next steps" OR unfortunately OR assessment OR "moving forward" OR candidate)`.
Scan snippets first; open a thread only when it looks like a real event. Re-running over the same days is safe:
the same thread and type is never recorded twice.

## 2. Classify
Keep:
- `confirmation`: "we received your application", ATS submission receipts
- `rejection`: "moving forward with other candidates", "unfortunately"
- `interview`: invites, scheduling requests, confirmed times, reschedules
- `assessment`: coding tests or questionnaires sent after applying

Ignore: job alerts and digests (LinkedIn, Indeed, builtin), "your application was viewed", newsletters, ATS
marketing, and cold recruiter outreach. Real events usually come from an ATS domain (greenhouse, lever,
ashbyhq, workday, icims, smartrecruiters, workable) or the company's own domain.

## 3. Match and record
For each event, work out the company and, when stated, the role title, then call `find_jobs`.
- One clear best match (score ≥ 0.8, and the role fits if the email names one) → `record_email` with the type,
  Gmail thread id, subject and received time. Pass `interviewDate` only for a specific time the user has
  confirmed, not for an invite still being scheduled.
- Several plausible matches, or none → don't guess. List it in the report with the sender and subject.

## 4. Report
Recorded events by type (company, title, new status), interviews with their dates first, and unmatched or
ambiguous emails for the user to resolve.
