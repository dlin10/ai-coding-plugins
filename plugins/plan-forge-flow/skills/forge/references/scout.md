# Forge reference: Scout, the Impact pass and the Evidence check

Read it at the first broad reconnaissance need, before the Impact pass that precedes the first `forge.plan.write`, and before every Evidence check.

## Scout reconnaissance

Use Scout for broad repository reconnaissance before the Orchestrator can settle requirements or
write a plan: multi-module exploration, caller/writer discovery, dependency tracing, or greenfield
orientation. These are the broad triggers. Keep one or two targeted semantic lookups in the
Orchestrator: they remain cheaper and clearer there. Use the local Roslyn or repository tools when
the question has a narrow file, symbol, or caller boundary. Do not turn every local lookup into a
Scout call. Scout has a second use point, the Impact pass below, which every plan gets.

Scout is lazy and independent of the separate Critic and Builder selections. On the first broad need,
make one just-in-time Scout selection round: call `forge.models`, offer up to three valid live/resolved
Vendor/model/effort combinations, clearly mark exactly one **recommended** combination, and include
the choice **continue without Scout**. Recommend the strongest combination the catalogue offers at high effort — judged by its position in the newest-first list, not by a remembered name — because the same selection serves the Impact pass, an exhaustive enumeration at which a weaker model invents test names. Do not silently select a Vendor or silently continue a session.
Before the question, say in a sentence or two what Scout is for and what continuing without it
costs: the Impact pass then falls to you.
A persisted decline suppresses later automatic Scout questions for this Run; enabling it later requires
an explicit `forge.scout.select` call. Scout selection is independent of and may happen before or
after final Critic/Builder selection; if the first need occurs before it, this Scout catalogue call
is still just-in-time and separate from the final refresh in `interview.md`.
When exactly one valid Vendor is available, omit the Scout Vendor question and use that Vendor for
the offered combination. Offer the Fast tier for Scout by
the same rule as for the other roles in `interview.md`, as its own yes/no after the combination is chosen, and
pass the answer as `fast` to `forge.scout.select`; it stays with the selection.

Run Scout beside your own work wherever its answer is not needed yet: start the question with
`forge.work.start` (`act: "scout"`, `question`, `sessionMode`), go on asking and writing, and poll
and fetch it at the first step that needs the answer. A question whose answer the next question to
the user waits on is a direct `forge.scout.run`. The orientation question is the first such job:
start it with `sessionMode: fresh` right after the Scout selection, ask the opening questions
`interview.md` names while it runs, and fetch it before the first question that needs the lay of
the land.

For every Scout call, pass `sessionMode` explicitly. Use `continue` only for a
direct follow-up in the same investigation. Use `fresh` for an independent question, a new
subsystem, stale evidence, or a deliberate reset. A `continue` runs alone: while a Scout job runs,
`forge.work.start` answers it with `started: false` and the running jobs in `activeJobIds`, and
`forge.scout.run` refuses it — fetch those jobs first. Fresh jobs run side by side, and Scout turns
that ran side by side leave no session to continue: after them, ask `fresh` and name in the question
the earlier answers it builds on. After a failure, retry with the saved selection;
reselect explicitly to clear the old session and failure; or explicitly choose continue without
Scout. Never silently choose session continuity and never fall back to another Vendor. A
`no_shell` failure is the exception to retrying at once: the Scout's commands never started, so it
read nothing, and the same selection fails the same way until its vendor is repaired. Tell the user
what the failure says, and ask whether to repair the vendor and retry, reselect, or continue without
Scout. Scout may use already available internet without per-call authorization or a widened grant. Require typed
repository citations (path plus positive line or non-empty symbol) and external citations (an
absolute HTTP(S) URL). Surface `scout-answers.json` only after each successful Scout call.
Do not copy Scout's report into Critic or Builder prompts automatically; only derived conclusions
the Orchestrator deliberately puts into a plan or task may reach those Workers.

