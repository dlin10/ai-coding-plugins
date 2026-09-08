# A fold yields the set a site may produce, and a set never grants a suppression

The string folder reduced a local assigned differently in several branches to one unknown value —
`{?}`, with the reason `assigned differently in N places`. That is sound: the folder never claimed a
template the code could not produce. It is also coarse enough to lose the finding. eShopOnContainers'
`API.Catalog.GetAllCatalogItems` builds `items` and then, when a filter is present, appends
`/type/{type}/brand/{brand}`; the call folded to `…/catalog/items{?}`, whose tail is unknown, so no
`serves` edge joined it to the Catalog endpoint and the chain stopped at an unresolved row the agent
had to annotate by hand. The same shape reaches cache keys, and a conditional expression —
`cond ? "a:{id}" : "b:{id}"` — was not a supported form at all and fell into the same unknown.

So a fold now yields a **set**: every value the site may produce, each folded independently. One
element is the ordinary case and behaves exactly as before. The conditional expression joins the
branchy local as a second syntax for the same thing, because fixing one and leaving the other would
have been a distinction no corpus can see.

A set is a *may*, and that is the whole of the design. At a `get` or `set` site the distinction does
not arise: the entry may be written under any of the templates, all of them are recorded, and each is
an ordinary `CacheKey` vertex. At an invalidation site it decides everything. A handler whose `Remove`
takes one of three templates removes exactly one of them at run time, so treating the set as coverage
would suppress two real findings — and a suppressed finding is invisible, where a false one is merely
noisy. The edges are therefore still recorded, so that `ORPHAN_INVALIDATION` does not call them dead
and the report can show them, but a set of more than one template covers nothing on its own. This is
not a new rule: the tag merge already states that a merge must never grant a suppression that one site
alone would not, and a multi-valued site is that same collision inside a single call.

The cap is eight, measured on the folded result rather than on the number of assignments, because sets
multiply: an interpolation of two three-valued parts yields nine templates from six assignments, and a
cap counting assignments would let it through. Overflowing the cap returns the previous behaviour —
one `{?}` — with a reason that names the cap rather than the branch, so a reader can tell a limit that
was reached from an expression that was never understood.

The folder is shared by three consumers, and only two of them take the set. Cache keys and HTTP routes
take every element. SQL does not: a multi-valued query text means parsing several batches and uniting
their table sets, which is a separate change with its own failure modes, and no corpus has shown the
need. The SQL policy takes the single element and keeps today's behaviour when there is more than one.

Rejected: letting a multi-valued invalidation suppress the finding. It trades a visible false positive
for an invisible false negative, which is the wrong direction for a tool whose whole claim is that it
does not guess. Rejected too: confining sets to `get` and `set` and leaving invalidation at `{?}`. A
dead `Remove` with two possible keys is still dead, and the report loses the only evidence that would
show it.

## The bound must not launder a choice into a certainty

Written above: a set of more than one template covers nothing on its own. Plan review found that the
size of the set is the wrong thing to read. A nine-valued local exceeds the bound and becomes one
`{?}`; the interpolation around it, `$"key:{choice}"`, then folds to the single literal-bearing
template `key:{?}` — a set of exactly one. By the rule as first written that site is a *must*, and it
suppresses. The bound, whose whole purpose was to stop the folder claiming too much, would have been
the thing that made it claim too much.

So the fold carries a mark beside the set, and the mark, not the count, decides. A fold is *certain*
only when it names exactly one value and nothing on the way to that value stood for several. The mark
is set when the bound was exceeded, when a member of a mixed set could not be named, or when the value
came from a variable built by updating itself, and it propagates through every composite: a composite
whose part is marked is marked, whatever its own size. Only a certain invalidation suppresses.

The same reasoning reaches the deferred path. An unfoldable key becomes a pending cache operation, and
an agent's annotation later turns that pending operation into an edge; without the mark travelling with
it, annotating the unnameable member of a mixed removal would create the very `must` the fold refused.
An annotation resolves what a key *is*. It does not resolve whether the site had a choice.
