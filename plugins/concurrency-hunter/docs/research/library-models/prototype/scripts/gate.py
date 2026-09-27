"""Gate: accept an AI label only when confidence >= 0.85 and the IL probe does not contradict it; otherwise fall back to the
unknown execution (framework-event), which is what the engine does today."""
import json
gold = json.load(open("gold.json")); il = {r["key"]: r["probe"] for r in json.load(open("il-verdicts.json"))}
RANK = {"invoke-now": 0, "startup": 0, "iterator": 1, "holder": 1, "di-registration": 1, "framework-event": 2}
KEPT = {"iterator", "holder", "stored", "di-registration", "invoke-now+stored"}
import os
for model in ("haiku", "sonnet", "haiku-decomp", "sonnet-decomp"):
    if not os.path.exists(f"ai-{model}.json"):
        continue
    ai = json.load(open(f"ai-{model}.json", encoding="utf-8"))
    exact = unsafe = narrowed = caught = total = 0
    for g, a in zip(gold, ai):
        if g["label"] == "non-delegate":
            continue
        for p in g["delegateParams"]:
            total += 1
            label, conf = (a.get("perParameter") or {}).get(p, a["label"]), a.get("confidence", 0)
            probe = il.get(g["key"], {}).get(p, {}) if isinstance(il.get(g["key"]), dict) else {}
            fate = probe.get("label", "escaped")
            contradicts = (fate == "invoke-now" and probe.get("clean") and label in KEPT | {"framework-event"}) or \
                          (fate in KEPT and label == "invoke-now")
            if contradicts and RANK.get(label, 2) < RANK[g["label"]]:
                caught += 1
            final = label if conf >= 0.85 and not contradicts else "framework-event"
            narrowed += final != "framework-event"
            exact += final == g["label"]
            if RANK.get(final, 2) < RANK[g["label"]]:
                unsafe += 1
                print(f"   unsafe {model}: {g['key']}.{p} truth={g['label']} final={final}")
    print(f"{model}: exact {exact}/{total}, narrowed {narrowed}, unsafe {unsafe}, unsafe answers the IL check caught {caught}")
