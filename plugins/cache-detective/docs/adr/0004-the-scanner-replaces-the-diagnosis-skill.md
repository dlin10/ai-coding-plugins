# The scanner replaces the diagnosis skill

The plugin shipped first as one model-invocable skill: a procedure for tracing a single suspect key
through its writes, reads and evictions, triggered whenever a user complained that a value was
stale. That skill is removed, and `skills/scan` — `disable-model-invocation: true`, reachable only
as `/cache-detective:scan` — takes its place.

The two do not coexist well. The scanner is a whole-workspace act: it indexes every solution, builds
a graph, applies rules and writes a report, and it is expensive enough that nobody should trigger it
by describing a symptom in passing. The diagnosis skill is the opposite — it fires on a symptom, by
design, and it competes for exactly the situations where the scanner would be the better answer.
Keeping both means the model chooses between them from a sentence of user prose, which is the least
reliable moment to make that choice.

The capability that is lost is real: without the diagnosis skill, nothing fires automatically when
someone says data is stale, and the plugin does nothing at all until it is called by name. That is
accepted for this phase — a scanner whose findings carry evidence and a chain is a stronger answer
than a procedure the model follows by hand — and the marketplace entry and both READMEs are
rewritten so the plugin no longer advertises what it stopped doing.

Rejected: merging the two into one skill with a mode argument — it re-creates the same choice inside
the skill and forfeits `disable-model-invocation`, which is the whole mechanism keeping an expensive
scan from starting on its own. Keeping both skills — two triggers for one subject, and the cheaper,
weaker one wins whenever the user's phrasing happens to match it.
