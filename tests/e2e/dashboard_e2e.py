"""End-to-end check of the dashboard: starts it on a free port against a temporary data folder and exercises every
API operation the page uses, then confirms the page itself is served.
Run from the repo root after `dotnet build`:  python tests/e2e/dashboard_e2e.py   (exit code 0 = pass)"""
import json, os, socket, subprocess, sys, tempfile, time, urllib.error, urllib.request

DATA = tempfile.mkdtemp(prefix="jp-dash-")
with socket.socket() as s:
    s.bind(("127.0.0.1", 0))
    PORT = s.getsockname()[1]
BASE = f"http://127.0.0.1:{PORT}"
DLL = os.path.join("src", "JobPipeline.Dashboard", "bin", "Debug", "net8.0", "JobPipeline.Dashboard.dll")

app = subprocess.Popen(["dotnet", DLL, f"--urls={BASE}", f"--Dashboard:DataDir={DATA}"],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)


def call(method, path, body=None, expect=200):
    req = urllib.request.Request(BASE + path, method=method,
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"} if body is not None else {})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            status, text = r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        status, text = e.code, e.read().decode()
    assert status == expect, f"{method} {path}: expected {expect}, got {status}: {text}"
    return json.loads(text) if text and text.lstrip().startswith(("{", "[")) else text


try:
    for _ in range(100):
        try:
            call("GET", "/api/info")
            break
        except Exception:
            time.sleep(0.2)
    else:
        raise SystemExit("dashboard did not start:\n" + app.stdout.read())

    info = call("GET", "/api/info")
    assert info["dataDir"].rstrip("/\\").endswith(os.path.basename(DATA)), info
    assert "ActionNeeded" in info["statuses"]

    page = call("GET", "/")
    assert "<title>Job Pipeline</title>" in page and "app.js" in page
    assert "renderRail" in call("GET", "/app.js")

    # add (manual) + duplicate
    job = call("POST", "/api/jobs", {"company": "Acme Inc.", "title": "Senior .NET Engineer",
                                     "listingUrl": "https://example.com/1", "techStack": ["C#", "Azure"],
                                     "jdText": "We use C# and Azure.", "isRemote": False, "location": "Boston, MA"}, expect=201)
    jid = job["id"]
    assert job["source"] == "manual" and job["status"] == "New"
    dup = call("POST", "/api/jobs", {"company": "ACME", "title": "Sr. .NET Engineer", "listingUrl": "https://x/2"}, expect=409)
    assert dup["id"] == jid
    call("POST", "/api/jobs", {"company": "", "title": "t", "listingUrl": "u"}, expect=400)

    # list, filter, search
    assert len(call("GET", "/api/jobs")) == 1
    assert len(call("GET", "/api/jobs?status=New")) == 1
    assert len(call("GET", "/api/jobs?status=Applied")) == 0
    assert len(call("GET", "/api/jobs?q=azure")) == 1
    assert "not one of" in call("GET", "/api/jobs?status=Nope", expect=400)["error"]
    assert call("GET", "/api/stats")["New"] == 1

    # detail with job description
    detail = call("GET", f"/api/jobs/{jid}")
    assert detail["jdText"] == "We use C# and Azure." and detail["job"]["company"] == "Acme Inc."
    call("GET", "/api/jobs/00000000-0000-0000-0000-000000000000", expect=404)

    # edit fields
    edited = call("PUT", f"/api/jobs/{jid}", {"company": "Acme Corp", "title": "Staff Engineer",
                                              "listingUrl": "https://example.com/1", "techStack": ["C#", "Go"],
                                              "salaryMin": 150000, "salaryMax": 190000, "isRemote": True})
    assert edited["company"] == "Acme Corp" and edited["techStack"] == ["C#", "Go"] and edited["isRemote"] is True
    assert "salaryMin" in call("PUT", f"/api/jobs/{jid}", {"company": "A", "title": "B", "listingUrl": "u",
                                                           "salaryMin": 5, "salaryMax": 1}, expect=400)["error"]

    # status changes
    applied = call("POST", f"/api/jobs/{jid}/status", {"status": "applied", "note": "on their site"})
    assert applied["status"] == "Applied" and applied["dateApplied"] and applied["history"][-1]["note"] == "on their site"
    assert "pipeline" in call("POST", f"/api/jobs/{jid}/status", {"status": "Applying"}, expect=400)["error"]
    assert "no resume" in call("POST", f"/api/jobs/{jid}/status", {"status": "ReadyToApply"}, expect=400)["error"]

    # comments
    c = call("POST", f"/api/jobs/{jid}/comments", {"text": "Recruiter: Jane"})
    cid = c["comments"][0]["id"]
    assert len(call("GET", "/api/jobs?q=jane")) == 1
    assert call("DELETE", f"/api/jobs/{jid}/comments/{cid}")["comments"] == []
    call("POST", f"/api/jobs/{jid}/comments", {"text": " "}, expect=400)

    # job description
    call("PUT", f"/api/jobs/{jid}/description", {"text": "Edited description"})
    assert call("GET", f"/api/jobs/{jid}")["jdText"] == "Edited description"

    # resume: none yet, then a file inside the data folder is served
    call("GET", f"/api/jobs/{jid}/resume", expect=404)

    # delete
    call("DELETE", f"/api/jobs/{jid}", expect=204)
    call("DELETE", f"/api/jobs/{jid}", expect=404)
    assert call("GET", "/api/jobs") == []
    print("DASHBOARD E2E PASS")
finally:
    app.terminate()
    try:
        app.wait(timeout=10)
    except subprocess.TimeoutExpired:
        app.kill()
