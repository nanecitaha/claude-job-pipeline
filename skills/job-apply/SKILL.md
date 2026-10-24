---
name: job-apply
description: Apply to job-pipeline jobs that are ReadyToApply, uploading each job's tailored resume, answering forms from the applicant profile and master resume, and hand anything that needs a person to the user with flag_action. Use when the user says "apply to the ready jobs", "run job apply", or a scheduled apply run fires.
---

# Job apply

Applies only to jobs the pipeline marks `ReadyToApply`. Never adds jobs. Every job ends this run in exactly one
of: `Applied`, `ActionNeeded`, `Expired` or `Skipped`.

## 1. Load the rules
Call `get_config` for `hunt` (apply rules, caps), `profile` and `resume`.
- If `profile` has `isSetUp: false`, stop and tell the user which `[fill]` fields to complete. Don't apply with gaps.
- If `resume` has `isSetUp: false`, continue, but answer from the profile only.

## Where answers come from
1. **`profile`** first: contact, eligibility, salary, notice, preferences, "Common questions", EEO.
2. **`resume`** (the master resume) second: work history, education, and experience questions.
   - "Have you used X?" / "Experience with X?": answer yes only if X appears in the master resume. If it doesn't,
     flag it; don't answer "no" on the user's behalf.
   - "Years of experience with X": add up the dates of the roles whose stack or bullets list X (end of a role
     marked "present" = today), round down to whole years. If the form's ranges or wording leave any doubt, flag.
   - Free-text answers (e.g. "describe your experience with ..."): built only from the resume's own bullets.
3. **Never** use the tailored PDF, its `.tech-added.txt` report, or the job description as a source of facts
   about the user. The tailored resume can list posting terms the user doesn't have.

Nothing else is a source. Anything neither file answers is flagged, never guessed.

## 2. Start the run
Call `begin_apply_run` with `limit` = `daily_cap` minus `appliedToday` (stop if that's 0).
- `interrupted`: jobs a previous run left mid-form. They're now ActionNeeded; list them in the report. Never
  retry them yourself; they may already have been submitted.
- `manualApply`: jobs at companies on the user's manual-apply list (e.g. FAANG). They're now ActionNeeded with
  the tailored resume path; list them in the report. Never apply to these companies, through any site or URL,
  even if one reaches you another way.
- `ready`: the queue for this run.

## 3. For each ready job, in order
1. If `otherApplicationsAtCompanyLast30Days` ≥ `per_company_cap_30_days`, call `close_job` (`skipped`,
   "per-company cap") and move on.
2. Call `start_apply` immediately before opening anything. It returns `applyUrl`, `listingUrl`, `isEasyApply` and
   `resumePath`. If `resumeFileExists` is false, `flag_action` ("tailored resume file missing") and move on.
3. Open `applyUrl` (else `listingUrl`).
   - Posting closed or gone → `close_job` with `expired`.
   - The page reveals an exclusion the listing hid (onsite, clearance, wrong stack) → `close_job` with `skipped`.
4. Stop and call `flag_action` the moment any of these appears. The reason must say exactly what the user has to
   do and where it stopped (site, step). For a question you couldn't answer, quote the question exactly and list
   its answer options, so the user can add the answer to the profile without opening the site, e.g.
   `Greenhouse, step 2: "How many years of Kubernetes experience?" options: 0-1 / 2-3 / 4+. Not in profile or resume.`
   - CAPTCHA or bot check
   - Account creation, a password, or a sign-in you weren't already in (never type a password)
   - Two-factor or email/SMS code
   - Assessment, coding test, video interview or questionnaire beyond the form
   - A question neither the profile nor the master resume answers, or a field in the profile's "never answer" list
   - Resume upload blocked or the file picker can't reach `resumePath`
   - LinkedIn Easy Apply while `linkedin_easy_apply` is off
   - The form still isn't submittable after about 25 actions
5. Fill the form:
   - Attach the tailored resume at `resumePath` first, so the form can autofill from it; then fix and fill the
     rest from the profile and master resume (see "Where answers come from").
   - Easy Apply: upload the tailored resume when the flow allows it; use the platform's saved resume only when
     it doesn't, and mention that in the `mark_applied` note.
   - Demographic/EEO questions: use the profile's answer.
   - Cover letter only if required: 3-4 sentences built from master-resume facts, aimed at the job description.
6. Before submitting, re-read every filled field and the attachment name. Then submit.
7. Only when the site shows a confirmation, call `mark_applied` with a short note (what confirmed it, which
   resume). If you submitted but no confirmation appeared, `flag_action` with "submitted, no confirmation shown;
   check email". The email sync will move it to Applied when the confirmation arrives.

Never enter payment details, SSN, date of birth or ID numbers, and never accept anything beyond the application's
own submit (no background-check or marketing consents that aren't required).

## 4. Report
Applied (company, title), flagged with each reason, manual-apply jobs handed over (company, title, resume path),
closed with reason, interrupted from earlier runs, and how many remain ReadyToApply.
