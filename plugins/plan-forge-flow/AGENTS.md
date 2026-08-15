# Plan Forge Flow release assets

Any change to C# source under `src/` requires rebuilding the complete release asset set before handoff. From this plugin directory, run:

```powershell
.\build\package.ps1 -InstallBinaries
```

Release 0.7.x temporarily supports only Windows x64. The command must refresh the single self-contained `bin/win-x64/planforge.exe` and the single versioned `artifacts/plan-forge-flow-<version>-win-x64.zip`. Verify the published binary with `run doctor` after the rebuild; no other RID binary or archive may be produced.
