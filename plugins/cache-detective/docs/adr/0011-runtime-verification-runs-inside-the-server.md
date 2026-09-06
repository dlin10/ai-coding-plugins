# Runtime verification runs inside the server

The specification draws runtime verification as a subagent driving two third-party read-only MCP
servers, one for Redis and one for SQL Server, and lists them in the architecture diagram beside the
cache-detective server rather than inside it. `CONTEXT.md` was written the other way round — "everything
the server touches, and later SQL Server and Redis, is read-only" — and only one of the two can be
built.

Verification is built into the server, and the `runtime-verifier` subagent calls one tool of it.

Three reasons, and the first is the one that decides it. The privacy rule says the report must not carry
a sensitive field's value and must not log a full cached value; with third-party MCP servers the value
reaches the agent's context before anything can redact it, because fetching it *is* the tool call. The
rule would be enforced at the point where it has already been broken. Inside the server, the sample is
drawn, compared and redacted before a single byte becomes a tool result.

Second, the read-only guarantee is checkable only where the code is ours. The database indexer already
demonstrates the shape: one class holds every statement the indexer issues, and a unit test drives it
against a fake connection and asserts what arrives. A third-party Redis server is a promise in a README;
`SCAN`, `GET`, `TTL` and `OBJECT IDLETIME` in one class with a test over the issued commands is a fact.

Third, one verification is one question — is this key older than the last write to the tables it depends
on — and it needs the graph to know which tables those are. Split across two foreign servers it becomes
three round trips and a join performed by the agent in prose, which is the thing the whole design exists
to avoid: engineering belongs in the server, and the agent reasons over what the server could not settle.

The cost is a Redis client in the published executable, which is a new dependency in a single-file
publish that ADR 0001 keeps deliberately thin, and a second SQL Server login with rights the indexing
login is documented not to have. Both are accepted and both are stated in the README, because the
alternative hides the same rights behind someone else's server.

Rejected: third-party MCP servers as the specification drew them — the privacy rule cannot be honoured
on the far side of a tool boundary. Rejected: the hybrid, SQL through our server and Redis through
someone else's — it keeps the privacy problem for exactly the half that holds the cached values, which
is the half that has it.
