# Advise the Evidence check through a configured Advisor

## Context

The Evidence check ([0021](0021-add-a-lazily-selected-scout-worker.md)) asks the Orchestrator to
compare its plan with every fact of every Scout answer that bears on a Change point, after each
Impact pass and before review. It is done by hand, over a cumulative report that reached 145–279 KB
in the Runs audited for issue #149, and it can miss. In Concurrency Hunter Run
`20261009-194113-1000db`, Scout answer 4 said that `UnknownCalls.Of` consumes supported summary
records and has no generic `SummarySpawn` branch; the plan never routed it into that method's
consumers, and plan-review round 7 raised it as `F-0030`. That shows a handoff gap; it does not show
that any model would have closed it.

A class of hosted models answers exactly the kind of narrow question such a check is made of: given
state and typed questions with fixed alternatives, they return a probability for each alternative
and generate no text. Three were checked against their own documentation on 2026-10-19:

- TypeSafe's Jev — `POST /v1/systemone`, `state` plus a map of questions, `choice` with
  `criteria` (at most 255 options), answers with `choice`, `probabilities` and `confidence`; pinned
  versions (`jev-1.13.0`, `jev-1.14.0`) beside aliases that move; 64k tokens a request, 32k for state
  plus its longest question; the standard service is not zero-retention.
- OpenAI's Decisions API, in public beta — `POST /v1/decisions`, `input` (text or messages) plus an
  array of named questions, `choices` as `{value, description}`, answers with `choice`,
  `probabilities` and `confidence`, and a separate `refusal` answer; only `gpt-6-luna`, no
  documented snapshots, no documented limits.
- Microsoft-Decision-1 in Foundry — `POST {resource}/providers/microsoft/v1/systemone`, the
  request's `model` naming a deployment and the response's `model` naming the model behind it,
  Entra or `api-key` authentication, a probability for every option.

What they share is the judgment: one pick among supplied options, with a full distribution. What
differs is everything around it — the wire shape, authentication, the endpoint, how a model is
identified, and the limits.

An offline replay of Jev on eight Concurrency Hunter Runs (2026-09-28) found it good at surface
relatedness and poor at completeness: matching words look to it like coverage. A model of this
kind is therefore safe to ask "how does this fact bear on this piece of the plan" and unsafe to
trust when it says "covered".

Three constraints come from the server itself. Every tool call is logged with its arguments, so a
credential can never be one. Cursor's CLI cancels any tool call at 60 seconds. And only what is
deliberately configured should ever leave the machine.

## Decision

Add **Advisor**: an external service that answers bounded typed questions over state the server
supplies. It is not a Vendor and never a Worker — no process, no session, no generated text — and
its answers are advice: they never approve a plan, close a finding, waive a review, change a gate or
certify code.

**The seam is the judgment.** `IAdvisor` answers Choice questions: state, questions each with an
ordered list of options, and per question either a pick with a probability for every option and a
confidence when the service reports one, or a refusal; the result names the model that actually
answered. A failed request is a typed failure — unauthorized, forbidden, rate limited, overloaded,
too large, timed out, invalid response. Each implementation owns its wire shape, authentication,
endpoint, limits and model identity; one shared core owns everything that decides validity: which
Scout items and plan pieces exist, which pairs are asked, the questions' wording, checking every
answer against the questions sent, the cache, and the accounting. Jev is the first implementation;
OpenAI's and Microsoft's services are expected to follow behind the same interface. No Advisor is
built on a general-purpose LLM. Rejected: making Jev an `IVendor` (a process seam for something with
no process); a mapper per Advisor (every implementation would repeat the validity rules); the Jev
wire format as the contract (others would have to emulate it).

**Configured means consent.** An Advisor is configured when every `PLANFORGE_`-prefixed variable it
requires is set for the server process — for Jev, `PLANFORGE_JEV_API_KEY`, with
`PLANFORGE_JEV_MODEL` optional and defaulting to a pinned version, never an alias. A vendor's own
variable, such as a standard API key another tool already uses, never configures one, so the
feature cannot be switched on as a side effect. `forge.begin` and `forge.status` report the
configured Advisors, and partly configured ones with the names of their missing variables. With
none, the Evidence check is what it was; with one, it is used without a question; with several, the
user chooses as with a Vendor, and the Orchestrator passes the choice on each call. Rejected: a
per-Run consent tool, and consent as an argument of every call.

