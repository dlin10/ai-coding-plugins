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
Store install it is the only `pwsh` on `PATH`.

**The gate's environment and the builder's extra roots arrive with the approval, not with
`forge.begin`.** The obvious home was the first call, and the first reason against it is the order of
the run: `forge.begin` precedes the interview, and the gates that need a connection string do not
exist until the plan does. `forge.plan.confirm` is where the plan is final and the orchestrator is
already asking the user something, so `gateEnvironment` and `builderRoots` are asked for there and a
re-approval replaces both. `builderRoots` reaches a codex builder as
`sandbox_workspace_write.writable_roots`, so a task that edits a sibling checkout no longer needs a
hand edit of `~/.codex/config.toml`; the other vendors ignore it. Only the variable names are logged.

**What is given up is the clean separation the old rule bought.** `status` is no longer purely the
builder's word — `gate_failed` is the server overwriting `done` — and `BuildResult`, the vendor
contract, now carries a field the vendor never fills. The critic is untouched: it still judges the
diff and never runs a build, for the reason `CONTEXT.md` gives — a build writes into the tree it is
reading. And nothing here reads the vendor's own event stream for exit codes, which the old rule
rightly refused because only codex reports them reliably; the host runs the command itself, so the
answer is the same for every vendor.
