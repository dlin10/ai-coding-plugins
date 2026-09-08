# Roslyn MCP project rules

## Publish extension changes

After any change under `extension/`, rebuild and publish the bundled VSIX before completing the task:

1. Run `msbuild extension/src/RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore` from this directory.
2. Replace `assets/RoslynMcpExtension.vsix` with `extension/src/RoslynMcpExtension/bin/Release/net48/RoslynMcpExtension.vsix`.
3. Run `npm run validate:plugins` from the repository root. It checks the extension version at every site that records it, and that the three host manifest versions agree with each other and with the `README.md` title. When a bump adds a new site, register it in `scripts/validate-plugins.mjs` rather than listing it here.
