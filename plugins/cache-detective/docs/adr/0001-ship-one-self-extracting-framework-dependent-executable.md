# Ship one self-extracting, framework-dependent executable

The plugin runs on developer machines, and a developer machine that can be scanned already has the
.NET SDK on it — `MSBuildWorkspace` needs that SDK to load a solution at all, so nothing is gained
by carrying a runtime as well. Publishing is therefore framework-dependent: `SelfContained=false`,
and the README states .NET 10 SDK as a requirement rather than pretending the plugin is standalone.

What is deployed is still exactly one file. That is a constraint, not a preference, and it collides
with how Roslyn loads projects now. Measured on 2026-09-02 against
`Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.9.0: the package ships `BuildHost-net472/` and
`BuildHost-netcore/` under `contentFiles`, and `MSBuildWorkspace` runs one of them as a separate
process. A plain `PublishSingleFile` bundles managed assemblies and leaves those two folders loose
next to the executable, so "one exe" and "modern MSBuildWorkspace" are not compatible by default.

`IncludeAllContentForSelfExtract=true` is what reconciles them: the whole publish output, build host
included, goes inside the executable and is extracted to the per-user bundle directory at launch,
and `AppContext.BaseDirectory` then points at that directory — which is where Roslyn probes for the
build host. One file on disk, one file in the release, and the launcher inherited from
`plan-forge-flow` needs no change: it finds `bin/win-x64/cachedet.exe` or fetches that single asset
from the release named by the manifest version. Trimming is off, and stays off: Roslyn and MSBuild
are precisely the code that lives on reflection and `AssemblyLoadContext`, and a trimmed build of
them fails on the user's machine, silently, months later.

The cost is a first-launch extraction pause and a hard floor of .NET 10 on the target machine. Both
are checked rather than hoped for: the end-to-end stdio test runs against the published executable,
and the smoke test indexes a real solution through it, so a build host the bundle failed to carry
fails in CI instead of in someone's repository.

Rejected: committing the executable to git, the way `find-files` does — 25–35 MB of incompressible
binary per rebuild, in a repository whose other plugin already moved off that model. Shipping a
folder in a release zip — predictable and slightly faster to start, but it makes the launcher an
archive extractor and abandons the one-file requirement outright. Writing our own project loader on
`Microsoft.Build.Locator` and dropping `MSBuildWorkspace` — guaranteed one file and a fast start,
but it is a hand-built design-time build, with multi-targeting, `.slnx` and restore state all to get
right, for a deployment detail that `IncludeAllContentForSelfExtract` already settles. Pinning an
older Roslyn that still loaded MSBuild in process — that is Roslyn 4.7-era, before `.slnx` support,
and this repository's own solutions are `.slnx`.
