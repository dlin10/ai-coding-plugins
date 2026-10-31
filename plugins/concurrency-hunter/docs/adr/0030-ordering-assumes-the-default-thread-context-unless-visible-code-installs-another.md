# Ordering assumes the runtime's default thread context unless code the analysis sees installs another

This decision amends [ADR 0008](0008-ordering-is-a-happens-before-graph-trusted-inside-one-instance-tree.md) and SPEC
TD-060, which keep a pair unless every edge on the ordering path holds for all instances it connects. An edge now holds
for all instances under a short, written list of environment assumptions: facts about the thread and the process that the
code under analysis does not show and the analysis does not prove. PRD already excludes a guarantee of full soundness for
arbitrary C#; this decision says which part of the environment the analysis takes as given.

## Context

5d run H2 was to order the passes of a spawn site that waits for its own work (question 189). eShop's seeding runs up to
four passes under a Polly retry and ends each pass with `SeedAsync(...).Wait()`; with the join of its own pass it stops
overlapping itself, and eShop goes from 36 findings to 2 ([ADR 0029](0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md)).
Its first attempt (forge run `20261028-115553-01bf54`, 2026-10-28 to 2026-10-31) required the order to hold for every
outcome of the wait:

- `Task.Wait()` might end before the task completes if a custom scheduler fails while inlining it or a custom
  `SynchronizationContext` returns from a wait notification early. A retry would then start the next pass while the
  previous one still runs, and 30 of the 34 removals came back.
- Proving that no such context exists at the wait needed every main-thread member from the entry point to the wait to keep
  the context null. A census of 277 members on the paths of eShop and the demo (kept with the attempt on branch
  `feature/race-hunter-h2-attempt-1`, `docs/runs/5d-H2-attempt-1/`) left 135 required members unexpressed, among them open sets of in-process observers (`DiagnosticListener.AllListeners`, event listeners, AsyncLocal change
  handlers), Reflection.Emit proxies (`Microsoft.Extensions.DiagnosticAdapter`) and native SNI callbacks.
- On the way the plan grew from 29 to 424 thousand characters, with a launch profile, configuration facts, runtime-asset
  binding and 13 draft ADRs, and task 1 of 8 never passed.

The same census found the assumption true where it looked. An IL scan of 434–436 assemblies on the eShop path found no
writer of the synchronization context on it: Serilog.Sinks.PeriodicBatching restores the context in `Dispose`, after the
wait, and Microsoft.AspNetCore.Components, System.ComponentModel.EventBasedAsync and `AsyncOperationManager` (for
SmtpClient, Ping and WebClient) are off the path. It also found that the runtime resets the context when a thread-pool work
item starts (`ThreadPoolWorkQueue`). The first counterexample does not reach the task of an `async` method at all: in
.NET 10.0.14 `TaskScheduler.TryRunInline` does not inline a promise task (`m_action == null`), and
`Task.WrappedTryRunInline` returns false for a task without a scheduler, so `Wait()` on it runs no scheduler code.

Static analyzers that are useful on real programs are sound only modulo documented assumptions about what they do not model
("soundiness", Livshits et al., *In Defense of Soundiness: A Manifesto*, CACM 58(2), 2015). The choice is which
assumptions, and where they are written.

## Decision

The analysis reads every happens-before edge under these environment assumptions:

- **Thread context.** A thread runs with no `SynchronizationContext` and on the default `TaskScheduler` unless code the
  analysis sees installs another: a reachable call that sets the synchronization context, a task created on a scheduler
  the program names, or a host the program starts that installs one (WinForms, WPF and other UI message loops, listed as
  data).
- **Thread-pool entry.** A thread-pool work item starts with no synchronization context.
- **Observers.** In-process observers the program does not subscribe itself — diagnostic listeners, event listeners,
  AsyncLocal change handlers attached by tooling — do not change the thread's context or the program's state.

Under them, a blocking wait for a task without a timeout or a cancellation token — `Wait()`, `Result`,
`GetAwaiter().GetResult()` — ends only when that task has completed, normally, with an exception or by cancellation. A wait
in code that installs a context, a wait for a task created on a scheduler the program names, and a wait with a timeout or a
token keep today's reading: they order nothing they do not prove.

`metrics` counts, by assumption, the pairs that an edge removed only under it, next to the other removal counters.

## Consequences

- 5d run H2 is split. H2a builds the join of a site's own pass (question 189) on these assumptions; H2b adds the Polly
  retry records. The first attempt's work is kept on branch `feature/race-hunter-h2-attempt-1`; its launch profile,
  configuration facts, runtime-asset binding, acquisition proofs and the other draft ADRs go to QUESTIONS without a run.
- Ordering a wait needs no launch profile, configuration fact or census of startup members, and eShop's acceptance no
  longer depends on how the program is launched.
- A race that only an unseen context could cause — a library that installs one the analysis does not know, a
  wait-notification override that returns early — is missed. The counters name every pair an assumption removed.
- A run without the assumptions is not built; whether to offer one is a question.

Rejected:

- **Prove the absence of a context at each wait** (H2's first attempt): the proof needs the whole startup path, open
  observer sets and dynamic code, and it grew without converging.
- **Lower the confidence of such pairs instead of removing them:** the 30 eShop seeding findings stay in the list.
- **No assumption:** the seeding findings stay until a proof exists.
