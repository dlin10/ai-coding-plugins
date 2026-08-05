# Plan Forge Flow release assets

Any change to C# source under `src/` requires rebuilding the complete release asset set before handoff. From this plugin directory, run:

```powershell
.\build\package.ps1 -InstallBinaries
```

Do not publish only a selected RID for a C# change. The command must refresh all six self-contained binaries in `bin/<rid>` and all six versioned archives in `artifacts/` (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`). Verify the current-platform published binary with `run doctor` after the rebuild.
