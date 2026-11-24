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
  words to avoid. A Russian text — an answer to the user, an issue's body or comment, a record in
  `docs/runs/` — keeps these words in English and does not translate them: `access`, not «доступ»;
  `execution instance`, not «исполнение». A word of the code that the vocabulary does not list
  (`copy`, `fold`) stays in English too. Text already written is not rewritten for this.
- `demo/SCENARIOS.md` (Russian) — the demo cases of every phase, with intent and verdict in words.
- `docs/PLAN.md` (Russian) — the finished phases and runs with their gates and what each delivered,
  and the temporary limits: what SPEC describes and the code does not do yet, with the phase or run
  that lifts it. A phase or run still to do is a row that points at its issue.
- `docs/QUESTIONS.md` (Russian) — questions 1–217, closed or moved to an issue (`Перенесён → #N`).
  It takes no new rows. Code, tests, evals and docs cite these questions by number
  (`question 183`); a newer question is an issue and is cited as `issue 312`.
- The issues (see "Issues" below) — the work still to do: phases, runs, questions and tasks.
- `docs/runs/` — one record per run that re-recorded snapshots, evals or expectations: every number
  that moved, with its cause. A record is written once, when its run ends, and is not kept current.
- `docs/research/library-models.md` — the measurements behind ADR 0012; read it before work on
  library models.
- `docs/concurrency-hunter-guide.ru.html` — a secondary explanation written at one point in time. A
  claim in it is a lead to check against the sources above, never evidence.

Precedence: PRD, then a later ADR, then SPEC, then SCENARIOS. PLAN, QUESTIONS, the issues and
`docs/runs/` set no behaviour.

SPEC describes the whole first version, phases 0–7, and the code is partway through it. Phase 8 is
the development PRD 8 names, outside the first version; it brings its own changes to PRD and SPEC.
Where code and SPEC disagree, read PLAN first: a phase or run whose row points at an issue is not
built yet, and the temporary limits name what is still missing. Report any other divergence to the user with both
sides quoted, and leave the code and the document as they are.

## Requirements and process

SPEC says what the first version is; PLAN, QUESTIONS, the issues and `docs/runs/` say how it is
being built. Keep the two apart:

- SPEC is written in the present tense about the finished version. It names no phase, run, date,
  question or issue, and says nothing like "from 5d", "until then", "closed in" or "introduced in". Two things
  only look like exceptions: `phase` and `until` of `expected-findings.json` (SPEC 12.2) are a file
  format, and the phase of a run (Run State, the deadline) is a term of the product. CONTEXT.md
  keeps the same rule: a word and a resolved ambiguity are stated as they stand, not when or where
  they were settled. `build/check-docs.ps1` finds what this rule forbids in both.
- A behaviour that is not built yet is written in SPEC as it will be, and the gap goes to PLAN's
  temporary limits with the phase or run that lifts it and a link to its issue.
- Links run one way: the issues, PLAN, QUESTIONS, SCENARIOS and `docs/runs/` cite SPEC; SPEC cites
  none of them.
- Closing a question moves its decision into SPEC or an ADR, and its issue is closed with a comment
  that names that place and the commit.
- When a run ends, its record goes to `docs/runs/<phase>-<run>.md` if it re-recorded anything, its
  issue is closed with a comment saying what it delivered, with the commit and the record, and the
  temporary limits it lifted leave PLAN. Deferred review findings and leftovers become issues under
  the phase or run that will take them. SPEC changes only where a requirement changed.

## Issues

Issues live in `dlin10/ai-coding-plugins`, on the GitHub project "Concurrency Hunter" (`gh project … 3 --owner dlin10`).

