# Keep the Scout report as JSON only

Replaces the part of [0021](0021-add-a-lazily-selected-scout-worker.md) that made `SCOUT.md` the
Scout report.

## Context

A Scout Vendor answers in a structure — a summary and six categories of items, each item a text,
a source kind and a locator — and until now the server rendered that structure to markdown and
appended the text to `SCOUT.md`. The structure itself was never written down. Each item became one
line, `- {text} — {kind}: {source}`, but an item's text has no size limit and may hold a line break
or the same dash, so the file cannot be read back into its items with certainty.

The Evidence map ([0036](0036-advise-the-evidence-check-through-a-configured-advisor.md)) has to
account for every item of every answer by a stable identity and to know, for each repository
locator, what the code at that place looked like when the Scout answered. Neither can be recovered
from the markdown.

Three ways were weighed: keep `SCOUT.md` and parse it, which is fragile and would change its format
to make it parseable; write a structured file beside it, which leaves two records that can
disagree; or write a structured file and regenerate `SCOUT.md` from it as a view nothing reads.

## Decision

`scout-answers.json` in the Run folder is the Scout report and the only Scout record. Every
successful answer is appended as an entry holding its number, the question it answers and the
answer's structure as the Scout returned it, and, for each repository item, whether its locator
resolved and the hash of the code window around it at that moment. An item is identified by its
answer number, its category and its position; answers are only appended, so an identity never
changes. A failed or refused answer adds nothing, as before. There is no markdown rendering, and
`documents.scout` points at the JSON file.

The derived markdown view was rejected for the sake of a single file, accepting what that costs a
reader.

## Consequences

The user reads the Scout report as JSON, with its escapes, rather than as a rendered document; the
complete answer still arrives in each tool result, and the Orchestrator reads JSON as easily as
markdown.

A Run begun on an earlier version has a `SCOUT.md` that nothing reads any more. Its answers count in
the Run state but have no entries, and the Evidence map lists them as not indexed, to be checked by
hand.

Everything that named `SCOUT.md` — the skill, the domain language, the README, `AGENTS.md`, the Scout
tests — names `scout-answers.json` instead. The cumulative behaviour, the numbering from the Run
state and the sensitive-content check before the append are unchanged.