**One tool, in portions.** `forge.evidence.map` takes the optional Advisor, the Change points the
Impact pass named, and optional implications — obligations the Orchestrator derived itself, each
citing the Scout items it rests on, which the core checks exist. A call works for a bounded time,
about forty seconds, stores every judgment as soon as it has it, and reports what is checked and
what remains; the Orchestrator calls again until the map is complete. The same shape works on every
host. Rejected: a background Job (built around a Vendor process) and one long call (dead on Cursor).

> **Amended 2026-10-23 (issue #186, [0043](0043-remove-cursor-as-host-and-vendor.md)).** The one long
> call was rejected for Cursor alone, and 0.59.0 removed Cursor. Both remaining hosts give a call an
> hour: Claude Code through `"timeout": 3600000` in `.claude-plugin/plugin.json`, Codex through
> `tool_timeout_sec: 3600` in `.mcp.json`. The forty-second portions cost calls and nothing else: in
> Run `20261022-214038-e08c4f` four of six Jev maps took two calls, the first ending incomplete with
> no failure only because dispatch had stopped. From 0.62.0 a call maps until done. No request starts
> after 3000 seconds, the bound `forge.work.poll` already waits to; a request already running
> finishes under the Advisor's own timeout, and only the caller's cancellation cancels one. Judgments
> are still stored as they arrive and the result still reports what remains, so a call that ends
> early — cancelled, stopped by an unauthorized or forbidden answer, left with failures after
> retries, or at the bound — is resumed by calling again: the exception now, not the protocol.

**What is asked.** The plan is divided into requirements, the Out-of-scope block, gates, the Fix
gate and tasks, with any other text falling back to the plan outline's targets. The requirements
with their exclusions and the Change points are state shared by every question. For each item the
core picks the candidate plan pieces that share an identifier with it — a path segment, a file name,
a part of a symbol, a code span, a Pascal-, camel- or snake-case word, a Change point — and always
the Out-of-scope block; nothing is cut to a top-K. Three independent questions go in one request:
whether the item bears on a Change point, and for each candidate how it relates to the item and
whether it holds an explicit obligation for it. The core, not the model, settles each item:
a contradiction anywhere outranks any support, then an explicit obligation means covered, then an
explicit exclusion, then insufficient context, else no coverage established — beside the states of
an item with no candidate, of context only, and of judgments left unchecked. Rejected: asking every
pair (about 18,000 a map, on every revision) and letting the Advisor choose the candidates (the
judgment it is worst at).

**No source leaves the machine.** Repository locators are resolved in code when a Scout answer is
recorded, and a hash of the window around each place is kept with the item; a changed window
invalidates that item's judgments and marks it as changed since the Scout answered. A locator that
does not resolve is marked, and the item is still judged on its text. Only Scout item text, plan text,
Change points and implications are sent, and the secret guard reads each request first. External
URLs are not fetched. Rejected: sending a few lines of code around each locator.

**After the manual check, never instead of it.** The Orchestrator makes the Evidence check by hand
first and completely; then it asks for the map, takes up only the flags the manual check had not
handled, decides each as it always does, and logs how many the map raised, how many were already
handled and how many were new. A "covered" from the map removes nothing. Rejected: the map first, as
a hint, which would anchor the check on the judgment the model is worst at.

**Content-addressed.** `evidence-map.json` holds each judgment under the hash of all its inputs: the
Advisor and requested model, the versions of the questions, candidate selection and schema, the shared
state, the item with its source hash, and the plan piece. A changed input is a different key, so a
change to any requirement re-asks the whole map and nothing stale is ever found. A changed reported
model drops that Advisor's judgments. The latest map and its counts live in the same file; each call
adds a line to the Flow log and an `advisor.usage` record — requests, tokens, latency, cache use, no
content — to the Telemetry file.

## Consequences

A configured Advisor sends excerpts of the plan and of Scout answers from every repository forge
runs in on that machine. The README says what is sent, where, and that TypeSafe's standard service
is not zero-retention; nothing else asks.

Nothing enforces the order or the reconciliation; like the Evidence check itself they are the
skill's guidance, per [0002](0002-mcp-server-surface-without-enforcement.md). A candidate the
identifier match misses sends its item to the manual list rather than to a false "covered", so the
selection errs on the safe side; its recall is measured, not assumed.

The feature starts as a shadow: one hand-labelled, sanitized fixture set drives the default tests
through a fake Advisor and an opt-in live run that reports candidate recall, obligation recall,
false coverage, abstentions, source validity, latency and cost without asserting quality thresholds.
Weak results keep it a shadow; they never relax the manual check. A later rollout that lets the map
lead needs reviewed results and its own decision.

Typed answers guarantee the shape, not the truth. A pinned model can still be retired; an
Advisor that cannot pin, as OpenAI's documents no snapshots, is detected only by the model its
answers report.
