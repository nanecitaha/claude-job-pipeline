"""Train the tech stack extractor: a small model that marks tech terms in a job description, taught from the
extractions Claude has already made. It only ever picks text that is in the posting, so terms come out exactly as
written.

    # 1. on the VPS: export what Claude has labelled
    JobPipeline.Dashboard.exe export-training training.jsonl
    # 2. on Google Colab (GPU) or a PC: train, test against Claude, export for .NET
    pip install -r requirements.txt
    python train_techstack.py --data training.jsonl --out techstack-model

Writes to --out: techstack.onnx (int8, what the worker loads), the tokenizer files, labels.json, settings.json and
metrics.json (how close it gets to Claude on jobs it never saw during training).
"""
import argparse
import hashlib
import json
import math
import os
import random
import shutil
import time
from collections import Counter

import numpy as np
import torch
from techstack_decode import decode
from transformers import (AutoModelForTokenClassification, AutoTokenizer, DataCollatorForTokenClassification,
                          Trainer, TrainingArguments)

LABELS = ["O", "B-TECH", "I-TECH"]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", required=True, help="training.jsonl from export-training")
    ap.add_argument("--out", default="techstack-model")
    ap.add_argument("--model", default="distilbert-base-cased",
                    help="pretrained model to start from (cased: 'Go' and 'go' differ)")
    ap.add_argument("--epochs", type=float, default=6)
    ap.add_argument("--lr", type=float, default=5e-5)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--max-len", type=int, default=256, help="tokens per window; longer postings are slid over")
    ap.add_argument("--stride", type=int, default=64, help="tokens of overlap between windows")
    ap.add_argument("--test", type=float, default=0.15, help="share of jobs held back to compare against Claude")
    ap.add_argument("--seed", type=int, default=13)
    ap.add_argument("--all", action="store_true",
                    help="after measuring, train the shipped model on every job (test jobs included)")
    args = ap.parse_args()

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    os.makedirs(args.out, exist_ok=True)

    jobs = [json.loads(line) for line in open(args.data, encoding="utf-8") if line.strip()]
    train, test = split(jobs, args.test)
    print(f"{len(jobs)} jobs: {len(train)} to train on, {len(test)} held back to compare against Claude")
    if len(train) < 20 or not test:
        raise SystemExit("Not enough jobs to train and test. Export more (300+ is a good target).")

    tok = AutoTokenizer.from_pretrained(args.model)
    if not tok.is_fast:
        raise SystemExit(f"{args.model} has no fast tokenizer (needed for character offsets).")

    device = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"Training on {device}" + ("" if device == "cuda" else " (slow: use a GPU, e.g. Colab, if you can)"))

    model = fit(args, tok, train)
    metrics = {"jobs": len(jobs), "train": len(train), "test": len(test),
               "pytorch": evaluate(lambda text: predict_torch(model, tok, text, args), test)}
    report("Model vs Claude on held-back jobs", metrics["pytorch"])

    if args.all:
        print("\nTraining the shipped model on all jobs…")
        model = fit(args, tok, train + test)

    onnx_path = export_onnx(model, tok, args.out)
    session = onnx_session(onnx_path)
    metrics["onnx_int8"] = evaluate(lambda text: predict_onnx(session, tok, text, args), test)
    report("Shipped ONNX model (int8) vs Claude" + (" (it has seen these jobs: --all)" if args.all else ""),
           metrics["onnx_int8"])

    tok.save_pretrained(args.out)
    json.dump(LABELS, open(os.path.join(args.out, "labels.json"), "w"))
    json.dump({"maxLength": args.max_len, "stride": args.stride, "baseModel": args.model, "trainedOn": len(train) + (len(test) if args.all else 0),
               "createdAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())},
              open(os.path.join(args.out, "settings.json"), "w"), indent=2)
    json.dump(metrics, open(os.path.join(args.out, "metrics.json"), "w"), indent=2)
    shutil.rmtree(os.path.join(args.out, "checkpoints"), ignore_errors=True)
    print(f"\nDone. Copy the folder '{args.out}' to the VPS (e.g. C:\\JobPipeline\\techstack-model).")


# ── data ──

def split(jobs, test_share):
    """The same job always lands on the same side (hash of its id), so reruns compare like with like."""
    def bucket(job):
        return int(hashlib.sha1(job["id"].encode()).hexdigest(), 16) % 1000 / 1000
    return [j for j in jobs if bucket(j) >= test_share], [j for j in jobs if bucket(j) < test_share]


def encode(tok, text, args, **kw):
    return tok(text, truncation=True, max_length=args.max_len, stride=args.stride,
               return_overflowing_tokens=True, return_offsets_mapping=True, **kw)


def windows(tok, job, args):
    """Token windows over one job, each token labelled B (starts a term), I (inside one) or O."""
    enc = encode(tok, job["text"], args)
    spans = job["spans"]
    for w in range(len(enc["input_ids"])):
        labels = []
        for (s, e), seq in zip(enc["offset_mapping"][w], enc.sequence_ids(w)):
            if seq is None or s == e:
                labels.append(-100)   # special tokens: not scored
                continue
            label = 0
            for sp in spans:
                if s >= sp["start"] and e <= sp["end"]:
                    label = 1 if s == sp["start"] else 2
                    break
            labels.append(label)
        yield {"input_ids": enc["input_ids"][w], "attention_mask": enc["attention_mask"][w], "labels": labels}


class Windows(torch.utils.data.Dataset):
    def __init__(self, items): self.items = items
    def __len__(self): return len(self.items)
    def __getitem__(self, i): return self.items[i]


# ── training ──

def fit(args, tok, jobs):
    items = [w for job in jobs for w in windows(tok, job, args)]
    print(f"{len(items)} windows of up to {args.max_len} tokens")
    model = AutoModelForTokenClassification.from_pretrained(
        args.model, num_labels=len(LABELS), id2label=dict(enumerate(LABELS)), label2id={l: i for i, l in enumerate(LABELS)})
    targs = TrainingArguments(
        output_dir=os.path.join(args.out, "checkpoints"), num_train_epochs=args.epochs, learning_rate=args.lr,
        per_device_train_batch_size=args.batch, weight_decay=0.01, logging_steps=25,
        warmup_steps=max(1, int(math.ceil(len(items) / args.batch) * args.epochs * 0.1)),   # 10% warm-up
        save_strategy="no", report_to=[], seed=args.seed, fp16=torch.cuda.is_available())
    Trainer(model=model, args=targs, train_dataset=Windows(items),
            data_collator=DataCollatorForTokenClassification(tok)).train()
    return model.eval()


# ── prediction (the worker does the same in C#) ──

def collect(enc, probs_per_window):
    out = {}
    for w, probs in enumerate(probs_per_window):
        for j, ((s, e), seq) in enumerate(zip(enc["offset_mapping"][w], enc.sequence_ids(w))):
            if seq is not None and s < e:
                out.setdefault((s, e), []).append(probs[j])
    return out


def predict_torch(model, tok, text, args):
    enc = encode(tok, text, args)
    probs = []
    with torch.no_grad():
        for w in range(len(enc["input_ids"])):
            ids = torch.tensor([enc["input_ids"][w]], device=model.device)
            mask = torch.tensor([enc["attention_mask"][w]], device=model.device)
            probs.append(torch.softmax(model(input_ids=ids, attention_mask=mask).logits[0], -1).cpu().numpy())
    return decode(text, collect(enc, probs))


def predict_onnx(session, tok, text, args):
    enc = encode(tok, text, args)
    probs = []
    for w in range(len(enc["input_ids"])):
        logits = session.run(["logits"], {"input_ids": np.array([enc["input_ids"][w]], dtype=np.int64),
                                          "attention_mask": np.array([enc["attention_mask"][w]], dtype=np.int64)})[0][0]
        ex = np.exp(logits - logits.max(-1, keepdims=True))
        probs.append(ex / ex.sum(-1, keepdims=True))
    return decode(text, collect(enc, probs))


# ── measuring against Claude ──

def evaluate(predict, jobs):
    """Per term, against Claude's tech stack for the same job. 'found' counts only terms that appear word for word
    in the posting (the most any extractor that copies text can get)."""
    tp = fp = fn = fn_findable = 0
    missed, extra = Counter(), Counter()
    started = time.time()
    for job in jobs:
        gold = set(job["techStack"])
        findable = {sp["term"] for sp in job["spans"]}
        pred = set(predict(job["text"]))
        tp += len(pred & gold)
        fp += len(pred - gold)
        fn += len(gold - pred)
        fn_findable += len(findable - pred)
        missed.update(findable - pred)
        extra.update(pred - gold)
    p = tp / (tp + fp) if tp + fp else 0
    r = tp / (tp + fn) if tp + fn else 0
    found = sum(len({sp["term"] for sp in j["spans"]}) for j in jobs)
    rf = (found - fn_findable) / found if found else 0
    return {"precision": round(p, 4), "recall": round(r, 4), "f1": round(2 * p * r / (p + r), 4) if p + r else 0,
            "recallOfFindable": round(rf, 4), "msPerJob": round((time.time() - started) * 1000 / max(len(jobs), 1), 1),
            "mostMissed": missed.most_common(15), "mostExtra": extra.most_common(15)}


def report(title, m):
    print(f"\n{title}\n  precision {m['precision']:.1%}  (of the terms it found, how many Claude also listed)\n"
          f"  recall    {m['recall']:.1%}  (of Claude's terms, how many it found)\n"
          f"  recall of terms that appear word for word: {m['recallOfFindable']:.1%}\n"
          f"  F1 {m['f1']:.1%}   speed {m['msPerJob']} ms per job here")
    if m["mostMissed"]:
        print("  missed most: " + ", ".join(f"{t} ({n})" for t, n in m["mostMissed"]))
    if m["mostExtra"]:
        print("  extra (Claude didn't list): " + ", ".join(f"{t} ({n})" for t, n in m["mostExtra"]))


# ── export ──

def export_onnx(model, tok, out):
    import onnxruntime.quantization as q
    model = model.to("cpu").eval()
    sample = tok("Senior .NET engineer: C#, ASP.NET Core, Azure", return_tensors="pt")
    fp32 = os.path.join(out, "techstack-fp32.onnx")
    kw = dict(input_names=["input_ids", "attention_mask"], output_names=["logits"], opset_version=17,
              dynamic_axes={"input_ids": {0: "batch", 1: "tokens"}, "attention_mask": {0: "batch", 1: "tokens"},
                            "logits": {0: "batch", 1: "tokens"}})
    try:
        torch.onnx.export(model, (sample["input_ids"], sample["attention_mask"]), fp32, dynamo=False, **kw)
    except TypeError:   # older torch: no dynamo argument
        torch.onnx.export(model, (sample["input_ids"], sample["attention_mask"]), fp32, **kw)
    int8 = os.path.join(out, "techstack.onnx")
    q.quantize_dynamic(fp32, int8, weight_type=q.QuantType.QInt8)
    os.remove(fp32)
    for f in os.listdir(out):   # external-data files some exporters leave behind
        if f.endswith(".onnx.data") or f.startswith("techstack-fp32"):
            os.remove(os.path.join(out, f))
    print(f"\nExported {int8} ({os.path.getsize(int8) / 1e6:.0f} MB)")
    return int8


def onnx_session(path):
    import onnxruntime as ort
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = 1   # what the worker uses on the VPS
    return ort.InferenceSession(path, opts, providers=["CPUExecutionProvider"])


if __name__ == "__main__":
    main()
