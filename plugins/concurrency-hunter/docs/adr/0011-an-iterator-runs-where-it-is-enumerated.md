# An iterator runs where it is enumerated

Up to phase 4 a call of an iterator method was an ordinary call: the whole body of the iterator ran,
for the analysis, at the call that creates it. C# runs none of it there. The call builds an
enumerator and returns; the body runs a piece at a time inside `MoveNext`, wherever and whenever
somebody enumerates, and its `finally` runs at `Dispose`. Placing the body at the call is wrong in
both directions that matter. `lock (g) { it = Walk(); } foreach (var x in it) { }` runs the body
without the lock, and the analysis called it protected. `var it = Hold(); _field = 1; foreach (...)`
with an iterator that takes a semaphore made the write between the two lines protected by a
semaphore nobody had taken yet. Both are the one failure this analysis must not have: a proof of
protection where there is none.

The decision is that an iterator's body runs in the execution that enumerates it, at the
enumeration. A `foreach` over a value that may be the iterator — in the body that created it or in
any body it reaches through the analysis's own points-to — runs the body at the loop: the locks held
at the loop head are held on entry to each `MoveNext`, a lock the body holds at a `yield return` is
held over the loop's body, and whatever the body still holds when it ends is held after the loop.
An iterator that reaches a consumer the analysis cannot follow — an opaque call, a framework
enumerator such as `ToList`, an explicit `GetEnumerator` it does not model, or a heap location it
escapes through — is, in addition, enumerated by an **Unknown execution**: one that may overlap
every execution of its process scope, itself included, that nothing orders, and that holds no lock
on entry.

Rejected: keeping the body at the call and only refusing to lift its locks there. It is simpler and
closes the semaphore case, but leaves the lock around the creation protecting a body that runs
outside it. Rejected: modelling only a `foreach` in the creating body and leaving an escaped
iterator at its call without protection. It keeps the execution wrong — an iterator stored at
startup and enumerated by every request would run in startup, ordered before all of them — and
wrong executions lose races as silently as wrong locks.

The consequence to hold: an escaped iterator costs findings. Its body overlaps everything, so a
shared helper that returns a lazy sequence the caller hands to LINQ can produce findings a reviewer
may read as noise. That is the price of not knowing who enumerates it, and it is paid in the open —
the finding names the unknown execution — rather than in a missed race. Phase 5 may narrow it once
the library table knows which framework consumers enumerate synchronously in their caller.

## Amendment, phase 5b: a delegate handed to an opaque call

Up to phase 5b a delegate passed to a call that is neither a recognized spawn or timer API nor a
source method was never invoked: its body was lowered with its member and reached by nobody. A
delegate handed to an **Opaque call** is the same unknown as an escaped iterator — code the analysis
knows will run without knowing who runs it, when, or how often — and it gets the same answer: its
body runs in an **Unknown execution**, which may overlap every execution of its process scope,
itself included, that nothing orders, and that holds no lock on entry.

Rejected: not invoking it and letting the call's unknown effect reach only what the delegate
captures. It keeps today's counts, but the body's own accesses stay out of every pair, and a lambda
that writes a singleton through a library the table does not describe would need an accepted AI
fact to be seen at all. Rejected: invoking it synchronously at the call, as a LINQ operator would.
That places the body in the caller's execution, under the caller's locks and ordered with the
caller's code, which is a proof of protection and of order the analysis does not have for a callee
it cannot read.

What the delegate captures of the execution that handed it over — a per-request object the lambda
fills — the unknown execution touches as that execution does, so those objects stay confined and
pair with nothing new; only what is shared overlaps everything. Measured on eShop before this was
settled, treating such captures as reached by a second execution would have paired every
per-request object handed to a view, a mapper or a LINQ operator with itself across requests.

A delegate handed at one site by an execution that runs once, and runs that site once, is the one
place the unknown call does not overlap itself: the call may run it many times, but one at a time,
as a spawn from such a site does not overlap itself. A token's `Register` from a hosted service is
the common case. Anywhere else — handed by a request, at two sites, or by two executions — two
calls of it may be under way at once. An escaped iterator keeps overlapping itself: anybody may
enumerate it, and nothing about where it was created says how often or by whom. Measured on eShop,
the narrowing still leaves the seed's Polly lambda overlapping itself, since the seed hands it both
from startup and from the retry lambda of `MigrateDbContext`, which the analysis cannot tell apart.

The consequence to hold: until the sub-phase that makes a library member known models its
invocation — LINQ operators with delegates, retry policies, mediators in 5e — their lambdas run in
unknown executions and cost findings, paid in the open as with iterators. An accepted inferred fact
cannot remove such an execution (TD-038); it can only add what it proves.
