"""Generator (synthesised drivers + engine) against the Sonnet oracle on every definite label of the breadth sample."""
import collections
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
RANK = {"invoke-now": 0, "iterator": 1, "holder": 1, "framework-event": 2}


def kind(label):
    for prefix in ("invoke-now", "iterator", "holder", "framework-event", "unknown"):
        if label.startswith(prefix):
            return prefix
    return label


rows = []
for i in range(4):
    members = json.load(open(os.path.join(HERE, f"oracle-members-{i}.json"), encoding="utf-8"))
    path = os.path.join(HERE, f"oracle-sonnet-{i}.json")
    if not os.path.exists(path):
        print("missing", path)
        continue
    oracle = json.load(open(path, encoding="utf-8"))
    for member, verdict in zip(members, oracle):
        for param, generated in member["generator"].items():
            o = (verdict.get("perParameter") or {}).get(param) or {}
            rows.append((member["key"], param, kind(generated), generated, o.get("label", "missing"), bool(o.get("alsoNow")),
                         o.get("confidence", 0), verdict.get("why", "")))

outcome = collections.Counter()
suspects = []
for key, param, gen, raw, ora, also_now, conf, why in rows:
    if ora in ("unknown", "missing"):
        outcome["oracle undecided"] += 1
    elif gen == ora:
        outcome["agree"] += 1
    elif RANK[gen] < RANK[ora]:
        outcome["generator narrower (suspect)"] += 1
        suspects.append((key, param, raw, ora, also_now, conf, why))
    elif RANK[gen] > RANK[ora]:
        outcome["generator wider (safe)"] += 1
    else:
        outcome["same exposure, other kind"] += 1
print(f"{len(rows)} definite parameters: {dict(outcome)}")
by_kind = collections.Counter((gen, ora if ora in RANK else "undecided") for _, _, gen, _, ora, *_ in rows)
for (gen, ora), n in sorted(by_kind.items()):
    print(f"   generator {gen:11} oracle {ora:16} {n}")
print("\nsuspects (generator narrower than the oracle):")
for key, param, raw, ora, also_now, conf, why in suspects:
    print(f" - {key[:110]}\n     {param}: generator={raw[:80]} oracle={ora}{' alsoNow' if also_now else ''} conf={conf} | {why[:150]}")