The work is a tree of sub-issues: a phase epic (`concurrency-hunter: phase 5g — …`), its runs
(`concurrency-hunter: 5d run H3 — …`), and under them questions (`concurrency-hunter: Q55 — …`, label `question`) and
tasks. A question no run has taken yet sits under its phase epic, and the run that takes it becomes its parent. Every issue
carries the label `concurrency-hunter` and the project field `Phase` (`5d`, `5g`, `5h`, `5e`, `5f`, `6`, `7`, `8` in the
order of work, then `Tooling` and `Unscheduled`). The engine on program code (`5g`) is built before the libraries (`5h`), and
the row order of the project's Backlog view is the order of work. A question a `5g` run finds goes under run E6 unless it
has an occurrence on a corpus or an example of a missed race in program code. The title is
English; the body of a question moved from QUESTIONS.md is its Russian row, verbatim. Status is only `Todo`, `In Progress`
or `Done`: work that waits for something is `Todo` with a "blocked by" link to the issue it waits for.

When you start work on an issue, move its Status there from `Todo` to `In Progress`: take the ids from
`gh project item-list 3 --owner dlin10 --format json` and `gh project field-list 3 --owner dlin10 --format json`, then run
`gh project item-edit --project-id <project> --id <item> --field-id <Status> --single-select-option-id <In Progress>`.

The project moves an issue to `Done` itself when the issue is closed. So a pull request that delivers an issue says
`Closes #<n>` in its description, and merging it closes the issue. A change merged into `main` without a pull request closes
its issue only if a commit message says `Closes #<n>`; otherwise close it by hand with `gh issue close <n>`. Work on
`feature/race-hunter` reaches `main` long after it is done, so close its issues by hand when the work ends.

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
  is not recognisably a rename or another row of the same theory. `-KeepResults <dir>` keeps each
  test project's trx.
- `build/check-demo-expectations.ps1` checks `demo/expected-findings.json` against the case files on
  disk and checks that the entries of finished phases are unchanged from `HEAD`; `-Complete` also
  requires every case of the current phase's catalog. The phase it checks is named in the script. It
  checks the file only; the analyzer's exact match against the file is `DemoExpectationTests`.
- `build/check-docs.ps1` fails when `docs/SPEC.md` or `CONTEXT.md` names a phase, run, question,
  issue, interview or date of the development process, or links PLAN, QUESTIONS or `docs/runs/`. Run it
  after editing either; CI does not.
- `build/package.ps1` is what CI runs (`build-concurrency-hunter.yml`, with `-Release`). It publishes the single
  `bin/win-x64/concurrency-hunter.exe`, checks that it reports the manifests' version, and runs the
  full suite with `CONCURRENCYHUNTER_REQUIRE_E2E=1`. A host session with the plugin loaded holds that
  exe open, and the publish fails with access denied until its MCP server stops. First it compares the engine fingerprint
  of the tree (`dotnet msbuild src/ConcurrencyHunter.Core/ConcurrencyHunter.Core.csproj -restore -t:ComputeEngineFingerprint
  -getProperty:EngineFingerprint`) with the one `skills/hunt/evals/models/framework-census.tsv` names: with `-Release` a
  difference fails the script, without it the script warns and goes on (ADR 0022). Every change to Analysis or Core that
  changes a token makes the census stale; regenerate it before merging to `main`. With `-Release` it also refuses a tree
  that holds `build/generator-version.pin` (ADR 0023, amendment of 2026-11-19).
- `build/generate-frameworks.ps1` regenerates the built-in census of `Microsoft.NETCore.App` and
  `Microsoft.AspNetCore.App` 10.0 (ADR 0012 and ADR 0022, amendments of 2026-11-07). It needs `CH_ESHOP_ROOT`, a restored
  eShop and a working tree with nothing uncommitted in the engine; it runs `metrics --census <folder>` on the demo and eShop,
  writes `src/ConcurrencyHunter.Core/Providers/LibraryModels/BuiltIn/<framework>/<assembly>.json` and the record, settles
  with `--settle` runs, and prints the report against the record it started from. Its scratch is
  `scratch/framework-census/<time>`. `-Since` lists what changed since the census's commit. Whether to regenerate is the
  owner's call: run `-Since` and ask first. After it, re-record the demo (`build/record-demo-models.ps1`): the census files are
  inputs of the generator version.
