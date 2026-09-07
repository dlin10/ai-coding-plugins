# Issue 70 experiment — not reproduced

Three A-to-B trials completed on 2026-09-06 in a live Visual Studio 18 Community workspace, root suffix `Issue70Exp`, with effective source-generator execution `Balanced` read from the workspace service. Loaded Roslyn: 5.9.0. Fixture generator reference: Roslyn 4.14.0. The original MCP schema baseline was captured from server 1.8.0 on port 5052; the experiment used its own server on 5062.

| Trial | Baseline compilation (ms) | Additional candidate phase (ms) | Stale B error |
|---|---:|---:|---|
| 1 | 67.7798 | 2.3294 | Absent |
| 2 | 0.0096 | 0.0414 | Absent |
| 3 | 6.1633 | 0.0287 | Absent |

Each trial established a generated `MemberA`, changed marker and consumer to B, checked workspace/disk text equality, successfully built B with the CLI, then requested a baseline compilation. The candidate requested generated documents followed by a compilation from that same captured Project. An IDE-build positive control followed. Finally, an intentional `UnknownIssue70Name` compiler error was present before and after the candidate. `ControlApi.Stable` was unchanged and depended on generated output.

The baseline already contained `MemberB` in all scored trials. The experiment therefore **does not demonstrate a refresh fix**. The `not-reproduced` token means non-reproduction in the scored A-to-B direction only. Stale `MemberA` diagnostics were observed during B-to-A preparation after successful IDE builds, with workspace texts matching A. Those preparations were deliberately excluded because the protocol required a clean A starting point; the candidate was not measured against their stale cache. Therefore the run does not establish whether this candidate can repair that observed stale condition. The trigger changes an AdditionalFile as well as consumer source, so it may behave differently from a branch switch or a source-only change. It tests the captured-Project sequence only, not switching to a newly published CurrentSolution snapshot.

The selected production branch keeps baseline diagnostics and exposes a nullable observed generated-document count. The observation has a 10-second budget; faults/timeouts leave the count unknown. A count request may run generators but is not evidence of freshness and does not replace this response's diagnostics. An IDE build may help but does not guarantee immediate freshness: the archived preparations remained stale after successful IDE builds. A CLI build validates disk code without refreshing that cache. Investigation of a candidate against the observed B-to-A stale state remains follow-up work.

## Evidence and verification

Run `pwsh -File plugins/roslyn-mcp/extension/experiments/issue-70/verify-evidence.ps1` from the repository root. This is offline: it validates immutable trial artifacts, mode, source hashes, stage order, expected generated members, CLI outcomes, IDE-build events, and the real-error control; then derives `validated-outcome.json`. `evidence.json` is a derived summary. `protocol-manifest.json` pins the measured fixture and archived probe source.

The `trials/trial-N/checkpoint.json` files hash each successful trial's raw SSE and CLI logs. Incomplete clean-A preparations are retained separately. One preparation observed the old generated member immediately after an IDE build; later preparations wait up to 30 seconds for clean A before measuring B. No completed trial was classified from an unavailable probe or an empty generated baseline.

Initial probe setup incorrectly attempted a settings change even though the workspace was already Balanced. The final measured probe only reads the setting and requires Balanced; no setting was changed and none needs restoration. The owned experimental VS was stopped after the trials. The fixture was restored to its versioned A input. The production VSIX contains no probe hook.

## Repeating the live experiment

The live preparation gate is retired in the shipping tree. To repeat intentionally, use baseline revision `c8d10b09a91afd1b884c8de20e27c90b731dfd52` with these fixture/scripts and restore the archived probe sources:

- `probe/Issue70ProbeService.cs` to `extension/src/RoslynMcpExtension/Services/`.
- `probe/Issue70ProbeModels.cs` to `extension/src/RoslynMcpExtension.Shared/`.
- `probe/Issue70ProbeTool.cs` to `extension/src/RoslynMcpExtension.Server/Tools/`.
- Add `Task<Issue70ProbeResult> Issue70ProbeAsync(string operation, string? expectedMemberFullName)` to `IRoslynAnalysisRpc`; proxy it in `RpcClient` and delegate to `new Issue70ProbeService(workspace).RunAsync(operation, expectedMemberFullName)` through `InvokeAsync` in `RoslynAnalysisService`.
- Register `.WithTools<Issue70ProbeTool>()` beside the nine ordinary server tools. This hook is experimental only.

Use a fresh, owned `Issue70Exp` deployment and fresh checkpoint/trial directories; preserve existing evidence. Confirm port 5062 is free and the ordinary baseline server's solution/version matches before capture. Run `verify-environment.ps1`, then `run-trials.ps1`. The scripts never change normal VS settings. If the experimental workspace is not Balanced, the environment gate stops with the precise manual setting required. Do not silently recapture or overwrite the published evidence.
