"""From synth-breadth.json, list the parameters the generator labelled definitely (the only ones that can be unsafe) in the
gold.json shape the decompiler harness reads, split into chunks for the oracle."""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
rows = json.load(open(os.path.join(HERE, "synth-breadth.json"), encoding="utf-8"))
DEFINITE = ("invoke-now", "iterator", "holder")
members = []
for row in rows:
    params = [p for p, label in row["Labels"].items() if label.startswith(DEFINITE)]
    if params:
        # Roslyn's declaration id carries "~ReturnType"; the XML-doc form the decompiler resolves does not, except for conversions
        doc_id = row["Member"] if ".op_Implicit" in row["Member"] or ".op_Explicit" in row["Member"] else row["Member"].split("~")[0]
        members.append({"key": row["Member"], "id": doc_id, "assembly": row["Library"], "version": "", "delegateParams": params,
                        "label": "", "generator": {p: row["Labels"][p] for p in params}})
CHUNKS = 4
for i in range(CHUNKS):
    chunk = members[i::CHUNKS]
    json.dump(chunk, open(os.path.join(HERE, f"oracle-members-{i}.json"), "w", encoding="utf-8"), indent=1)
print(len(members), "members,", sum(len(m["delegateParams"]) for m in members), "definite parameters")