- `build/package.ps1` and `build/check-test-baseline.ps1` isolate .NET bundle extraction in a unique
  temporary directory inherited by their child processes, remove it in `finally`, and restore the
  caller's `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. `PublishedExecutableEndToEndTests` extracts each test's
  bundle into a folder of its own and deletes it with the test, so a direct `dotnet test` and the
  gates leave nothing in `%TEMP%\.net`. `run-hosts.ps1` isolates the hosts it starts the same way,
  which reaches a host's MCP server only if the host passes its environment on; a host session
  started by hand still extracts into `%TEMP%\.net`, one copy per build.
- `build/test-bundle-extraction.ps1` checks both wrappers' child environment, cleanup on success and
  failure, exit status, and preservation of the caller's extraction cache without running a build.
- `build/bench-corelib.ps1 -Label <name>` appends one CoreLib engine measurement to
  `skills/hunt/evals/metrics/corelib-bench.json`; its log goes to `scratch/`.
- `build/generate-corelib.ps1` generates every public member of the installed
  `System.Private.CoreLib` 8.0 with `tools/ConcurrencyHunter.CoreLibGeneration` (about two hours)
  and compares the snapshot with `skills/hunt/evals/models/corelib-generation.tsv`. `-Members <file>`
  generates and compares only the declaration ids listed; the same `-Out` resumes a run cut short.
  `<out>/answers.jsonl` keeps every answer with the words of its causes. `-Since` generates nothing
  and lists what changed in the generator, the engine and the build inputs since the commit the
  snapshot was recorded on (ADR 0022).
- `skills/hunt/evals/run-hosts.ps1 [-Hosts claude-code,codex,cursor]` runs the hunt skill headless on
  the demo and writes `skills/hunt/evals/<host>/run.json`. It first mirrors `bin` into the installed
  Codex plugin cache; robocopy exit 8 there means a `concurrency-hunter.exe` started from that cache
  is still running — stop that one process.
- `metrics --target <solution> --out <file> [--limits depth,contexts,scc] [--generation on|read|off] [--recording | --census <folder> [--settle]] [--offline]` is
  the measurement behind the snapshots and `skills/hunt/evals/metrics/*.json`; after a Release build run
  it as `dotnet run --project src/ConcurrencyHunter.Cli -c Release --no-build -- metrics ...`.
  `--generation` is what the run does with the generated model layer: `on`, valid only with `--recording` or `--census`, generates the
  models no layer describes and writes `.concurrency-hunter/models/generated/` under the repository
  root, `read` (the default) only reads and applies those files, `off` leaves the layer out. `--recording`, valid only with
  `--generation on`, spends the recording's own limit (ADR 0031); only
  `build/record-demo-models.ps1` passes it. `--census <folder>` is the owner's census run, which only
  `build/generate-frameworks.ps1` passes: the census budget, the folder as the repository root, and the built-in models
  without the census's own records unless `--settle` is given. `--offline`, valid with every generation mode,
  recording and census, sends no requests and reads library sources only from disk and the source cache; online is the default.
  Metrics schema 1.7 records the mode, library code counts and download totals in `sources`, call-site coverage and each scope analysis's seconds.
