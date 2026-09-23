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
