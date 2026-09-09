# Verification observes a moment and never suppresses a finding

Runtime verification can produce a result that looks conclusive: the last write to every table a key
depends on is older than the moment that key was cached, so the key cannot be stale. The obvious next
step is to suppress the finding, the way a TTL inside a table's budget already suppresses one.

It does not. Verification attaches its observation to the finding and changes neither the finding's
confidence nor whether it is reported.

A budget and an observation are different kinds of statement. `ttl(k) <= budget(t)` is a property of
the code: whatever happens at run time, the entry cannot outlive the staleness the table is allowed,
so there is nothing to fix. "Nobody has written to `dbo.Prices` in the last four hours" is a property
of the last four hours. The missing invalidation is still missing, and the next write exposes it. A
scan run at 03:00 on a quiet system would suppress every finding it has, and report a clean codebase.

The asymmetry is worth keeping because it runs the other way from intuition. Verification refutes far
better than it confirms. A last-write timestamp older than the key's age is decisive for that moment:
the key is not stale now, and that is a fact, not an inference. A last-write timestamp newer than the
key's age proves much less — the write may not have touched the rows the value was built from, because
the graph joins on tables and not on rows. So the strong result of verification is the negative one,
and the positive one is evidence, not proof.

What it therefore produces is a third vocabulary beside rules and confidence: an observation of
`refuted`, `possible`, or `not verifiable`, carried alongside the finding in the report's own section.
A reader triaging twenty findings can start with the ones verification could not refute, which is the
real value on offer, and none of the twenty has been hidden from them.

Rejected: suppressing refuted findings, or lowering their confidence — it converts "did not happen in
this minute" into "is not a defect", which is the substitution the three confidence levels exist to
prevent. Rejected: raising confidence on a `possible` result — the observation adds nothing the static
path did not already prove, and `confirmed` must keep meaning that the analysis proved every edge.

## The age of an entry cannot refute; only field agreement can

Written above: a last write older than the moment a key was cached "settles that key for that
moment". That is wrong, and the planning review found two independent counterexamples.

The first is `EXPIRE`. The only way to date a Redis entry from outside is its declared TTL minus its
remaining TTL, and any code may extend an entry's deadline after writing it. Set at 09:00 with a
one-hour TTL, extended at 09:59, and read at 10:00, the entry computes as sixty seconds old. A table
changed at 09:30 then looks older than the entry, and the finding is refuted on an entry that is in
fact an hour stale. Nothing observable distinguishes the extension from a fresh write.

The second needs no Redis feature at all. A handler reads a row, the table changes while the handler
is still working, and the handler then writes the value it already had. The entry's write moment is
genuinely later than the table's, and the value is genuinely stale. The gap is the handler's own
duration, which is unbounded, so no clock margin closes it.

So the write moment of an entry says nothing reliable about the freshness of what the entry holds,
and refutation cannot rest on it.

What can rest on itself is comparison. If every field the scan could match between the cached value
and the current database row is equal, the entry agrees with its source right now, for those fields.
That statement needs no clock, no TTL and no assumption about who called `EXPIRE`. It is narrower —
it covers the compared fields and nothing else, and it requires the workspace to declare a table's
key column and where the key's value comes from — and it is sound.

Refutation therefore requires field agreement across an exhaustive sample. Age stays in the design
and stays in the report, because "the table changed after this entry was written" is real evidence
that staleness is *possible*, and because the numbers help a reader judge. It simply never produces
the strong half.

Rejected: declaring by configuration that nothing extends TTLs — it would close the first
counterexample and leave the second, which is the one no configuration can promise away.

## A write to the finding's table after the entry withholds refutation

Age cannot produce the strong half, but it can withhold it, and this is not a contradiction of the
section above. Refutation rests on comparison, and the comparison is narrower than the claim it is used
to set aside: it covers the fields the scan could match, which are only some of what the value was built
from. A table written after the entry was created may have changed a column that is in the cached value
and was not comparable, or one that is not in it at all but feeds a field that is.

So `refuted` requires both halves: the fields of the finding's **own** table agree, and that table was
not written after the entry. A write after it gives `possible` with `basis: age`. This is the same
asymmetry as everywhere else here — evidence of staleness is cheap to trust and evidence of freshness is
not — and it errs toward leaving a finding in front of the reader, which is what this ADR is for.

Another dependency's table has no say either way: its fields agreeing cannot refute a finding made about
a different table, and its clock cannot withhold that finding's refutation.