- `build/record-demo-models.ps1` writes the demo's committed generated models,
  `demo/.concurrency-hunter/models/generated/`, with an online `--generation on` run of the demo, then re-reads them to show they suffice.
  Record online so transient offline source unavailability does not leave answers that apply only to that run unwritten.
  While `build/generator-version.pin` exists, the build takes that fingerprint as the generator version, and an engine
  edit leaves the demo's files current (ADR 0023, amendment of 2026-11-19). `-Check` sets the committed files aside,
  generates afresh, compares the answers, puts the files back byte for byte, and exits 1 naming each member that differs;
  `-Repin` sets the pin to the tree's computed fingerprint and then records. Without the pin, a source edit in `Analysis`
  or `Core` that changes a token or a directive changes the generator version (an edit of comments or whitespace alone
  does not, ADR 0023), and the script rewrites the demo files.
  Run it when a demo case reaches a library member no committed model describes: a demo run that would generate fails
  `DemoGeneratedModelsTests`. The committed files hold package members only: the script removes the framework folders first
  and fails when the run writes one again, since then the census misses a framework member the demo reaches.
  A difference where either answer is a `timeout` refusal is unsettled, not moved: `-Check` names it apart and exits 2;
  repeat on a quiet machine and repin only if the repeat gives the same.
- `build/check-model-drift.ps1` runs the model evals without recording (`CH_MODEL_EVALS=1`, in its own process), with
  `-Demo` also `build/record-demo-models.ps1 -Check`, and writes its logs to `scratch/model-drift/<commit>/`. See "Engine
  runs and the generated models".

## Engine runs and the generated models

The generator's code is frozen until phase 5h (#427), and the generator version is pinned (ADR 0023, amendment of
2026-11-19). An engine run is one whose diff leaves alone the generator (`src/ConcurrencyHunter.Core/Providers/LibraryModels/Generation/`),
the CoreLib tool (`tools/ConcurrencyHunter.CoreLibGeneration/`), the built-in models, the model evals' gold and snapshot
(`skills/hunt/evals/models/`) and the pin; any other run is a generator run.

- **An engine run's gates.** Its scope gate refuses every change below `demo/.concurrency-hunter/models/generated/`, to
  `build/generator-version.pin` and to `skills/hunt/evals/models/generator-snapshot.json`, and no step sets
  `CH_MODEL_EVALS`. Its final gate runs `build/record-demo-models.ps1 -Check` after the run's last engine change. When the
  check fails, the run moved a demo answer: it runs `-Repin`, its scope then admits the pin and the demo's files, and its
  record names each moved answer with its cause.
- **A generator run's gates** run the model evals, and it may repin and record.
- **Between runs.** After an engine run's commit, before the next run starts, run `build/check-model-drift.ps1` in its own
  `pwsh -NoProfile -File` process. It runs the model evals only: the run's final gate already ran `-Check` on that tree. A
  run is one commit, so a shift it finds belongs to that commit: open an issue naming the commit and the moved answers. A
  failure of `ModelEvalTests.Answers_match_the_recorded_snapshot` on `SerializeToUtf8Bytes<T>` alone, `closure-bound`
  against `timeout`, is the machine's load (#425), not a shift. After a census regeneration run it with `-Demo`: the
  built-in models move answers without moving the pin, and no run gate ran `-Check`.
- **The pin ends** when phase 5h opens the generator or before the branch merges to `main`, whichever comes first: delete
  it, record the demo, and run the model evals.

## Environment variables

| Variable | Effect |
|---|---|
| `CH_ESHOP_ROOT` | An eShopOnContainers checkout at the revision recorded in `skills/hunt/evals/metrics/eshop.json`. Tests marked `[RequiresEShopFact]` skip without it. |
| `CH_MODEL_EVALS=1` | Runs the model evals of SPEC 12.1 item 8a, where a missing package, shared framework or gold file fails. `CH_MODEL_EVALS_RECORD=1` rewrites `skills/hunt/evals/models/generator-snapshot.json` under 8a's threshold rule; `CH_MODEL_EVALS_REPORT=<folder>` writes the verdict reports. |
| `CH_ANALYSIS_LIMITS=depth,contexts,scc` | Overrides `AnalysisLimits` where the caller passes none; `skills/hunt/evals/metrics/run-limits-grid.ps1` uses it. |
| `CH_ENGINE_BENCH`, `CH_ENGINE_BENCH_LABEL` | Set by `bench-corelib.ps1` for its own invocation. |
| `CONCURRENCYHUNTER_REQUIRE_E2E=1` | Set by `package.ps1`. Makes the end-to-end tests fail, rather than pass empty, when the published exe is absent. |

