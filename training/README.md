# Training the tech stack extractor

A small model (DistilBERT, ~65 MB once shrunk) that marks the tech terms in a job description, taught from the
extractions Claude already made with your rules. It only picks out text that's in the posting, so every term comes
out exactly as written. It runs on the VPS's CPU in about 100–300 ms per job.

You need: the latest `JobPipeline.Dashboard.exe` on the VPS, and a Google account (free Colab) or a PC with Python.

## 1. Export the training data (VPS)

```powershell
C:\JobPipeline\bin\dashboard\JobPipeline.Dashboard.exe export-training C:\JobPipeline\training.jsonl
```

It prints a report. Things to look at:

- **Wrote N jobs**: 300 or more is good. 200 works for a first try.
- **% of all tech stack terms appear word for word**: aim for 90%+. A low number means Claude reworded terms or
  saved a summary instead of the full description. Those jobs are left out (under 80% of their terms found), and
  the "most often not found" list shows which terms.
- To be stricter or looser about which jobs to keep: `export-training C:\JobPipeline\training.jsonl 0.9`.

The file holds the postings' text and company names, no personal data. It stays out of git
(`*.jsonl` is ignored).

## 2. Train (Google Colab, ~20 minutes)

1. Open https://colab.research.google.com → **New notebook**.
2. **Runtime → Change runtime type → T4 GPU** → Save.
3. Click the folder icon on the left and upload three files: `training.jsonl`, `train_techstack.py` and
   `requirements.txt` (the last two are in this `training/` folder).
4. In a cell, run:
   ```
   !pip install -q -r requirements.txt
   !python train_techstack.py --data training.jsonl --out techstack-model
   ```
5. Read the result at the end:
   ```
   Shipped ONNX model (int8) vs Claude
     precision 93.1%  (of the terms it found, how many Claude also listed)
     recall    90.4%  (of Claude's terms, how many it found)
     ...
     missed most: ...
     extra (Claude didn't list): ...
   ```
   These numbers come from about 15% of your jobs that the model never saw. As a rough guide: **F1 of 90% or
   more** is close enough to Claude to switch. 80–90% is usable, with Claude checking the jobs where the model is
   unsure. Under 80%, export more jobs and train again.
6. Happy with it? Train the final model on all jobs, zip it and download it:
   ```
   !python train_techstack.py --data training.jsonl --out techstack-model --all
   !zip -r techstack-model.zip techstack-model
   ```
   Then right-click `techstack-model.zip` in the file panel → **Download**.

On a PC instead: install Python 3.10+, then run the same two `python` commands from this folder. Without an NVIDIA
GPU it works, but it takes 1–3 hours. Don't train on the VPS: it would take hours and slow everything else down.

## 3. Put it on the VPS

Unzip to `C:\JobPipeline\techstack-model\`. You should see `techstack.onnx`, `vocab.txt`, `tokenizer.json`,
`labels.json`, `settings.json` and `metrics.json`.

(The worker doesn't use the model yet. Wiring it in comes next, once the numbers above look good.)

## Retraining

Every month or two, or after a few hundred more jobs: export again, train again, replace the folder.
`metrics.json` in each model folder records how it scored, so you can compare.

## Options

| Option | Default | What it does |
|---|---|---|
| `--epochs` | 6 | Passes over the data. Try 8–10 if recall is low and you have under 300 jobs |
| `--max-len` / `--stride` | 256 / 64 | Window size and overlap in tokens; long postings are read in overlapping windows |
| `--test` | 0.15 | Share of jobs held back for the comparison (always the same jobs, picked by id) |
| `--model` | `distilbert-base-cased` | Starting model. `bert-base-cased` is bigger (~110 MB) and slightly more accurate |
| `--all` | off | After measuring, train the shipped model on every job |
