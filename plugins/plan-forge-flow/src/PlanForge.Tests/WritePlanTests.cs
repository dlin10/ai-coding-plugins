using System.Text.Json;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The tool that exists so the plan link does not wait for the critique: it writes `PLAN.md` and
/// answers with it, and it is the only worker-adjacent tool that starts no worker at all.
/// </summary>
public sealed class WritePlanTests : IDisposable
{
    private const string Draft =
        """
        # Plan: nightly export

        ## Approach

        1. Snapshot the orders table.
        """;

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The plan and its shape are visible as soon as the write completes, before a critic runs.
    /// </summary>
    [Fact]
    public async Task The_written_plan_comes_back_with_its_shape_in_the_flow_log()
    {
        var run = NewRun();

        var json = await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, Draft,
                                              CancellationToken.None);
        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanWriteResult)!;

        Assert.Equal(run.RunId, result.RunId);
        Assert.Equal(PlanOutline.Of(Draft).PlanHash, result.Outline!.PlanHash);
        Assert.Equal(JsonSerializer.Serialize(PlanOutline.Of(Draft), ForgeToolJson.Default.PlanOutline),
                     JsonSerializer.Serialize(result.Outline, ForgeToolJson.Default.PlanOutline));
        Assert.Equal(run.PlanPath, result.Documents.Plan!.Path);
        Assert.Contains("watch it change", result.Documents.Plan.Next, StringComparison.Ordinal);
        Assert.Equal(run.FlowLogPath, result.Documents.FlowLog!.Path);
        Assert.Equal(Draft.Length, result.Shape.Characters);
        Assert.Contains("## Plan written", File.ReadAllText(run.FlowLogPath), StringComparison.Ordinal);
        Assert.Equal(Draft, run.ReadPlan());
    }

    [Fact]
    public async Task A_blank_draft_is_refused_and_writes_nothing()
    {
        var run = NewRun();

        var rejection = await Assert.ThrowsAsync<ArgumentRejectedException>(
            () => ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, "  ", CancellationToken.None));

        Assert.Contains("planDraft", rejection.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(run.PlanPath));
    }

    [Fact]
    public async Task A_failed_flow_entry_reports_that_the_plan_was_written()
    {
        var run = NewRun();
        using var lockedFlow = new FileStream(run.FlowLogPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<IOException>(
            () => ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, Draft, CancellationToken.None));

        Assert.Equal(Draft, run.ReadPlan());
        Assert.Contains(PlanOutline.Hash(Draft), error.Message, StringComparison.Ordinal);
        Assert.Contains("read forge.plan.outline", error.Message, StringComparison.Ordinal);
    }

    private RunDirectory NewRun()
    {
        const string runId = "20260101-000000-abcdef";
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now,
            ReviewRounds: 0, ReviewRoundCap: 5));
        return run;
    }
}
