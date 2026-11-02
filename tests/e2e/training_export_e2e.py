"""The training data must be identical whichever way it's made: from jobs.db (C#, export-training) or from a
hiring.cafe CSV (training/csv_to_training.py). Posts jobs through the dashboard, exports them, rebuilds the same jobs
in Python and compares; then merges a CSV in and checks dedupe, filtering and the agreement report.
Run from the repo root after `dotnet build`:  python tests/e2e/training_export_e2e.py"""
import http.cookiejar, io, json, os, socket, subprocess, sys, tempfile, time, urllib.error, urllib.request
from contextlib import redirect_stdout

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "training"))
import csv_to_training as conv  # noqa: E402

DATA = tempfile.mkdtemp(prefix="jp-train-")
DLL = os.path.join("src", "JobPipeline.Dashboard", "bin", "Debug", "net8.0", "JobPipeline.Dashboard.dll")
with socket.socket() as s:
    s.bind(("127.0.0.1", 0))
    BASE = f"http://127.0.0.1:{s.getsockname()[1]}"

# Tricky cases for the span rules: longest first, word boundaries, punctuation, repeats, CRLF, missing terms.
JOBS = [
    {"company": "Acme Inc.", "title": "Senior .NET Engineer", "listingUrl": "https://builtin.com/job/acme",
     "applyUrl": "https://boards.greenhouse.io/acme/jobs/1?gh_jid=1",
     "jdText": "We build with C#, ASP.NET Core and Azure.\r\nC# again; JavaScript isn't Java. Go, not Google.",
     "techStack": ["C#", "ASP.NET Core", ".NET", "Azure", "Java", "Go", "Kubernetes"]},
    {"company": "Hooli", "title": "Staff Engineer (C++/Rust)", "listingUrl": "https://builtin.com/job/hooli",
     "jdText": "C++, Rust and CI/CD. Node.js/Express services. T-SQL on SQL Server 2019.",
     "techStack": ["C++", "Rust", "CI/CD", "Node.js", "Express", "T-SQL", "SQL Server", "SQL Server 2019"]},
]


def cli(*words, password=None):
    r = subprocess.run(["dotnet", DLL, *words, f"--Dashboard:DataDir={DATA}"], input=password,
                       capture_output=True, text=True, timeout=120)
    assert r.returncode == 0, r.stdout + r.stderr
    return r.stdout


cli("user", "add", "tester", password="correct horse battery\n")
app = subprocess.Popen(["dotnet", DLL, f"--urls={BASE}", f"--Dashboard:DataDir={DATA}"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
browser = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def post(path, body):
    req = urllib.request.Request(BASE + path, method="POST", data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", "X-Requested-With": "JobPipeline"})
    with browser.open(req, timeout=30) as r:
        return json.loads(r.read() or "null")


try:
    for _ in range(100):
        try:
            post("/api/login", {"username": "tester", "password": "correct horse battery"})
            break
        except (urllib.error.URLError, ConnectionError):
            time.sleep(0.2)
    else:
        raise SystemExit("dashboard did not start")
    ids = [post("/api/jobs", job)["id"] for job in JOBS]
finally:
    app.terminate()
    app.wait(timeout=10)

db_export = os.path.join(DATA, "db.jsonl")
report = cli("export-training", db_export, "0.5")
assert "Wrote 2 jobs" in report, report
exported = {e["id"]: e for e in map(json.loads, open(db_export, encoding="utf-8"))}

# 1. Same jobs rebuilt in Python: identical text, spans and not-found terms.
for job_id, job in zip(ids, JOBS):
    cs = exported[job_id]
    py = conv.build(job_id, job["company"], job["title"], job["jdText"], job["techStack"])
    for field in ("text", "techStack", "spans", "notFound"):
        assert cs[field] == py[field], f"{job['company']} {field} differs:\n C#: {cs[field]}\n Py: {py[field]}"
    assert cs["labelSource"] == "claude" and cs["listingUrl"] == job["listingUrl"]
acme = exported[ids[0]]
assert [s["term"] for s in acme["spans"]] == [".NET", "C#", "ASP.NET Core", "Azure", "C#", "Java", "Go"], acme["spans"]
assert acme["notFound"] == ["Kubernetes"]
hooli = exported[ids[1]]
assert "SQL Server 2019" in [s["term"] for s in hooli["spans"]] and "SQL Server" not in hooli["notFound"]

# 2. Merge a CSV: dedupe rows, match the Acme job by apply URL, drop low-match jobs, report agreement.
out = os.path.join(DATA, "merged.jsonl")
buf = io.StringIO()
with redirect_stdout(buf):
    conv.main(["--csv", os.path.join("tests", "fixtures", "hiringcafe_sample.csv"), "--db", db_export, "--out", out])
text = buf.getvalue()
merged = [json.loads(l) for l in open(out, encoding="utf-8")]
by_company = {m["company"]: m for m in merged}
assert len(merged) == 3, text                                         # Acme (Claude), Hooli (Claude), Globex (CSV)
assert by_company["Acme Inc."]["labelSource"] == "claude", "Claude's labels win for a job in both"
assert by_company["Globex"]["techStack"] == ["Go", "Kafka", "PostgreSQL", "Redis"], "last CSV row wins"
assert by_company["Globex"]["title"] == "Backend Engineer – Platform"
assert "Google" not in [s["term"] for s in by_company["Globex"]["spans"]]
assert "Initech" not in by_company, "under 80% word for word: left out"
assert "4 rows, 3 unique jobs, 1 already in jobs.db, 1 left out" in text, text
assert "hiring.cafe lists 43% of Claude's terms" in text, text          # 3 of Claude's 7 Acme terms
assert "Claude has, hiring.cafe doesn't: " in text and "Kubernetes (1)" in text, text
assert "hiring.cafe has, Claude doesn't: Microsoft Office (1)" in text, text

# 3. --csv-only-if-agree drops CSV jobs when the sources disagree too much.
with redirect_stdout(io.StringIO()):
    conv.main(["--csv", os.path.join("tests", "fixtures", "hiringcafe_sample.csv"), "--db", db_export, "--out", out,
               "--csv-only-if-agree", "0.9"])
assert {json.loads(l)["labelSource"] for l in open(out, encoding="utf-8")} == {"claude"}

print("TRAINING EXPORT E2E PASS")
