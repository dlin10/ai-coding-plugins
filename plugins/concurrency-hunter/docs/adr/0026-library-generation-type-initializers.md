# Library generation keeps type initializers inert, then leaves them out with recipe values

## Context

The `MessageParser<T>` constructor of Google.Protobuf keeps its factory and runs it in the caller's
execution on every parse. Its holder is covered by the driver's triggers, including the span
overloads, but confirmation also reaches the library's descriptor bootstrap through `ParseJson`.
The measurement behind [ADR 0025](0025-a-holders-confirmation-run-collects-accesses-only-in-the-drivers-actions.md)
reached 2,006 bodies and took 45 s with a separate confirmation bound. The factory probe fired in
eight library type initializers as well as in the triggers: the library's parsers share method
instances with the driver's parser. The bound of 1,500 bodies and those extra firings each prevent
confirmation.

Question 101 measured two ways to exclude those firings. Variant A kept initializer bodies in the
reachable set and heap without running their effects as accesses; it confirmed the 3.30.2 holder
in 77–106 s. Variant C left the bodies out and confirmed in 15 s, with 402 confirmation bodies,
but lost static values: `KeepHub.Keep(Action a) { s_box.A = a; }` received `effects: {}` and
`not-run` instead of `library-state`. Raising contexts per method from 16 to 32 did not remove
the merge or change an answer, and doubled heap time. The generation needs the library's existing
objects without treating descriptor bootstrap as an invocation of the member's caller.

[ADR 0006](0006-a-construction-belongs-to-the-execution-that-triggers-it.md) assigns construction
to the execution that triggers it. Program analysis retains that rule. Library generation needs an
explicit exception for inert initializer executions. [ADR 0013](0013-a-driver-seeds-what-a-program-could-have-put-there.md)
supplies user objects through seeds; a replacement for the library's own static value must have a
different origin. [ADR 0017](0017-executions-share-one-walk-graph.md) supplies the
shared execution walk; a second representation of initializer membership would disagree with it.
The constructed-type reachable set of [ADR 0019](0019-corelib-generation-reaches-only-the-types-it-constructs.md),
the every-receiver model of [ADR 0024](0024-a-library-model-holds-for-every-receiver-of-its-member.md)
and the confirmation accesses rule of ADR 0025 remain in force.

## Decision

- **R1.** The engine has three treatments, `run`, `inert` and `off`. `run` executes initializers as in
  program analysis. With `inert`, their bodies remain reachable and lowered, and the heap solves
  their allocations, static stores and calls. Their `TypeInitializer` executions participate in
  the shared walk and give membership to the instances they reach, parenthood to work they start,
  and handing executions to delegates they give unseen code. They collect no access, either in
  the executions' projection or interprocedurally, have no discovery target, code-flow step or
  construction edge from first use, and attribute no semantic gap to a root through themselves.
  Work they start is an ordinary execution with its own accesses. An inert initializer instance
  still runs once for the proof of one object per process. With `off`, the initializer body is
  outside the reachable set, is not lowered and is not activated in the heap.
- A type initializer whose closed arguments contain, at any depth, an open-world driver type or
  an open type parameter runs under all treatments. A heap instance merged from several closed
  types runs when any of their initializers runs. Program analysis and CoreLib generation use
  `run`; their behavior does not change.
- **R2.** Generation reads inert membership in exactly two questions: allocation origin and library
  statics handed to unseen code by reference. Both read it as ordinary `TypeInitializer`
  membership. An object whose creators run only there is `LibraryBefore`; mixed creators are
  classified as under `run`. An inert membership makes the static handoff `library-state`, with
  `library-state-store` naming the static region of the field's type, even when the instance also
  runs in setup or an action. Every other reader excludes inert executions: probe handoffs and
  witnesses, unresolved receivers, foreign accesses, unseeded reads, effects, action trees,
  setup-only questions, fates and holder confirmation. No probe fires in an inert execution.
- **R3.** A non-CoreLib member starts with `inert`. If its final fate pass or its holder confirmation
  reaches the closure bound, the entire member generation restarts with `off`, from driver
  synthesis through effects and the model. The returned answer is the `off` answer. A fate pass
  stopped by the bound is not an overflowing attempt when adding comparison witnesses causes a
  later pass to fit. `closure-bound` is refused only when the final fate pass of `off` also
  overflows; an overflowing `off` confirmation leaves the holder `unknown-execution`. CoreLib
  makes one `run` attempt. The bound stays 1,500 bodies, contexts stay 16 per method, and ADR
  0025 still decides confirmation accesses.
