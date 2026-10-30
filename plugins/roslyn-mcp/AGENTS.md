# Roslyn MCP project rules

## Publish extension changes

After any change under `extension/`, rebuild and publish the bundled VSIX before completing the task:

1. Run `msbuild extension/src/RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore` from this directory.
2. Replace `assets/RoslynMcpExtension.vsix` with `extension/src/RoslynMcpExtension/bin/Release/net48/RoslynMcpExtension.vsix`.
3. Run `npm run validate:plugins` from the repository root. It checks that the three host manifest versions agree with each other, with the `README.md` title and with `<Version>` in `extension/src/Directory.Build.props`, the one place the extension's version is written. Anything new that needs the version takes it from there; if it cannot, register it in `scripts/validate-plugins.mjs` rather than listing it here.

Work toward a version whose `CHANGELOG.md` entry is still marked `unreleased` skips steps 1 and 2 and leaves `assets/RoslynMcpExtension.vsix` alone. The plugin installs the VSIX from there, so replacing it would ship the change under the version already released. The change that releases that version rebuilds the VSIX.

Such work may still cut a preview: `X.Y.Z-preview.N`, written everywhere step 3 checks. Merging a version that has no tag yet to main is a release, so it needs the user's OK. `.github/workflows/release-roslyn-mcp.yml` publishes `roslyn-mcp.exe` for it, as a GitHub pre-release when the version is a preview, and the VSIX stays the last release's. The hosts keep one copy of the plugin per version, so a change to `bin/` or to the manifests reaches an installed plugin only with a new version (`docs/adr/0008`).
