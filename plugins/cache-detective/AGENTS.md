# cache-detective

This plugin exposes MCP tools for configuring a scan workspace, indexing `.sln`, `.slnx`, and
`.csproj` inputs, tracing cache keys and tables, querying findings and unresolved analysis, retrieving
evidence, exporting the graph, and annotating unresolved analysis with `annotate`. Use `/cache-detective:scan` when the user explicitly requests a
whole-workspace cache scan and report; read `skills/scan/SKILL.md` for the required tool order and
`skills/scan/report-template.md` before writing the report.

Two rules matter regardless of which host you are running in:

- **A `likely` finding is an inference.** Say that it is inferred and preserve its confidence; do not
  present it as a statically proven path.
- **An unresolved entry means the graph does not know.** It does not mean the code is clean. Report
  what could not be reduced, its source snippet, and the reason before drawing a conclusion from an
  apparent absence.

The server reads source and build metadata. It must not edit application files and must not execute the
indexed application. Two reads of a live system are part of the design, and both are reads only:

- **The catalogue indexer reads a live database's catalogue when `index_database` asks it to.** It
  issues `SELECT`s over `sys.` catalogue objects to learn which procedures, triggers and views touch
  which tables. It performs no DDL, no DML, and executes none of your procedures.
- **Runtime verification reads the cache and the database only when a scan requests it**, through the
  `verify` section of the workspace configuration and either `--verify` or `"auto": true`. It sends
  `SCAN`, `TYPE`, `GET`, `HGET`, `TTL` and `OBJECT IDLETIME` to Redis, and `SELECT`s the rows a finding
  depends on. It writes nothing to either.
