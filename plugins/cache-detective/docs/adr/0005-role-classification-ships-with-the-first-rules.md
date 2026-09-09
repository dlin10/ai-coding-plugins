# Role classification ships with the first rules

The specification schedules `role` for phase 3, alongside the HTTP joining and `ExternalSource` work
it was written next to. It is pulled forward into phase 1, because every detection rule in the
specification is defined as applying only to keys with `role: cache`, and a rule set shipped without
the classification it filters on is a rule set that runs on the wrong keys.

What it costs is small: by the time the classification runs the graph already exists, so the three
signals are all cheap. A template matching a built-in mask (`session:*`, `lock:*`, `idempotency:*`,
`ratelimit:*`, `token:*`) is a store; a write path with no `reads` on it is a store, because a value
derived from nothing cannot go stale; and `increment`, `SetNX`, `Expire` and `lock` semantics are
stores by construction. Anything left is a cache, and anything genuinely ambiguous becomes
`unresolved` with kind `role` rather than being defaulted into the rules' scope.

What it buys is precision on the finding that would otherwise dominate a first real report. A
session entry, a distributed lock and a rate-limit counter are all written and never invalidated,
which is `UNGUARDED_WRITE` by the letter of the rule and correct behaviour in fact. Reporting them
would teach the first user of this plugin that its findings need filtering, and that lesson is very
hard to unteach.

The heuristic's own accuracy is the open question the specification already names, and it is not
answered here — it is measured later, on nopCommerce and Orchard, before anything else is built on
top of it.

Rejected: masks only — five lines, and it catches the obvious names, but a counter or a lock named
anything else walks straight into the rules. Deferring the whole thing to phase 3 and letting rules
run on every key — it makes the first report noisy in exactly the way that costs a tool its
credibility, and the noise is indistinguishable from a real finding without reading the code.
