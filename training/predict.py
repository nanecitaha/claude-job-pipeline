"""Get the tech stack of a job description with a trained model. Needs only onnxruntime, tokenizers and numpy
(no PyTorch, no GPU):

    pip install onnxruntime tokenizers numpy
    python predict.py --model techstack-model posting.txt          # one comma-separated line
    pbpaste | python predict.py --model techstack-model            # Mac: whatever you copied
    python predict.py --model techstack-model --title "Senior .NET Engineer" posting.txt
    python predict.py --model techstack-model --json a.txt b.txt   # several postings, JSON per line

Include the job title when you have it: the model was trained on "title, blank line, description".
"""
import argparse
import json
import os
import sys

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

from techstack_decode import decode


class Extractor:
    def __init__(self, model_dir, threads=1):
        settings = json.load(open(os.path.join(model_dir, "settings.json")))
        self.tok = Tokenizer.from_file(os.path.join(model_dir, "tokenizer.json"))
        self.tok.no_padding()
        self.tok.enable_truncation(max_length=settings["maxLength"], stride=settings["stride"])
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = threads
        self.session = ort.InferenceSession(os.path.join(model_dir, "techstack.onnx"), opts,
                                            providers=["CPUExecutionProvider"])

    def extract(self, description, title=None):
        text = f"{title.strip()}\n\n{description.strip()}" if title else description.strip()
        text = text.replace("\r\n", "\n")
        first = self.tok.encode(text)
        token_probs = {}
        for w in [first] + first.overflowing:      # long postings: overlapping windows, like in training
            ids = np.array([w.ids], dtype=np.int64)
            mask = np.array([w.attention_mask], dtype=np.int64)
            logits = self.session.run(["logits"], {"input_ids": ids, "attention_mask": mask})[0][0]
            probs = np.exp(logits - logits.max(-1, keepdims=True))
            probs /= probs.sum(-1, keepdims=True)
            for j, ((s, e), special) in enumerate(zip(w.offsets, w.special_tokens_mask)):
                if not special and s < e:
                    token_probs.setdefault((s, e), []).append(probs[j])
        return decode(text, token_probs)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("files", nargs="*", help="job description text files (none: read standard input)")
    ap.add_argument("--model", default="techstack-model", help="folder from train_techstack.py")
    ap.add_argument("--title", help="job title, if the text doesn't start with it")
    ap.add_argument("--json", action="store_true", help="one JSON object per posting")
    args = ap.parse_args()

    ex = Extractor(args.model)
    inputs = [(f, open(f, encoding="utf-8").read()) for f in args.files] or [("-", sys.stdin.read())]
    for name, text in inputs:
        terms = ex.extract(text, args.title)
        print(json.dumps({"file": name, "techStack": terms}, ensure_ascii=False) if args.json else ", ".join(terms))


if __name__ == "__main__":
    main()
