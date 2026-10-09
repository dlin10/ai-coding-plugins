# Access paths carry no construction

The accesses stage walks the paths from each execution's entries, up to 16 of them for each state a path
reaches, and binds the guards, the locks of enumerations and the code flow of every access along its
path (TD-090). A state was an instance, the object under construction on the path and a segment: the
innermost construction, which a nested `new` replaced. On decompiled CoreLib, scope `four`, the driver's
root reaches 16,922 instances in 5.68 million states, 472 objects under construction per instance at
the median over 1,701 allocations, so 96.5 million path nodes: the walk alone took 657 s, and emitting
the accesses did not finish in 20 minutes. Keeping the object only where an access below could still
observe it leaves 69% of the states, since the call graph of CoreLib is one strongly connected component.
TD-090 bounds the paths per access at 16; keying the bound by the object under construction multiplied
it by the number of such objects.

The decision: a path carries no construction.

- A state is an instance and the segment of its body it runs. Each entry reaches a state on at most 16
  paths of its own, and on one more that forgets where it came from.
- Whether an access is construction-local is asked of the construction analysis of ADR 0017, which the
  execution model keeps after it is built. For the execution, the instance and the operation, over every
  node of the walk graph for that instance whose segment runs the operation, the access is local where
  the object is in `MayIn` at one of them and its construction does not publish it; it is not local
  where the object is not in `MustIn` at one of them, where the execution's own walk reaches what the
  analysis does not, or where the construction publishes the object. One access can be both.
- A read of a readonly field is never canonical where its object may be under construction.
- A construction step of a code flow names the object its edge builds.

The consequences to hold. An object is under construction in every construction its own starts, not only
in the innermost: in `A() { new B(this); }`, a write of B's constructor to A is local to A's
construction, as the execution model already says. Guards and code flows come from at most 16 paths per
instance and segment, as TD-090 says; a state reached on more also gets the path that forgets where it
came from, whose guards are the body's own. On the demo and on eShop the executions, the interprocedural
accesses and the findings are byte-identical to what they were, and the model evals answer as they did.
On `four` the root's walk takes 1 s over 287,415 path nodes instead of 657 s; collecting its 544,983
accesses takes minutes, and 91 of the 208 executions reach the whole component, so the stage is still
cut there. What is left is the number of executions times the accesses each one emits, which turns on
how CoreLib's models are generated.

Rejected: carrying the object under construction on the path as an attribute and bounding the paths per
instance. It is exact on the paths walked, but beyond the bound it is unknown, and an access there would
be either not local in constructors, which reports races nobody can run, or local by the execution
model's rule, which mixes two rules in one access. Rejected: keeping the states and making the walk
cheaper; the 96 million path nodes of each large execution remain. Rejected: dropping the object only
where nothing below observes it; measured, 69% of the states remain.
