# The dependency closure searches for one row per target

`depends_on` enumerated paths. Every distinct way the graph could reach a table from a key produced its
own `KeyDependency` row, carrying the confidence and the edges of that particular path, and the walk
that produced them cut cycles with a *path* set: `Descend` added a source's id on the way in and removed
it on the way out. Removing it is correct cycle pruning and it is also the absence of a memo, so a
method reachable by many distinct paths was walked once per path. At the project's depth limit of twelve
and the out-degree of about 4.6 that a real solution has among its handlers, that is on the order of
4.6^12 walks per starting handler, and the rules ask for the closure once per cache key. Each visit then
ran `edges.OfType<Calls>().Where(edge => IsSource(edge.From, source))` over the whole edge list, with the
source id rebuilt as a string at every comparison.

On `DataService.sln` — 64 projects, 5405 vertices, 24 687 edges of which 24 551 are `calls` —
`index_solution` did not return. One core at 100% for 46 minutes, working set flat at 1.1 GB, killed by
hand; a `dotnet-stack` sample named `UnguardedWriteRule.Evaluate → CacheGraphDependencies.Build →
ExpandKey → WalkSource`/`Descend` about twelve frames deep → `GetSourceId` → `Memmove`. No corpus had
ever caught it, because `MetricsCommand` evaluates no rules and never calls the closure: nopCommerce and
Orchard Core were only ever measured through `metrics`, and the corpora that do reach the rules —
eShopOnContainers at 431 edges, and the demo stand — are three orders of magnitude too small to show it.

This is the same defect `docs/adr/0014` diagnosed and fixed for the call-graph *indexer* walk, left alive
in the *dependency closure* walk.

## What the rows were for

Every caller of the closure already threw the enumeration away. `UnguardedWriteRule`,
`StaleParentKeyRule`, `ExternalNoTtlRule` and both trace builders each group the rows by target and take
`OrderBy(Confidence).ThenBy(Path.Count).First()` — the same expression, written out five times. The one
caller that reads every row, `WorkspaceSession.KeySnapshots`, folds them into a digest for change
detection and needs the rows to be stable, not to be many.

So the closure was computing an exponential number of rows to answer a question with one row in it, and
the fix is to ask that question instead: **`depends_on` returns one row per target, at the strongest
confidence the graph reaches it by, and the shortest path at that confidence.** Nothing downstream had
to change, because that is the row all five selecting callers were already computing for themselves.

## Why a memo on the source alone would have been wrong

The obvious repair — memoise the subtree by `(source, remaining depth)` — is unsound while cycles are
cut by a path set, and the counterexample is two methods. With `A → B → A`, the walk that reaches `B`
through `A` prunes the edge back into `A` and returns `B`'s dependencies without it; a later walk that
reaches `B` through `C` would follow that edge. A memo keyed on `B` reuses the first, narrower answer
under an ancestor set that no longer justifies it, and silently loses dependencies.

What is sound is to stop carrying the path at all. A state is a source reached at a confidence with a
depth already spent, and the walk is a shortest-path search over those states: `PriorityQueue<WalkState,
int>` keyed on the number of edges, the same shape `Reachability` already uses for the same
`(Confidence, Length)` label. Two paths arriving at one source, at one confidence, having spent the same
depth continue identically from there, so the shorter dominates the longer at every target below it —
which makes the shortest length per state the whole of what is worth remembering, and bounds the work at
three confidences and thirteen depths per source. Confidence is part of the state rather than minimised
alongside the length, because it must be: `(Confirmed, 10)` and `(Likely, 2)` are incomparable, since the
first is the better row here and the second is the better prefix for everything below. Three confidence
levels make that frontier three entries wide, so it is kept exactly rather than approximated.

