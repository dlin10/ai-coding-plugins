# Verifying findings against the running system

Verification looks at the cache and the database as they are right now and says what that was worth.
It observes. It never changes a finding's confidence and never decides whether a finding is reported;
see `docs/adr/0012`.

## Which findings are taken

Only findings whose confidence is `confirmed` or `likely`, and only where the key's role is `cache`.

A finding at `unknown` rests on analysis that did not complete, so a reading of the live system cannot
settle it either — what would be compared is not known to be what the value was built from. A key whose
role is `store` is not a cache at all: a session entry, a lock and a rate-limit counter are written and
never invalidated by design, which is why the role exists.

At most 20 keys are read for one finding. That is the tool's limit and not a preference: a template
matches as many keys as the application has entries, and a scan that read them all would be a load test.

## What each observation means

- **`refuted`** — every field the scan could compare between the cached value and the row it was built
  from is equal, across an exhaustive sample of keys that existed throughout the traversal, **and** the
  finding's own table was not written after the entry was created. The entry agrees with its source *for
  those fields, right now*. It does not mean the missing invalidation is not missing: the next write
  still exposes it.
- **`possible`** — at least one comparable field differs, or the finding's table was written after the
  entry was — a write to that table withholds refutation even when every compared field agrees, because
  the compared fields are only some of what the value was built from.
  Staleness is possible. This is the observation worth triaging first. `basis` says which of the two it
  rests on — `field_difference`, `age`, or `both` — so do not go looking for a field that differs when
  the answer came from the clock alone.
- **`not_verifiable`** — the tool could not tell. The reason says why, and the reasons are different in
  kind: no `verify` section, an unreachable cache, a refused permission, a key whose role is not
  `cache`, a store outside `verify.stores`, a key with no table dependency, a traversal that stopped
  short, a sample trimmed to the limit, an ambiguous match between key and template, or a field type the
  comparison does not cover. Report the reason as given.

A result may also be marked partial: something failed part of the way through and the readings taken
before it are still reported.

## Only field comparison refutes

Refutation rests on field agreement and on nothing else. If every field the scan can match between the
cached value and the current row is equal, the entry agrees with its source for those fields. That
statement needs no clock, no TTL, and no assumption about who called `EXPIRE`.

**Age never refutes**, and cannot be made to. Two independent reasons, both in `docs/adr/0012`:

1. `EXPIRE` may extend an entry's deadline long after the value was written. An entry set at 09:00 with
   an hour to live, extended at 09:59 and read at 10:00, computes as sixty seconds old and is an hour
   stale. Nothing observable tells the extension from a fresh write.
2. A handler may read a row, wait while the table changes, and only then write the value it already
   had. The entry's write moment is genuinely later than the table's and the value is genuinely stale.
   The gap is the handler's own duration, which is unbounded, so no clock margin closes it.

So a table written *after* an entry is real evidence that staleness is possible, and a table written
*before* it settles nothing at all. Age contributes the `possible` signal and the numbers in the
report. It never produces the strong half.

## Do not say refuted where the server did not

Report the tool's observation verbatim. Do not conclude `refuted` from a reassuring age, from a small
sample, from a partial result, or from your own reading of the evidence. If the tool said
`not_verifiable`, the finding is not verified, and the report says so along with the reason.

## What you are not given

You never see a real cache key or a cached value. A key reaches you as its template and a short hash;
a field reaches you as its name and whether it differed. A field whose name matches a sensitive mask is
marked redacted. Do not ask for more, do not reconstruct a key from the placeholder values, and do not
put either into the report.
