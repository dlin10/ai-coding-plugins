# Points-to, SMT and a normalized IR are in the first version

The interview weighed the RacerD-style simplification against the SPEC's model: syntactic access
paths rooted at `this`, parameters and statics, sharing decided by DI lifetime, ownership as
"allocated locally and not escaped", a lockset for protection, no points-to, no solver, no IR of
our own. It is a fraction of the code and it is what Infer ships in production.

It is rejected for the first version, and the SPEC's model is built: a normalized SSA-like IR over
Roslyn's control-flow graph, field-sensitive allocation-site points-to with ownership and escape,
and an SMT solver for the last mile of path and selector refinement.

Three false negatives decided it. Two fields aliasing one object (`_a = _b`, then writes through
both) go unreported without points-to, and a race hidden behind an alias is exactly the kind of
defect the product exists to find. Disjointness of `a[i]` and `a[j]` cannot be shown without a
solver, so every `Parallel.For` over an array would report at low confidence or not at all. And
not every shared object is a DI registration: a static field holding an allocation, an object
handed to a thread, a factory result stored on a singleton all need a heap identity that no
lifetime table gives. The IR is kept for the reason TD-010 states: every engine downstream of the
frontend must be testable and evolvable without Roslyn's object model.

The cost is accepted with open eyes: the build is several times `cache-detective`'s size, and the
phase split has to deliver a usable vertical slice before points-to and the solver land, with the
missing proofs answered `Unknown` until they do.
