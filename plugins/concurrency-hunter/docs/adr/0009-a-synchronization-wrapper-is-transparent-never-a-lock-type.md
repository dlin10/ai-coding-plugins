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
