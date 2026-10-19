# Roslyn MCP project rules

## Publish extension changes

After any change under `extension/`, rebuild and publish the bundled VSIX before completing the task:

1. Run `msbuild extension/src/RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore` from this directory.
2. Replace `assets/RoslynMcpExtension.vsix` with `extension/src/RoslynMcpExtension/bin/Release/net48/RoslynMcpExtension.vsix`.
3. Run `npm run validate:plugins` from the repository root. It checks that the three host manifest versions agree with each other, with the `README.md` title and with `<Version>` in `extension/src/Directory.Build.props`, the one place the extension's version is written. Anything new that needs the version takes it from there; if it cannot, register it in `scripts/validate-plugins.mjs` rather than listing it here.
