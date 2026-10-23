# CoreLib generation reaches only the types it constructs

The model generator stops a member whose reachable set passes 1,500 bodies (TD-034b). On
`System.Private.CoreLib` 8.0.31 the reachable set's own call graph, with every body rooted and a virtual
or interface call reaching every override the class hierarchy offers, has one strongly connected
component of 8,253 bodies (8,036 members); the next has 13, and 12,859 members lie above it. Of the
16,612 public members, 3,827 take a delegate or a value that can hold a user object: 681 lie below the
component, 1,026 in it and 2,120 above it. Dispatch holds the component together. Without the dispatch
edges the largest component is 711 bodies, the resource strings of `System.SR` and the `ResourceManager`
behind them; without type initializers, without delegates, or without the dispatch of `Object` and
interface members alone it stays above 6,000. A driver drags the component in even for a member whose
own closure is one body: its setup calls base constructors that throw with an `SR` message, and its
seeds are enumerated through `IEnumerable`. Of 18 sampled candidates, 2 generate; the others reach
about 11,500 bodies.

The decision, for the generation of `System.Private.CoreLib` only:

- A virtual or interface call reaches only the overrides of types the run constructs, by a constructor
  call in a reached body or by a construction the reachable set starts itself, and of a value type only
  once a reached body boxes it — or, for a value type of an open-world assembly, the driver's, once a
  reached body creates it: such a type stands for a user's value, and a constrained call reaches its
  override with no box. An override whose type is constructed later is reached then.
- The bodies of `System.SR` are never entered, nor those of a type a recognizer owns: a type one claims
  by name, the types nested in it, and a task's awaiters and configured awaitables — but for the type
  declaring the member generated, whose own body is what the run analyses, as an awaiter's
  `OnCompleted`, which no recognizer claims.
- Diagnostics are off, as the runtime runs with no event listener and no stack trace taken: the types of
  `System.Diagnostics.Tracing`, every `EventSource` of another namespace, `StackTrace`, `StackFrame` and
  `StackFrameHelper`, with the types nested in them, are never entered, and a call of their members, a
  creation of their objects and an access of their fields do nothing, what they give back a constant of
  its type — but in the generation of a member of their own, which runs with them on.
- A body outside that set may still run, so a call the heap resolves into one is an opaque call: an
  unknown effect on what its receiver and arguments reach, and a delegate handed to it runs in an
  unknown execution. It never resolves to nothing.
- A member one of the engine's recognizers claims is refused with `engine-recognized`: in the analysis of
  a program its calls are the recognizer's, so a model of it would never be read. Every other member is
  generated. Inside the run a type a recognizer owns is read as a program reads it from metadata: a call
  of its public or protected member is the recognizer's — a lock, an atomic operation, a spawn, a member
  of the collection table — and a call through an interface on its object is decided by that table. What
  a caller outside CoreLib could not do is handed to code the analysis does not see: a call of an
  internal or private member is a call of a member without a body, and a load, a store or an address of
  a field that is not public is an operation on a `dynamic` value, with an unknown effect on its
  receiver and on what it writes, and what it reads followed nowhere. Accessibility is CoreLib's own,
  before the driver opens fields. `ComparerHelpers`, of which the recognizers claim two factories alone,
  stays source. Any other assembly is still refused whole when a recognizer claims one of its methods,
  since a claim by name cannot tell its own type from a namesake there.

The consequences to hold. On the same sample 11 of 18 candidates generate, each within 2 to 96 bodies and
3 to 8 s. The other 7 reach about 6,500 bodies through the runtime's own core, reflection with
`Reflection.Emit`, globalization and formatting, which constructs hundreds of types. Generating all 3,827
candidates confirmed the split: 1,990 were analysed within 2 to 376 bodies, 443 of them giving a model, and
1,837 reached 6,400 to 8,053 bodies; no member fell between. The analysis of a
program, the generation of a package and the `generate` command, which still answers `corelib` for a
CoreLib member, are unchanged. A member that hands a user object to an exception message gets an unknown
effect on that object through `SR.Format`. A constrained call on a value the run creates but never boxes
reaches no override, so the heap's resolution on that value is an opaque call where class-hierarchy
dispatch would run the body — unless the value type is of an open-world assembly, the driver's, which is
constructed where a reached body creates it: a constrained call on the struct probe the driver creates
with `new` (`value.GetHashCode()` in `Nullable<T>`, `x.CompareTo(y)` in `GenericComparer<T>`) reaches
the probe's override.

