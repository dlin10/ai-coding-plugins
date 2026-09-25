# Add a lazily selected Scout Worker for orientation and impact

## Context

The Orchestrator needs broad repository reconnaissance at two moments. Before it can settle
requirements or write a plan, it needs to know where things are. Once it has drafted an approach,
it needs to know what else depends on what that approach changes. Keeping multi-module exploration,
caller and writer discovery, or dependency tracing in the host conversation materially expands its
context. The Critic and Builder cannot absorb that work: the Critic judges independently, while the
Builder edits against an approved plan.

The Vendor seam already provides read-only Worker launches, structured output, resumable sessions,
live model catalogues, Worker-tool grants, sensitive-input checks, and Run-scoped state. A
reconnaissance role can reuse those facilities without turning evidence into another source of
requirements or instructions.

The first version asked the Scout only the first kind of question, returned a digest bounded to three
items per category, and kept only the latest answer on disk. An audit on 2026-09-24 of the four Runs
that had used it measured the result:

- **Accurate, not complete.** Of 128 claims, 108 were correct, 16 partial, 1 badly cited and 3
  false. But about 57 of about 280 plan-review findings traced to the Scout, almost all to
  omissions, and the same four recurred in every Run: who else uses what changes; the tests, gates
  and snapshots that pin current behaviour; surface outside the code (`skills/` went unsearched);
  and facts the Scout had read but left out of its report.
- **Correct facts that never arrived.** Eight Critic findings re-discovered a fact the Scout had
  already reported correctly. In the one Run whose report survived, the cited facts sat at positions
  7, 8, 15, 20 and 22 of a 24-item category, and the Orchestrator had received the first three. The
  other Runs' reports had been overwritten by later answers, so the same check could not be made.

Two blind A/B experiments on one historical question then separated the question from the model.
Asked as an orientation question, every model — the strongest included — found 1 of 9 consumers of
the changed symbol and 0 of 3 key pinned tests. Asked as an impact question that named the change
points and asked for consumers, pinned tests and artefacts beyond `src/`, the strongest model found
9 of 9 and 3 of 3 in both runs, taking about ten minutes; the cheapest model scored 13–14 and 6 of
17 and invented two test method names at real cited lines.

## Decision

Add **Scout** as a fourth Worker role. It receives one bounded research question and the minimum
Run context needed to answer it, runs under the selected Vendor's existing read-only boundary, and
uses only the Run's granted read-only Worker tools. It returns sourced evidence and never plans,
judges, edits, or settles requirements. The Orchestrator remains responsible for deciding what the
evidence changes.

The Scout is asked at two use points. An **orientation** question comes whenever broad
reconnaissance is needed before an approach exists. An **Impact pass** comes after the approach is
drafted and before the plan is first written for review: one question naming every Change point and
asking, for each one, for its consumers, the tests asserting its current behaviour, and the
artefacts outside the code that pin it, over an explicitly named search scope. The Impact pass is
required for every plan and repeats when a revision introduces a new Change point. After each one
the Orchestrator makes an **Evidence check**: every fact bearing on a Change point is either used by
the plan or departed from on purpose, and a deliberate departure goes to the Run log and, wherever a
Critic would otherwise raise it, into the plan. A Run that continues without Scout still gets both
checks, made by the Orchestrator with its own tools.

Every answer carries six categories, the sixth being **Dependents and pinned behaviour** for every
location in the likely change surface; a location with nothing found says so and names what was
searched. Every name the report gives must appear at the line it cites, everything read that bears
on the question belongs in the report, and "who uses X" comes from reference and caller queries
rather than from symbol search when a Roslyn server is granted.

Scout selection is lazy and independent of the Critic and Builder selections. On the first need —
which the Impact pass always is — the Orchestrator reads the live catalogues and asks for one valid
Vendor, model, and effort combination, also offering to continue without Scout. Because the same
selection serves the Impact pass, the recommended combination is the strongest the catalogue offers
at high effort, judged by its position in the newest-first list rather than by a remembered name.
The exact choice is retained for the Run and reused for every call without another question.
Choosing to continue without Scout suppresses later automatic questions for that Run, although an
explicit later choice may enable it. A failed Scout never silently falls back; retry keeps the
selection, while explicit reselection starts a fresh Scout session and is recorded.

Calls resume one Scout session when the Vendor supports it, and the Orchestrator chooses
continuation explicitly: the first Impact pass starts fresh, so the orientation framing cannot
anchor the enumeration, and a repeat for new Change points continues it. Questions are bounded at
8000 characters, enough to name a phase's Change points and too little to paste a plan the Scout
would then start judging.

The tool result carries the complete answer, unclipped. `SCOUT.md` is cumulative: every successful
answer is appended as its own numbered section headed by its question, numbered from a counter in
the Run state rather than from the file's content, and an answer that fails or is refused before it
is appended adds nothing. The Run itself changes nothing in the working tree before the first build,
so earlier answers stay valid through the plan phase unless someone edits the tree meanwhile.

Scout evidence is not automatically copied into Critic or Builder prompts. Both the question sent to
the Vendor and the report written to disk pass the applicable sensitive-content checks, and the
report stores neither raw secrets nor unrestricted command output. Scout may always use internet
access that its Vendor or the Run's granted read-only Worker tools already provide; there is no
second per-call permission. Repository evidence cites a path plus line or symbol, while external
evidence cites its URL. This decision adds no connector and widens no Worker-tool grant.

## Consequences

Broad discovery moves to a persistent read-only Worker without changing who owns requirements,
planning, approval, or review. One or two targeted semantic lookups remain cheaper and clearer in
the Orchestrator.

Every plan now waits for an Impact pass before its first review — about ten minutes on the model
measured reliable at it — in exchange for review rounds not spent re-discovering dependents. Nothing
enforces the pass or the Evidence check; like the rest of the loop they are the skill's guidance,
per [0002](0002-mcp-server-surface-without-enforcement.md).

The Orchestrator's context grows by each complete report, a few kilobytes, rather than by the
exploration behind it, which stays in the Scout's session. The bounded digest is gone: it saved that
much and cost the facts the Critic later had to find.

`SCOUT.md` grows through the Run and is the Evidence check's source after the host compacts its
context. Once building begins, an earlier answer can describe code the Builder has since changed;
each section carries its number and its question, and the Scout is a plan-phase tool.

The Run state and status surface carry a third independent Worker selection and session identity.
The MCP surface, background-Job dispatch, source-generated JSON contracts, prompts, packaging
assertions, and orchestration skill all recognize Scout. Each Vendor must prove that Scout uses the
same read-only launch boundary as a Critic. A Scout failure leaves the report and the chosen
selection explainable and requires the Orchestrator to retry, explicitly reselect, or continue
without Scout.
