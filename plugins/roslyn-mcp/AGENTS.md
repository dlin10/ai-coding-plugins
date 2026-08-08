# Roslyn MCP project rules

## Publish extension changes

After any change under `extension/`, rebuild and publish the bundled VSIX before completing the task:

1. Run `msbuild extension/src/RoslynMcpExtension.slnx /p:Configuration=Release /t:Rebuild /restore` from this directory.
2. Replace `assets/RoslynMcpExtension.vsix` with `extension/src/RoslynMcpExtension/bin/Release/net48/RoslynMcpExtension.vsix`.
3. Verify the bundled VSIX manifest version matches `source.extension.vsixmanifest`, `RoslynMcpPackage.cs`, `Program.cs`, and the bundled-version statement in `README.md`.
