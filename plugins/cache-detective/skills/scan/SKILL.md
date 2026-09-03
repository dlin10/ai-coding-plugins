---
name: scan
description: Run a whole-workspace cache scan across configured .NET solutions, collect cache consistency findings and evidence, and write a Cache Detective report. Use only when the user explicitly invokes the scan.
disable-model-invocation: true
argument-hint: "[--solution <name>]"
---

# Scan the workspace

Treat the repository as read-only except for `.cache-detective/workspace.json` written by
`workspace_init` and the report written by this procedure. Read [report-template.md](report-template.md)
before composing the report.

1. Resolve the repository root and call `workspace_init` with `root` only.
2. If no workspace file exists and that call reports that solutions are required, find every `.sln`
   and `.slnx` beneath the root. Show the relative paths to the user and ask them to confirm the exact
   list. Do not write configuration before confirmation. Call `workspace_init` again with `root` and
   the confirmed `solutions` to create the file.
3. If the first call returns an existing configuration, use it unchanged. Never replace its budgets
   with defaults. If `--solution <name>` was supplied, select the single configured solution whose
   configured path or filename matches `<name>`; report an ambiguous or missing match instead of
   guessing.
4. Call `index_solution` once for every selected solution path. Continue after a load or indexing
   failure. Retain every failure and workspace diagnostic for the report.
5. Call `find_issues` with `include_suppressed: false`, paging until every returned finding has been
   collected. Preserve the header's suppressed count even though suppressed findings are withheld.
6. For every returned finding, call `get_evidence` with its `finding_id`, paging until every fragment
   in the chain has been collected.
7. Render `.cache-detective/report-<timestamp>.md` from the template, using a sortable UTC timestamp
   such as `yyyyMMdd-HHmmss`. Group findings by confidence: `confirmed` under Confirmed findings,
   `likely` under Likely findings, and `unknown` under Needs checking. Put load/index failures and
   workspace diagnostics under Needs checking as well.
8. Render each finding's evidence as one linear, top-to-bottom chain. Include a file and line for
   every code site and a database object name for every database site. Never render a diagram and
   never invent a missing link.
9. Return the report path and a compact summary including the visible finding count, suppressed
   count, and number of solutions that failed to load or index.
