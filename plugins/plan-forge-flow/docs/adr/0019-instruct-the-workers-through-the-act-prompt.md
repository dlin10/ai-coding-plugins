# Let the user instruct the workers through the act prompt, and check only for secrets

Until now a user had no channel to a worker but the plan. A **Critic** sees the plan or the diff,
the review log and its role contract, and there was nowhere to say "answer in Russian" or "in this
repo, do not flag X". A **Builder** receives one task or set of kept findings per turn; the first
turn of a session also receives the approved plan's `# Builder Brief`, but neither is a channel for
the user's per-run instruction. Telling it to use a particular skill therefore meant copying the
same sentence into every task and never forgetting one. The third candidate, editing `prompts/`, is
a knob only in a checkout: an
installed plugin keeps `prompts/` under its plugin root, where an edit applies to every project and
is lost on the next upgrade.

The run now carries **run instructions** — one free text for the Critic, one for the Builder, set
once by `forge.instructions.set` and kept in the run state beside `workerTools`.

**They travel in the act prompt, not the role prompt.** The role prompt is loaded by
`PromptLibrary` and reaches each vendor by its own mechanism, and one of those mechanisms cannot
carry a change at all: a resumed codex thread keeps the `developer_instructions` its thread started
with, which is why `CodexCliSession` calls sending them again "harmless rather than effective". The
act prompt is the user turn, the one channel every vendor has on every call, and putting the text
there leaves `PromptLibrary` and all three vendor implementations untouched.

**A Critic is fresh every round, so it is handed its text every round.** A Builder holds a session,
so it is handed its text exactly on a call that starts one — `Build` or `ReviewFix` with no resume
token, which is also what a vendor switch, a reopened plan, or re-approval after the Builder Brief
changes produces. On that turn the Builder Brief
comes first, the task or findings follow, and the user's instructions remain the final directly
addressed block. A resumed session already has both the Brief and the instructions in its history.
The consequence is accepted rather than fixed: instructions changed after a builder session started
do not reach that session, and the tool's answer says so instead of refusing the call.

**The code-review Critic is shown the Builder's text as data**, in a separately framed section
saying it is context for judging the diff and not an instruction to the critic: do not raise a
finding for a choice the user asked for, unless it breaks the approved plan or introduces a
correctness or security defect. Without it, a builder told "the simplest solution that works" draws
findings for the abstractions it left out on purpose. Plan review gets no such section — no code
exists yet.

**Considered and rejected**

- *`forge.begin`.* It runs before the interview, and the question that produces this text is asked
  at the end of Act 1, after the vendor and model questions.
- *A per-call argument, like `model`.* Text the orchestrator has to carry across every round is
  text that can drift or vanish after a context compaction; the run state is the thing that does
  not. `forge.status` returns the run state, so an orchestrator that has lost the text can read it
  back.
- *A repository-level file* picked up from the workspace. It suits a standing preference better
  than a per-run question does, and it is deliberately out of scope until the same text turns out
  to be typed into every run.

**What is not checked.** The only refusal is `SensitiveInput.Guard`, run over both texts before any
worker starts and for the same reason it guards every other prompt: a secret that reaches a vendor
CLI has left the machine. Nothing checks that the text is the user's own rather than the
orchestrator's, and nothing checks that it does not tell a critic what to conclude — a length cap
picks a number nothing measured, and a regular expression over natural language is evaded by
rewording while refusing legitimate text like "do not raise findings about naming". This is the
posture of [0002](0002-mcp-server-surface-without-enforcement.md) and
[0003](0003-approval-through-the-orchestrator.md): the server records, it does not enforce. What
makes it auditable is that both texts go verbatim into `flow_log.md`, which the user reads, and into
`forge.log`, which records every tool call with its arguments. `SKILL.md` states the rule the
orchestrator is held to — pass what the user typed, nothing of your own — in the same words it
states it for `approved`.

**Consequences.** A run with no instructions omits the instruction block; the independently defined
Builder Brief still appears on a fresh Builder session. The critic's independence now has a
documented way to be weakened, by a user who asks for it, in a file they can read afterwards. And the guarantee that
travels with the code-review section is weaker than it looks: it shows whatever is set at the time
of the round, so a user who changes the builder's text mid-run can have a critic told about
instructions the builder never received. The flow log records both the change and the round, which
is what makes that case explicable rather than invisible.