- **R4.** An `off` attempt uses its own open library copy, derived from the ordinary copy. Its
  object-holding static fields and auto-properties become internal and writable; `readonly` is
  removed and a get-only auto-property gains a setter. The analysis index and lowering retain
  the readonly semantics of the ordinary copy: declared readonly except where ordinary seed
  opening removes it. This preserves the one-object proof, defensive struct copies and settled
  paths. Ordinary copies used by `run`, `inert` and CoreLib retain their existing opening. The
  opened copy belongs to the library compilation result: the first `off` attempt opens it lazily,
  all members' `off` attempts share it under the compile cache's rules, and releasing the result
  releases the copy.
- Before setup's seeds, every loaded closed library static storage whose type's initializer is
  off receives a recipe value. Storage identity includes the closed declaring type and the field.
  A new storage found by another fate pass or by confirmation restarts synthesis, seeding and
  confirmation until that set stops growing. A storage is unseeded when it cannot be written
  (including a reverted opening or an interface static the copy does not open), or no recipe
  succeeds. An already writable static needs no opening.
- A recipe value, and every object-holding value nested in it, is built only by a constructor,
  factory, internal constructor of the library copy, concrete implementation of an interface or
  abstract class, or a shared static value from another assembly. A class other than `string`,
  interface, abstract class or struct containing references requires such a source; `default(S)`
  is not a source for a struct containing references. Non-holding arguments keep their ordinary
  defaults. Probes, driver subclasses, delegates, expression trees, containers and reading this
  library's own statics are forbidden sources at every depth, including through canned values.
  A failed candidate rolls back completely before the next is tried. Recipe construction is
  optional, like a seed: failure leaves the storage unseeded and synthesis continues.
  A factory expression alone does not prove an object source: the completed heap must contain
  an object for the assigned value and each nested object-holding recipe argument. A factory
  returning only this library's off static, including a cycle of such assignments, builds no object.
  Such storage joins the same unseeded closed-storage set read by direct and reference accesses.
  The check also applies after the confirmation discovery loop adds storage.
  A selected candidate whose evaluation has no object is rejected through the existing rollback,
  and synthesis tries the next source in its original order. A finished off heap can supply this
  evidence: synthesis repeats while rejected candidates grow, bounded by those candidates. Only
  exhausted candidates leave the storage unseeded. Confirmation reads unseeded recipe storage;
  this adds no reader of ordinary unseeded Seed paths, whose field-definition identity is unchanged.
- A recipe value is not a seed. Its whole construction graph is `LibraryBefore`. Each origin
  reader treats a creator on the recipe assignment path as a creator in a running type
  initializer, including created-by-call and created-in-setup questions. A creator instance shared
  with setup or an action gives the same mixed origin as under `run`; other setup values remain
  driver objects. Seeds add user objects on top as in ADR 0013. A read or write outside setup
  through an unseeded recipe storage gives `incomplete` with `unseeded-read`, directly or through
  a ref parameter, local or return. A merged or open storage, including a reference projection
  that loses its closing, overlaps every matching closing; a distinct seeded closing does not.
- In `off`, scan every body reached in the `inert` set from an initializer that does not run in
  `off`, using the traversal edges recorded by that attempt. Scan it even if `off` also reaches
  it from setup, a recipe or an action. Passing a library static by `ref`, `in`, `out` or as a
  struct receiver to a member without a body or a call with an unresolved target gives
  `library-state`. This scan always treats virtual, interface and constrained calls as unresolved:
  without the inert attempt's heap, a receiver is not proven by a recorded edge to a constructed
  override. Only static or non-virtual calls with a body are followed. Which ref/in/out parameters
  of each scanned helper reach a handoff is computed transitively to a fixpoint; passing a library
  static through those parameters is the same handoff (D-0021). A possible work start gives `incomplete` with `initializer-work-not-run`, naming
  the initializer: any spawn (`Task.Run`, `StartNew`, `ContinueWith`, pool queue, `Thread.Start`,
  `Parallel.For`/`ForEach`), a callback timer (construction of `System.Threading.Timer` at any due
  time, `Elapsed` subscription, `Change`, `Start`, or `Enabled` other than constant `false`), or an
  async call without immediate awaiting. A `Thread` constructor without `Start` and an async
  iterator without enumeration do not start work. A callback timer without a visible start still
  counts, since code outside the body can enable it.
  An async, non-iterator delegate argument passed to a call whose body the scan cannot see
  (a library model, extern, virtual, interface, constrained or unresolved delegate call) may
  start work, even when the call itself is awaited. A scanned callee decides by its own
  delegate invocation. An argument delegate never resolves the handoff call (D-0025).