### The Scout question

Write every Scout question — orientation or impact — with these parts:

- **Goal** — what is going to change and why, in one or two sentences.
- **Workspace and tooling** — which Roslyn MCP server and port serves this code, the absolute path
  to use with it, and that its reference and caller queries answer "who uses". A Scout left to
  guess falls back to text search.
- **Numbered, narrow sub-questions.**
- **Search scope** — named beyond `src/`: the test projects, `build/`, `skills/`, `docs/`,
  `prompts/`, and whatever else in the repository pins behaviour.

A question may run to 8,000 characters. Split an Impact pass by area into several questions when it
names more than six Change points or they do not fit, and run the parts as fresh jobs side by side.

### The Impact pass

After you have sketched the plan's approach — its mechanisms and the Change points they introduce —
and made its matrix check, and before the first `forge.plan.write`, ask the Scout one Impact pass.
Every plan gets one. It is a Scout need, so it starts the Scout selection round when none has been
made. Name every Change point the approach
introduces — symbols, contracts, wire and persisted formats, counters, prompts and skill text — and
ask, for each one:

- every consumer, and what its behaviour becomes;
- every test that asserts today's behaviour, with file:line and test method name;
- every artefact outside the code that pins current values or wording: snapshots, metrics and
  baselines, gate scripts, docs, specs, skills and prompts;
- for a rule — a predicate, a classification, an identity, a mapping — every other place that
  decides the same question, and whether it agrees today.

```text
Impact check for a drafted approach. Do not redesign or judge it; find everything it breaks or must also touch.

Planned change:
1) <change point, named by symbol or contract>
2) ...

For each of 1–N, list:
a. Every consumer of what changes and what its behaviour becomes.
b. Every test that asserts today's behaviour and would fail or need updating (file:line and test method name).
c. Every artefact outside the code that pins current values or definitions: recorded snapshots, metrics or baseline files, gate scripts, docs, spec or skill text.
d. For a change point that is a rule (a predicate, classification, identity or mapping): every other place that decides the same question, and whether it agrees today.

Search <the scope, beyond src/>.
```

Run the first Impact pass with `sessionMode: fresh`, so the orientation framing cannot anchor the
enumeration, and run it as a job. While it runs, ask the Critic and Builder questions — "Choosing
the vendor and model" in `interview.md` — and write the requirements, gates and tasks in full: the
tasks' depth follows the Builder choice, which is settled by then. Then poll and fetch the pass, and
fold its complete answer into the plan — tasks for affected consumers, tests to update, artefacts to
re-record — before the plan is written. A plan written before its pass returns is the failure the
pass exists to prevent. When a revision introduces a Change point no Impact pass has checked, ask
again with `sessionMode: continue` about the new Change points only, before its review — or
`fresh`, naming the answers it extends, after a pass split into parallel jobs. When the Run
continues without Scout, make the same check yourself with reference and caller queries and text search, and record what
you searched with `forge.log.append`.

### The Evidence check

After the matrix check and the Impact pass, write the first draft with `forge.plan.write`. Then,
after each Impact pass and before the review that follows it, make the manual Evidence check over
every entry of `scout-answers.json` — every answer of the Run, not only the last — comparing every
fact that bears on a Change point with the plan. Each such fact is either used by the plan or
departed from on purpose. Record a deliberate departure with `forge.log.append`, and patch it into
the plan as well — in the exclusions or the task text — wherever a Critic would otherwise raise
it: the Critic never reads the Run log.

With no configured Advisor, that completes the Evidence check. With one, use it after the manual
check; with several, ask the user once which Advisor to use, as for a Vendor, and pass its id as
`advisor` on every call. `forge.begin` and `forge.status` report `advisors`; a partly configured
Advisor is not configured. An Advisor answers typed Choice questions, and its Evidence map is
shadow-mode advice: a `covered` excuses nothing and removes no manual check.

