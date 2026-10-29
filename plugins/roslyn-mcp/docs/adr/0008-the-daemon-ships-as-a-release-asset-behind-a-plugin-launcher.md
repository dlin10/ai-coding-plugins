# The daemon ships as a release asset behind a plugin launcher

`roslyn-mcp.exe` (ADR 0004) ran only from build output. The plugin has to start it in Claude Code and in Codex
without the user building anything, the way cache-detective ships `cachedet.exe` (its ADR 0001).

So the release carries one asset, `roslyn-mcp.exe`, and the plugin starts it through a launcher that downloads the
asset of the plugin's own version on first start.

- **One self-extracting file.** `dotnet publish -r win-x64` makes a framework-dependent single file with
  `IncludeAllContentForSelfExtract`. MSBuildWorkspace runs its build hosts as separate processes from
  `AppContext.BaseDirectory`, so they travel inside the file and are unpacked beside the rest. The shim starts the
  daemon from `Environment.ProcessPath`, the file itself, so one asset serves both modes and a daemon is always of
  its shim's version. The project sets no runtime identifier, so a build keeps the plain output that the tests start.
- **Declared as `roslyn-mcp`.** `.mcp.json` declares `cmd.exe /d /c .\bin\roslyn-mcp-launcher.cmd` for Codex, which
  runs a plugin server in the versioned plugin folder and lists it as `roslyn-mcp`. Claude's manifest repeats the
  entry with `${CLAUDE_PLUGIN_ROOT}`. Claude loads both and lets the manifest's entry replace the same-named one, and
  lists it as `plugin:roslyn-mcp:roslyn-mcp`. forge's worker pattern `roslyn-*` and its plugin-scoped grant match
  that key.
- **Timeouts for a first start.** Codex waits 10 s for a server to start and 60 s for a tool by default. The first
  start downloads about 35 MB inside the start, hence `startup_timeout_sec` 60. On a cold machine, the first
  `roslyn_load` waits up to 30 s for the new daemon to answer and up to 50 s more for the load before it answers
  `loading`, hence `tool_timeout_sec` 120. Claude's defaults (30 s to start, no practical limit on a tool) suffice.
- **The launcher** is `cmd`, because it stays the shim's parent for the whole session, and cmd's working set is under a
  fifth of PowerShell's. A short-lived inbox PowerShell helper, `roslyn-mcp-cache.ps1`, prepares
  `%LOCALAPPDATA%\roslyn-mcp\bin\<version>` and exits before the shim starts. Its file handling is plan-forge-flow's
  after #203:
  - the download goes to a file with a GUID name, held open while curl writes it;
  - the launcher never overwrites an executable another launcher promoted first, and removes only its own download;
  - a launch records its use in a lease file, under a lock that a cleaner takes too.

  With no arguments the launcher runs `mcp`, because a launcher that forwarded nothing once left cache-detective's
  server unable to start in any host.
- **Unpacked beside the executable.** The launcher points `DOTNET_BUNDLE_EXTRACT_BASE_DIR` at the version folder, and
  the daemon inherits it. The default, `%TEMP%\.net`, fails with "Failed to open file" when `TEMP` is deep: the longest
  unpacked file adds 113 characters to the base, and the host writes with MAX_PATH-limited calls. .NET never removes
  what it unpacked, so keeping it in the version folder lets it go when the version does.
- **Bounded retention.** On every launch the helper retires each version nobody has started for seven days. It keeps
  the two most recently used of them for a rollback, and any version whose executable is running, because Windows
  refuses to delete a running image. Retiring deletes the executable, then the unpacked copy. Downloads of ours older
  than a week go too.
- **Release on merge.** `release-roslyn-mcp.yml` cuts `roslyn-mcp-v<version>` when a push to main carries a version
  that has no tag. Before attaching the asset it publishes the file, tests the launcher and loads a solution through
  the launcher. The build workflow runs the same publish and load on every pull request, so a broken asset fails
  there before a merge releases it.
- **Previews.** While the CHANGELOG entry says `unreleased`, the plugin may carry `X.Y.Z-preview.N`. That releases
  `roslyn-mcp.exe` as a GitHub pre-release and leaves the VSIX at the last release, whose manifest version takes
  numbers only. Both hosts key the installed plugin by its version, so a new version is the only way a changed
  `.mcp.json` or launcher reaches an installed plugin before a release.

## Considered Options

- **Keeping 2.0.0, with a launcher override for a local build,** releases nothing, but the hosts' 2.0.0 folders never
  receive the new `.mcp.json`. Day-to-day use would need a plugin folder loaded by hand in each host.
- **Releasing on a tag push only** leaves a window after the merge in which Claude, which updates the plugin
  automatically, starts a launcher with nothing to fetch.
- **A zip of the publish folder** starts slightly faster, but makes the launcher an archive extractor and gives two
  versions of the file layout to keep in step.
- **Protecting every version a host has installed,** as plan-forge-flow does, leaves the bound to the hosts. On the
  development machine Claude kept 23 plan-forge-flow version folders from the past fortnight. An installed but
  idle version pays only a download when it is next started.

## Consequences

- The first start of each version downloads about 35 MB and unpacks 197 files, which took under a second on the
  development machine.
- A packaged host, such as the Claude desktop app, has its writes to `%LOCALAPPDATA%` redirected into the package's
  own folder, which adds 47 characters there. The longest unpacked path then comes to 219 characters, still inside
  MAX_PATH.
- Each preview has its own pipe and daemon. A shim of the next preview starts a new daemon while the old one idles
  out (ADR 0004).
- Merging a version bump to main is a release. A change to anything the hosts copy, such as the launcher or the
  manifests, reaches installed plugins only with a version bump.
- Windows x64 and an installed .NET 10 are required, as MSBuildWorkspace already needs the SDK.
- Until the 3.0.0 cutover, the plugin server and the 2.x per-solution servers both reach an agent.
