---
status: accepted
---

# Tool results carry what the Orchestrator acts on, compact and unescaped

Issue #176. Amends [0026](0026-a-builder-session-per-task-and-per-round.md), whose `forge.status`
showed `builderSessionScope` and `taskChanges` with the rest of the run state.

## Context

The Orchestrator's context fills between compactions, and forge tool results are part of what fills
it. The study on issue #176 measured the concurrency-hunter runs of 12–20 October 2026 (A9 `6fc7b1`,
A10 `5c1f5a`, B3 `675224`, B3b `7586e3`, B3c `0d2a47`) from the Orchestrator transcripts, pricing
each result in tokens by regressing context growth on 452 single-result turns:

- Forge results were 10–24% of the context growth over a run; two thirds was the model's own
  output, prompts and reminders. No change to results removes more than about one compaction.
- Every result was serialised with the default encoder and most of them indented. An escaped
  Cyrillic letter (`В`) cost about 4.1 tokens, the letter itself 0.5. On the two runs held in
  Russian, two thirds of the forge result tokens were escapes and indentation: B3c 495k → 183k,
  B3b 314k → 105k, 0.5–0.7 of a compaction window each. English runs lost 10–13%. The run files had
  switched to relaxed escaping in `61953bf`; the results never did.
- `forge.work.fetch` carried the act's JSON as an escaped string inside JSON.
- `forge.status` returned the whole `RunState`: the files each task changed (5 KB of a 6.6 KB
  `run` by the end of a build), the builder's resume token, vendor and scope, the Worker tool
  patterns — and the gate environment's values, which `forge.log` then recorded in full, although
  ADR 0015 says only their names are logged.
- A passing `forge.build.next` carried the Builder's account, its evidence, its file list and the
  gate's command and output. Nothing in the skill acts on them on a pass; the Flow log keeps them.
- A10 polled 1,437 times, and half of what polling put in context was the same `documents` object.

## Decision

1. **One filter at the edge sends every JSON result compact, with its text as written.**
   `ToolResultText.Compact` rewrites each JSON text block with `UnsafeRelaxedJsonEscaping` and no
   indentation, after the tool and every helper that attaches a warning have produced it. Values
   are written back as read; only their spelling changes. A text that is not a JSON object or array
   passes untouched.
2. **`forge.work.fetch` carries the act's payload as an object.**
3. **`forge.status` shows `StatusRun`, not `RunState`:** everything the Orchestrator decides from,
   and none of the server-only state — `taskChanges`, `builderSessionId`, `builderVendor`,
   `builderSessionScope`, `workerTools`, `builderRoots`, `startedAt`, `baselineHead`. The gate
   environment goes out as its sorted names, as the resume card already printed it.
   `forge.plan.confirm` answers with `approved` and `taskCount` only: the Orchestrator reads drift
   from `forge.status` before it asks, the only moment drift can matter.
4. **A clean pass goes out as its verdicts.** When the Builder said `done`, its own checks
   `passed` and the gate `passed`, `forge.build.next` (directly or through a Job) drops `summary`,
   `filesChanged`, `verification.evidence`, `gate.command` and `gate.output`. Any other result goes
   out whole: a failed or unrun gate, a blocked turn, and a pass the Builder could not check itself
   are the moments its words are read. The ledger, the replay and the Flow log keep the whole result.
5. **What the Orchestrator already has stays out:**
   - a poll carries `documents` only when they changed since the job last answered with them;
   - `forge.plan.patch` no longer echoes `baseHash`, the applied edits or their sizes, which the Flow
     log and `forge.log` record;
   - `forge.models` drops each model's vendor description.

## Rejected

- **No outline on `forge.plan.patch` and `forge.plan.write`** (issue #170's proposal).
  `forge.plan.confirm` takes `planHash` from that outline, and ADR 0035 returns it so the next patch
  needs no `forge.plan.outline` call; each extra call costs its own overhead plus the outline anyway.
- **The ledger summary behind a flag.** It is 0.3–0.5 KB and is what the skill reads for pending
  findings and fix timing.
- **A relaxed encoder in each serialiser.** The results are built in a dozen places; one filter
  decides it for all of them, including results added later.
- **Cutting the critique's findings, the Scout report or the `documents` paths.** Each is read by the
  skill and has no other source in context; ADR 0021 already tried and undid a clipped Scout report.

## Consequences

On the measured runs the decisions together would have saved about 0.07 of a compaction window on
A9 and B3, held in English, and 0.52–0.82 on B3b and B3c, held in Russian; most of it is the
encoding. A10's 0.6 came mostly from its polls, which the long poll of issue #169 has since cut.

The Orchestrator no longer sees, without asking: the Builder's words on a clean pass (in
`flow_log.md`), drift on the confirm result (in `forge.status`), the gate environment's values
(the user gave them), and the server's own bookkeeping. After a compaction nothing changes: the
resume card read the run files, never these fields.

`forge.log` records a result before the filter, so its `tool.result` still holds the indented text,
truncated at 2,000 characters as before; `forge.status` no longer writes gate values into it.
