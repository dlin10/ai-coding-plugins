using System.Text.Json;
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
    /// The two halves of the answer the issue asked for: the plan the moment it is on disk, and no
    /// flow log — that file is first written by the first critique, and a path to a file that does
    /// not exist is a dead link on the host this was written for.
    /// </summary>
    [Fact]
    public async Task The_written_plan_comes_back_as_a_document_with_no_flow_log_beside_it()
    {
        var run = NewRun();

        var json = await ForgeTools.WritePlan(SessionRoots.None, _workspace, run.RunId, Draft,
                                              CancellationToken.None);
        var result = JsonSerializer.Deserialize(json, ForgeToolJson.Default.PlanWriteResult)!;

        Assert.Equal(run.RunId, result.RunId);
        Assert.Equal(run.PlanPath, result.Documents.Plan!.Path);
        Assert.Contains("watch it change", result.Documents.Plan.Next, StringComparison.Ordinal);
        Assert.Null(result.Documents.FlowLog);
        Assert.Equal(Draft, run.ReadPlan());
        Assert.False(File.Exists(run.FlowLogPath));
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

    private RunDirectory NewRun()
    {
        const string runId = "20260101-000000-abcdef";
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now,
            ReviewRounds: 0, ReviewRoundCap: 5));
        return run;
    }
}
