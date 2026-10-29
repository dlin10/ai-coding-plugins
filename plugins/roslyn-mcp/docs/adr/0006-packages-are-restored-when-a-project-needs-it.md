# Packages are restored when a project needs it

An unrestored project loads into MSBuildWorkspace without a single `WorkspaceFailed` event and then compiles without its
packages. A solution that was never restored answered with thousands of CS0246, and a PackageReference added after the
last restore left the old assets file in place and its uses reported as CS0103, both with `WorkspaceFailed` silent
(measured 2026-10-29). So before the daemon loads a solution, it checks every project the way the Roslyn language
server does (`ProjectDependencyHelper.NeedsRestore` and `ProjectAssetsReader` in dotnet/roslyn):

- The assets file is the one the project's evaluation names in `ProjectAssetsFile`, not `obj/project.assets.json`. The
  daemon evaluates the projects in its own process, with the properties a solution build passes every project
  (`SolutionDir` and the rest) and the import settings `msbuild -restore` evaluates with.
- A project needs a restore when that file is missing or unreadable, or when the name of one of its PackageReference
  items is not among the packages listed in the file's `libraries`, which is what the restore resolved.
- A project whose evaluation names no assets file, such as an old-style project without package references or a fixture
  broken on purpose, is not applicable and never triggers a restore.

When a project needs a restore, the daemon runs `dotnet restore` on the solution from the solution's folder, with
`--disable-build-servers -nodeReuse:false -p:NuGetAudit=false` and without the MSBuild variables that MSBuildLocator put
in its environment. Then it checks the projects that needed the restore again. `roslyn_load` reports the projects
restored, the ones that still fail with the reason, and the ones not applicable. `partial` is true while any project
failed. The load goes on either way.

## Considered Options

- **Restore on every load** is always right, but it costs a full restore each time: about 24 s on OrchardCore when
  nothing changed, and again after every project edit once the daemon follows edits on disk.
- **A plain existence check on `obj/project.assets.json`** restores on every load under `UseArtifactsOutput`, which
  writes the file to `artifacts/obj/<project>/`. It also misses an assets file that exists but is stale.
- **The dependencies under `project.frameworks`** record what the project asked for, not what the restore resolved, and
  a failed restore writes them too. A project whose restore failed would pass the check and never be restored again.
- **Comparing versions as well**, as the language server does by testing `libraries` against each reference's version
  range, would notice a version raised without a restore. But under central package management the evaluated reference
  carries no version, so the language server compares only names there too. The daemon compares only names everywhere.
- **`WorkspaceFailed`** reports nothing for an unrestored solution and reports failures for projects that are fine.

## Consequences

- The check evaluates every project once more on top of the load's own evaluation: about 1 s on CacheDetective, 4.5 s on
  nopCommerce and 14.6 s on OrchardCore, of which 12 s is evaluation.
- The check runs on any set of a solution's projects, so the daemon can check only the projects an edit affected. A
  restore still runs on the whole solution, and it skips the projects that are already up to date.
- The check misses a raised version, and a PackageReference conditioned on one target framework of a multi-targeted
  project, because it reads no versions and evaluates each project once, without a target framework.
- A package that already reaches a project transitively is already in `libraries`, so adding a direct reference to it
  triggers no restore. The compilation already has that package.
- The evaluation runs on the MSBuild the daemon registered, from the newest SDK. The restore runs under the SDK that a
  `global.json` beside the solution picks.
- A project that cannot be evaluated is reported as failed and triggers no restore.
- Only C# and Visual Basic projects are checked, because those are the ones MSBuildWorkspace loads.
- The daemon runs one restore at a time, because two restores that share a project would race on its obj folder.
- The check belongs to loading with MSBuildWorkspace, not to the registry of solutions. A solution the daemon does not
  load itself is never restored by the daemon.
