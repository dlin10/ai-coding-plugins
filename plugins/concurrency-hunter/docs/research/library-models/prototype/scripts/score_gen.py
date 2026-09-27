import json
gen = json.load(open("generator-verdicts.json"))
il = {r["key"]: r["probe"] for r in json.load(open("il-verdicts.json"))}
RANK = {"invoke-now": 0, "iterator": 1, "holder": 1, "di-registration": 1, "framework-event": 2, "unknown": 3}
IL_SOUND = {"invoke-now": "invoke-now", "iterator": "iterator"}
def norm_gen(label):
    if label.startswith("invoke-now"): return "invoke-now"
    if label.startswith("holder"): return "holder"
    if label.startswith("framework-event"): return "framework-event"
    if label.startswith("unknown"): return "unknown"
    return label
rows = []
for g in gen:
    for p, label in g["perParameter"].items():
        e = norm_gen(label)
        i = IL_SOUND.get(il[g["key"]][p]["label"], "framework-event") if isinstance(il.get(g["key"]), dict) else "unknown"
        # union: take the narrower of two answers only when both are proven kinds; engine and IL never disagree on a proven kind here
        u = min((e, i), key=lambda x: RANK[x]) if RANK[e] != RANK[i] else e
        rows.append((g["key"], p, g["gold"], e, i, u))
for name, col in (("generator (engine)", 3), ("IL probe, sound", 4), ("union", 5)):
    exact = sum(r[col] == r[2] for r in rows)
    unsafe = sum(RANK[r[col]] < RANK[r[2]] for r in rows)
    print(f"{name:20} exact {exact}/{len(rows)}  unsafe {unsafe}")
print()
for r in rows:
    if r[3] != r[2] or r[4] != r[2]:
        print(f"  {r[0]}.{r[1]:22} gold={r[2]:12} engine={r[3]:16} il={r[4]:16} union={r[5]}")
