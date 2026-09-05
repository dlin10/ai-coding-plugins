---
name: scan
description: Run a whole-workspace cache scan across configured .NET solutions, collect cache consistency findings and evidence, and write a Cache Detective report. Use only when the user explicitly invokes the scan.
disable-model-invocation: true
argument-hint: "[--solution <name>] [--budget <n>]"
---

# Scan the workspace

Treat the repository as read-only except for `.cache-detective/workspace.json` written by
`workspace_init` and the report written by this procedure. Read [report-template.md](report-template.md)
before composing the report.

1. Resolve the repository root and call `workspace_init` with `root` only. Its configuration can retain
   `services` client mappings and declared `events` recognizers as well as solutions and budgets.
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
5. Call `index_database` once with the `name` of the configured database, and retain its counts and
   its list of objects the catalogue could not answer for.
   - A configuration with no database is **not an error**. The call says so; treat it as a skipped
     step, not a failure, and record it in the report — without the catalogue, a chain that runs
     through a stored procedure, a trigger, or a view stops where the code stops, and the reader has
     to know that is why.
   - Order does not matter. `index_database` may run before or after `index_solution`; both halves
     pour into one graph. Calling it twice is safe: it replaces that database's half rather than
     adding to it.
6. Call `find_issues` with `include_suppressed: false`, paging until every returned finding has been
   collected. Preserve the header's suppressed count even though suppressed findings are withheld.
7. Call `get_unresolved`, then delegate paged rows to the `static-analyst` subagent with `--budget`
   (50 by default). Run `find_issues` again after annotations. Findings that appear after annotations
   belong under Likely findings and name the annotation assumption.
8. For every returned finding, call `get_evidence` with its `finding_id`, paging until every fragment
   in the chain has been collected.
9. Call `get_unresolved`, paging until every row has been collected. Some rows were recorded while
   indexing; four kinds are derived from the graph as it stands now: two procedure gaps (*the database
   is not indexed* and *the procedure is not in the catalogue of `<database>`*), an event with no
   consumer, and an external call with no unique service endpoint. Report them as the tool words them
   and do not merge them, because they call for different actions.
10. Render `.cache-detective/report-<timestamp>.md` from the template, using a sortable UTC timestamp
   such as `yyyyMMdd-HHmmss`. Group findings by confidence: `confirmed` under Confirmed findings,
   `likely` under Likely findings, and `unknown` under Needs checking. Put load/index failures,
   workspace diagnostics, a skipped database step, and unresolved rows under Needs checking as well.
11. Render each finding's evidence as one linear, top-to-bottom chain. A code site carries
    `file:line`; a stored procedure, trigger, or view carries the name of the database object instead,
    because it has no file and no line. Never render a diagram and never invent a missing link.
12. Return the report path and a compact summary including the visible finding count, suppressed
    count, number of solutions that failed to load or index, and whether the database step ran.
