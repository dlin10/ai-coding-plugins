---
name: roslyn-install-vsix
description: Install or upgrade the bundled Roslyn MCP Visual Studio extension on native Windows, the optional accelerator that lets the plugin's roslyn-mcp server answer from a solution Visual Studio has open. Use when the user wants to install, update, or reinstall the VSIX for Codex, Claude Code, or Cursor.
---

# Install the Roslyn MCP Visual Studio extension

The plugin's `roslyn-mcp` server needs no Visual Studio. The extension is an optional accelerator: once Visual Studio has loaded a solution, the extension serves it on a named pipe to the current Windows user, and the daemon forwards the calls for exactly that solution file to Visual Studio, which answers from its own workspace, unsaved editor buffers included (`docs/adr/0007`). It reads no port and needs no settings or repository setup. The extension must be the plugin's version; a daemon and an extension of different versions never meet, and the daemon then loads the solution itself.

The extension serves no port, so a per-solution server that a 2.x setup configured stops answering once it is installed. Install it after the repositories whose solutions Visual Studio opens are migrated with `roslyn-setup-repo`.

Resolve the plugin root in this order:

1. Codex: `$env:PLUGIN_ROOT`.
2. Cursor: `$env:CURSOR_PLUGIN_ROOT`.
3. Claude Code: `$env:CLAUDE_PLUGIN_ROOT`.
4. Otherwise, search the installed plugin caches under `%USERPROFILE%\.codex\plugins`, `%USERPROFILE%\.cursor\plugins`, and `%USERPROFILE%\.claude\plugins`, and use the newest `RoslynMcpExtension.vsix` match.

Then perform these steps and report each result:

1. Verify the OS is native Windows. WSL is unsupported.
2. Check for `devenv` processes. If any Visual Studio instance is running, stop and ask the user to close every Visual Studio window themselves; Visual Studio is theirs to close.
3. Locate `VSIXInstaller.exe` with `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe -latest -property installationPath`. If `vswhere` is absent, use the newest `C:\Program Files\Microsoft Visual Studio\*\*\Common7\IDE\VSIXInstaller.exe`.
4. Run `VSIXInstaller.exe` with the resolved `assets\RoslynMcpExtension.vsix` and let its UI complete.
5. Ask the user to reopen Visual Studio and load a solution. **View ▸ Output ▸ Roslyn MCP Extension** then shows `Serving <solution path> to the Roslyn MCP daemon`, and `roslyn_load` with that solution file's path answers `servedBy: visualStudio`. The same solution in a linked Git worktree has another path, and the daemon loads it itself.
