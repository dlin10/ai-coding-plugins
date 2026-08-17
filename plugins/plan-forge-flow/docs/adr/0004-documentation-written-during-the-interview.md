# Let the interview write documentation and keep it out of delegated review

## Context

The documented interview mode maintains the domain model as the conversation resolves terms and
decisions. `CONTEXT.md` and ADRs are therefore part of that interview's work product: allowing the
orchestrator to write them records the vocabulary and durable trade-offs while they are established,
before the implementation plan is approved. The write boundary is deliberately narrow; before
approval the orchestrator may write only those documentation paths in documented mode.

Those files are not implementation changes for the delegated workers to judge. Including them in
the baseline comparison would report the interview's own documentation as working-tree drift, and
including them in the code-review diff would send interview notes and decisions to the critic and
builder. The server also cannot rely on a mode flag: these tools do not receive a reliable interview
mode signal, and callers do not declare where their documentation lives. The path boundary therefore
has to be a stable server invariant rather than behaviour selected by a caller.

The sensitive-path guard follows the same boundary. It exists to stop a secret reaching a vendor, and
a path excluded from the diff has no contents to reach one; guarding on its *name* would abort the
run for no gain. `SensitiveName()` matches any segment containing `secret`, `token`, `password` or
`credential`, so an ADR legitimately called `0005-token-rotation.md` would otherwise kill every code
review for the rest of the run, fixable only by renaming the file.

## Decision

Use one shared Git pathspec for the baseline, drift, code-review diff and sensitive-path guard:
exclude every `CONTEXT.md` and every `docs/adr/**` path at any depth. Apply it to both content diffs
and changed name lists. Run the guard before deciding that the filtered diff is empty, so it still
sees a documentation-only tree. Do this unconditionally; the server does not vary the filter by
interview mode.

The documented interview may write the two documentation kinds within the write boundary described
by the forge skill. The delegated critic and builder receive only the remaining working-tree diff.

## Consequences

Documentation written by the orchestrator during the interview no longer appears in `forge.status`
drift and is not expected by code review. A sensitive-looking name under an excluded documentation
path no longer aborts the run.

Two costs were accepted knowingly. A third party's edit to `CONTEXT.md` or an ADR is now invisible to
both drift and code review — the filter cannot tell whose edit it is, and no mode flag was added to
let it try. And the pathspec governs only what is handed to a vendor, not what a vendor can reach: a
worker runs in the workspace and can read an excluded file it was never sent. Isolating worker
filesystem access is a different problem, and it is not solved here.

A newly created, untracked ADR was already invisible before this change, because `git diff` lists
neither untracked nor staged files. That limit is left as it is; widening the window is its own
change, with its own review.
