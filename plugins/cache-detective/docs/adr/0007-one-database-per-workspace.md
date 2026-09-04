# One database per workspace

A `Table` vertex is identified by `schema.name`, and `database` rides along as an attribute that
merges across sites. That is what makes the phase-2 join free: EF knows a table's schema and name
from the model but has no idea which database the context points at, while the catalogue knows the
database and nothing about the code. Identify the vertex by `schema.name` and the two halves meet
without anybody being asked to supply the missing half.

Identify it by `database.schema.name` instead and the join stops working, because the code half
genuinely cannot supply the database. Recovering it would mean mapping each `DbContext` and each
connection string to a configured database — information that usually lives in a config file the
scan does not read, or in a deployment the scan cannot see. The tool would be asking the user to
hand-maintain the very mapping it exists to derive.

So the identity stays `schema.name`, and the price is paid where it is cheapest: the workspace
accepts exactly one database. `workspace_init` refuses a configuration naming two or more, saying
why. The `databases` field stays plural, because the restriction is this phase's and not the
model's.

The failure this prevents is silent and severe. Two configured databases, each with a `dbo.Products`,
collapse into one vertex; a write in one database then appears to invalidate a key derived from the
other, and every chain through that vertex is fiction. A refusal at configuration time is the
"graceful degradation" the project already commits to — an honest stop rather than a quietly wrong
graph.

Rejected: keeping the identity and allowing several databases, with a warning — the warning is read
once and the wrong chains are read forever. Rejected: qualifying the identity and asking the user
for a context-to-database map — it makes the common case (one database, which is what almost every
workspace has) pay for the rare one, and the map is exactly the kind of hand-maintained truth that
goes stale without anybody noticing.
