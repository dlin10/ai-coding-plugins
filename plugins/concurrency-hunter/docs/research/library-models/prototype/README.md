# Research prototypes

Reference code from the research of 2026-09-27 (see `../../library-models.md`); no build includes it. Build a prototype
from a copy outside the repository: inside it, `plugins/Directory.Build.props` and central package management apply and
the projects do not restore. Every project restores from the global packages folder only (`RestoreSources`), so nothing
is downloaded; the paths below are the ones of the research machine and need editing on another.

| Prototype | What it does | Run |
|---|---|---|
| `engineprobe` | The generator research: the engine on a user program with a library as metadata or as decompiled source (`Program.cs`), hand-written drivers (`Generator.cs`), synthesized drivers (`Synth.cs`), the question-37 repros (`Repro.cs`) | `engineprobe <dir>` for scenarios S0–S5, `engineprobe <dir> gen`, `synth-gold`, `synth-breadth`, `repro`, `repro-e2e` |
| `compilecheck` | Decompiles whole assemblies, compiles them against the .NET 8 reference packs, counts method bodies without errors; with `DUMP_DIR` set, writes `<assembly>.cs` | `DUMP_DIR=<dir>/decompiled compilecheck <assembly.dll>…` |
| `decomp` | Decompiles each member of a gold-shaped list and its direct callees into the AI input | `decomp <dir> [members.json] [out.md]` |
| `ilprobe` | Taint interpreter over IL, class-hierarchy analysis, no decompiler | `ilprobe <dir>` writes `il-verdicts.json` |
| `scripts` | Scoring: `compare.py` (AI and IL against the gold or source verdicts), `gate.py` (AI vetoed by IL), `score_gen.py`, `score_synth.py`, `oracle_input.py` and `oracle_compare.py` (breadth against a blind labeller); `find_ids.py` and `make_gold.py` build `gold.json` and `ai-input.md` from XML docs | `python <script>` in the working folder |

`<dir>` is one working folder holding `gold.json` (from `skills/hunt/evals/models/`), the files of `../data/` a script
reads, and `decompiled/` with `Polly.cs`, `Google.Protobuf.cs`, `System.Linq.cs` and `System.Security.Claims.cs` written
by `compilecheck`. The scripts expect every input in that same folder.

- `engineprobe.csproj` references `ConcurrencyHunter.Core.Tests.csproj` by the absolute path of the research checkout;
  point it at yours. It uses `EngineFixture` and `StubAssemblies` as they are and changes nothing in the repository.
- `decomp` and `compilecheck` reference ICSharpCode.Decompiler 9.1 from the Visual Studio 18 installation by `HintPath`;
  the product takes the NuGet package (ADR 0012). `compilecheck` uses `Microsoft.CodeAnalysis.CSharp` 5.9.0 from the
  cache.
- `ilprobe`, `decomp` and `compilecheck` read the packages at eShop's versions from the NuGet cache and the shared
  framework 8.0.31: Polly 7.2.3, Google.Protobuf 3.21.9, Serilog.AspNetCore 6.1.0-dev-00289 with Serilog 2.12.0 and
  Serilog.Extensions.Hosting 5.0.1, Swashbuckle.AspNetCore.SwaggerGen 6.4.0, Microsoft.AspNetCore.Authentication.JwtBearer
  8.0.0, Grpc.AspNetCore.Server 2.50.0, Microsoft.EntityFrameworkCore 8.0.0, Duende.IdentityServer 6.2.0.
- `compilecheck` numbers for shared-framework assemblies are meaningless: its reference set mixes the implementation
  directory with the reference pack (CS0433, CS0518). Judge those assemblies inside the engine fixture, as `Synth.cs` does.

`examples/` holds three drivers as `Synth.cs` wrote them: an iterator (`Where`), a holder with its trigger actions
(`WaitAndRetry`), and a stub class for an interface constraint (`FieldCodec.ForMessage` over `IMessage<T>`).
