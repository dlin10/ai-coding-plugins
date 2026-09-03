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
