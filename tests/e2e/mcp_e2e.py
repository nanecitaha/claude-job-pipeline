"""End-to-end check: MCP server (stdio) and worker as separate processes on one data folder.
Run from the repo root after `dotnet build`:  python tests/e2e/mcp_e2e.py   (exit code 0 = pass)"""
import json, subprocess, sys, time, os, tempfile
D = tempfile.mkdtemp(prefix="jp-e2e-"); S = tempfile.mkdtemp(prefix="jp-e2e-res-")
open(os.path.join(S, "master.pdf"), "w").write("%PDF-1.4 test resume")
mcp = subprocess.Popen(["dotnet", os.path.join("src","JobPipeline.Mcp","bin","Debug","net8.0","JobPipeline.Mcp.dll"), "--data", D],
                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
n = 0
def rpc(method, params=None, notify=False):
    global n
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None: msg["params"] = params
    if not notify: n += 1; msg["id"] = n
    mcp.stdin.write(json.dumps(msg) + "\n"); mcp.stdin.flush()
    if notify: return None
    return json.loads(mcp.stdout.readline())
def call(name, args):
    r = rpc("tools/call", {"name": name, "arguments": args})["result"]
    body = r["content"][0]["text"]
    return r["isError"], (body if r["isError"] else json.loads(body))

init = rpc("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "test", "version": "0"}})
print("initialize:", init["result"]["protocolVersion"], init["result"]["serverInfo"])
rpc("notifications/initialized", notify=True)
tools = [t["name"] for t in rpc("tools/list")["result"]["tools"]]
print("tools:", ", ".join(tools))
print("unknown method:", rpc("bogus/method")["error"])

err, cfg = call("get_config", {"name": "hunt"})
print("hunt config starts:", cfg["content"].splitlines()[0])
err, res = call("get_config", {"name": "resume"})
assert not err and res["isSetUp"] is False and res["path"].endswith("resume.md"), res
err, prof = call("get_config", {"name": "profile"})
assert not err and prof["isSetUp"] is False, "profile with [fill] fields must report isSetUp false"
open(res["path"], "w").write("# Jane Doe\n## Experience\nAcme 2020 to present: C#, .NET 8\n")
assert call("get_config", {"name": "resume"})[1]["isSetUp"] is True
print("resume config: placeholder -> isSetUp false, filled -> true")

jd = "Senior Backend Engineer. Must know C#, .NET 8, ASP.NET Core, RabbitMQ, SQL Server, Azure."
err, added = call("add_jobs", {"jobs": [
  {"source": "builtin", "company": "Acme Inc.", "title": "Senior Backend Engineer (.NET)", "listingUrl": "https://builtin.com/job/acme/1",
   "applyUrl": "https://job-boards.greenhouse.io/acme/jobs/4001", "externalJobId": "4001", "location": "Remote, US", "isRemote": True,
   "salaryText": "$160K-$190K", "salaryMin": 160000, "salaryMax": 190000, "salaryCurrency": "USD",
   "techStack": ["C#", ".NET 8", "ASP.NET Core", "RabbitMQ", "SQL Server", "Azure"], "datePosted": "2026-10-21", "jdText": jd},
  {"source": "builtin", "company": "Globex", "title": "Senior .NET Developer", "listingUrl": "https://builtin.com/job/globex/2", "jdText": jd},
  {"source": "linkedin", "company": "ACME, Inc", "title": "Sr. Back-End Engineer (.NET)", "listingUrl": "https://linkedin.com/jobs/view/77", "jdText": jd},
  {"source": "builtin", "company": "Google LLC", "title": "Senior Software Engineer, .NET", "listingUrl": "https://builtin.com/job/google/3", "jdText": jd},
]})
assert "stats" not in added, "add_jobs result stays small: stats come from pipeline_stats"
print("add_jobs:", [(r["company"], r["result"]) for r in added["results"]], call("pipeline_stats", {})[1]["New"], "new")
err, au = call("add_urls", {"source": "builtin", "urls": [
    "https://builtin.com/job/x/10", "https://builtin.com/job/x/10?utm_source=a", "https://builtin.com/job/acme/1", "nope"]})
assert not err and au["added"] == 1 and au["duplicate"] == 2 and au["rejected"][0]["url"] == "nope", au
assert call("pipeline_stats", {})[1]["Discovered"] == 1
print("add_urls:", au)
err, msg = call("add_jobs", {"jobs": [{"source": "x", "company": "", "title": "t", "listingUrl": "u", "jdText": "j"}]})
print("invalid job:", msg["results"][0]["result"], "-", msg["results"][0]["error"])

# Worker runs as its own process against the same folder.
env = dict(os.environ, Pipeline__Tailor="CopyMaster", Pipeline__DataDir=D, Pipeline__MasterResumePath=os.path.join(S, "master.pdf"), Pipeline__PollSeconds="5")
worker = subprocess.Popen(["dotnet", os.path.join("src","JobPipeline.Worker","bin","Debug","net8.0","JobPipeline.Worker.dll")], env=env,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
for _ in range(60):
    time.sleep(0.5)
    if call("pipeline_stats", {})[1]["ReadyToApply"] == 3: break
worker.terminate(); wlog = worker.communicate(timeout=10)[0]
print("worker log:", [l.strip() for l in wlog.splitlines() if "Ready to apply" in l])

err, run = call("begin_apply_run", {"limit": 5})
assert [j["company"] for j in run["manualApply"]] == ["Google LLC"], run["manualApply"]
assert run["manualApply"][0]["status"] == "ActionNeeded" and "manual-apply list" in run["manualApply"][0]["actionDesc"]
assert "Google LLC" not in [j["company"] for j in run["ready"]]
print("manual-apply: Google LLC handed over, not queued:", run["manualApply"][0]["actionDesc"][:60])
print("ready:", [(j["company"], j["status"]) for j in run["ready"]], "interrupted:", len(run["interrupted"]))
acme = next(j for j in run["ready"] if j["company"] == "Acme Inc.")
glob = next(j for j in run["ready"] if j["company"] == "Globex")

err, st = call("start_apply", {"id": acme["id"]})
print("start_apply:", st["applyUrl"], "resume exists:", st["resumeFileExists"], os.path.basename(st["resumePath"]))
print("double start:", call("start_apply", {"id": acme["id"]})[1])
err, ap = call("mark_applied", {"id": acme["id"], "note": "Greenhouse confirmation shown"})
print("mark_applied:", ap["status"], ap["dateApplied"] is not None)
assert "history" not in ap and "comments" not in ap, "state-change results are brief"
err, gj = call("get_job", {"id": acme["id"]})
assert "history" not in gj["job"] and "jdText" not in gj, gj
err, gjh = call("get_job", {"id": acme["id"], "includeJd": True, "includeHistory": True})
assert gjh["job"]["history"] and gjh["jdText"] == jd, gjh
raw = rpc("tools/call", {"name": "get_job", "arguments": {"id": acme["id"]}})["result"]["content"][0]["text"]
assert "\n" not in raw and ": null" not in raw and ":null" not in raw, "tool results are compact and omit nulls"
print("get_job: slim by default, history/JD on request; results compact")

call("start_apply", {"id": glob["id"]})            # simulate crash: never finished
err, run2 = call("begin_apply_run", {})
print("after crash:", [(j["company"], j["status"]) for j in run2["interrupted"]])
print("flag on wrong status:", call("flag_action", {"id": glob["id"], "reason": "x"})[1][:60])

err, hits = call("find_jobs", {"company": "Acme", "title": "Senior Backend Engineer"})
print("find_jobs:", [(h["score"], h["job"]["company"]) for h in hits])
err, em = call("record_email", {"id": acme["id"], "type": "interview", "threadId": "abc", "subject": "Interview with Acme",
                                 "receivedAt": "2026-10-23T16:00:00Z", "interviewDate": "2026-10-29T10:00:00-07:00"})
print("record_email:", em)
err, rs = call("resolve_action", {"id": glob["id"], "outcome": "retry"})
print("resolve retry:", rs["status"])
print("bad arg:", call("list_jobs", {"status": "Nope"})[1][:50])
print("stats:", {k: v for k, v in call("pipeline_stats", {})[1].items() if v})
stats = call("pipeline_stats", {})[1]
assert stats["Interviewing"] == 1 and stats["ReadyToApply"] == 1 and stats["ActionNeeded"] == 1, stats
# The enricher starts, finds its data folder and filters, and idles on an empty queue (no network needed).
E = tempfile.mkdtemp(prefix="jp-e2e-enrich-")
enr = subprocess.Popen(["dotnet", os.path.join("src","JobPipeline.Enricher","bin","Debug","net8.0","JobPipeline.Enricher.dll")],
                       env=dict(os.environ, Enrich__DataDir=E, Enrich__PollSeconds="5"), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
started = ""
deadline = time.time() + 30
while time.time() < deadline:
    line = enr.stdout.readline()
    if not line: break
    if "Enricher started" in line: started = line.strip(); break
enr.terminate()
try: enr.communicate(timeout=10)
except subprocess.TimeoutExpired: enr.kill()
assert started, "enricher did not start"
assert os.path.exists(os.path.join(E, "tech-terms.txt")), "starter tech-terms.txt written"
print("enricher:", started[-120:])
print("E2E PASS")
mcp.stdin.close(); mcp.wait(timeout=10)
print("mcp stderr:", mcp.stderr.read().strip())
