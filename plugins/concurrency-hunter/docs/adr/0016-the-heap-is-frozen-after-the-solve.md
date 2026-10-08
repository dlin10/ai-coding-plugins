# The heap is frozen after the solve

Every stage after the solve — the execution model, ownership, happens-before, the accesses stage —
asks the solved heap what a value points to. Some answers name an object the solve never computed:
the object of `lock (new object())`, the event `HappensBefore` waits on, what a call without a body
is handed. Resolving such a value added its region to the solver's live map, and the heap handed
the map out by reference. So the heap a stage saw depended on which stage asked first: on CoreLib
scope `all` each `ExecutionModel.Build` added the `char[]` allocation of
`CultureInfo.GetUserDefaultUICulture`, a second `Build` on the same heap gave one more ownership
entry, and caching, lazy evaluation, an early exit or parallel calls of `Resolve` each changed the
set — parallel calls also raced on the map.

The decision: the heap is frozen when the solve ends, and every stage after it only asks.

- Every query runs in a query frame and writes nothing the solver tracks. A region the solve did not
  make is named by id without a place in the heap: a lookup by id finds it, the list of regions and
  its count do not, it has no fields, and no region points to it. A delegate keeps no state, a
  registered region gains no types, and an empty set is read without a write.
- The `reference-lookups` counter counts only the lookups of the solve.
- A write to tracked solver state inside a query throws, so a stage that would change the heap fails
  where it tries, instead of changing what the stages after it see.

Rejected: a solve that makes a region of every allocation its summaries name. The object of a
`lock` on a fresh object would become one object per process, and that lock would protect what it
does not. Rejected: resolving an allocation the solve did not make to no object. `HappensBefore`
would lose its wait handles and quiet events, and with them orderings it proves today.

The consequences to hold. Every stage after the solve sees the same heap, a repeated
`ExecutionModel.Build` gives the same ownership, and caching, lazy evaluation and parallel `Resolve`
calls are safe to add. A named region is not in the list of regions, so code that wants every
object a value may be must resolve the value, not enumerate the heap.
