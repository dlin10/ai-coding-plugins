# The later runs of a sequential-awaited call start in its continuation

## Context

A library record can give the delegate of a call the run relation `sequential-awaited`: the library runs the delegate
0..n times, one run at a time, and awaits the task each run returns before the next run starts and before the call itself
completes. Polly 7's `AsyncPolicy.ExecuteAsync` has such a built-in record. The engine follows the delegate once for all
its runs: when the delegate's target is an async body, its synchronous part — up to its first suspension — is a step of
the caller's execution, and its continuation is the call's child execution, `async:{callingBody}#{operation}`, the
**call's continuation**.

That placement holds for the first run only. The first run starts in the caller, synchronously. A later run starts once
the task of the run before it completes — when that run suspended, after the call has already returned its task to the
caller — so it starts in the library's continuation and overlaps what the caller does after a call whose task it does not
wait for. With every run's synchronous part in the caller's execution, two of its accesses never pair: one execution that
does not overlap itself has no pair with itself. The demo's `polly-nested-retry/unwaited` case shows the miss:

```csharp
_retry.Execute(() => { var detached = SeedAsync(); });   // nobody waits for SeedAsync()
private async Task SeedAsync() => await _seed.ExecuteAsync(async () => { _left = 1; await Task.Yield(); _left = 2; });
```

Attempt 2 of the inner retry writes `_left = 1` in the inner call's continuation while the next pass of the outer retry
writes it again in the root. With Polly 7.2.3's own bodies analysed at the call site
([ADR 0029](0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md)) the engine finds that pair, because the
later attempts run in the library loop's continuation; with the record it did not.
[ADR 0008](0008-ordering-is-a-happens-before-graph-trusted-inside-one-instance-tree.md) already describes a retry loop
that runs its first attempt in its caller and the next ones in its continuation; the record's reading did not.

## Decision

For a delegate edge that carries `sequential-awaited` and whose target is an async body, the synchronous part of the
delegate is entered into the call's continuation as well as staying a step of the caller's execution, whatever the
record's layer. The occurrence in the caller's execution stands for the first run and for a later run that starts before
the call returns; the occurrence in the continuation stands for the runs that start after an earlier run suspended.
Awaited callees of that synchronous part, walked in the continuation, continue in it. The call's continuation already
holds the runs' continuations, so what the engine knows of it — the wait for the call's task, the serialization of an
admitted call's runs, where an admitted call's continuation starts — covers the later runs' synchronous parts unchanged.

A delegate whose target is not an async body — a non-async delegate that forwards an async call's task, or one that
forwards nothing — has no continuation at the call and keeps every run in the caller's execution. That is a known miss
of the same kind, left for a decision of its own.

Rejected:

- **A separate execution for the later runs.** A new kind of execution beside the continuation, with its own policy and
  joins, and every reader of the continuation — the call's task join, the proof of an admitted call's runs, the start
  position of admitted continuations — taught about a second one, for the same meaning.
- **The synchronous part kept in the caller, its accesses overlapping across passes when the call's task is not
  waited.** It breaks the rule that an execution that does not overlap itself has no pair with itself, on which the whole
  ordering layer stands, and it would pair the first run of one pass with the first run of the next — not the real
  interleaving, a later run of one pass with the first run of the next.
- **A relation of Polly's own.** The relation already says the library awaits each run, so a later run's start after a
  suspension holds for every record that carries it.

## Consequences

- With the Polly records present, the demo gains `0d8df6303781e5e8` and gives the findings of the run with Polly's bodies
  admitted; eShop and the run with the records withheld and nothing admitted keep every finding.
- Pairs the old placement hid appear: the earlier targets of a multicast delegate, whose tasks the library does not
  await; the runs of two delegate parameters a record does not order against each other; two detached seedings.
- Some answers become wider than the truth — safe, only less useful — until each gets a decision of its own: a
  `sequential-awaited` call made in the synchronous part of an admitted call's delegate is now made by two executions and
  loses its admission; a task a later run starts and waits for is not proven complete through the call's task; a
  continuation of one call is not ordered against another continuation of the same call. A call outside the admission
  gains next-run pairs, since its runs are never proven ordered.
- The access emission that merges the method instances of one body still hides pairs; a shape that showed it through
  the old placement now finds its pair through the continuation instead.
- The engine's source changes, and with it the version of the model generator: the demo's generated models are
  re-recorded, and the framework census names a stale engine until its owner regenerates it.
