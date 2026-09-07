# The call graph walk is breadth-first

The specification gives the call graph one traversal rule and one budget: walk from the entry points,
cut cycles, stop at depth 12. It does not say what depth a method has when several paths reach it, and
the first implementation answered that question by accident.

That implementation was depth-first with a memo of the shallowest depth each method had been seen at.
A method was skipped when the memo already held a depth at least as shallow as the one it was reached
at, and walked again when a later path offered a shallower one. Both halves of that rule are decided by
the order the paths happen to arrive in. A method whose first path was long was expanded at that depth —
recording the depth cut, if the path was long enough — and then expanded a second time from a shallower
caller, recording every edge below it twice. Reached in the other order it was expanded once. The edge
count the metrics report counts sites rather than distinct pairs, so the difference is visible in the
number.

Nothing fixes that order. `Solution.Projects` yields projects in whatever order the workspace loaded
them, and `SymbolFinder.FindImplementationsAsync` searches the projects in parallel and does not
specify the order it returns an interface's implementations in. Four runs of `cachedet metrics` over one
clean Orchard Core checkout at one revision produced four different edge counts — 15252, 15204, 15027,
14854 — and four different counts of unresolved rows of kind `call`. Everything else held: 2466
vertices, 23 cache operations, 0.338 cache coverage, 24 load diagnostics, every run. The drift was
confined to exactly the two numbers the traversal order decides, which is what named the cause.

So the walk is breadth-first. The frontier is expanded one depth at a time, a method enters it the first
time any caller reaches it, and it is expanded exactly once. That makes the depth of a method its
shortest distance from any entry point — the reading of "depth 12" a reader would assume — rather than
the length of whichever path arrived first, and it makes the set of expanded methods, the edges recorded
from them and the sites the limit cuts a function of the solution alone. Cycles need no separate rule:
a method already expanded is never queued again, and the edge back into it is still recorded, so
`B → A` in a two-method cycle survives exactly as before.

Two orderings are normalised beside it, for the ids rather than for the counts. The projects are
iterated in path order and an interface's implementations in symbol order, because an `unresolved` row's
id is what an `annotate` binds to, and an id that moves between two runs of one solution binds the
annotation to a different site.

The counts move, and every one of them moves down. A second expansion did not only record the edges
below a method again — it ran every per-method analyser again, and `AddCacheOperation` and
`AddUnresolved` append to lists that deduplicate nothing, so a cache call in a twice-expanded method was
counted as two cache operations and an unfoldable key in it as two unresolved rows. Orchard's `before`
row went from 15252 edges, 906 unresolved `call` rows and 23 cache operations to 7433, 336 and 21, and
its cache coverage rose from 0.338 to 0.389, because a duplicated site was inflating both sides of that
fraction. The vertex count did not move at all, at 2466 both ways: the vertex indexes are keyed, so they
were the one place a duplicate could not show, which is what made the drift look narrower than it was.

Reproducibility was never the same as correctness here. nopCommerce reproduced its counts exactly, run
after run, and was inflated in exactly the same way — 23699 edges against 19752 now, and 231 unresolved
keys after its declared recognizer against 121.

Coverage of the graph goes up rather than down: any method within 12 hops of an entry point is now
expanded, where before one whose first path was longer than that could be cut and stay cut.

Rejected: keeping the depth-first walk and sorting its inputs. It would make one machine's runs agree
and would still let a method's depth depend on which caller Roslyn reported first, so the sort would be
load-bearing for correctness rather than for presentation — and the memo would keep re-walking subtrees
that breadth-first visits once. Rejected too: deduplicating edges at the graph instead. It would hide
the symptom in the count while the traversal still decided, run by run, which methods were cut at the
depth limit.
