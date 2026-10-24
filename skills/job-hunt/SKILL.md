---
name: job-hunt
description: Find new job postings on the sites in the job pipeline's hunt config and save them to the pipeline with add_jobs. Never applies. Use when the user says "run the hunt", "find jobs", "look for new postings", or a scheduled hunt fires.
---

# Job hunt

Discovery only. Finds postings and saves them through the `job-pipeline` tools. The .NET worker tailors a resume
for each one later; the `job-apply` skill applies. Never open an application form, sign up, or apply here.

## 1. Load the rules
Call `get_config` with `name: "hunt"`. Its **Keep** and **Exclude** sections are the only filters: apply exactly
those and add none of your own (no extra seniority, location, job-function or "main stack" judgments). When a
posting is borderline, keep it; the user would rather review an extra job than miss one. The config also holds the
experience rule and the sites to search, in order. If the `job-pipeline` tools are missing, stop and say the MCP
server isn't connected.

## 2. Search each site, in the listed order
For each row in the Sites table:
1. Open the search URL in the browser. Read pages as text (not screenshots). Follow up to `pages` result pages.
2. From the result list, drop a listing on its title alone only when the title clearly hits an **Exclude** line
   (e.g. "Intern") or is plainly not a software engineering role. Everything else gets opened: titles rarely
   show the stack, so don't drop for language, seniority or location at this step.
