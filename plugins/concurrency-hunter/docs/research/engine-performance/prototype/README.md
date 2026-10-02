# Profiling prototypes

Reference code from the engine profile of 2026-10-01 (see `../../engine-performance.md`); no build includes it. Build a
prototype from a copy outside the repository: inside it, `plugins/Directory.Build.props` and central package management
apply and the projects do not restore. Every project restores from the global packages folder only (`RestoreSources`),
so nothing is downloaded; the paths are those of the research machine and need editing on another.

| Prototype | What it does | Run |
|---|---|---|
| `corelib-spike` | Decompiles `System.Private.CoreLib` 8.0.31 one file per top-level type (or as one file) and compiles it with Roslyn as the corlib, without references; reports bodies with errors and binds a `string.Join` driver against it | `corelib-spike decompile <spike>\out\pertype`, `corelib-spike compile <spike>\out\pertype` |
| `engine-spike` | The engine as built over the decompiled CoreLib and a driver compiled against it: `EngineFixture.ReachScope` replicated with one hand-made root, then `EngineFixture.Solve`, `Execute` and `Analyze`. `study <root> <exclusion> [solve]`: root `run` (`string.Join`), `setcount` or `register`; exclusion `none`, `four` (CoreLib overrides of `ToString`, `Equals`, `GetHashCode`, `Dispose` made bodiless), `four-linkcut`, `top10`, `top20`, `all` (all 944 dispatch slots). Each stage stops at 10 minutes; `MAX_WS_GB` stops it on its working set (default 6) | `engine-spike <spike>\out\pertype study run four solve` |
| `profile` | `profile-solve.ps1`: the `four` solve with counters, a 3-minute sampled trace and two gcdumps. `profile-executions.ps1`: the `all` executions stage with counters, a stack snapshot and a gcdump at 2 GB and at 4 GB, no memory cap, and a guard that stops the harness (its exact PID) when the system's free commit falls under 1.5 GB. `speedscope_top.py`: top methods by inclusive and exclusive time on one thread, recursion depth and callers. `counters_table.py`: a counters CSV as one row per sample | `pwsh -File profile-solve.ps1 -Spike <spike>`; `python speedscope_top.py <file.speedscope.json> [thread] --top 20` |
| `gcretained` | Retained sizes from a `.gcdump`: the dominator tree of the object graph, per type the retained size of its outermost instances, the largest single objects with the types dominating them, and the dominator-tree children of an owner object (its fields' collections, with their reference counts) | `gcretained <file.gcdump> [top] [type-substring] [exact owner type]` |

`<spike>` is one working folder: `out\pertype` holds the decompiled CoreLib, `engine\bin2` the built `engine-spike` with
the engine DLLs next to it, and the profile scripts write `profile\`.

- `engine-spike.csproj` references the engine by `HintPath` into the repository's built
  `ConcurrencyHunter.Core.Tests\bin\Release\net10.0` and copies those DLLs into its output. Build it once into a separate
  folder (`bin2`) and profile that copy: another session may rebuild the repository's bin meanwhile. It reaches two
  private members by reflection (`IrLowering.RootBodyId`, `ProgramIndex._variantTypes`), so it breaks when they change.
- `corelib-spike` references ICSharpCode.Decompiler 9.1 from the Visual Studio 18 installation by `HintPath` (as the
  library-models prototypes do) and `Microsoft.CodeAnalysis.CSharp` 5.9.0 from the cache.
- `gcretained` references `dotnet-gcdump.dll`, `Microsoft.Diagnostics.FastSerialization.dll` and
  `Microsoft.Diagnostics.Tracing.TraceEvent.dll` from the installed `dotnet-gcdump` 10.0.745401 tool
  (`~/.dotnet/tools/.store`); `GCHeapDump` and `Graphs.MemoryGraph` are public there.
- The tools are `dotnet-trace`, `dotnet-gcdump` and `dotnet-counters` 10.0.745401 and `dotnet-stack` 10.0.731102, installed
  globally.
