# Every start of an execution is proven on its own

## Context

A spawn, an async call or a timer's creation starts an execution, one per parent execution and site:
`spawn:{body}#{op}`, `async:{body}#{op}`, `timer:{body}#{op}`. The code that reaches a site is a method instance, one
per body and context (SPEC TD-022), so two instances of one body that reach the same site in one parent start one
execution with a start (`SpawnAnchor`) for each. The order graph kept one start per execution — the first in an ordinal
sort of the caller instances — and five readers took that one start for all of them: the pass proof (its frames and
the next pass), the runs of an admitted call, the composite reading of a spawn, the bounding of joined work and the
thread-pool assumption.

That reading is wrong both ways. Analysis of library bodies at the call site
([ADR 0029](0029-a-deciding-gap-is-answered-by-a-record-the-call-site-or-ai.md)) gives each library body an instance per
call site. In the demo's `polly-retry-attempts` case `AsyncRetryWorker` calls one Polly 7 retry policy twice, so six
Polly async executions each stood for both calls. For an attempt of the first call, the proof read the start of the
second call, cut at that call's await, and the first call's own path — its adapter's await may throw, the engine
catches, retries and starts the next attempt — reached the same `Task.Run` again uncut: the `_joined` writes of two
attempts stayed a finding although each attempt awaits its work. The other way, a start that is not the first is never
examined: one receiver waiting for the task it starts, another of the same class starting it at the same site without
waiting and called twice, gave no pair, although the second receiver's two tasks can overlap.

Splitting the execution per instance was tried first. It removed the finding, but the ordering layer reads one
execution per parent and site in about twenty places — the own pass and the next pass, the tails of async bodies, the
runs of admitted calls, the paths of the order graph, the no-overlap decision, the overlap of semantic gaps — and
sequential programs gained pairs: two migrations handing one seeder to a retry, two receivers each waiting for a nested
`Task.Run`, a body awaiting its `Task.Run` and then writing the field, two receivers on mutually exclusive branches.

## Decision

An execution stays one per parent execution and site. Every reader of the one start the order graph kept reads all of
the execution's starts:

- The pass proof proves each start with its own frame, its own joins and its own cuts; the cuts of different starts
  are never combined, and the completion evidence its climb records is kept per start. A climb that reaches an
  execution with several starts continues through every start whose call reaches the instance it climbs from, each
  with its own frame. The next pass is any start of the execution. The work is ordered only when it is for every start.
  A request for the one start of an execution with several, outside a climb or a frame that selects it, proves nothing.
- The runs of an admitted call are proven for every continuation start.
- A spawn is composite when any start's work is, and a continuation is ordered after its antecedent only when every
  start proves the same one; a join bounds the joined work only when every start is accounted for: a start that can
  reach the join is waited by it, and one that cannot — another branch of an exclusive choice — is waited by another join
  of the same work on its way to the end, so exclusive starts each joined in their own branch stay bounded and an
  unjoined branch does not; an exceptional exit leaves the work running when it does for any start. The thread-pool reading needs no rule of its own: every start
  of an execution shares the site's operation, so it answers alike for each.

The handle rule of [ADR 0008](0008-ordering-is-a-happens-before-graph-trusted-inside-one-instance-tree.md) — the waiting
execution's own start within one stretch — stays as it is.

Rejected:

- **One execution per method instance.** The keys carry the instance, so no execution has two starts. Every reader of
  one execution per site would then need the executions the split makes of it, and the flow of one of them does not
  hold the other's nodes; the regressions above are what each forgotten reader costs.
- **The climb alone by instance.** The first start would still decide the next pass, the composite reading and the
  bounding of joins, and so would still hide a start that does not wait.
- **The call-site component of the context only.** It covers library bodies admitted at the call site and leaves two
  receivers of one class in the program with the first-start reading.

## Consequences

- With Polly 7 bodies admitted and their records withheld, the `_joined` finding of the demo goes; the `_left` finding,
  whose attempt may throw before it waits, stays. The normal runs of the demo and eShop keep every finding.
- An execution whose starts differ can gain a pair the first start hid: a start that does not wait now keeps its pair,
  and so, conservatively, does a merged spawn one of whose starts is composite.
- No execution id changes; finding fingerprints and pair identities are untouched.
- The order graph's own spawn edges still read one start for the whole execution: a source before any one start is
  ordered before all of the execution's work, a join after one start orders the rest after all of it, and the return of
  one start's `Parallel` call joins every start's iterations. `a.Start(); Value = 2; b.Start();` with
  `Start() => Task.Run(...)` therefore misses the race of `a`'s task with `Value = 2`. Reading every start there is a
  decision of its own.
- The engine's source changes, and with it the version of the model generator: the demo's generated models are
  re-recorded, and the framework census names a stale engine until its owner regenerates it.
