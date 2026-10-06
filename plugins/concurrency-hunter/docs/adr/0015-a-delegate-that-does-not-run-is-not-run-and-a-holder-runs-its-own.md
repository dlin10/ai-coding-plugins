# A delegate that does not run is not-run, and a holder runs its own

Phase 5d run B1 gave a delegate that the **Driver** saw neither run nor kept the fate
`unknown-execution`, out of the caution of the **Open-world rule**. Run B2b makes the `remove`
accessor of every event a member the generator describes, and that is exactly such a member: it
compares the handler with what it holds and drops it. Answered `unknown-execution`, every
unsubscription would run its handler in an **Unknown execution** and erase the precision of a
subscription that a model describes as `holder` `this` — in the common shape of a class that
subscribes in its constructor and unsubscribes in `Dispose`. Run B1 also confirmed a **Holder** when
triggers covered every member of its type and none ran the delegate anywhere else, so an options bag
— an object that stores a delegate only for another component to read and run, as
`SslClientAuthenticationOptions.RemoteCertificateValidationCallback` does for `SslStream` — was
confirmed `holder` `this`, though no member of it ever runs the delegate.

The decision has two parts:

- A **Delegate fate** `not-run`: the call neither runs the delegate nor keeps it. Built-in, project
  and generated models may give it to any delegate parameter of any member; a run neither reaches nor
  runs such a delegate for that call. The generator answers it for a probe that the analysis followed into the call — one passed as an
  `in` temporary the engine cannot name is not — that ran nowhere, that
  nothing keeps, and that was handed to nothing the analysis cannot follow, provided every body the
  call reached was lowered whole, no call in it was a dispatch with no receiver object — a
  delegate passed `in` and invoked is exactly such a dispatch — and no unsupported operation
  received anything that reaches the probe. The heap answers for every path of the code the call reached, not for the paths a driver
  happened to take, so a delegate that is kept nowhere has no way to run later. What the open-world
  rule guards against — calls without a body, dispatches with no receiver, unknown executions, code
  the engine could not lower — is exactly what the condition rules out. `not-run` is narrower than every other fate, so a
  `not-run` answer where the truth is any other fate is an unsafe narrowing.
- A holder runs its delegate in its own members. `holder` — on the result or on `this` — stands only
  when, besides run B1's conditions, at least one trigger ran the delegate the holder keeps in that
  trigger's own execution. An object that only stores a delegate for others is not a holder, and
  its delegate's fate is `unknown-execution`.

Rejected: `not-run` for `remove` accessors only. One observation would get two answers by the kind of
member. Rejected: no new fate, with a hand-written `remove` model repeating the `add`'s fate and a
generated one answering `unknown-execution`. The generated answer would undo every precise
subscription. Rejected: `holder` `this` for an options bag. It is sound — a bag handed to a call
without a body other than as its receiver adds an unknown execution — but that rule adds and does
not replace, so the bag's handler would also run at each call of the bag's own members: wider than
`unknown-execution` where the bag reaches its consumer, and a description of nothing the bag does.

The consequences to hold. The fate matrix of run B1 expects `not-run` where the member does nothing
with the delegate, no longer `unknown-execution`; its holder cells keep their expectation, because
their holder type runs the delegate in a member. A holder that runs its delegate only from a member
the triggers cannot call, or only on a path the analysis does not reach, now answers
`unknown-execution` — wider, and safe. `not-run` rests on the lowering being whole: a body whose
lowering was dropped, or an unsupported operation in reach of the probe, makes the answer
`unknown-execution` instead.
