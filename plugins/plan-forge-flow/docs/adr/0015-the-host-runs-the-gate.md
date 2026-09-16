# Run the task's gate on the host, and let its exit code decide the task

Reverses the rule of "Verification is self-reported, and the run log is its audit" in `CONTEXT.md`:
the builder's `verification` was its own word, the server re-checked nothing, and the skill told the
orchestrator to run a gate itself only when the builder answered `unavailable` or `failed`.

Run `20260904-173914-9254ec` in `plugins/cache-detective` is why. Its plan named, for each task, the
tests the task had to add — fourteen by name for task 1 — and its builder (codex `gpt-5.6-luna`,
then `gpt-5.6-terra`) answered `done` with `verification: passed` for tasks 1 through 6 while
writing none of them: the suite it ran was the old suite, green at the old count, and "the tests
pass" is what it reported. Gates that needed the host — SQL Express through `CD_TEST_SQL_CONN`, a
sibling checkout at `C:\Dev\eShopOnContainers`, a `NuGet.Config` the codex sandbox may not read —
came back `unavailable`, and the skill's remedy for that, "run it yourself", ran once, after task 11.
Every defect of tasks 3 to 6 surfaced there, an evening after the turns that made them.

The self-report was not lying so much as answering a different question. The builder ran *a*
verification and it passed; the gate named *the* verification, and nobody ran that. Where the two
can be told apart by a machine, a machine should tell them apart.

**After every builder turn the server runs the gate command itself**, from the workspace root, with
the environment the approval carried, in PowerShell, and the exit code is the verdict. A task whose
gate exits non-zero comes back as `gate_failed`: `tasksCompleted` does not move, the next
`forge.build.next` retries the same task, and the builder is handed the command, its exit code and
the tail of its output in the prompt — the host's evidence in place of its own recollection. A fix
round answers to the plan's `## Gates` entries the same way, because a fix belongs to no single task
and those are the checks that span the change. The run is `build.result.gate` and `fix.gate` on the
wire, a `Gate:` line beside `Verification:` in the flow log, and `gate.start` / `gate.finished` in
the run log.

**What makes a gate executable is where the code sits, not that code appears in it.** The command
is the inline span or fenced block that immediately follows `**Gate:**`, or `**G1.**` under
`## Gates`; a gate that opens with prose is a condition, and the builder's word stands as before,
with the flow log saying so. The stricter reading is deliberate: that same run had gates of the form
"тесты `Rules/CrossServiceGapTests.cs` зелёные", and the first backticked span in those is a file
name that would fail as a command every time. A spurious `gate_failed` costs a builder turn and the
user's trust in the mechanism; a gate left to the self-report costs what it always cost.

**PowerShell rather than `cmd.exe`, and `-EncodedCommand` rather than a quoted argument.** A gate
that has to prove a test *exists* — the distinction this whole change is for — counts lines of
`dotnet test --list-tests`, which `cmd.exe` cannot do in one line and PowerShell can. The codex
builder ran its own checks in PowerShell too, so a gate written for the host is a gate the builder
could have run. The command travels base64-encoded because it is the one path onto a PowerShell
command line that no quote, dollar sign or newline can break, and the script around it makes the exit
code mean what a gate needs: a cmdlet error exits 1 through a trap that first writes the error, a
native command's non-zero exit is the script's exit on PowerShell 7.4+, and a script that ran only
PowerShell exits 0. The Store's execution alias for `pwsh` is *not* skipped here, unlike in
`docs/adr/0013`: it refuses codex's restricted token, but this server runs as the user, and on a
Store install it is the only `pwsh` on `PATH`. *(The base64 half of this was amended on 2026-09-16:
see the amendment at the foot of this record. PowerShell over `cmd.exe`, the wrapping script and the
alias decision all stand.)*

**The gate's environment and the builder's extra roots arrive with the approval, not with
`forge.begin`.** The obvious home was the first call, and the first reason against it is the order of
the run: `forge.begin` precedes the interview, and the gates that need a connection string do not
exist until the plan does. `forge.plan.confirm` is where the plan is final and the orchestrator is
already asking the user something, so `gateEnvironment` and `builderRoots` are asked for there and a
re-approval replaces both. `builderRoots` reaches a codex builder as
`sandbox_workspace_write.writable_roots`, so a task that edits a sibling checkout no longer needs a
hand edit of `~/.codex/config.toml`; the other vendors ignore it. Only the variable names are logged.

**A `blocked` turn the builder could not verify is gated too, and a gate that passes counts it.**
The first cut ran the gate only for `done`, on the reading that a builder which could not finish has
nothing to check. Run `20260905-144900-e42174` disproved it. Task 13's first attempt answered `done`
and failed its gate on a deserialization error, so the failure was stored for the retry; its second
attempt fixed that error but answered `blocked`, because the codex workspace-write sandbox cannot
reach SQL Express and three of the nine gated tests need it. No gate ran for a blocked turn, and the
stored failure clears only on a gate that passes, so every later attempt was handed the superseded
attempt-1 evidence, reasoned — correctly — that it predated the fix and that only the host could
verify, and answered `blocked` again, the last of them changing no files at all. An orchestrator ran
the gate by hand and it passed; `tasksCompleted` had not moved in four turns.

