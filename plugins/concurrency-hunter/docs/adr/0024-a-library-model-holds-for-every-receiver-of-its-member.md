# A library model holds for every receiver of its member

## Context

Phase 5d run B3 wrote three demo cases whose library calls the generated layer did not yet describe,
and recorded for each a finding expected from run B4 on:

- `polly-execute-invokes-delegate` — DCA1002 `High` on `State.Hits`, because
  `ResiliencePipeline.Execute` runs the delegate before it returns, in the action's execution;
- `polly-circuit-breaker-callback-at-execute` — DCA1002 `High` on `Breaker.Opened`, because the
  circuit breaker runs `OnOpened` inside `Execute`;
- `protobuf-parser-factory-at-parse` — DCA1002 `High` on `Tally.Created`, because
  `MessageParser<T>` runs its factory at `ParseFrom`.

Before run B4 was planned, the three were checked against the decompiled Polly.Core 8.7.0 and
Google.Protobuf 3.30.2.

- `ResiliencePipeline` is sealed and keeps an internal `PipelineComponent`, and that component
  decides what `Execute` does with its callback. The pipeline of an empty builder holds
  `PipelineComponent.Empty`, which calls the callback at once. A hedging strategy runs it in
  `Task.Run`. A strategy a program adds through `AddStrategy` is user code the analysis does not
  see. A **Library model** describes a member once, for every receiver, and the **Open-world rule**
  already makes a seed in a receiver's field stand for library and user code alike
  (`receiver-member-unseen`). An `invoke-now` fate for `Execute` would be an **Unsafe narrowing** for
  every pipeline with hedging.
- `CircuitStateController<T>` does not call `OnOpened` itself. It queues it on its own
  `ScheduledTaskExecutor`, whose single loop, started by `Task.Run`, runs one queued task at a time,
  and a synchronous `Execute` waits for the task it queued. Two runs of `OnOpened` never overlap, so
  the expected pair on `Breaker.Opened` would be a false positive.
- `MessageParser<T>` keeps its factory and calls it in each `ParseFrom`, in the caller's execution,
  so the expected finding is true. The **Model generator** cannot confirm the **Holder** yet:
  `ParseFrom(ReadOnlySpan<byte>)` is a trigger the **Driver** cannot call, the type has 25 trigger
  candidates against the driver's 24, and a confirming run whose roots include `ParseFrom` and
  `ParseJson` passes the closure bound of 1500 bodies.

## Decision

A library model holds for every receiver of its member. Its fates and effects do not depend on how
the program built the receiver, and a delegate whose fate depends on what the receiver holds — the
strategies of a Polly pipeline — keeps `unknown-execution`.

The expectation of a demo case that calls a library is decided against the library's own code. A
case whose recorded future finding the library's code contradicts gets the verdict the code gives,
through the replacement its `until` already requires.

For the three cases, run B4 records:

- `polly-execute-invokes-delegate` keeps DCA1002 `Medium`;
- `polly-circuit-breaker-callback-at-execute` has no finding, because the breaker runs `OnOpened`
  one at a time;
- `protobuf-parser-factory-at-parse` keeps no finding until run B4b, which confirms the holders of
  large packages together with their narrower reachable set, and the `High` finding moves to B4b.

Rejected: models generated for the receiver as the program built it, keyed by the receiver's
construction or found by following the builder chain at the call. It would give the Polly case its
`High` finding and would see the breaker's serial executor. It needs a rule for when a model applies
at a call, a generator that runs per receiver shape, and an engine that carries the receiver's
construction to the call: a run of its own, not part of the generator's precision.

Rejected: relaxing the open-world rule for an internal type in a sealed receiver's field. The
library's own hedging component is reached all the same, and it runs the callback in `Task.Run`.

Rejected: keeping the circuit-breaker case's `High` expectation and rewriting the case into a true
race between the action and `OnOpened`. Finding that race needs the same per-receiver analysis.

## Consequences

- A call on a receiver the program composed — a pipeline, a client built from options — keeps the
  widest fate its member allows for any receiver. Precision for one particular composition lies
  beyond the first version, and `polly-execute-invokes-delegate` documents the limit until then.
- `polly-circuit-breaker-callback-at-execute` guards against the false positive a later run would
  raise if it saw `OnOpened`'s execution without the executor's order.
- The Protobuf case's replacement carries `until: 5d-B4b`, so the run order gains B4b.
