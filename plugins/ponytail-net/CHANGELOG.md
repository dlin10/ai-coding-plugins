# Changelog

## 0.1.0

Initial release.

- Skill `ponytail-net`: the simplest implementation that delivers the whole requested C#/.NET contract, with `lite`, `full` (default), `ultra`, and `off` levels.
- A reuse ladder — existing code, then BCL/framework/database/adopted library, then a small direct implementation, then a new dependency or abstraction — applied only after the change boundary is established, with Roslyn MCP for symbol evidence when the `roslyn-mcp` plugin is present.
- C#-specific decision rules for interfaces added for symmetry, async collapse, LINQ and EF Core rewrites, `IMemoryCache` substitution, serializer or entity exposure, and `Span<T>`/pooling/`ValueTask`.
- An explicit never-simplify-away list, a verification procedure that distinguishes VSTest from Microsoft.Testing.Platform and stops once the required checks pass, and a four-line report shape that restates the active level.
- One skill file for every host with no host-specific section: invocation syntax lives in the README, and Roslyn MCP access per host is delegated to the `roslyn-first` skill.
- A member reached by name (a string in a command map or configuration, reflection, a serializer attribute) counts as referenced, and `ultra` leaves public members and signatures alone when a consumer outside the repository is implied; both rules come from the eval runs on Sonnet 5, gpt-5.6-terra, and Fable 5.1 that removed a reflection-invoked member or a public constructor.
- A request to simplify, `ultra` included, is not permission to drop existing behavior: an assumed intent is not evidence that a member is unneeded, and an existing test goes only when its requirement changed independently of the change, since rewriting the implementation does not change the contract. Both rules answer the gpt-5.6-terra eval run that retired `ExportLegacy`, the dispatcher, and their test on the assumption that the request allowed it.
- The out-of-repository consumer that blocks a public removal at `ultra` must be described by the code or its comments; the `public` modifier alone describes no consumer, so unreferenced public scaffolding is still removed. Added after one gpt-5.6-terra run kept a dead public options class and factory because "a package user" might exist; with the reworded row the same model removed the scaffolding and kept the by-name consumer in 3 of 3 runs.
- Manifests for Claude Code, Codex, and Cursor; upstream Ponytail attribution in `THIRD-PARTY-NOTICES.md`.
