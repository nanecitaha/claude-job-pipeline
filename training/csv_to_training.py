"""Turn a hiring.cafe CSV into training data, and merge it with the jobs.db export into one training.jsonl.

    python csv_to_training.py --csv jobs_extracted.csv --out training.jsonl
    python csv_to_training.py --csv jobs_extracted.csv --db training-db.jsonl --out training.jsonl

CSV columns used: "Company & Title" (split on the first " - "), "Full View URL" (the job's id), "Apply URL", "Tools"
(comma-separated) and "Full JD". Other columns are ignored.

Each job gets the same treatment as `JobPipeline.Dashboard.exe export-training` (TrainingExport.cs): text is the
title, a blank line, then the description; every word-for-word occurrence of each tool is marked; jobs where fewer
than --min-match of their tools appear word for word are left out.

When the same job is in both files (same apply URL, or same company and title), Claude's labels are kept by default
and the report shows how much the two sources agree, so you can decide whether to mix them.
Needs only the Python standard library.
"""
import argparse
import csv
import hashlib
import json
import re
import sys
from collections import Counter
from urllib.parse import parse_qsl, urlencode, urlsplit, urlunsplit


# ── same rules as TrainingExport.Build (C#) ──

def _boundary(text, i):
    """A term must not run into a neighbouring letter or digit ("Go" isn't in "Google")."""
    return i < 0 or i >= len(text) or not text[i].isalnum()


def build(job_id, company, title, description, tech_stack, **extra):
    text = f"{title.strip()}\n\n{description.strip().replace(chr(13) + chr(10), chr(10))}"
    stack = []
    for t in (t.strip() for t in tech_stack):
        if t and t not in stack:
            stack.append(t)
    taken = [False] * len(text)
    spans, not_found = [], set()
    for term in sorted(stack, key=len, reverse=True):          # longest first: "ASP.NET Core" before ".NET"
        found = False
        at = text.find(term)
        while at >= 0:
            end = at + len(term)
            if _boundary(text, at - 1) and _boundary(text, end):
                found = True
                if not any(taken[at:end]):
                    for i in range(at, end):
                        taken[i] = True
                    spans.append({"start": at, "end": end, "term": term})
            at = text.find(term, at + 1)
        if not found:
            not_found.add(term)
    spans.sort(key=lambda s: s["start"])
    return {"id": job_id, "company": company, "title": title, "text": text, "techStack": stack, "spans": spans,
            "notFound": [t for t in stack if t in not_found], **extra}


def match_rate(example):
    n = len(example["techStack"])
    return (n - len(example["notFound"])) / n if n else 0.0


# ── CSV ──

def split_company_title(value):
    company, sep, title = value.partition(" - ")
    return (company.strip(), title.strip()) if sep and title.strip() else ("", value.strip())


