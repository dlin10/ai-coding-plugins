# AI Coding Plugins

A monorepo of cross-host developer plugins for Codex, Claude Code, and Cursor. Each plugin lives in
`plugins/<name>` and ships three host manifests — `.claude-plugin/plugin.json`,
`.codex-plugin/plugin.json`, `.cursor-plugin/plugin.json` — that the root catalogs
(`.claude-plugin/marketplace.json`, `.cursor-plugin/`, `.agents/plugins/`) point at. Some plugins are
skills only; others carry a .NET solution and publish a binary.

Most plugins keep their own `AGENTS.md` next to the code. That file is the authority for anything
about that plugin — its solution, its test filters, its packaging script, its design constraints.
Read it before touching the plugin; what follows here is only what holds across all of them.

## Commands

```bash
npm run validate:plugins
```

Checks every plugin's manifests, skill frontmatter, cross-host catalog agreement, and version
consistency. CI runs it on any push or pull request touching `plugins/**`, `scripts/**`,
`package.json`, or the root catalogs, so a manifest edit in one plugin can turn the whole repository
red. Run it locally before opening a pull request rather than discovering the failure in CI.

The .NET plugins each have their own solution, and tests are run from the plugin directory:

```bash
dotnet test src/CacheDetective.slnx      # plugins/cache-detective
dotnet test src/FindFiles.slnx           # plugins/find-files
dotnet test src/PlanForgeFlow.sln        # plugins/plan-forge-flow
```

Filters, gate scripts, and packaging differ per plugin — see that plugin's `AGENTS.md`.

## Long-running commands

Never pipe a long-running build, test, or orchestration command through `grep`, `head`, or
`Select-String`, and never leave one attached to stdout. Redirect it to a log file and read the tail:

```bash
dotnet test src/PlanForgeFlow.sln > test.log 2>&1; tail -n 50 test.log
```

```powershell
dotnet test src/PlanForgeFlow.sln *> test.log; Get-Content test.log -Tail 50
```

Two distinct failures make this a rule rather than a preference. A verbose suite attached to stdout
can exceed the harness output cap and kill the whole turn, losing the run and everything else in
flight. And a pipe to a filter can deadlock: the producer blocks writing to a full pipe while the
consumer waits for input that never comes, so the command hangs with no output and no error.

For the same reason, `dotnet restore` run as a redirected child process can stall until the idle
timeout, because MSBuild node reuse holds the pipes open after the work is done. When a script
restores into a log file, pass `--disable-build-servers -nodeReuse:false`.

Log files belong outside the repository or under an ignored path — `.forge/`, `scratch/`, `*.tmp`
and `*.scratch` are already ignored at the root.

## Releasing

Releasing a plugin is bumping its version, and the version lives in more than one place. All of
these must move in the same commit:

- `.claude-plugin/plugin.json`, `.codex-plugin/plugin.json`, `.cursor-plugin/plugin.json` — the
  validator fails when the three disagree.
- `README.md`, when its title carries a version (`# Plan Forge Flow 0.32.0`). The validator compares
  the first version in the heading against the manifests; a title left behind fails CI after the
  merge, not before it.
- `CHANGELOG.md`, where the plugin has one, with an entry for the new version.

Then run `npm run validate:plugins` locally and fix anything it reports **before** committing.

For the plugins with a release workflow, the bump is what cuts the release: a merge to `main` that
carries a version no tag knows yet builds, packages, and creates the tag and GitHub release
together. A merge that does not bump releases nothing. Do not push a release tag by hand as part of
ordinary work — the tag path exists only to re-cut a release whose upload failed, and it refuses a
tag that disagrees with the manifest. Where a launcher downloads the binary from the release
matching the manifest version, a manifest naming a version with no release behind it breaks every
fresh install.

## Documentation

Documentation describes the **current** state of the product, not how it got there. Do not write
changelog-style narrative — "before 0.31.0 there was no channel…", "this used to be handled by…" —
in a README, a reference page, or the generated HTML guides. A reader wants to know what the thing
does today; the history is noise they have to subtract.

Version history goes in `CHANGELOG.md` and nowhere else.

This applies to updating docs across a release too: rewrite the affected passage to describe the new
behaviour, rather than appending a paragraph about what changed.
