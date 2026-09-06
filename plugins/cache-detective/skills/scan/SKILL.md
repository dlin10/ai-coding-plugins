---
name: scan
description: Run a whole-workspace cache scan across configured .NET solutions, collect cache consistency findings and evidence, and write a Cache Detective report. Use only when the user explicitly invokes the scan.
disable-model-invocation: true
argument-hint: "[--solution <name>] [--budget <n>] [--verify]"
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
   - Page the diagnostics to the end. `pages` in the envelope says how many there are, and a second
     page is read back from the last index rather than indexing again, so it is cheap. A long message
     arrives split across items that share an `id` and are numbered `part` of `parts`: join those in
     `part` order before reporting it, or the report will quote half a sentence.
   - Record `loadComplete`. When it is false, `missingProjects` names the projects the solution
     declared and the load did not open, and every finding that would have come from them is absent
     from the report — say so instead of letting a partial scan read as a clean one. Both count only
     C# and VB projects, the kinds MSBuild opens; every other declared entry is listed in
     `skippedProjects` with its extension as the reason and does not make the scan partial. When
     `missingProjectsHidden` is above zero the rest of the names arrive as a diagnostic beginning
     `missingProjects continued:`, paged like any other long message. `emptyProjects` names the
     projects that opened cleanly but hold no source at all — they gave the graph nothing, and the
     scan is still complete. `skippedProjectsHidden` and `emptyProjectsHidden` count the names those
     two lists could not show, and the rest arrive the same way under `skippedProjects continued:`
     and `emptyProjects continued:`.
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
9. Decide whether to verify against the running system, from the workspace's `verify` section — whose
   `auto` is visible as `verifyAuto` in `workspace_status` — and the `--verify` flag. Four combinations,
   and each writes something different in the report:
   - **No `verify` section and no `--verify`.** Do not verify. Record that runtime verification was not
     configured, so every finding rests on the static analysis alone.
   - **No `verify` section, `--verify` given.** Do not verify, and do not treat this as a failure of the
     scan. Record that verification was asked for and could not run because the workspace declares no
     `verify` section, and name what one needs: an `env:` reference for `redis`, one for `database`, and
     a `tables` entry giving each dependent table's key column and where the key's value comes from.
     Without `tables` nothing can be refuted at all.
   - **A `verify` section, `auto` not set, and no `--verify`.** Do not verify. Record that the workspace
     can verify but this run was not asked to, and that `--verify` would have.
   - **A `verify` section with `auto: true`, or `--verify`.** Verify. Read
     [verifying.md](verifying.md), then delegate the `confirmed` and `likely` findings to the
     `runtime-verifier` subagent, which calls `verify_finding` for each and pages until every sampled key
     has been collected. Record every observation with its reason, and record the findings that were not
     verified along with why.

   Verification never changes a finding's confidence and never decides whether a finding is reported.
   Whatever it observes, the findings stay where step 11 puts them.
10. Call `get_unresolved`, paging until every row has been collected. Some rows were recorded while
   indexing; four kinds are derived from the graph as it stands now: two procedure gaps (*the database
   is not indexed* and *the procedure is not in the catalogue of `<database>`*), an event with no
   consumer, and an external call with no unique service endpoint. Report them as the tool words them
   and do not merge them, because they call for different actions.
11. Render `.cache-detective/report-<timestamp>.md` from the template, using a sortable UTC timestamp
    such as `yyyyMMdd-HHmmss`. Group findings by confidence: `confirmed` under Confirmed findings,
    `likely` under Likely findings, and `unknown` under Needs checking. Put load/index failures,
    workspace diagnostics, a skipped database step, and unresolved rows under Needs checking as well.
    Put whatever step 9 observed under Runtime verification, which is its own section and never moves a
    finding out of the group its confidence put it in.
12. Render each finding's evidence as one linear, top-to-bottom chain. A code site carries
    `file:line`; a stored procedure, trigger, or view carries the name of the database object instead,
    because it has no file and no line. Never render a diagram and never invent a missing link.
13. Return the report path and a compact summary including the visible finding count, suppressed
    count, number of solutions that failed to load or index, whether the database step ran, and
    whether verification ran.
