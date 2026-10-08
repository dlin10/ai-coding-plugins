# Concurrency Hunter

Concurrency Hunter finds data races and lost updates on the managed heap of one .NET process. It
ships as `concurrency-hunter.exe`, an MCP stdio server driven by the `hunt` skill
(`skills/hunt/SKILL.md`), packaged for Codex, Claude Code and Cursor. Commands below run from
`plugins/concurrency-hunter`.

## Layout

```text
src/ConcurrencyHunter.Analysis/   IR, heap, executions, accesses, the solver seam, the narrative validator
src/ConcurrencyHunter.Core/       Roslyn frontend, root providers, library models, Z3, report rendering
src/ConcurrencyHunter.Cli/        the executable: `mcp` (the server), `metrics`, `generate`
src/*.Tests/                      Core.Tests and Cli.Tests; no test carries Category=Integration
demo/                             the ground-truth corpus: Demo.slnx, expected-findings.json, SCENARIOS.md
skills/hunt/                      the skill, composing.md, evals, recorded metrics
build/                            baseline, expectation, packaging and benchmark scripts
```

`ConcurrencyHunter.Analysis` carries no package and no project reference: `AnalysisBoundaryTests`
reads its csproj and fails on one. A dependency the analysis needs lives in Core behind a seam
Analysis owns, as `Z3ConstraintSolver` (Core) implements `IConstraintSolver` (Analysis). The
architecture is SPEC §2.

## Where the truth lives

- `docs/PRD.md` — product requirements. `docs/SPEC.md` — architecture, algorithms, data model and
  test strategy (§12) of the finished first version. Both are in Russian.
- `docs/adr/` — the decisions. SPEC's header lists the ADRs it has absorbed; a later ADR wins over
  the SPEC passage it decides.
- `CONTEXT.md` — the vocabulary. Name things in code, tests and docs with its words; it lists the
  words to avoid.
- `demo/SCENARIOS.md` (Russian) — the demo cases of every phase, with intent and verdict in words.
- `docs/PLAN.md` (Russian) — the phases and runs with their gates, what each delivered, and the
  temporary limits: what SPEC describes and the code does not do yet, with the phase that lifts it.
- `docs/QUESTIONS.md` (Russian) — the numbered questions, open and closed. Code, tests, evals and
  docs cite them by number.
- `docs/runs/` — one record per run that re-recorded snapshots, evals or expectations: every number
  that moved, with its cause. A record is written once, when its run ends, and is not kept current.
- `docs/research/library-models.md` — the measurements behind ADR 0012; read it before work on
  library models.
- `docs/concurrency-hunter-guide.ru.html` — a secondary explanation written at one point in time. A
  claim in it is a lead to check against the sources above, never evidence.

Precedence: PRD, then a later ADR, then SPEC, then SCENARIOS. PLAN, QUESTIONS and `docs/runs/` set
no behaviour.

SPEC describes the whole first version, through phase 8, and the code is partway through it. Where
code and SPEC disagree, read PLAN first: a row of a later phase is not built yet, and its temporary
limits name what is still missing. Report any other divergence to the user with both sides quoted,
and leave the code and the document as they are.

## Requirements and process

SPEC says what the first version is; PLAN, QUESTIONS and `docs/runs/` say how it is being built.
Keep the two apart:

- SPEC is written in the present tense about the finished version. It names no phase, run, date or
  question, and says nothing like "from 5d", "until then", "closed in" or "introduced in". Two things
  only look like exceptions: `phase` and `until` of `expected-findings.json` (SPEC 12.2) are a file
  format, and the phase of a run (Run State, the deadline) is a term of the product.
- A behaviour that is not built yet is written in SPEC as it will be, and the gap goes to PLAN's
  temporary limits with the phase that lifts it.
- Links run one way: PLAN, QUESTIONS, SCENARIOS and `docs/runs/` cite SPEC; SPEC cites none of
  PLAN, QUESTIONS or `docs/runs/`.
- Closing a question moves its decision into SPEC or an ADR; the header of QUESTIONS.md says what
  stays of the row.
- When a run ends, its record goes to `docs/runs/<phase>-<run>.md` if it re-recorded anything, its
  row in PLAN says what it delivered, and the temporary limits it lifted leave PLAN. SPEC changes
  only where a requirement changed.

## Commands

```powershell
dotnet test src/ConcurrencyHunter.slnx -c Release *> test.log; Get-Content test.log -Tail 50
./build/check-test-baseline.ps1
./build/package.ps1
```

- `build/check-test-baseline.ps1` runs the whole suite and fails when a case that
  `build/test-baseline.txt` records as Passed is missing, skipped or failed — a plain `dotnet test`
  stays green through a skip. `-Record` rewrites the baseline from a green run; afterwards
  `build/check-baseline-rerecord.ps1` compares the rewrite with `HEAD` and refuses a lost case that
  is not recognisably a rename. `-KeepResults <dir>` keeps each test project's trx.
- `build/check-demo-expectations.ps1` checks `demo/expected-findings.json` against the case files on
  disk and checks that the entries of finished phases are unchanged from `HEAD`; `-Complete` also
  requires every case of the current phase's catalog. The phase it checks is named in the script. It
  checks the file only; the analyzer's exact match against the file is `DemoExpectationTests`.
- `build/package.ps1` is what CI runs (`build-concurrency-hunter.yml`). It publishes the single
  `bin/win-x64/concurrency-hunter.exe`, checks that it reports the manifests' version, and runs the
  full suite with `CONCURRENCYHUNTER_REQUIRE_E2E=1`. A host session with the plugin loaded holds that
  exe open, and the publish fails with access denied until its MCP server stops.
