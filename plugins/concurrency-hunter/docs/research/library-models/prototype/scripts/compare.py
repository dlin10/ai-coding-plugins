"""Score the IL probe and the AI-from-docs runs against the truth (source verdicts when present, else my gold labels)."""
import json
import os
import sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
TRUTH_SOURCE = sys.argv[1] if len(sys.argv) > 1 else "gold"


def load(name):
    path = os.path.join(HERE, name)
    return json.load(open(path, encoding="utf-8")) if os.path.exists(path) else None


gold = load("gold.json")
source = load("source-verdicts.json")
truth = {}
for n, g in enumerate(gold, start=1):
    truth[g["key"]] = {p: g["label"] for p in g["delegateParams"]} or {"-": g["label"]}
if TRUTH_SOURCE == "source" and source:
    for n, (g, s) in enumerate(zip(gold, source), start=1):
        per = s.get("perParameter") or {}
        truth[g["key"]] = {p: per.get(p, s["label"]) for p in g["delegateParams"]} or {"-": s["label"]}

# how exposed to concurrency the delegate is under each label; predicting lower than the truth is an unsafe narrowing
RANK = {"invoke-now": 0, "startup": 0, "iterator": 1, "holder": 1, "di-registration": 1, "framework-event": 2, "unknown": 3}
IL_MAP = {"invoke-now": "invoke-now", "iterator": "iterator", "holder": "holder", "stored": "holder",
          "di-registration": "di-registration", "invoke-now+stored": "holder", "escaped": "unknown", "no-effect": "invoke-now"}


# What the IL probe can actually prove: that a delegate runs before the call returns, or that it is kept and returned inside a
# sequence. Anything merely stored or registered may run later on any thread, so the sound reading is the unknown execution.
IL_MAP_SOUND = {**IL_MAP, "holder": "framework-event", "stored": "framework-event", "di-registration": "framework-event",
                "invoke-now+stored": "framework-event"}


def predictions_ai(rows):
    out = {}
    for g, r in zip(gold, rows):
        per = r.get("perParameter") or {}
        out[g["key"]] = {p: (per.get(p, r["label"]), r.get("confidence")) for p in g["delegateParams"]}
    return out


def predictions_il(rows, mapping=IL_MAP):
    out = {}
    for r in rows:
        if not isinstance(r["probe"], dict):
            continue
        out[r["key"]] = {p: (mapping.get(v["label"], "unknown"), 1.0 if v["clean"] else 0.5) for p, v in r["probe"].items()}
    return out


def score(name, predictions):
    exact = unsafe = total = 0
    unsafe_rows, wrong_rows = [], []
    by_conf = Counter()
    for g in gold:
        if g["label"] == "non-delegate":
            continue
        for p in g["delegateParams"]:
            t = truth[g["key"]][p]
            if t == "non-delegate":
                continue
            pred, conf = predictions.get(g["key"], {}).get(p, ("unknown", 0))
            total += 1
            ok = pred == t
            exact += ok
            bucket = "hi" if (conf or 0) >= 0.85 else "lo"
            by_conf[(bucket, ok)] += 1
            if not ok:
                wrong_rows.append(f"{g['key']}.{p}: truth={t} pred={pred} conf={conf}")
            if RANK.get(pred, 3) < RANK.get(t, 3):
                unsafe += 1
                unsafe_rows.append(f"{g['key']}.{p}: truth={t} pred={pred} conf={conf}")
    print(f"== {name}: exact {exact}/{total}, unsafe narrowings {unsafe}")
    hi = by_conf[("hi", True)] + by_conf[("hi", False)]
    if hi:
        print(f"   confidence>=0.85: {by_conf[('hi', True)]}/{hi} right; <0.85: {by_conf[('lo', True)]}/{by_conf[('lo', True)] + by_conf[('lo', False)]} right")
    for row in wrong_rows:
        print("   wrong ", row, "  <-- UNSAFE" if row in unsafe_rows else "")


print("truth:", TRUTH_SOURCE if (TRUTH_SOURCE == "gold" or source) else "gold (no source verdicts yet)")
for name, file, kind in [("IL probe", "il-verdicts.json", "il"), ("AI haiku (docs only)", "ai-haiku.json", "ai"),
                         ("AI sonnet (docs only)", "ai-sonnet.json", "ai"),
                         ("AI haiku (decompiled)", "ai-haiku-decomp.json", "ai"), ("AI sonnet (decompiled)", "ai-sonnet-decomp.json", "ai")]:
    rows = load(file)
    if rows is None:
        print("missing", file)
        continue
    score(name, predictions_il(rows) if kind == "il" else predictions_ai(rows))
    if kind == "il":
        score(name + ", sound reading", predictions_il(rows, IL_MAP_SOUND))
