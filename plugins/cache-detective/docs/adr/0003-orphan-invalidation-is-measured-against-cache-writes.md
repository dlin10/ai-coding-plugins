# Orphan invalidation is measured against cache writes

The specification defines `ORPHAN_INVALIDATION` as "a `Remove` of a key nobody writes", and glosses
it as dead code or a typo in the key. "Writes" has two readings, and they behave very differently in
a graph that has no database half yet.

Read as a write to the **cache**, the rule fires when a template is removed somewhere and cached
nowhere: no `caches` edge to any key the removal pattern covers. That is exactly dead code or a
misspelled key, it is decidable from the code alone, and it pairs with `PATTERN_MISMATCH`, which
then names the template the author probably meant. This is the reading the gloss describes, and the
one implemented.

Read as a write to the **database**, the rule fires when a cached key is invalidated but no handler
in the graph writes any table it depends on — so the invalidation looks unnecessary. In phase 1 that
is almost always false. Stored procedures, triggers, migrations and services outside the workspace
all write tables the graph cannot see, and each of them turns a correct invalidation into a reported
defect. The reading becomes defensible once the DB indexer lands, and can be added then as a
distinct rule with its own name rather than by quietly redefining this one.

Rejected: shipping both readings, with the database one marked `likely` — the phase-1 report would
fill with `likely` noise whose only real content is "the graph does not have a database in it yet",
which is a fact about the tool, not about the code being scanned.

## Phase 2 did not meet the condition

Written above: the database reading "becomes defensible once the DB indexer lands". The DB indexer
landed in phase 2, and the reading is still not defensible, because that sentence named the wrong
condition. What the rule actually needs is that the graph sees *every* write to a table — and the
DB indexer sees the procedures and triggers of one configured database. Migrations, services
outside the workspace, SQL Agent jobs and ad-hoc scripts write tables it still cannot see, and each
of them turns a correct invalidation into a reported defect exactly as before. Phase 2 lowers the
false-positive rate; it does not establish the premise.

The reading is therefore deferred again, and this time with a measurable condition instead of a
milestone: it ships when the false-positive rate has been measured on a real corpus — nopCommerce
and Orchard, the same runs that ADR 0005 defers role accuracy to.
