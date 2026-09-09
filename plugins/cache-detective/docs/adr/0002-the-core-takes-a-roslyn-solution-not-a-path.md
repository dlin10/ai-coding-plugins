# The core takes a Roslyn Solution, not a path

`CacheDetective.Core` — the indexer, the graph, the key normaliser, the rules — is handed a Roslyn
`Solution` and never learns where it came from. Resolving `.sln`, `.slnx` or a project path into one
belongs to `CacheDetective.Cli`, alongside the MCP tools and the JSON.

The reason is the shape of the test suite. Everything worth testing here is semantic-model work:
does this call site fold to `product:{id}`, does this `SaveChanges` reach `dbo.Discounts`, does this
`Remove` cover that template. All of it runs on sources compiled in memory, with metadata references
borrowed from the test project's own package references — so a fixture is a handful of `.cs` files
under `tests/fixtures`, an `AdhocWorkspace`, and a few milliseconds. The alternative, real `.csproj`
fixtures loaded through `MSBuildWorkspace`, means a NuGet restore inside every test run: minutes
instead of milliseconds, network in the inner loop, and a red test that most often means "the feed
was unreachable" rather than "the rule broke".

What that leaves untested is the MSBuild adapter itself, and it is covered deliberately rather than
forgotten: one smoke test loads a real solution from this repository end to end and asserts that
indexing completes and that every `WorkspaceFailed` diagnostic is reported rather than swallowed.
The adapter has almost no logic, so one honest test of it is worth more than a fixture suite that
pretends to exercise it.

Rejected: two fixture mechanisms in one run — the in-memory suite plus a handful of real projects —
which buys confidence the smoke test already buys, at the price of two ways to write a fixture and a
standing invitation to write the slow kind. Passing paths into the core and hiding the workspace
behind an interface — the interface would exist only for the tests, and the thing it hides is a type
Roslyn already made substitutable.
