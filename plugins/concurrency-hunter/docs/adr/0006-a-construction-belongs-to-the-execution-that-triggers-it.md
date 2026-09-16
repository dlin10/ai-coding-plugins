# A construction belongs to the execution that triggers it

Most objects the analysis tracks get their fields in a constructor: a singleton allocates the box it
later hands out, a hosted service initializes a field, a type initializer fills a static. A
constructor also writes state that looks shared once construction is over. `SiteCatalog` sets
`Title` in its constructor and actions read it afterwards. Read as ordinary code, that write races
with every read, even though no other execution can see the object until the container hands it
out.

A constructor's accesses to the object it produces, and a type initializer's accesses to its own
type's statics, form no candidate unless the constructor publishes the object (stores `this`
somewhere, passes it on, captures it) before construction ends. Every other access a construction
makes belongs to the execution that triggered it. A controller's constructor runs once per request,
inside each of its actions. A lazily resolved singleton's constructor runs at most once, in an
instance that may overlap every root. A type initializer runs at most once, on the first use of
its type. A hosted service's constructor, and the constructor of every singleton it pulls in, runs
when the host starts: the host resolves all hosted services, the web server among them, before
starting any. So these accesses precede every root. Until phase 3 models happens-before, they are
dropped and counted in coverage instead of being paired. A constructor called by an explicit `new`
is an ordinary call inside the caller's execution.

Rejected: constructors contribute points-to facts and nothing else. Simpler, but a controller
constructor that bumps a static counter is a real race across requests, and it would silently
disappear. Rejected: constructors are ordinary code under whichever root reaches them. Every
singleton configured in its constructor would become a false positive, and the fix would need a
flow-sensitive "not yet published" ownership state that nothing else in the model uses.

The consequence to hold: the demo's construction cases (`controller-constructor-static-counter`,
`construction-other-state`, `hosted-constructor-before-roots`, `constructor-leaks-this`,
`singleton-configured-in-constructor`) encode this rule as final v1 answers. Phase 3 replaces the
dropped startup accesses with happens-before edges without changing any of those answers.