1. Call `forge.evidence.map` with every Change point of the Run's Impact passes and your
   implications. Each implication has `text` and `derivedFrom`, the existing Scout item IDs it
   rests on (`A{number}.{category}.{position}`, with positions starting at 1). The server keeps
   judgments, the items complete maps have judged and the latest result in `evidence-map.json`.
   A complete map judges each item once: a later call asks only about items no complete map of
   the same Advisor and model has judged — those of new Scout answers and new implications — and
   counts the rest as `alreadyMapped`.
2. One call maps the whole map. Call again only when `complete` is false, and not after a call
   whose `checkedThisCall` is 0 unless step 5 says so. Keep the Change-point list the same from
   call to call until an Impact pass adds Change points, then append those.
   Only while the list would otherwise pass 200, merge related Change points into one entry each.
   If merging related ones cannot bring it to 200, make no
   further map call in this Run: log `Evidence map: stopped, N Change points` with
   `forge.log.append`, tell the user, and rely on the manual Evidence check alone from then on,
   as with no configured Advisor.
3. Take up only the flags the manual check had not handled, decide each as you always do, and
   patch the plan where needed. Settle a `contradiction` with evidence, never with a sentence:
   either name the task that changes the very thing the fact describes, or read the code the fact
   names or run a probe, and patch the plan with what that showed. Never answer one by writing
   into the plan that the code is unaffected or works as before without such a check — a Critic
   tests that claim, and an untested one comes back as a finding. Log one line
   `Evidence map: raised N, already handled M, new K, $C (Run $R of $B)` with `forge.log.append`,
   the call's `costUsd`, `runCostUsd` and `budgetUsd`, adding
   "incomplete" when the map did not complete and the not-indexed answer numbers when there are
   any. Not-indexed answers are evidence the map could not read:
   tell the user their numbers and make sure the manual Evidence check covered them, from the
   complete answers received in Scout results or from an earlier version's `SCOUT.md`, which you
   read yourself — the server never does.
4. After every later Impact pass, before the review that follows it, call the map again the same
   way: it asks only about the new answers' items and new implications. Take up their flags. A
   revision with no new Scout answer needs no map call: the map does not judge an item again
   against a changed plan, and the manual Evidence check stays yours. When the map did not
   complete, check every `new` unchecked item by hand. A revision
   with a new Change point gets its Impact pass and manual Evidence check before its review.
   Once the Run has used the map, `forge.plan.write`, `forge.plan.patch` and `forge.plan.review`
   answer with `evidenceMapWarning` when a Scout answer arrived after the last complete map,
   and a round that runs so also records it in the Flow log. A warning means this
   step is due: call the map before the next round. If you stopped the map under step 2 or 5,
   the warning repeats; tell the user once and go on.
5. Every result prices itself: `estimatedCostUsd` is what the call had to send, measured before
   sending; `costUsd` what it reported; `runCostUsd` the Run's Advisor spending so far; and
   `budgetUsd` the Run's Advisor budget, 5 USD until the user names another. When the Run's
   spending and a call's estimate together would pass the budget, the call sends nothing and
   answers `stoppedBy: "budget"`. Tell the user the three amounts and ask whether to raise the
   budget or stop the map for this Run. On a raise, call again with `budgetUsd` set to the amount
   they named; the Run keeps it. On a stop, log `Evidence map: stopped, budget` with
   `forge.log.append` and rely on the manual Evidence check alone from then on.

The Evidence map is never shown to the Critic, Builder or Scout and has no place in code review.
Each accepted map call attempts to write an `## Evidence map` Flow-log entry and an `advisor.usage`
record to
`telemetry.json`, with counts, models and usage, never item or plan text. The Telemetry file has
three kinds: `vendor.usage` for Worker attempts, `orchestrator.usage` for Orchestrator phases, and
`advisor.usage` for Evidence map calls.
