---
status: accepted
---

# Refuse a shell-less answer, and report a sandbox that cannot start a shell

In run `20261012-163837-6fc7b1` every command the codex Critics and Scouts tried failed before it
started: codex-cli 0.162.1's sandbox setup refresh failed on the desktop app's `node_repl.exe`, and
each command came back as "Failed to create unified exec process: helper_unknown_error: setup
refresh had errors". The acts were reported as ordinary successes. One Scout answer said it could
not open the files and went into `SCOUT.md`; one plan-review round said repository reads were
unavailable and its findings went into the decision ledger. The other answers had read files
through the app's `node_repl` MCP server and did not say they had no shell at all. The single
probe, at `forge.begin`, ran before any refresh had failed and saw nothing. `CONTEXT.md` carries the
measurements; issue #147 the report.

## Decision

**A Shell-less answer is not used.** A codex turn that tried at least one command and started none
fails as a vendor failure, after the stream ends and before its result is read. A command codex
could not start is the shape codex pins in its own tests — `status` `failed`, `exit_code` -1,
output opening "Failed to create unified exec process:"; a command that started and failed, or a
turn that tried no command, is not shell-less. The rule holds for every role. A Critic's round is
not spent and nothing reaches the ledger; the error and the flow log quote codex's diagnostic and,
for a sandboxed role, the newest "setup refresh completed with errors" line of codex's sandbox log,
which names what blocked the setup. A Builder's act fails with the files it had written, as any
builder failure does. A Scout's failure is the fixed `no_shell`, which carries codex's error code
only when it is a bare identifier: Scout failures pass no other vendor words, and the full text is
already in the run log.

**`forge.models` reports a Degraded vendor.** A vendor entry gains `degraded`, null unless its
Critic and Scout cannot start a shell; `available` stays true, because codex runs its Builder outside
the sandbox. Two sources feed it, the newer winning. The probe reads doctor's `sandbox.helpers`,
which fails while codex's record of a failed refresh exists, and adds the sandbox log's newest
failure. And the server process keeps what its sandboxed Workers showed: a Critic or Scout without
a shell sets it, and a later Critic or Scout that starts a command clears it. A Builder neither sets
nor clears it, since its commands start while the sandbox fails. A degraded probe is not cached, so
a repair is seen on the next call. The skill offers a degraded vendor for the Builder only, and on a
shell-less failure asks the user rather than retrying.

## Considered and rejected

- **Fold the answer with a warning.** It keeps the blind critique in the ledger and the blind
  answer in `SCOUT.md`, which is the harm; the orchestrator would have to undo what the server did.
- **Return the answer without folding it, under a new status.** It lets the orchestrator keep an
  answer that read files another way, but adds a status to three acts, the job path and the skill
  for a case the user has to repair anyway. An answer with no shell cannot say what it did not
  read.
- **Refuse on any command that failed to start.** A later command that starts proves the shell
  works; one transient failure does not make the answer blind.
- **Make codex unavailable.** The Probe's definition — a vendor that cannot execute a command is
  unavailable — was written for a shell every role lacks. Here the Builder works, and withholding
  it would cost a working role for a fault in two others.
- **Probe with a real sandboxed command.** `codex sandbox -- cmd` does not run the refresh step
  that failed, and a `codex exec` probe is a model turn per probe.
- **Doctor alone.** It describes the last refresh any codex ran, the desktop app's included, and
  the run's one probe came before the first failure: it would not have reported this run at all.

## Consequences

On a machine where the sandbox cannot start a shell, every codex Critic and Scout act now fails
instead of answering — run A9 would have stopped at its first Scout, at 16:46, rather than at 18:30
when the user noticed. The cost is an answer that read the repository some other way: such
answers are discarded too. Claude's Critic and Scout read through Read, Grep and Glob rather than a
shell, so the rule does not reach them; cursor-agent's stream shape for a command it could not start
is unmeasured. The rule leans on codex's launch-failure text, which codex's own test pins, and on its
sandbox log format, which only names the file and may change without breaking the refusal.
