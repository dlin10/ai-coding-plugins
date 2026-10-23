# A holder's confirmation run collects accesses only in the driver's actions

## Context

A **Holder** stands only when the driver's triggers cover every member a caller can call on its type,
and a second run, rooted at the triggers too, shows each trigger running the held delegate only in its
own root execution ([ADR 0015](0015-a-delegate-that-does-not-run-is-not-run-and-a-holder-runs-its-own.md),
SPEC TD-034b). That confirmation run has the fate run's closure bound, 1,500 bodies, which SPEC chose
when the `MessageParser<T>` constructor's driver with triggers reached 1,766 bodies and its heap solve ran
over 13 minutes. A confirmation run past the bound confirms nothing, and the delegate's fate is
`unknown-execution`.

`MessageParser<T>` of Google.Protobuf keeps the factory its constructor is given and calls it in every
`ParseFrom`, in the caller's execution; the demo case `protobuf-parser-factory-at-parse` and the model
evals' `MessageParserCtor` both rest on that holder. With the reachable set of the
[amendment to ADR 0019](0019-corelib-generation-reaches-only-the-types-it-constructs.md), the
constructor's confirmation run was measured, by the reachable set alone of each trigger:

| Roots of the confirmation run | 3.30.2 | 3.21.9 |
|---|---|---|
| The driver's actions only | 22 bodies | 22 bodies |
| One `ParseFrom`, `ParseDelimitedFrom`, `With…` or `Object` member | 25 to 51 | 25 to 50 |
| `ParseJson(string)` alone | 1,904 | 1,449 |
| Every trigger but the two `ParseJson` | 169 | 168 |
| Every trigger | 2,193 | 1,830 |

`ParseJson` alone carries the confirmation past the bound: its closure is the descriptor bootstrap,
`Google.Protobuf.Reflection` (884 bodies), the root namespace (419) and the well-known types (351).
With the bound raised to 20,000, the 2,193-body run solved the heap in 22.6 s and its executions in
1.9 s, but its accesses stage had not finished after 641 s, at 7.3 GB.

What the generation reads of a confirmation run's accesses stage is narrower than what the stage
collects. The probes' firings, which confirm the holder, come from the executions stage. The holder's
inputs read the accesses stage, but only in the executions of the driver's own actions: what the call and
the enumeration stored in library objects, what instances that run only in setup stored, and the
delegate seeds setup stored. No reader looks at an access made in a trigger's execution, and those are
where `ParseJson`'s bodies run.

With the accesses stage narrowed as decided below and a bound of its own of 3,000 bodies, the
confirmation run of the 3.30.2 constructor, its holder covered by span triggers and without
`System.Object`'s members, reached 2,006 bodies and was not stopped; the generation took 45 s at 813 MB,
against about 12 s when the confirmation stops at 1,500. The holder was still not confirmed. The
factory's probe fired in the own root execution of every `ParseFrom`, `ParseDelimitedFrom` and
`ParseJson` trigger, and also in eight type initializers of the library — the well-known types'
`…Reflection` classes, `DescriptorReflection` and `FeatureSetDescriptor` — which build their descriptors
by parsing them with the library's own `MessageParser<T>` instances. The method instances of `ParseFrom`
and `CreateTemplate` are shared between those parsers and the driver's, so the load of `factory` there
sees the probe too, and a firing outside the driver's actions makes the fate `unknown-execution`.

## Decision

- The confirmation run's accesses stage collects only the executions of the driver's own actions —
  setup, the call and the enumeration — and none of a trigger's executions or of what a trigger
  starts. Its executions stage stays whole. One classification of executions by the driver's actions
  decides both what the stage collects and what the holder's inputs read.
- The confirmation run keeps the fate run's closure bound, 1,500 bodies.

Rejected: a bound of its own for the confirmation run, 3,000 bodies. Measured above, it took the
`MessageParser<T>` constructor's generation from about 12 s to 45 s, close to the member limit of 60 s
with which a run generates (TD-034c), and the answer stayed `unknown-execution` for the firings in the
library's type initializers. A bound of its own is worth its cost once those firings are told apart from
the driver's; that stays open. Rejected: collecting only the stores into the driver's static fields. The
holder's inputs read stores into library objects in the call's executions and in setup, and would lose
them. Rejected: collecting only the accesses that are not reads, in every execution. The readers would
see the same, but the executions of `ParseJson` would still be walked. Rejected: one confirmation run
per trigger, each within 1,500 bodies. `ParseJson` alone reaches 1,904 bodies in 3.30.2, and one question
becomes two dozen runs.

## Consequences

- A confirmation run walks no trigger's executions in its accesses stage, whatever its size: the cost
  that made a 2,193-body run pass 7.3 GB is gone from every confirmation.
- The confirmation run has no accesses in a trigger's executions. Code that reads them from that run
  reads nothing, and must widen what the stage collects first.
- The holder of `MessageParser<T>` stays `unknown-execution`, and the demo case's `High` finding waits
  until the firings in the library's own type initializers are told apart from the driver's.