def read_csv(path):
    """Rows as examples, one per Full View URL (the last row wins)."""
    with open(path, encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
    needed = {"Company & Title", "Full View URL", "Tools", "Full JD"}
    if rows and not needed <= set(rows[0]):
        raise SystemExit(f"{path} is missing columns: {', '.join(sorted(needed - set(rows[0])))}")
    by_url = {}
    for r in rows:
        url = (r.get("Full View URL") or "").strip()
        if not url or not (r.get("Full JD") or "").strip():
            continue
        company, title = split_company_title(r.get("Company & Title") or "")
        by_url[url] = build("csv:" + hashlib.sha1(url.encode()).hexdigest()[:16], company, title, r["Full JD"],
                            (r.get("Tools") or "").split(","), listingUrl=url,
                            applyUrl=(r.get("Apply URL") or "").strip() or None, labelSource="hiringcafe")
    return by_url, len(rows)


# ── matching the same job across sources ──

def norm_url(url):
    if not url:
        return None
    p = urlsplit(url.strip())
    query = urlencode(sorted((k, v) for k, v in parse_qsl(p.query) if not k.lower().startswith(("utm_", "gh_src", "source", "ref"))))
    return urlunsplit(("https", p.netloc.lower().removeprefix("www."), p.path.rstrip("/"), query, ""))


def norm_name(s):
    s = re.sub(r"[^a-z0-9#+ ]", " ", (s or "").lower())
    s = re.sub(r"\b(inc|llc|ltd|corp|corporation|co|company|group|sr|senior)\b", " ", s)
    return " ".join(s.split())


def keys(example):
    out = {u for u in (norm_url(example.get("applyUrl")), norm_url(example.get("listingUrl"))) if u}
    if example.get("company"):
        out.add(("name", norm_name(example["company"]), norm_name(example["title"])))
    return out


def agreement(pairs):
    """pairs: [(claude_example, csv_example)] → how much hiring.cafe's tools agree with Claude's."""
    both = only_claude = only_csv = 0
    missed, added = Counter(), Counter()
    for c, h in pairs:
        a, b = set(c["techStack"]), set(h["techStack"])
        both += len(a & b)
        only_claude += len(a - b)
        only_csv += len(b - a)
        missed.update(a - b)
        added.update(b - a)
    return {"jobs": len(pairs),
            "csvFoundOfClaude": both / (both + only_claude) if both + only_claude else 0,
            "claudeFoundOfCsv": both / (both + only_csv) if both + only_csv else 0,
            "missedByCsv": missed.most_common(10), "onlyInCsv": added.most_common(10)}


# ── main ──

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--csv", required=True, action="append", help="hiring.cafe CSV (repeat for several files)")
    ap.add_argument("--db", help="training.jsonl from `JobPipeline.Dashboard.exe export-training` (Claude's labels)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--min-match", type=float, default=0.8)
    ap.add_argument("--prefer", choices=["claude", "csv"], default="claude",
                    help="whose labels to keep when a job is in both")
    ap.add_argument("--csv-only-if-agree", type=float, default=0.0, metavar="RATE",
                    help="drop all CSV jobs if hiring.cafe finds less than RATE of Claude's terms (e.g. 0.9)")
    args = ap.parse_args(argv)

    csv_jobs, rows = {}, 0
    for path in args.csv:
        jobs, n = read_csv(path)
        csv_jobs.update(jobs)
        rows += n
    claude = [json.loads(l) for l in open(args.db, encoding="utf-8") if l.strip()] if args.db else []

    index = {}
    for c in claude:
        for k in keys(c):
            index.setdefault(k, c)
    pairs, csv_new = [], []
    for h in csv_jobs.values():
        match = next((index[k] for k in keys(h) if k in index), None)
        (pairs.append((match, h)) if match else csv_new.append(h))

    low = [h for h in csv_new if match_rate(h) < args.min_match or not h["techStack"]]
    csv_keep = [h for h in csv_new if h not in low]
    agree = agreement(pairs) if pairs else None
    if agree and args.csv_only_if_agree and agree["csvFoundOfClaude"] < args.csv_only_if_agree:
        csv_keep = []
    if args.prefer == "csv":
        swapped = {id(c) for c, h in pairs if match_rate(h) >= args.min_match}
        claude = [c for c in claude if id(c) not in swapped] + [h for c, h in pairs if match_rate(h) >= args.min_match]

    out = claude + csv_keep
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        for ex in out:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")

    terms = sum(len(h["techStack"]) for h in csv_jobs.values())
    found = sum(len(h["techStack"]) - len(h["notFound"]) for h in csv_jobs.values())
    not_found = Counter(t for h in csv_jobs.values() for t in h["notFound"])
    print(f"Wrote {len(out)} jobs to {args.out}: {len(claude)} labelled by "
          f"{'Claude' if args.prefer == 'claude' else 'Claude or hiring.cafe'}, {len(csv_keep)} from the CSV")
    print(f"  CSV: {rows} rows, {len(csv_jobs)} unique jobs, {len(pairs)} already in jobs.db, {len(low)} left out "
          f"(under {args.min_match:.0%} of tools word for word)")
    if terms:
        print(f"  {found / terms:.1%} of CSV tools appear word for word in their description")
    if not_found:
        print("  CSV tools most often not word for word: " + ", ".join(f"{t} ({n})" for t, n in not_found.most_common(12)))
    if agree:
        print(f"\nOn the {agree['jobs']} jobs in both: hiring.cafe lists {agree['csvFoundOfClaude']:.0%} of Claude's terms; "
              f"Claude lists {agree['claudeFoundOfCsv']:.0%} of hiring.cafe's.")
        print("  Claude has, hiring.cafe doesn't: " + (", ".join(f"{t} ({n})" for t, n in agree["missedByCsv"]) or "-"))
        print("  hiring.cafe has, Claude doesn't: " + (", ".join(f"{t} ({n})" for t, n in agree["onlyInCsv"]) or "-"))
        print("  Rough guide: both 90%+ → mixing is fine; lower → consider --csv-only-if-agree 0.9 or Claude's jobs alone.")
        if not csv_keep and args.csv_only_if_agree:
            print(f"  CSV jobs dropped: under {args.csv_only_if_agree:.0%} agreement.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
