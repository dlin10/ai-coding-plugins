---
status: accepted
---

# Resume the Orchestrator after a compaction from the run's own files

Issue #170, parts 1 and 2. Part 3, smaller tool results, went to issue #176 to be measured first.
Builds on [0038](0038-approve-the-plan-on-disk-by-its-hash.md), which made a call that lost its
argument shapes explain itself; this one gives the shapes back before the call.

## Context

The Orchestrator's host compacts its context several times a run — 3, 1, 5 and 6 times in the
concurrency-hunter runs A10, B3, B3b and B3c — and afterwards works from a summary it wrote itself.
Claude Code re-injects an invoked skill capped at 5,000 tokens, keeping its start; the forge skill
was 97 KB, about 25k tokens. Right after B3c's third compaction the Orchestrator called
`forge.plan.show` and `forge.plan.confirm` with shapes copied from a table row of its summary and
was refused twice, and it declared no phase for the rest of an eleven-hour run. `CONTEXT.md` has
the measurements.

## Decision

**A third entry point prints a resume card.** `planforge.exe --resume-card [<directory>]` serves no
MCP, like the Telemetry collector ([0032](0032-record-the-orchestrator-per-declared-phase.md)): it
reads a Run's `state.json`, `PLAN.md`, `decision-ledger.json`, `orchestrator-phase.json` and the
tool calls in `forge.log`, and prints, in under 3 KB for B3c, the run id and folder, approval, tasks
built, rounds and caps, the saved `fullGate`, the gate environment's names, the critic's and the
builder's selection as last passed and Scout's, the finding IDs by disposition and those pending
full verification, where the run stands, the next calls with their exact argument shapes — `planHash`
filled in, `decisions` as one object, `plan` and `planDraft` never — and the skill reference to read
before the next call. It names the declared phase too, and says so when the run has moved past it.
It always exits 0 once its arguments parse; an unreadable Run still sends the Orchestrator to
`forge.status`.

**It prints only for this Host session's Run.** The issue asked for the newest Run under `.forge/`.
A folder keeps its Runs for weeks, and the hook runs after every compaction of every session there,
so the newest Run would follow sessions that have nothing to do with forge. The card picks the
newest Run whose open phase in `orchestrator-phase.json` names this session, the id the server
already writes there at each phase opening. A Run that declared `done` keeps no session and gets no
card. Each host hands the hook its session its own way — measured, see `CONTEXT.md`.

**Claude Code and Codex each run it from a hook file of their own, named by its manifest.** The
issue named one `hooks/hooks.json`; the two hosts need different commands, and both load a plugin's
`hooks/hooks.json` by convention, Claude Code beside the file its manifest names — so neither file
has that name.

- Claude Code: `hooks/claude.pff.hooks.json`, named by `.claude-plugin/plugin.json` — `SessionStart`,
  matcher `compact`, `"shell": "powershell"`, the launcher under `${CLAUDE_PLUGIN_ROOT}` with
  `$env:CLAUDE_PROJECT_DIR`, where a session's Runs live
  ([0011](0011-the-run-follows-the-session-not-the-workspace.md)); the session is the
  `CLAUDE_CODE_SESSION_ID` the hook inherits. PowerShell because an exec-form hook cannot start a
  `.cmd` and Git Bash may be absent.
- Codex: `hooks/codex.pff.hooks.json`, named by `.codex-plugin/plugin.json` — `SessionStart`, matcher `compact`,
  `commandWindows` `cmd.exe /d /c "${PLUGIN_ROOT}\bin\planforge-launcher.cmd" --resume-card`, a form
  both PowerShell and `cmd.exe` run. With no directory the card reads Codex's hook input from stdin:
  `session_id`, the root thread id the server records from `_meta.threadId`, and `cwd`, where a
  Codex session's Runs live, since Codex declares no roots. `command` is `exit 0`: only a
  non-Windows Codex, which this plugin does not support, would run it. Codex runs the hook only
  once the user has trusted it through `/hooks`.

The card's own text is ASCII, because PowerShell may decode a native program's output in the
console's code page. Cursor runs neither file.

**The skill keeps a core under the re-injection cap.** `SKILL.md` holds the participants, a map of
the reference files, the phases with the file each reads, a table of every tool's arguments, the
`decisions` shape, and the rules that hold throughout — 13.8 KB. Every other section moved verbatim
into `skills/forge/references/`: `asking.md`, `interview.md`, `scout.md`, `planning.md`,
`plan-review.md`, `ledger.md`, `approval.md`, `build.md`, `code-review.md` and `tools.md`, which
keeps every tool's contract in full. The core's first section says what to do after a compaction,
so it is among what comes back. `build/package.ps1` reads its phrase markers across all the files,
checks that the core names each reference and stays under 18 KB, ships both hook files, and runs
the published binary's `--resume-card` in both forms.

## Considered and rejected

- **The newest Run, as the issue wrote.** See above: a card in sessions that are not running forge.
- **A `forge.resume` MCP tool.** After a compaction the Orchestrator does not know to call it; the
  hook needs no knowledge from it.
- **Codex's `PostCompact` hook.** It fires at the right moment, but its output cannot add context:
  only `systemMessage`, which the user sees, and `continue`.
- **One hook file for both hosts.** Claude Code runs `command` in Git Bash unless told otherwise, and
  Codex accepts no `shell` field; a command that both shells and `cmd.exe` read the same way had to
  give up the measured Claude Code form for nothing.
- **Re-injecting the whole skill from the hook.** The hook's output is capped at 10,000 characters
  and the skill was ten times that; and the phase's reference is what matters, not all of them.
- **Rewording the moved sections while moving them.** They carry the phrases the pin tests and the
  packaging markers hold; moving them verbatim kept every one, and a wording change is its own issue.

## Consequences

On Claude Code and Codex an Orchestrator comes out of a compaction with its Run's state and exact
call shapes in context and the name of the one file to read. On Codex that needs the user to trust
the hook once, and again whenever its command changes. On Cursor nothing runs; the core tells an
Orchestrator without a card to call `forge.status` and read the phase's reference, and the smaller
core helps there too. A Codex Run whose `workspaceRoot` is not the session's directory keeps its
files where the card does not look, and gets no card. A section is now a file read away rather than in context from the start, so
the core's map and the phases' file names are what keep it reachable. The tool results stay as they
were until #176 has measured what dropping parts of them would cost.
