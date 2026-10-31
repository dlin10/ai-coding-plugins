# Forge reference: the interview and the vendor choice

Read it right after `forge.begin`. The interview ends when its decisions are settled and `phase: "planning"` is declared; `planning.md` takes over from there, and the vendor choice at the end of this file runs while the Impact pass does.

## Act 1: the interview

Call `forge.begin` first, before asking an interview question or invoking an interview skill. Then
ask exactly one mode question, unless the user already said which mode they want when they invoked
`$forge`. Offer exactly these two modes every time:

- interview without documentation;
- interview that maintains the domain model as it goes.

`forge.begin` opens `phase: "interview"` itself, so the interview needs no declaration of its own.
Every later phase you declare through `forge.log.append`'s `phase` argument, at the moment the phase
begins, in the sections below.

Keep the skill-availability chain out of that question. Each mode resolves down a two-step ladder,
taking the first step that is available:

| | without documentation | with documentation |
|---|---|---|
| 1 | `grilling` | `grilling` **and** `domain-modeling` |
| 2 | the interview paragraph below | the built-in documented rules below, plus the two references |

A step is available only when **every** skill it names is in the host's catalogue. A name absent
from the catalogue is absent for you; never guess that it is available. But absent for you is not
uninstalled: hosts keep slash-only skills out of the model-facing catalogue, so a skill you cannot
see may be sitting right under the user's `/`. Tell the user which skill is running the interview,
and never claim that a skill is missing or not installed.

If the host publishes no catalogue at all, each skill may be attempted once: make one attempt per
name, not one attempt per run, and treat any error as absence. When a catalogue exists, do not
attempt a name that it does not contain.

For the documented mode's composite step, attempt `grilling` first. If it errors, the composite
step is absent. If `grilling` succeeds and `domain-modeling` errors, the interview is already
running: continue it under the built-in documented rules and use the
[`CONTEXT-FORMAT.md`](CONTEXT-FORMAT.md) and [`ADR-FORMAT.md`](ADR-FORMAT.md)
references; do not restart it.

The built-in documented rules are to challenge terms against `CONTEXT.md`, sharpen vague language,
test the model with concrete scenarios, update `CONTEXT.md` as terms resolve, and offer an ADR only
when the decision is hard to reverse, surprising without context, and a real trade-off. Create
documentation lazily and follow the [`CONTEXT-FORMAT.md`](CONTEXT-FORMAT.md) and
[`ADR-FORMAT.md`](ADR-FORMAT.md) references. If the selected interview skill is absent,
use the built-in rules here for documented mode and the interview paragraph below for the mode
without documentation.

Ask grilling questions **one at a time**, each explained as "Asking the user" describes in `asking.md`, and wait
for each answer, whichever skill is running the interview. You are looking for the decisions the
plan would otherwise leave to whoever implements it: what is out of scope, what happens on the
error paths, what existing behaviour must not change, how the result will be verified.

The opening questions are those whose answer does not depend on where things are in the code: the
boundary of the change, what stays out of it, and the verification question below. When the
interview needs the lay of the land and Scout runs, ask them while the orientation job runs
(`scout.md`), and fetch it before the first question that needs it; every other question waits for
its answer.

Before the first plan, ask one verification question in the language of the conversation: full
verification after each Builder fix turn, or only at the end of the run? Explain that the latter
uses a meaningful short check after each fix and leaves fixes pending until all full checks pass.
The first answer selects `gate=full; fullGate=beforeNextRound` (the default); the second selects
`gate=targeted; fullGate=final`. No separate question about enabling targeted or choosing timing is
required. Honor an explicit `gate=targeted; fullGate=beforeNextRound` choice too. Record the initial
choice in the Brief, with its label in the language of the plan and the parameter values explicit.
Pass timing on first confirm and gate on each fix; never repeat this interview between fixes.
Without an explicit targeted choice, keep full verification.

When settling acceptance and stop rules, distinguish an outcome on a real target — a finding
confirmed or gone, a model complete, a measurement under a bound — from acceptance such as a
refactor or documentation change. For an outcome, once the target, acceptance and mechanism are
concrete, run the feasibility question in `scout.md` during the interview, before declaring
`phase: "planning"` and before the first `forge.plan.write`. A stop rule alone does not establish
that the outcome is reachable.

Show the answer before drafting. For each unmet dependency or one the check cannot settle, ask
the user through `asking.md` whether to change the acceptance, change the mechanism, or plan a
probe first. Explain the dependency and its evidence, recommend an option with a reason, and wait
for the answer; record it with `forge.decision.record`. Check the affected dependencies again when
the acceptance or mechanism changes. The interview is settled only when they hold or the user
has chosen a probe whose first task checks the unsettled dependency and stops the run if its gate
fails (`planning.md`). Refactors and docs without an outcome on a real target need no feasibility question.

When the interview has settled the decisions, declare `phase: "planning"`, sketch the approach and
make its matrix check (`planning.md`), and start the Impact pass on it as a Scout job (`scout.md`).
While it runs, choose the vendors and models — the whole of the "Choosing the vendor and model"
section below — and write the requirements, gates and tasks in full. The requirements — the
`## Requirements` section `planning.md` describes — are the interview's own output and do not
depend on who implements them. The tasks do: the builder's selection is an input to them, the
plan's depth is calibrated to the model and effort that will execute it, so tasks written before
that choice are written blind.