Cycles then need no rule of their own. Coming back to a source is never the shorter arrival, so it
improves no label and expands nothing, which is all that cutting a cycle ever did. And the optimum is
always attained on a path that neither of the old prunes would have blocked — a path that visits a
source, or expands a key, twice can have the loop cut out of it, giving a shorter path whose confidence
is the maximum over fewer edges. So the row this returns is exactly the row the enumeration's callers
selected, not an approximation of it.

Paths are rebuilt rather than carried. Each state records the edges of the hop that reached it and the
state before, and the path is walked back from those links for the rows that survive — which is also why
the search costs one array per hop taken instead of one per hop per path through it. Because states are
settled shortest-first, a settled state's predecessor chain never moves under it afterwards.

## What `depth limit reached` had to become

Losing the path set cost something, and a test named it: `Pruning_a_source_already_on_the_path_is_not
_incompleteness`. A two-method cycle used to stop at the second meeting of a source and report the walk
complete. Without a path to test against it instead goes round the cycle spending depth until the budget
is gone, and reporting that as incompleteness would have set the flag on every solution with a recursive
call in it — which is every solution — and made the verifier withhold refutation everywhere. That is a
worse failure than the one being fixed, because it is silent.

So the judgement moves to the end of the walk. A refused descent records the source and the confidence
it was refused at; when the search is over, the walk is incomplete only if some recorded source was never
settled at a confidence at least as strong. A source the walk settled anyway was seen with strictly more
budget than the refusal would have had — every settled state is within the limit, and the refused one is
past it — so nothing below it was missed. Going round a cycle records a stop at a source the walk has
certainly settled, and reports nothing.

Both halves are now pinned. `A_chain_longer_than_the_depth_limit_is_reported_as_incomplete` builds a
sixteen-handler chain with a table at the far end, asserts the flag is set and that the far table is
absent while the near one is present; the cycle test asserts the flag is clear. The positive case had no
test before, which would have let this rule pass vacuously.

## What it measures

`DataService.sln`, same binary, same recognizer, same machine: `index_solution` returns in 152 s where it
had not returned in 46 minutes. Of that, `metrics` — which loads the same solution and runs no rules —
takes 152.6 s on its own, so the closure for every cache key now costs less than the run-to-run noise of
the MSBuild load. The graph is unchanged at 5405 vertices and 24 687 edges, cache-site coverage 0.757,
and `find_issues` returns an `EXTERNAL_NO_TTL` at `confirmed` — a finding whose rule reads both the
dependency row and its rebuilt path, so the closure is exercised rather than merely fast.

`demo/behaviour-snapshot.json`, which records five findings with their chains and confidences, did not
move, and neither did eShop's — though eShop's records no findings at all, so only the demo stand's is
evidence about the rules. The full suite passes at 695, with the eShop and SQL gates set.

An adjacency index is built once per graph version beside the dependency cache, so a lookup costs one
string build rather than a pass over every edge in the graph. That is a second, independent cause of the
same failure and it is fixed on its own terms: with the walk bounded but the scan left in, every visit
would still pay 24 687 comparisons.

Rejected: making the walk breadth-first and visiting each source once, as `docs/adr/0014` did for the
indexer. It answers termination and gets the depth semantics right, but it records a shortest path,
and the shortest path is not always the strongest — a table reachable by a two-hop `likely` chain and a
five-hop `confirmed` one would be reported `likely`, downgrading findings that are currently confirmed.
The confidence dimension in the state is what buys that back.

Rejected: dropping `MAXIMUM_DEPTH` now that the search is polynomial. It would explore more of the graph
and is tempting for that reason, but the constant is the project's one traversal budget — the indexer
walk and `UnguardedWriteRule` both stop at twelve — and lifting it in one walk alone would make them
disagree about what "the chain" is, changing findings on every corpus for a reason unrelated to the
defect being fixed.

Rejected: keeping the row-per-path contract and only bounding the enumeration. The rows would still be
tens of thousands per key on a real solution, every caller would still discard all but one, and the
snapshot digest would still move whenever a path that nothing reads changed shape.
