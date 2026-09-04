# Database indexer tests run on Linux CI

Cache Detective ships a Windows x64 executable, requires the .NET SDK for MSBuild, and its README
says Windows in the first line of the requirements. Its database-indexer tests will nevertheless
run, in CI, on Linux.

Measured on 2026-09-03: the `windows-latest` GitHub runner image (Windows Server 2025) contains no
SQL Server engine at all — not Express, not LocalDB. It ships SQL client drivers and command-line
utilities, and the only database engines present are PostgreSQL and MongoDB, both of which this
project puts out of scope. Testing the catalogue queries on the Windows job therefore means
installing an engine on every run: three to eight minutes of chocolatey or MSI, in the most
fragile position in the pipeline, for a component whose entire substance is a handful of `SELECT`s
against `sys.*`.

A Linux job with an `mcr.microsoft.com/mssql/server` service container gets a real engine in
seconds. `CacheDetective.Core` is portable — it holds the analysis and, by the seam ADR 0002 drew,
the database indexer too, taking an open connection rather than a connection string.
`CacheDetective.Cli`, with its win-x64 RID and MSBuildLocator, stays on the Windows job and is not
built there.

The tests read `CD_TEST_SQL_CONN` and skip, with a stated reason, when it is unset. That is what
makes both jobs work from one test project, and what lets a developer with SQL Express run the same
tests locally by exporting one variable.

What this costs, said plainly: the catalogue queries are exercised in CI against SQL Server 2022 on
Linux and by the developer against SQL Server 2019 Express on Windows, and no job exercises the
combination a user is most likely to have. For `sys.*` catalogue views and
`sys.dm_sql_referenced_entities` the difference is negligible — they have been stable since 2008 —
but "negligible" is not "none", and a future divergence will show up as a test that passes in CI
and fails on a user's machine.

Rejected: installing SQL Express or LocalDB on the Windows runner — it buys platform fidelity that
these particular queries do not need, and pays for it with the slowest and least reliable step in
the build. Rejected: not running them in CI at all — the database indexer would then be the only
component of the plugin with no automated gate, and it is the component with the most external
surface.
