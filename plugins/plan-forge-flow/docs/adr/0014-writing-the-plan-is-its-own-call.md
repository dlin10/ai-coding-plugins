# Write the plan in its own call, and let the round read it from disk

docs/adr/0009 put `PLAN.md` on disk before the critic starts so the user would have the plan to read
during the minutes a round takes. Measured in run `20260904-173914-9254ec`, the user got the link
seven minutes after the file existed, and the round it belonged to was already over:
the orchestrator streamed a 52 KB draft into `forge.plan.review` for about five minutes, the server
wrote the file the moment the call arrived at 18:19:40, the critic ran for six more, and the path —
which travels only inside `documents`, built after the critique — reached the orchestrator at
18:26:00. The plan was written to be watched while the round ran, and handed over when it was done.

Both halves of that are now split apart. **`forge.plan.write` writes the draft and answers with
`documents` and nothing else**, running no worker and taking seconds; `planDraft` on
`forge.plan.review` — and on `forge.work.start` for act `plan.review` — is optional, and a round
handed none reviews the file. So a round is write → surface the path → review, the link arrives
before the critic starts rather than after it stops, and the 50–90 KB draft crosses the wire once
instead of twice. On Cursor the same fact reaches the same place through `WorkStartResult` and
`WorkPollResult`, which now carry `documents` too: the plan is on disk before the job is started, so
neither the start nor the polls that follow have any reason to withhold it until the fetch.

**The flow log deliberately does not move.** `flow_log.md` is first created when the first critique
is appended, so `documents.flowLog` arriving with that critique is already the earliest honest
moment for it. A path handed over before the file exists is a dead link on the host this was written
for, which is the same reason the skill-only version of this change was rejected: telling the
orchestrator that `forge.begin` returns `runPath` and that `PLAN.md` appears under it costs nothing
and delivers nothing on round 1, because during the five minutes the draft is streaming the file is
not there yet. It would have been live from round 2 on, holding the previous round's draft.

**The withdrawal of an approval moves with the write it guards.** A plan rewritten by the write tool
is text nobody approved, exactly as a plan rewritten inside a review round is, so `forge.plan.write`
clears `approved`, resets `tasksCompleted` and drops the builder session, in that order and before
the file changes — the ordering docs/adr/0009 established, for the reason it established it. What
the write deliberately does not consult is the round cap: writing a draft is not reviewing it, and a
write whose round is then refused at the cap leaves the user reading the very draft they are being
asked about.

**What is given up is that the draft under review is now identified by a file rather than by an
argument.** A round that omits `planDraft` reviews whatever `forge.plan.write` last wrote, so an
orchestrator that revises the plan and forgets to write it has the critic judge the previous draft,
and nothing here can tell the difference — the same class of assertion as `approved` in
docs/adr/0003. What is caught is only the empty case: a round with no draft anywhere is refused,
before a background job is started rather than inside it. The alternative that avoided this — keeping
`planDraft` required and adding the write beside it — was rejected because it doubles the streaming
that caused the lag, and a round would then have paid two five-minute uploads to save one.