## Choosing the vendor and model

This happens while the Impact pass runs, after the approach is sketched and before the tasks are
written — the depth rule in `planning.md` reads the builder's selection. Without Scout it follows the
sketch directly.

Critic/Builder selection remains at most six questions total: two Vendor questions, two
model/effort questions, and one Fast question for each role whose choice offers it. Ask them role
by role, the critic's first: the vendor question alone, then one call carrying that role's
model-and-effort, Fast and instruction questions, in the order of the steps below — three questions
at most. The vendor question never shares a call with the model-and-effort question: the questions
in a call cannot see each other's answers, so the model options would have to span every vendor,
and a user who chose one vendor would be offered another's models. A single just-in-time Scout
selection round is additional; there is no numeric cap on the domain interview.
The combinations come from the server, not from your own knowledge. Call `forge.models` (no
`vendor` argument) again immediately before the Critic/Builder Vendor question: successful probes
return from `CatalogCache` immediately, while previously unavailable probes rerun so a repaired
CLI or sign-in can re-enter the choices. Do not claim that the catalogue is called only once. Build
every call from that one answer: it holds every available vendor's models, efforts and Fast tiers,
which is what lets one call carry questions that would otherwise wait on each other's answers.

The questions in a call cannot see each other's answers, so every option says what it depends on:
the Fast question names the pairs that offer it. When the answers do not fit together — a
free-text model the chosen vendor's catalogue does not list, Fast for a pair that has none — ask
one follow-up question about the part that does not fit before that role's selection is used.

1. The vendor question offers only vendors the catalogue reports `available: true`. Never offer a
   vendor with `available: false`; its `detail` names the cause — a missing CLI, a sign-in — so
   tell the user why it is out and what would bring it back.
   - If none is available, stop and relay what each probe reported; no act can run without a
     working vendor CLI. Then declare `phase: "done"`.
   - If exactly one is available, do not ask either vendor question. Tell the user which vendor
     both roles will use, and draw step 2's pairs from it alone.
2. The model-and-effort question requests a valid combination for that role's chosen vendor, drawn
   from that vendor's catalogue in the tool's order — it is already newest first. Offer three
   concrete model-plus-effort pairs, your recommendation first, among them the vendor's own pick
   where it names one (`isDefault`, `defaultEffort`), and say which that is; always leave free text
   open. Never offer a pair from a vendor the user did not choose for that role.
   Say what kind of list you are offering: `source: "live"` came from the vendor CLI just now;
   `source: "resolved"` (claude) is a list of aliases this repo remembers, each one turned by the
   CLI into the model it currently stands for — so name that model beside the alias, because
   `displayName` carries it and the alias alone does not say which model the run would use. A
   family released since this plugin shipped appears only if the CLI knew to name it. Never offer
   one vendor's model family under another vendor.

3. Ask the Fast question only when a pair the call offers lists its effort under `fastEfforts` —
   any entry at all for a pair offered without an effort — with `fastUnavailable` null: should the role run at the vendor's Fast tier,
   quicker, at a higher usage price? Name the pairs that offer it. Offer "standard speed" first, as
   the recommendation unless the user has asked for speed, and name `fastHint` beside Fast where
   the catalogue gives one — it is the vendor's own word on the price. Pass `fast: true` to that
   role's tools only on a yes for a pair that offers it. A model whose `fastUnavailable` is set
   offers Fast but this account will not serve it: do not ask, and say why when the user asks for
   Fast anyway (`extra_usage_disabled` means claude bills Fast as extra usage, and the account has
   it off). The server refuses a Fast request the catalogue does not confirm, and claude refuses one
   again at the start of the worker's session; a turn claude served at standard speed for part of
   the way still counts, and its result carries `speedWarning` — tell the user.

4. The instruction question asks whether the user wants to tell that role's worker anything for
   this run, offering "no instructions" first. This is their only channel to a worker besides the
   plan: a language to answer in, a class of finding this repository does not want raised, a skill
   the builder should use, a house style. Say that much before the call, and propose no wording of
   your own. Pass what they answer to `forge.instructions.set` **verbatim**, and add nothing of your
   own. If both roles' answers are "no instructions", do not call the tool at all.

   The two roles hear it differently, which is worth saying if they ask. A critic is a fresh process
   every round and is handed its text every round. A builder holds a session for one plan task or
   one code-review round and is handed its text only when a session starts, so instructions given
   mid-task reach it with the next task or round — or sooner, when a vendor switch, a reopened plan,
   or direct re-approval after a changed Builder Brief starts a new session. The tool's answer says
   so when that is the case, and you should pass that on rather than assume it landed. The builder's text is also shown to the code-review critic as context, so that
   critic does not raise findings for a choice the user asked for.

The catalogue is advisory for model and effort: an unfamiliar model arriving as free text is worth
mentioning, not refusing, because the vendor CLI decides. Fast is the exception — the server
refuses what the catalogue does not confirm. The roles are not interchangeable in strength. The
builder works against an already-hardened plan and can be cheap; the critic is judging, so lean
nearer the strong end.

Once both roles' selections and instructions are settled, write the tasks, then fetch the Impact
pass and fold it in as `scout.md` describes.