- `build/bench-corelib.ps1 -Label <name>` appends one CoreLib engine measurement to
  `skills/hunt/evals/metrics/corelib-bench.json`; its log goes to `scratch/`.
- `skills/hunt/evals/run-hosts.ps1 [-Hosts claude-code,codex,cursor]` runs the hunt skill headless on
  the demo and writes `skills/hunt/evals/<host>/run.json`. It first mirrors `bin` into the installed
  Codex plugin cache; robocopy exit 8 there means a `concurrency-hunter.exe` started from that cache
  is still running — stop that one process.
- `metrics --target <solution> --out <file> [--limits depth,contexts,scc]` is the measurement behind
  the snapshots and `skills/hunt/evals/metrics/*.json`; after a Release build run it as
  `dotnet run --project src/ConcurrencyHunter.Cli -c Release --no-build -- metrics ...`.

## Environment variables

| Variable | Effect |
|---|---|
| `CH_ESHOP_ROOT` | An eShopOnContainers checkout at the revision recorded in `skills/hunt/evals/metrics/eshop.json`. Tests marked `[RequiresEShopFact]` skip without it. |
| `CH_MODEL_EVALS=1` | Runs the model evals of SPEC 12.1 item 8a, where a missing package, shared framework or gold file fails. `CH_MODEL_EVALS_RECORD=1` rewrites `skills/hunt/evals/models/generator-snapshot.json` under 8a's threshold rule; `CH_MODEL_EVALS_REPORT=<folder>` writes the verdict reports. |
| `CH_ANALYSIS_LIMITS=depth,contexts,scc` | Overrides `AnalysisLimits` where the caller passes none; `skills/hunt/evals/metrics/run-limits-grid.ps1` uses it. |
| `CH_ENGINE_BENCH`, `CH_ENGINE_BENCH_LABEL` | Set by `bench-corelib.ps1` for its own invocation. |
| `CONCURRENCYHUNTER_REQUIRE_E2E=1` | Set by `package.ps1`. Makes the end-to-end tests fail, rather than pass empty, when the published exe is absent. |

Set `CH_ESHOP_ROOT` before any change that can move the engine's output: without it the eShop tests
skip, `dotnet test` stays green, and only `check-test-baseline.ps1` and `package.ps1` notice. The
suite restores the demo (`DemoWorkspace.EnsureRestoredAsync`) but never eShop. With its
`obj/project.assets.json` files gone, the eShop measurement shrinks to 2 findings instead of 36 and
reads exactly like an engine regression. Before re-recording anything measured on eShop, check that
every `.csproj` has its `obj/project.assets.json`, and restore it if not:
`dotnet restore $env:CH_ESHOP_ROOT/src/eShopOnContainers-ServicesAndWebApps.sln --disable-build-servers -nodeReuse:false`.

## Demo and expectations

`demo/expected-findings.json` is written by hand, before the implementation it checks, and never
generated from the analyzer's output. Its format, the identity of a finding, `phase` and `until`, and
the one change a finished phase's entry admits are SPEC 12.2; read it before editing the file or
adding a case. SPEC 12.3 is the regression policy: a confirmed false positive or false negative gets
a minimal demo case. `demo/SCENARIOS.md` gives each case's intent and verdict, and its rules section
says how a case file is shaped.

`demo/Demo.slnx` is a solution of its own, outside `AiCodingPlugins.slnx`, which holds the plugin's
projects. A Roslyn server loaded on the plugin's projects answers nothing about demo cases; query one
loaded on `Demo.slnx`, or read the files.

## Snapshots

`src/ConcurrencyHunter.Cli.Tests/Snapshots/demo.json` and `eshop.json` hold the deterministic part
of a `metrics` run: `counts`, including every finding's fingerprint, and the per-scope `coverage`.
Any change to the engine, the built-in models or the demo can move them; the comparing test prints
the unified diff. There is no record mode. To rewrite one:

1. Build in Release and run `metrics --target demo/Demo.slnx` (or
   `$env:CH_ESHOP_ROOT/src/eShopOnContainers-ServicesAndWebApps.sln`) `--out` a scratch file.
2. Keep the lines from `  "counts": {` through the `  ],` that closes `coverage`, drop that line's
   comma, and wrap them in `{`/`}`. The serializer options match, so the slice is byte-identical to
   what the test builds.
3. Write it with `[IO.File]::WriteAllText`, with no trailing newline, as the committed files have
   none.

Account for every number that moved before re-recording. A green eShop snapshot proves that every
eShop finding's identity is unchanged, but no eShop pair is skipped as `ordered`: a change to the
happens-before graph cannot move it, and the demo and the engine tests are the only evidence for one.

## The skill's text

`skills/hunt/composing.md` is read by the composer agents, and its prose is their example. Its rules
for what may stand in backticks are the ones `NarrativeValidator` enforces: a backticked word outside
them comes back from a live host run as `inventedSymbol` or `inventedLocation`. Backtick in
`composing.md` only what those rules accept, and change the rules and `NarrativeValidator` together.

## plugins/Common

`plugins/Common` is shared with cache-detective. A change there also passes cache-detective's
`build/check-test-baseline.ps1`, with its snapshots unchanged (SPEC TC-21); that script requires
`CD_ESHOP_ROOT` and `CD_TEST_SQL_CONN`.