On the owner's machine `CH_ESHOP_ROOT` is always `C:\Dev\eShopOnContainers`: use that value, in gates
and runs alike, without asking for it.

Set `CH_ESHOP_ROOT` before any change that can move the engine's output: without it the eShop tests
skip, `dotnet test` stays green, and only `check-test-baseline.ps1` and `package.ps1` notice. The
suite restores the demo (`DemoWorkspace.EnsureRestoredAsync`) but never eShop. With its
`obj/project.assets.json` files gone, the eShop measurement shrinks to 2 findings, which is also the
number a correct measurement gives, so a broken restore and a correct run read alike. Before measuring
eShop, and before re-recording anything measured on it, check that every `.csproj` has its
`obj/project.assets.json`, and restore it if not:
`dotnet restore $env:CH_ESHOP_ROOT/src/eShopOnContainers-ServicesAndWebApps.sln --disable-build-servers -nodeReuse:false`.

A variable set with `$env:` stays set for every later command in the same PowerShell process. A run's
evals gate sets `CH_MODEL_EVALS=1`, so a `check-test-baseline.ps1` started after it in that process also
runs the model evals and takes twice as long, which reads like an engine slowdown. Run each gate and each
timing measurement in its own `pwsh -NoProfile -File` process, and compare two suite times only when
their skipped counts agree, back to back on one machine.

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

1. Build in Release, then run the individual facts `MetricsCommandTests.Demo_measurement_matches_the_snapshot`
   and `EShopSnapshotTests.EShop_measurement_matches_the_snapshot`, with eShop restored and `CH_ESHOP_ROOT` set.
   They measure the demo with the generated layer read and eShop with it off, decompiling library code
   without source retrieval. CLI `metrics` retrieves original sources and can count library code differently;
   it is not the source of these snapshots.
2. Account for every changed number, then apply the unified diff each failing fact prints to its snapshot.
   Write with `[IO.File]::WriteAllText`, with no trailing newline, as the committed files have none.
3. Run each fact again and see it pass.

`skills/hunt/evals/models/corelib-generation.tsv` is the generator's snapshot of CoreLib: a header
naming the implementation it read and the commit the generator was built from, then one line per
public member with its outcome, the codes of its causes and its model entry. No test reads it;
`build/generate-corelib.ps1` compares a run with it and `-Record` rewrites it, only from a run over
every member with no `timeout` or `threw` line, on a working tree with nothing uncommitted in what the
generator is built from. A different implementation in the header explains differences the generator
did not make; the comparison leaves the commit out. Whether CoreLib is generated again is the owner's
call (ADR 0022): run `-Since` and ask before regenerating.

Account for every number that moved before re-recording. A green eShop snapshot proves that every
eShop finding's identity is unchanged, but the eShop snapshot counts the pairs skipped as `ordered`
and the pairs counted under `assumed` as totals, not by pair: a change to the happens-before graph or
to the proof of a pass moves them, and each shift is explained before the snapshot is re-recorded.

## The skill's text

`skills/hunt/composing.md` is read by the composer agents, and its prose is their example. Its rules
for what may stand in backticks are the ones `NarrativeValidator` enforces: a backticked word outside
them comes back from a live host run as `inventedSymbol` or `inventedLocation`. Backtick in
`composing.md` only what those rules accept, and change the rules and `NarrativeValidator` together.

## plugins/Common

`plugins/Common` is shared with cache-detective. A change there also passes cache-detective's
`build/check-test-baseline.ps1`, with its snapshots unchanged (SPEC TC-21); that script requires
`CD_ESHOP_ROOT` and `CD_TEST_SQL_CONN`.
