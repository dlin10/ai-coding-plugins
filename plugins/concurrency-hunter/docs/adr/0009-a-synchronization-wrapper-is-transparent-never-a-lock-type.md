# A synchronization wrapper is transparent, never a lock type

TD-086 said that an unknown type with `Lock`/`Unlock`/`AsyncLock`-shaped members is not protection.
The rule bought safety cheaply: no shape-matching heuristic can mistake a logging helper or a
domain type called `PriceLock` for a mutex, and protection is the one verdict that *removes* a
candidate, so a wrong proof loses a real race silently.

It also refuses the most common hand-written synchronizer in .NET. An `AsyncLock` over
`new SemaphoreSlim(1, 1)`, whose `LockAsync` returns an `IDisposable` releaser and is used as
`using (await _lock.LockAsync())`, is ordinary C#, written because `lock` cannot span an `await`.
Under TD-086 every access inside such a scope is reported unprotected. Measured on the demo, that
is a false positive on a shape a reviewer recognises at a glance, and a tool whose first finding is
obviously wrong is a tool nobody reads to the second.

The decision is not to recognise custom locks. It is to make the wrapper transparent, so that
nothing about it is ever recognised at all. A method summary already carries its lock transfers, so
a call that acquires a modelled primitive inside is already an acquire at the call site; phase 4
adds the other half, that disposing an object whose disposal releases a modelled primitive is a
release at the point of disposal. Protection is then proved by the same must-hold dataflow that
proves `lock`, and the lock identity is the region of the underlying primitive, never of the
wrapper.

A wrapper therefore proves protection only when all three hold: the acquire reduces to a modelled
primitive — `Monitor`, `System.Threading.Lock`, `Mutex`, `ReaderWriterLockSlim`, or a
`SemaphoreSlim` whose capacity is proven to be the constant `1` — on a region with a proven
identity; the release reduces to that same primitive on that same region; and the release happens on
every path out of the scope, exceptional ones included. Anything left unproven is not protection,
exactly as TD-086 intended.

Rejected: matching the shape — a type whose name ends in `Lock` with a method returning
`IDisposable`. It is the heuristic TD-086 banned, and it would prove protection for a wrapper whose
`Dispose` does nothing. Rejected: trusting `using` syntactically, so that any `using` around an
access counts. It proves nothing about what is disposed. Rejected: leaving TD-086 alone and
declaring the false positive a known limitation. The machinery needed to fix it — an effect on a
returned value, lifted to the call site — is the same machinery phase 4 needs for a callee that
joins on a parameter, so the limitation would have been kept at no saving.

The consequence to hold: identity is the primitive's region and not the wrapper's, and that cuts
both ways. Two wrapper instances that hand out scopes over the same proven `SemaphoreSlim` do
protect each other, so a pair under them is `sufficient`; two wrappers over different semaphores
are `different-identity`, however alike their types. A wrapper over a `SemaphoreSlim` whose capacity
is a parameter, or over a type the analysis does not model, gives no protection at all — not
`partial`, not a lowered confidence, nothing.

Phase 4 removes candidates in several ways — proven-distinct selectors and guards that cannot both
hold are the others — but this is the only one that turns a synchronization-shaped construct into a
proof, which makes it the one whose failure is indistinguishable from safety.

## Amendment, phase 4b: what a lifted scope carries

A scope a callee opens and its caller closes keeps what was true of it inside the callee. If the
callee held the primitive across a suspension point — an `await` or a `yield return` — the scope is
`partial` for a thread-owned primitive where the caller holds it too, exactly as it would be had the
caller written the same code inline (TD-083); a `SemaphoreSlim` whose permits the callee does not
give back one for one stays unpaired. Phase 4 lost both on the way out of the callee.

An iterator opens its scopes where it is enumerated, not where it is called (ADR 0011). A monitor it
holds at a `yield return` covers the body of the `foreach` that enumerates it, and one it holds both
when its body ends and at every `yield return` a `break` can leave it at, net of the `finally` blocks
the disposal runs there, is held after that loop; both are `partial`, because the holding crosses the
`yield`. In the simplest shape the enumerating thread does hold the monitor, so this is conservatism
rather than a missed race, and it was chosen deliberately: an enumerator can be moved to another
thread between two `MoveNext` calls, the analysis does not prove that it is not, and the error a
wrong `sufficient` makes is the silent one. Phase 4 lifted the iterator's exit at the call, so a
write between creating the iterator and enumerating it counted as protected. An iterator also closes
a scope its enumerator opened, on the same terms as a call: a lock its body lets go on every path
without having taken it is let go by the enumeration — at every step when an element may still
follow that exit, since nothing proves which `MoveNext` runs it, and at the disposal when none can,
so a `finally` after the last `yield` leaves the loop body held and the code after the loop not. A
handler around the enumeration holds none of the locks the body lets go on some path, because the
step that throws may have let them go first. Left out, a `Monitor.Exit` in the iterator's `finally`
kept the enumerator's monitor held after its loop.

Closing a scope and forgetting a lock are two questions. A scope counts as closed only where the body
lets the lock go on every path, because that is what proves protection. A caller stops holding a lock
as soon as one path of a call, or of a step of an enumeration, may let it go without the body ever
taking it itself: an exit some path from the body's entry reaches without passing the body's own
entry of that lock. A `lock` statement's exit stands behind a flag the analysis does not follow, and
the must-state before it holds nothing, but every ordinary path to it passes the entry, so it stays
the body's own; `if (flag) Monitor.Enter(gate); else Monitor.Exit(gate);` does not. Phase 4 asked the first question for both, so
`if (x) Monitor.Exit(gate)` in a callee left its caller's monitor held after the call.

A call its caller does not await is a spawn (TD-060a): its synchronous prefix runs in the caller and
its tail after the first `await` is another execution. Such a call opens at the call site only what
the prefix acquires and the body still holds wherever it can hand control back — at every `await`
and at its end; whatever the tail acquires belongs to the tail's execution and never protects the
caller, and neither does a lock the prefix took and the tail lets go, because that release runs
concurrently with the caller's code. Rejected: lifting nothing from a call that is not awaited,
which is simpler and errs the safe way, but loses a scope a synchronous prefix really opens.
Rejected in code review: lifting everything held at the first `await`, which reads the requirement
literally and proves protection for a caller whose lock the tail may already have released.
