# The run folder follows the session, and the workspace root keeps the review

`workspaceRoot` arrives as a tool argument and used to decide three unrelated things: where
`.forge/<runId>/` lives, the git window the baseline and the code-review diff are taken over, and the
working directory the critic and builder processes get. Nothing ever tied it to the host's own
directory, and nothing should: the orchestrator picks it from the shape of the task, so on a monorepo
it correctly names the repository root. The first of the three jobs was the one that never belonged
to it. `PLAN.md` and `flow_log.md` are written to be read by a person, and Claude Code renders a file
reference as a link only when the href is relative to the session's working directory — so the run's
most-read document arrived as a backticked absolute path that renders as plain text, two directory
levels above where the user was sitting. Issue #53 measures the run it happened in.

So the run folder is asked for rather than passed in: it goes to the first directory the client
declares through MCP's roots capability, and to `workspaceRoot` when the client declares none.
`SessionRoots` asks once per connection — roots belong to the connection, and every tool call has to
know where the run folder is before it can do anything — and `RunDirectory.OpenAsync` tries the
session root first and the workspace root second, so a run begun under the old layout is still found
by the same workspace root and run id it always was. The other two jobs do not move. The git window
and the workers' working directory stay on `workspaceRoot`, which is what keeps a run started against
a repository root reviewing the whole repository.

Rejected, and the reason this is not a one-line change: **instructing the orchestrator to pass the
session directory as `workspaceRoot`.** It is the zero-code fix and it silently shrinks the review.
`GitPathspec.WithoutDocumentation` opens with `"."`, a `.` pathspec is resolved against git's own
working directory, and `git ls-files --others` is working-directory scoped too — so the baseline, the
drift shown at approval and the diff handed to the critic would all have stopped at the subdirectory,
and the root files the same plan rewrites would have been invisible in exactly the way issue #25
describes. The workers would have run in the subdirectory as well. `CONTEXT.md` carries the
measurement.

Rejected too: **replacing the `workspaceRoot` + `runId` pair with the `runPath` `forge.begin` already
returns**, with `workspaceRoot` read back out of `state.json`. It is the better surface — one argument
per call instead of two, and `RunDirectory.FromPath` is already there for the job registry — and it
works on every host rather than on the one that declares roots. It is also a breaking change to
thirteen tools, the three host manifests, `build/package.ps1` and the skill, and it is not worth a
breaking minor on its own; it should ride with the next surface change that earns one. The
measurement decided between them: claude-code declares roots and answers with the session directory,
so the host where the symptom was measured is the host the non-breaking fix reaches.

`SessionRoots` is bound from the container the way `JobRegistry` and `CatalogCache` already are, so
no tool changed shape and nothing reached the published schema — `build/package.ps1` asserts that
`forge.begin` still takes `workspaceRoot` and nothing else, against the real handshake. The declared
capability, not the shape of the answer, decides whether to ask: the two hosts that declare no roots
answer a `roots/list` anyway, one with an empty list and one with `-32601`, and neither is worth
sending a request it never advertised.

The cost accepted: roots is deprecated by the specification of 2026-07-28 (SEP-2577), on the grounds
of vague semantics and low adoption, and names no successor — after it goes, nothing in MCP tells a
server where the user is sitting. Deprecated features stay functional for a year of spec versions,
and the fallback is what makes that a degradation rather than a deadline: a host that stops declaring
the capability reads as a host that never had one, and the run folder goes back under
`workspaceRoot`. The removal costs the clickable link, not the run. If it lands before a successor
does, the surface change above is the answer.