3. Call `find_jobs` with the company and title; if a match scores 1.0, it's already tracked, skip it.
4. Open the listing and capture:
   - `jdText`: the full job description as plain text. Required; the worker tailors from it.
   - `applyUrl`: follow the apply link to the employer's own page (Greenhouse, Lever, Ashby, Workday, careers
     site) and record that URL. Keep the site's page as `listingUrl`. If the site hides the employer link behind
     its own sign-in and you aren't signed in, save the job with `listingUrl` only and mention it in the report.
   - `externalJobId`: the job/requisition id from the posting or the URL (e.g. `/jobs/4001`), if shown.
   - `location`, `isRemote`, `datePosted` (yyyy-MM-dd), `isEasyApply` (the site's own one-click apply).
   - Salary: `salaryText` exactly as written; `salaryMin`/`salaryMax`/`salaryCurrency` only when stated as numbers.
   - `techStack`: extract it from the full posting (title and company description included) with the rules in
     **Tech stack extraction** below, and send the keywords as a list in order of first appearance.
   - `matchedTitle`: the Keep rule it matched, in a few words (e.g. "C# mentioned, remote").
5. Check the full description against **Keep** and **Exclude** only (e.g. "C# or .NET mentioned anywhere" means
   one mention is enough, even in a "C# or Java" list). Apply the experience rule: don't exclude for asking more
   years than the user has when the config says so.
6. Collect kept postings and send them with `add_jobs` in batches of up to 25. Duplicates come back as
   `duplicate` and are fine.

Jobs at companies on the manual-apply list are still saved like any other: the user wants them found and
tailored, and applies to them personally. Don't exclude them.

If a site shows a CAPTCHA, a login wall the user hasn't set up, or blocks the page, skip that site for this run
and report it. Never try to get past it.

## Tech stack extraction
Used for every job's `techStack`. Read the whole posting, not a summary.

<task>
Read the whole posting, including the job title and the company description, and return every skill or knowledge keyword in it: things a candidate can learn or know. This includes languages, frameworks, libraries, tools, platforms, databases, infrastructure, methodologies, engineering practices, engineering qualities or concepts (e.g., "highly reliable software solutions", "technical constraints"), and business functions that exist in any industry (e.g., "analytics", "digital advertising").
</task>

<rules>
1. Copy every keyword exactly as written: same spelling, hyphens, slashes, symbols, spacing, and capitalization (except as rule 8 says). Never normalize, correct, or abbreviate. "C#.NET" stays "C#.NET". "Bazel/Pants/Buck" stays "Bazel/Pants/Buck". "Dynamo" stays "Dynamo".
2. Never add a keyword that is not in the text, even an obviously related one. The only word you may add is a shared head noun under rule 4.
3. Strip the wording around a skill and keep only the skill phrase:
   - Experience and proficiency wrappers, e.g. "Experience with", "Experience in", "Experienced in", "Expertise in", "Deep understanding of", "Deep knowledge and experience in", "Demonstrate working knowledge", "Programming experience in", "Production experience with", "Front-end experience in", "Take an active role in", "1+ years of rigorous experience building"
   - Leading filler: "key", "similar"
   - Trailing generic nouns: "tools", "concepts", "technologies", "projects", "experience"
   - Connecting verbs: "writing unit testable software" → unit testable software; "profiling and improving application performance" → profiling, application performance. Keep a verb when it is the skill itself: "designing software systems", "build consensus", "mentoring developers".
4. Split lists into separate keywords: "architecture, design, and code reviews" → architecture, design, code reviews; "deployment & monitoring" → deployment, monitoring. When the items share a head noun and each item plus that noun is a standard term, attach the noun to each: "unit, performance, and automation testing" → unit testing, performance testing, automation testing. Otherwise keep the phrase whole: "social and reputation management". Keep slash groups together as written: "SQL scripts/ETL jobs".
5. Leave out:
   - Company phrases that contain no skill once the company name is removed ("used across SpaceX", "improve how SpaceX operates"). When a skill is attached to a company name, drop the name and keep the skill: "CoStar's product architecture" → product architecture, "CoStar database standards" → database standards.
   - Industry or vertical terms that only describe the employer's market ("real estate information", "rental marketplace", "property listings").
   - Work products and activities that are not a skill or knowledge ("prototypes", "new applications", "web applications", "Facilitate discussions").
   - Vague phrases about users or problems ("users' problems").
   - Years of experience, degrees, location, onsite/remote, schedule, clearance, citizenship, salary, benefits.
6. List each keyword once, in the order it first appears.
7. If you cannot read the full posting word for word (e.g., a web fetch returned a summary), don't guess the wording: save the job with an empty techStack and list it in the report as "tech stack not extracted: full text unavailable".
8. Lowercase a word that is capitalized only because it starts a sentence or bullet ("Build consensus" → build consensus). Keep capitals that belong to the term ("Object Oriented design principles", "Agile/Scrum").
</rules>

<output_format>
The keywords, in order of first appearance, as the items of the `techStack` list (one keyword per item). When the
user asks to see a job's keywords, show them as one comma-separated line, with no headings, explanations, analysis
or counts.
</output_format>

<examples>
<example>
<input>
RESPONSIBILITIES:
* Develop highly reliable software solutions that are used across SpaceX
* Create new applications that improve how SpaceX operates
* Build prototypes to prove out key design concepts and quantify technical constraints
* Own all aspects of software engineering and product development
* Deep dive into your users' problems to find and create efficient solutions
* Bring your voice to the table in team collaborations on architecture, design, and code reviews
</input>
<output>
highly reliable software solutions, design concepts, technical constraints, software engineering, product development, efficient solutions, team collaborations, architecture, design, code reviews
</output>
</example>

<example>
<input>
PREFERRED SKILLS AND EXPERIENCE:
* Programming experience in Python, C#.NET, Go, Scala, Java, or similar object-oriented language
* 1+ years of rigorous experience building single page web applications
* Experience with version control, continuous integration, and continuous delivery concepts
* Expertise in designing software systems
* Deep understanding of testing, continuous integration, build, deployment & monitoring
* Expertise in profiling and improving application performance
* Experience with build systems (Make, Bazel/Pants/Buck, Gradle) and package management tools (pip, npm)
* Experience with UNIX-like operating systems and tools like Terraform, Ansible, Puppet for managing them
* Experience working with in-stream data processing using Apache Kafka, RabbitMQ, ActiveMQ, or similar technologies
* Production experience with Docker and Kubernetes
* Front-end experience in Angular, React, or similar JavaScript framework
* Database experience with PostgreSQL, SQL Server, or similar database technologies
</input>
<output>
Python, C#.NET, Go, Scala, Java, object-oriented language, single page web applications, version control, continuous integration, continuous delivery, designing software systems, testing, build, deployment, monitoring, profiling, application performance, build systems, Make, Bazel/Pants/Buck, Gradle, package management, pip, npm, UNIX-like operating systems, Terraform, Ansible, Puppet, in-stream data processing, Apache Kafka, RabbitMQ, ActiveMQ, Docker, Kubernetes, Angular, React, JavaScript framework, PostgreSQL, SQL Server
</output>
</example>

<example>
<input>
Senior Software Engineer (Java/Cloud) - Acme Lending
Acme is a leading provider of mortgage data, analytics, and online marketplaces. Its services include digital advertising and social and reputation management.
* Develop an in-depth understanding of Acme's platform architecture and frameworks.
* Build consensus among teams working on software development projects.
* Facilitate discussions with other Java developers.
* 5+ years of production development experience with Java
* Experienced in unit, integration, and load testing
</input>
<output>
Java/Cloud, analytics, online marketplaces, digital advertising, social and reputation management, platform architecture, frameworks, build consensus, software development, Java developers, production development, Java, unit testing, integration testing, load testing
</output>
</example>
</examples>

## Re-extracting saved jobs
When the user asks to re-extract tech stacks (e.g. after these rules changed): for each job they name (or every
job with `list_jobs` if they say all), call `get_job` with `includeJd: true`, extract from `jdText` with the rules
above, and save with `update_tech_stack`. Jobs not yet applied to are re-tailored automatically. Report each job
with its new keywords as one comma-separated line.

## 3. Company boards (optional)
For each slug under "Company boards", read the public JSON feed and treat each matching job like a listing:
- Greenhouse: `https://boards-api.greenhouse.io/v1/boards/{slug}/jobs?content=true`
- Lever: `https://api.lever.co/v0/postings/{slug}?mode=json` (EU boards: `api.eu.lever.co`)
- Ashby: `https://api.ashbyhq.com/posting-api/job-board/{slug}?includeCompensation=true`

## 4. Report
Call `pipeline_stats`, then give the user a short summary: added per site, duplicates, excluded with the main
reasons, and any site skipped and why.
