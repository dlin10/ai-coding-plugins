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

The server reads source and build metadata. It must not edit application files, contact databases or
caches, or execute the indexed application.
