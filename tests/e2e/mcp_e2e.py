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
]})
print("add_jobs:", [(r["company"], r["result"]) for r in added["results"]], added["stats"]["New"], "new")
err, msg = call("add_jobs", {"jobs": [{"source": "x", "company": "", "title": "t", "listingUrl": "u", "jdText": "j"}]})
print("invalid job:", msg["results"][0]["result"], "-", msg["results"][0]["error"])

# Worker runs as its own process against the same folder.
env = dict(os.environ, Pipeline__Tailor="CopyMaster", Pipeline__DataDir=D, Pipeline__MasterResumePath=os.path.join(S, "master.pdf"), Pipeline__PollSeconds="5")
worker = subprocess.Popen(["dotnet", os.path.join("src","JobPipeline.Worker","bin","Debug","net8.0","JobPipeline.Worker.dll")], env=env,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
for _ in range(60):
    time.sleep(0.5)
    if call("pipeline_stats", {})[1]["ReadyToApply"] == 2: break
worker.terminate(); wlog = worker.communicate(timeout=10)[0]
print("worker log:", [l.strip() for l in wlog.splitlines() if "Ready to apply" in l])

err, run = call("begin_apply_run", {"limit": 5})
print("ready:", [(j["company"], j["status"]) for j in run["ready"]], "interrupted:", len(run["interrupted"]))
acme = next(j for j in run["ready"] if j["company"] == "Acme Inc.")
glob = next(j for j in run["ready"] if j["company"] == "Globex")

err, st = call("start_apply", {"id": acme["id"]})
print("start_apply:", st["applyUrl"], "resume exists:", st["resumeFileExists"], os.path.basename(st["resumePath"]))
print("double start:", call("start_apply", {"id": acme["id"]})[1])
err, ap = call("mark_applied", {"id": acme["id"], "note": "Greenhouse confirmation shown"})
print("mark_applied:", ap["status"], ap["dateApplied"] is not None)

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
assert stats["Interviewing"] == 1 and stats["ReadyToApply"] == 1, stats
print("E2E PASS")
mcp.stdin.close(); mcp.wait(timeout=10)
print("mcp stderr:", mcp.stderr.read().strip())
