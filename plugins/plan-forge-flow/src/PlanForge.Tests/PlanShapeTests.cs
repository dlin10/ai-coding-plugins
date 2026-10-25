using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

public sealed class PlanShapeTests : IDisposable
{
    private const string Draft = "# P\n## Requirements\nR1 R2 R1\n## Approach\n" +
                                 "1. A AlphaTests\n2. B BetaMatrixTests\n## Gates\nDone\n";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Write_and_patch_report_exact_shape_in_results_and_flow_log()
    {
        const string runId = "20260101-000000-abcdef";
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5));

        var writtenJson = await ForgeTools.WritePlan(SessionRoots.None, _workspace, runId, Draft, CancellationToken.None);
        var written = JsonSerializer.Deserialize(writtenJson, ForgeToolJson.Default.PlanWriteResult)!;
        Assert.Equal(new PlanShape(92, 2, 37, 21, 25, 49, 1.96, 2, 2, 1, false), written.Shape);
        Assert.Contains("## Plan written", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.Contains("- Requirements: 25; Approach: 49; ratio: 1.96", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);

        var task = written.Outline!.Targets.Single(target => target.Label == "2. B BetaMatrixTests");
        var edit = new PlanEdit("replace", task.Id, task.Hash, "2. C GammaMatrixTests\n");
        var patchedJson = await ForgeTools.PatchPlan(SessionRoots.None, _workspace, runId,
                                                    written.Outline.PlanHash, [edit], CancellationToken.None);
        var patched = JsonSerializer.Deserialize(patchedJson, ForgeToolJson.Default.PlanPatchResult)!;
        Assert.Equal(new PlanShape(93, 2, 38, 22, 25, 50, 2.00, 2, 2, 1, false), patched.Shape);
        var flow = File.ReadAllText(run.FlowLogPath);
        Assert.Contains("## Plan patched", flow, StringComparison.Ordinal);
        Assert.Contains("- Requirements: 25; Approach: 50; ratio: 2.00", flow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_split_threshold_uses_approach_characters()
    {
        const string prefix = "## Approach\n1. A\n";
        var small = prefix + new string('x', 22_000 - prefix.Length - 1);
        var large = small + "x";

        Assert.False(PlanShape.Of(small, PlanOutline.Of(small)).SplitSuggested);
        Assert.True(PlanShape.Of(large, PlanOutline.Of(large)).SplitSuggested);
    }

    [Fact]
    public void An_empty_level_two_heading_ends_the_approach_as_the_build_parses_it()
    {
        const string approach = "## Approach\n1. A\n";
        var plan = approach + "##\n" + new string('x', 23_000);

        var shape = PlanShape.Of(plan, PlanOutline.Of(plan));

        Assert.Equal(approach.Length, shape.ApproachCharacters);
        Assert.Equal(1, shape.TaskCount);
        Assert.False(shape.SplitSuggested);
    }
}