So the gate runs whenever the builder's `verification` is `unavailable`, whatever its `status`, and
the exit code decides in both directions: `done` where the command exits 0, `gate_failed` where it
does not. A `blocked` report with a verification of `failed` is still left alone — there the builder
ran the check itself and watched it fail, and its word is not in doubt. What this rules out is the
alternative of stamping the stored failure with the tree it was seen against and dropping it once
those files change: by the third attempt nothing *had* changed, and such a stamp would have judged
the stale text current. Only running the gate can tell a fixed task from a stuck one.

**What is given up is the clean separation the old rule bought.** `status` is no longer purely the
builder's word — `gate_failed` is the server overwriting `done`, and `done` is the server overwriting
`blocked` — and `BuildResult`, the vendor contract, now carries a field the vendor never fills. The
overwrite is not silent: `verification` keeps the builder's account of what it could not check, and
the flow log prints it beside the gate, so a task counted over a `blocked` report still reads as one
the builder could not prove for itself. The critic is untouched: it still judges the
diff and never runs a build, for the reason `CONTEXT.md` gives — a build writes into the tree it is
reading. And nothing here reads the vendor's own event stream for exit codes, which the old rule
rightly refused because only codex reports them reliably; the host runs the command itself, so the
answer is the same for every vendor.

## Amendment, 2026-09-16: the command travels as a file, not as base64

`-EncodedCommand` had a ceiling, and the ceiling was low enough to reach. Base64 of UTF-16 runs
about 2.7 characters of argument per character of script, and `CreateProcess` caps the whole command
line at 32,767 characters. Measured on 2026-09-16 against pwsh 7.6.6, launched from the Store path
this box resolves — 85 characters of it — with the same five arguments and the same wrapping the
runner uses: a gate command of **11,930 characters still starts** (base64 argument 32,608
characters, and it exits with the code its script asks for), and **11,931 does not**. The boundary
moves with the length of the shell's own path, so "roughly 12,000 characters" is as precise as the
number deserves to be.

Run `20260915-143837-8bc4d1` reached it. Task 1's gate was a 14,824-character fenced PowerShell
block — 40,328 characters of base64 — and `forge.build.next` returned `gate.outcome = "not_run"`
with `the host could not start …\pwsh.exe: … The filename or extension is too long`. **The task was
counted anyway**, on the builder's own verification, because a `not_run` gate leaves the self-report
standing. That is the exact outcome this record exists to prevent, arriving through the one door it
left open: the gate a plan writes is a fenced block, and nothing ever told a plan to keep one small.
Run by hand from a file, with the same preamble, the same script passed.

So the wrapped script is written to a temporary `.ps1` and run with `-File`, for every gate rather
than only the long ones — a fallback that engages above 12,000 characters would be exercised only in
the rare case, and the rare case is the one that just failed silently. **Nothing of the gate reaches
a command line at all**, so the original reason for base64 — that no quote, dollar sign or newline
can break it — holds more strongly than before, and no length can break it either. The file is UTF-8
*with* a BOM: Windows PowerShell 5.1, still the fallback, reads a BOM-less script as the system
codepage, which would mangle exactly the non-ASCII paths and test names a plan in Russian puts in a
gate. `-ExecutionPolicy Bypass` was already on the command line, so an unsigned script in `%TEMP%`
runs without a new argument. The file is deleted in a `finally`, which lands even on the timeout
path: measured the same day, deleting a `.ps1` three seconds into its own `Start-Sleep` succeeds and
the script runs on regardless, and so does deleting one the instant its pwsh is killed — PowerShell
reads a script in full before running it and holds no lock on it afterwards.

What this does not change: the preamble, the trap, the epilogue, the working directory, the
environment, the timeout and every `gate.start` / `gate.finished` field are as they were, and
`GateRunnerTests` pins them against the new path. What it adds: a gate now sees `$PSCommandPath` and
`$PSScriptRoot` pointing at that temporary file, where under `-EncodedCommand` they were empty; and a
temp directory that cannot be written is its own `not_run`, logged as `gate.no-script`, rather than
being reported as a shell that would not start.

**What is still open is the `not_run` rule itself.** A gate the host cannot run leaves the builder's
word standing and the task counted, which is what turned this bug from a failed gate into a false
`done`. Making it fail instead is a larger change than this one, and not obviously right: `not_run`
means two different things in `Gatekeeper` — *the turn was not gatable* and *the host could not
check* — so failing the second needs the two separated first, and a `gate_failed` re-queues the task
with evidence a builder cannot act on, so a host with no PowerShell would spend a run's turns on a
task nothing can advance. What has changed is that a plan can no longer trigger `not_run` by writing
a long gate, which is what made the gap dangerous.
