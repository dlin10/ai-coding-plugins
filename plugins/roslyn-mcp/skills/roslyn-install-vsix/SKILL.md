---
name: roslyn-install-vsix
description: Install or upgrade the bundled Roslyn MCP Visual Studio extension on native Windows. Use when the user wants to install, update, or reinstall the VSIX for Codex, Claude Code, or Cursor.
---

# Install the Roslyn MCP Visual Studio extension

This workflow supports native Windows with Visual Studio. The extension VSIX is bundled with the plugin.

Resolve the plugin root in this order:

1. Codex: `$env:PLUGIN_ROOT`.
2. Cursor: `$env:CURSOR_PLUGIN_ROOT`.
3. Claude Code: `$env:CLAUDE_PLUGIN_ROOT`.
4. Otherwise, search the installed plugin caches under `%USERPROFILE%\.codex\plugins`, `%USERPROFILE%\.cursor\plugins`, and `%USERPROFILE%\.claude\plugins`, and use the newest `RoslynMcpExtension.vsix` match.

Then perform these steps and report each result:

1. Verify the OS is native Windows. WSL is unsupported.
2. Check for `devenv` processes. If any Visual Studio instance is running, stop and ask the user to close every Visual Studio window.
3. Locate `VSIXInstaller.exe` with `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe -latest -property installationPath`. If `vswhere` is absent, use the newest `C:\Program Files\Microsoft Visual Studio\*\*\Common7\IDE\VSIXInstaller.exe`.
4. Run `VSIXInstaller.exe` with the resolved `assets\RoslynMcpExtension.vsix` and let its UI complete.
5. Ask the user to reopen Visual Studio, load a solution, and inspect **View ▸ Output ▸ Roslyn MCP Extension** for `MCP Server started on http://localhost:<port>/mcp`.

Installing the VSIX does not configure a repository's MCP clients. Run `$roslyn-setup-repo` in Codex, `/roslyn-mcp:setup-repo` in Claude Code, or the `roslyn-setup-repo` skill in Cursor afterward.
