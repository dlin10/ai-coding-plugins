"""Summarise synth-breadth.json: how often a driver is synthesised, and what the engine then says."""
import collections
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
rows = json.load(open(os.path.join(HERE, sys.argv[1] if len(sys.argv) > 1 else "synth-breadth.json"), encoding="utf-8"))


def kind(label):
    for prefix in ("invoke-now", "iterator", "holder", "unknown", "framework-event"):
        if label.startswith(prefix):
            return prefix
    return label


by_library = collections.defaultdict(list)
for row in rows:
    by_library[row["Library"]].append(row)

for library, items in by_library.items():
    compiled = [r for r in items if r["Compiled"]]
    engine_ok = [r for r in compiled if r["EngineError"] is None]
    labels = collections.Counter(kind(label) for r in engine_ok for label in r["Labels"].values())
    params = sum(labels.values())
    definite = labels["invoke-now"] + labels["iterator"] + labels["holder"]
    print(f"== {library}: members {len(items)}")
    print(f"   driver compiled      {len(compiled)}/{len(items)}")
    print(f"   receiver was null    {sum(r['ReceiverNull'] for r in compiled)}   default! fallbacks in {sum(r['NullFallbacks'] > 0 for r in compiled)} drivers")
    print(f"   engine ran           {len(engine_ok)}/{len(compiled)}")
    print(f"   parameters labelled  {params}: definite {definite} ({100 * definite / max(1, params):.0f}%) -> {dict(labels)}")
    print(f"   seconds per member   {sum(r['Seconds'] for r in items) / max(1, len(items)):.1f}")
    for message, count in collections.Counter((r["CompileError"] or "")[:110] for r in items if not r["Compiled"]).most_common(5):
        print(f"      no-compile x{count}: {message}")
    for message, count in collections.Counter((r["EngineError"] or "")[:110] for r in compiled if r["EngineError"]).most_common(5):
        print(f"      engine error x{count}: {message}")
