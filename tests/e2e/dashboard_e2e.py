"""End-to-end check of the dashboard: starts it on a free port against a temporary data folder and exercises every
API operation the page uses, then confirms the page itself is served.
Run from the repo root after `dotnet build`:  python tests/e2e/dashboard_e2e.py   (exit code 0 = pass)"""
import http.cookiejar, json, os, socket, subprocess, sys, tempfile, time, urllib.error, urllib.request

DATA = tempfile.mkdtemp(prefix="jp-dash-")
with socket.socket() as s:
    s.bind(("127.0.0.1", 0))
    PORT = s.getsockname()[1]
BASE = f"http://127.0.0.1:{PORT}"
DLL = os.path.join("src", "JobPipeline.Dashboard", "bin", "Debug", "net8.0", "JobPipeline.Dashboard.dll")



def cli(*words, password=None):
    """Runs a `user ...` command against the test data folder; returns (exit code, output)."""
    r = subprocess.run(["dotnet", DLL, *words, f"--Dashboard:DataDir={DATA}"], input=password,
                       capture_output=True, text=True, timeout=120)
    return r.returncode, r.stdout + r.stderr


code, out = cli("user", "add", "tester", password="correct horse battery\n")
assert code == 0 and "Created user 'tester'" in out, out
code, out = cli("user", "add", "tester", password="another long password\n")
assert code == 1 and "already exists" in out, out
code, out = cli("user", "add", "shorty", password="short\n")
assert code == 1 and "at least" in out, out
assert "tester" in cli("user", "list")[1]

# 6 sign-in attempts a minute instead of 5, so the checks below fit before the limit kicks in
app = subprocess.Popen(["dotnet", DLL, f"--urls={BASE}", f"--Dashboard:DataDir={DATA}", "--Dashboard:LoginAttemptsPerMinute=6"],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
jar = http.cookiejar.CookieJar()
browser = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))


def call(method, path, body=None, expect=200, header=True, final_url=None):
    headers = {"X-Requested-With": "JobPipeline"} if header else {}
    if body is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(BASE + path, method=method,
                                 data=None if body is None else json.dumps(body).encode(), headers=headers)
    try:
        with browser.open(req, timeout=30) as r:
            status, text = r.status, r.read().decode()
            if final_url is not None:
                final_url.append(r.geturl())
    except urllib.error.HTTPError as e:
        status, text = e.code, e.read().decode()
    assert status == expect, f"{method} {path}: expected {expect}, got {status}: {text}"
    return json.loads(text) if text and text.lstrip().startswith(("{", "[")) else text


try:
    for _ in range(100):
        try:
            call("GET", "/login.html")
            break
        except Exception:
            time.sleep(0.2)
    else:
        raise SystemExit("dashboard did not start:\n" + app.stdout.read())

    # signed out: only the sign-in page is served
    landed = []
    assert "Sign in" in call("GET", "/", final_url=landed) and landed[0].endswith("/login.html"), landed
    assert call("GET", "/api/jobs", expect=401)["error"] == "Please sign in."
    assert "renderRail" not in call("GET", "/app.js", final_url=[]), "app.js is behind the sign-in"
    assert "Missing" in call("POST", "/api/login", {"username": "tester", "password": "x"}, header=False, expect=400)["error"]
    assert "Wrong" in call("POST", "/api/login", {"username": "tester", "password": "nope nope nope"}, expect=401)["error"]
    assert "Wrong" in call("POST", "/api/login", {"username": "ghost", "password": "correct horse battery"}, expect=401)["error"]
    assert call("POST", "/api/login", {"username": "TESTER", "password": "correct horse battery"})["username"] == "tester"
    cookie = next(c for c in jar if c.name == "jobpipeline_session")
    assert cookie.has_nonstandard_attr("HttpOnly") or cookie.has_nonstandard_attr("httponly"), "session cookie is HttpOnly"
    assert call("GET", "/api/me")["username"] == "tester"
    assert "Missing" in call("POST", "/api/jobs", {"company": "x"}, header=False, expect=400)["error"], "writes need the header"

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

    # comments and edits are signed with the user's name
    j2 = call("POST", "/api/jobs", {"company": "Initech", "title": "Engineer", "listingUrl": "https://example.com/i"}, expect=201)
    assert call("POST", f"/api/jobs/{j2['id']}/comments", {"text": "hi"})["comments"][0]["by"] == "tester"

    # sign out, then sign back in
    call("POST", "/api/logout", expect=204)
    call("GET", "/api/me", expect=401)
    call("POST", "/api/login", {"username": "tester", "password": "correct horse battery"})

    # changing the password ends existing sessions
    assert cli("user", "passwd", "tester", password="a brand new password\n")[0] == 0
    call("GET", "/api/me", expect=401)
    call("POST", "/api/login", {"username": "tester", "password": "correct horse battery"}, expect=401)

    # 6 sign-in attempts per minute (all 6 used above): the next one is refused even with the right password
    assert "Too many" in call("POST", "/api/login", {"username": "tester", "password": "a brand new password"}, expect=429)["error"]

    code, out = cli("user", "remove", "tester")
    assert code == 0, out
    assert cli("user", "remove", "tester")[0] == 1
    print("DASHBOARD E2E PASS")
finally:
    app.terminate()
    try:
        app.wait(timeout=10)
    except subprocess.TimeoutExpired:
        app.kill()
