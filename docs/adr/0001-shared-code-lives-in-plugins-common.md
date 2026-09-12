# Shared code lives in `plugins/Common`, and every plugin still packages alone

`cache-detective` and `concurrency-hunter` are both Roslyn analyzers behind a stdio MCP server, and
they need the same adapter code: the MSBuild workspace loader with its build-host handling
(`plugins/cache-detective/docs/adr/0001`), the paged 8 KB response envelope, version and exit-code
plumbing, the launcher, and the packaging and test-baseline scripts. Copying it was considered.

The shared code moves to `plugins/Common/`, as two projects split by what trimming allows:
`Common.Roslyn` holds the loader and is marked not trimmable, because MSBuild and
`Workspaces.MSBuild` are reflection through and through; `Common.Mcp` holds the envelope and
plumbing, is marked trimmable and builds with the trim analyzer on, so it stays safe for a
consumer that publishes trimmed even though today's consumers do not. Its charter is narrow:
adapter code only. Entry-point finders, recognizers and every other piece of analysis semantics
stay in the plugin that owns them, because a shared finder would tie the meaning of two reports to
one release.

Three things follow. One root `plugins/Directory.Build.props` and one root
`plugins/Directory.Packages.props` carry the repository's invariants and package versions for every
project except the Visual Studio extension, whose own files shadow them and pin what the VS SDK
pins; the four publish flags that differ between executables (trimmed or not, single file, native
self-extract, reflection-based JSON) live in each executable's `.csproj`, where `cachedet` already
keeps them. One umbrella solution, `plugins/AiCodingPlugins.slnx`, exists for the IDE and for a
single Roslyn MCP port; it holds every project but the VSIX, which `dotnet build` cannot build, and
it is never the unit of a gate, of CI or of packaging. Those stay on each plugin's own solution, so
a `concurrency-hunter` task does not pay for its neighbour's 75-second suite. And a change under
`Common` runs the test baseline of every consumer before it merges, because a loader fixed for one
analyzer must not break the other's recorded behaviour quietly.

Consumers today are `cache-detective` and `concurrency-hunter`. `plan-forge-flow` joins the
umbrella solution and the root package versions now, and becomes a consumer only if its vendor
runner is extracted into a `Common.Agents` that `concurrency-hunter` decides to use.

Rejected: copying, which leaves two loaders and two build-host fixes to drift apart. Rejected: a
NuGet package, a third release artifact for two consumers in one repository. Rejected: the umbrella
solution as the gate, for the reasons above.
