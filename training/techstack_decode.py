"""Turns per-token predictions into tech stack terms. Shared by training (scoring) and predict.py, and mirrored by
the C# extractor, so all three give the same answer. Needs only numpy."""
import numpy as np


def decode(text, token_probs):
    """token_probs: {(start, end): [probabilities from each window that saw the token]} → terms, in order."""
    spans, cur = [], None
    for (s, e) in sorted(token_probs):
        label = int(np.argmax(np.mean(token_probs[(s, e)], axis=0)))
        if label == 1 or (label == 2 and cur is None):
            if cur: spans.append(cur)
            cur = [s, e]
        elif label == 2:
            cur[1] = e
        else:
            if cur: spans.append(cur)
            cur = None
    if cur: spans.append(cur)
    # Widen each span to the whole word it's glued to, then merge overlaps: "NET" -> ".NET", "ASP" + "NET Core" ->
    # "ASP.NET Core", "C" -> "C#". Still the posting's own text.
    merged = []
    for s, e in sorted(widen(text, s, e) for s, e in spans):
        if merged and s <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], e)
        else:
            merged.append([s, e])
    terms = []
    for s, e in merged:
        term = text[s:e].strip()
        if term and term not in terms:
            terms.append(term)
    return terms


def widen(text, s, e):
    """Letters and digits glued on either side join the term; so does a '.' between word characters (ASP.NET,
    Node.js) or in front of one (.NET), and '#' or '+' right after (C#, C++). A sentence's final '.' doesn't."""
    while s > 0 and (text[s - 1].isalnum() or (text[s - 1] == "." and text[s].isalnum())):
        s -= 1
    while e < len(text):
        c = text[e]
        if c.isalnum() or c in "#+" or (c == "." and e + 1 < len(text) and text[e + 1].isalnum()):
            e += 1
        else:
            break
    return s, e