- **R7.** One server constant limits a member to 120 s across both attempts in a run, model evals and
  `generate`. The run's 900 s budget, timeout/budget/cancellation distinction and stored timeout
  refusal remain; a run keeps no partial generation record. `generate` and model evals return
  `timeout` with the record up to the cut, including the interrupted stage; `generate` writes the
  document and exits 0. External cancellation retains its behavior. The CoreLib tool keeps its
  five-minute limit and its behavior.
- **R6.** G-6 uses schema 4. Its generation record contains answer `mode` (`run`, `inert`, `off`, or
  null before any attempt), ordered `attempts` with mode, fate bodies, nullable confirmation
  bodies and stage seconds, and separate `stages` before the first attempt. Each second from
  the first stage check to completion belongs to exactly one stage; repeated stages accumulate
  within their attempt. All seconds round to 0.1, and their sum equals total seconds within
  rounding. Top-level `reachedBodies` belongs to the answer attempt's fate pass. Model eval
  snapshots retain mode, without timings. Generated model files keep schema 2.

## Consequences

- The `MessageParser<T>` constructor is confirmed as `holder` `result` in `off` for Google.Protobuf
  3.21.9 and 3.30.2, but has no model: `modelReason` is `incomplete`, with `unseeded-read`.
  The `ParseJson(string)` trigger reads `JsonParser.NullValueDescriptor`; no permitted recipe
  source builds that `EnumDescriptor`, which needs a generated-reflection `FileDescriptor`.
  Question 101 stays open. The demo's DCA1002 `High` finding
  `protobuf-parser-factory-at-parse/factory-runs-at-parse` moves to `5d-C`; at `5d-B4c` it has no
  finding (`/descriptor-unseeded`, D-0023). Program analysis, the eShop snapshot and the CoreLib
  generation table stay unchanged. Measurements and all moved snapshot answers are recorded in
  [the run record](../runs/5d-B4c.md).
- The eval snapshot's `fateGoldEntries` floor is lowered manually from 4 to 3 before recording,
  solely for that constructor's lost model with `incomplete` / `unseeded-read` (D-0024). Its
  holder remains confirmed. No other member loses a model and no other floor decreases; the
  snapshot recording mechanism is unchanged.
- **R5.** In fixtures whose `run` fits the bound, `inert` agrees with `run`; `off` agrees except for
  unseedable statics and possible initializer work, for which it refuses a model. There is one
  deliberate handoff difference: a static region passed by reference from an initializer also
  holds a probe seeded through another field of that type. The unordered heap under `run` sees
  the probe and gives `unknown-touch` / `probe-handed-to-unseen`; `inert` and `off` give
  `library-state` / `library-state-store`. The initializer actually runs before the seed is
  written. All three refuse a model (D-0020).
- A bound-stopped pass followed by a comparison witness that narrows the next pass currently
  occurs only in CoreLib generation, where there is no treatment switch. The switching decision
  function is tested directly rather than asserting an unavailable non-CoreLib fixture (D-0012,
  D-0018).
- Recipe values can increase the closure; they do not promise a performance threshold. The run
  measures them against the old fate sizes and the 402-body confirmation measurement. A timeout
  before a completed fate pass does not measure the final closure size.

Rejected alternatives:

- Variant C, leaving initializers out without recipe values: it loses statics and admits the
  empty model for `KeepHub.Keep` described above.
- 32 contexts per method: merging remains and heap time doubles.
- A separate confirmation bound: ADR 0025's measurement does not confirm the holder, and makes
  generation more costly; the same 1,500-body bound applies to both runs.
- A separate map of sites without an execution: it duplicates the membership answered by the
  shared walk and loses the parent of started work and the handing execution of delegates.
- Scanning the bodies of set A minus set B: a helper reached in both sets can still hand statics
  to unseen code or start work on behalf of the excluded initializer.
- A second traversal from the initializers: it can disagree with the dispatch decisions of the
  original reachable-set traversal; use that traversal's recorded edges.
- Leaving every originally unwritable static unseeded: opening the `off` copy makes library
  objects constructible while preserving the analysis's ordinary readonly facts.
- Opening recipe storage in every treatment: it changes ordinary generation, CoreLib and the
  readonly facts whose behavior is to stay fixed.
- A separate copy for each `off` attempt: members of the library share one consistent opened copy
  while closed storage discovery and confirmation converge (D-0017).