Reading the recognized types as a program does shrinks the closures that ran their bodies. Of the
1,416 members the snapshot of 24669ef stopped at the closure bound, 204 now reach analysis: 11 give a
model, 142 are refused with `unknown-touch` (the awaiters' `OnCompleted` among them, which hand the
continuation to `Task`'s internal code), 22 with `vocabulary`, 15 with `incomplete` and 12 with
`library-state`; 2 builders' `AwaitOnCompleted` no longer get a driver that compiles, its seeds naming a
type parameter of a generic type's statics the larger closure never loaded. The models the evals read
do not move.

Turning diagnostics off breaks the core the bound still stopped at. The 1,210 members it stopped once the
recognized types had left the run each reached 6,029 to 6,791 bodies, through two gates: `ArrayPool<T>.Shared`,
whose rent and return write `ArrayPoolEventSource` events, behind which `EventSource` builds manifests from
resource strings and formats them (about 3,000 bodies), and `Object.ToString` dispatched to
`Exception.ToString`, whose stack trace resolves methods by reflection (about 1,700). Without them what all
of them reach falls from 6,029 bodies to 1,747. On a sample of 251 of the 1,210 — 240 at random, and every
overload of `String.Join`, `String.Split(char[])` and `AssemblyName.get_Name` — 102 reach analysis within 20
to 1,131 bodies, and 149 stop at 1,755 to 5,230: the globalization core that formatting, parsing and
calendars run, and reflection, which stay past the bound. None of the 102 gives a model yet; most meet the
driver's stand-in for a culture, which runs a culture's real initialization (question 178).

Rejected: generating bottom-up, the models of lower components replacing their bodies. 82% of the
candidates lie in the component or above it, and a driver reaches it through `SR` and dispatch even from
below. Rejected: one access record for many executions, with pairs built over sets of executions. A
generation stops at the closure bound before any access is collected, so the cost it removes is not the
one in the way. Rejected: reaching a body lazily once the heap resolves a call into it. It is exact, but
it is a fixpoint between the reachable set and the heap; it stays an open question. Rejected: leaving diagnostics out as `SR` is, each call of them
an opaque call: it hands what its receiver and arguments reach to code the analysis does not see, where a
runtime with no listener runs nothing. Rejected: raising the closure bound for the globalization core
that formatting and parsing run (about 1,750 bodies): a member past it meets the driver's culture
stand-ins next, and the run grows by minutes and gigabytes. Rejected: serving
`SR` through models of its own assembly. A library model never applies to a member declared in the run,
and changing that is where bottom-up generation would start.

## Amendment, 2026-10-22: the generation of every library reaches only the types it constructs

Question 143 asked whether the narrower reachable set applies to the generation of a package. It does,
and the sentence above that the generation of a package is unchanged no longer holds. A package's
generation dispatched a virtual or interface call to every override its class hierarchy offers, and a
library with many implementations of one interface pulled them all in. In Google.Protobuf, a call of
`IMessage.MergeFrom` reaches every message the assembly declares — `Any`, `Timestamp`, the descriptor
protos — though a parse constructs one: the program's own.

The decision, for the generation of every library:

- A virtual or interface call reaches only the overrides of types the run constructs, by the first rule
  above, with the same reading of value types and of the open-world assembly, the driver's.
- A body outside that set that the heap resolves a call into is an opaque call, as above. It never
  resolves to nothing.
- The rest of the decision stays `System.Private.CoreLib`'s own: `System.SR` never entered, the types a
  recognizer owns read as a program reads them, and diagnostics off. Any other assembly is still refused
  whole when a recognizer claims one of its methods.

Measured on Google.Protobuf before the decision, with constructed-type dispatch put in place of the
class hierarchy for a package and nothing else changed:

| Generation | Class hierarchy | Constructed types |
|---|---|---|
| `MessageParser<T>.ParseFrom(byte[])`, 3.30.2 | 1,571 bodies, `closure-bound` | 65 bodies, analysed, `unknown-touch` |
| `MessageParser<T>.ParseFrom(byte[])`, 3.21.9 | no answer in 15 minutes | 65 bodies, 12 s, `unknown-touch` |
| `MessageParser<T>` constructor, its fate run | 22 bodies | 22 bodies |
| that constructor's holder confirmation, 3.30.2 / 3.21.9 | 2,744 / 2,170 bodies | 2,193 / 1,830 bodies |
| `FieldCodec.ForMessage`, 3.21.9, its holder confirmation | 1,937 bodies | 166 bodies |

No fate and no answer of the constructor or of `ForMessage` moved. The confirmation of the
`MessageParser<T>` constructor still passes 1,500 bodies, through `ParseJson` alone;
[ADR 0025](0025-a-holders-confirmation-run-collects-accesses-only-in-the-drivers-actions.md)
records what the confirmation run collects and why its bound stays.

The consequences to hold. Every generated answer of a package can move, so the model evals' snapshot and
the demo's generated files are recorded again, each move with its cause. A call that the class hierarchy
ran into an override of a type no reached body constructs is now an opaque call: wider, never narrower.

Rejected: narrowing only a generation whose class-hierarchy set passes the bound. A member's precision
would depend on the size of its closure, and a member past the bound would be analysed twice. Rejected:
narrowing only the holder's confirmation run. `ParseFrom(byte[])` would stay past the bound, and a fate
run and its confirmation would read different reachable sets.
