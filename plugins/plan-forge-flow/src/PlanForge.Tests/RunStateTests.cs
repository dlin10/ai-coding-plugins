using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

public sealed class RunStateTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A <c>state.json</c> written before the granted-round counters existed has neither key, and
    /// must still deserialize with both counters defaulting to zero.
    /// </summary>
    [Fact]
    public void A_state_file_without_the_granted_round_counters_still_deserializes()
    {
        const string runId = "test-run";
        var run = RunDirectory.Create(_workspace, runId);
        File.WriteAllText(Path.Combine(run.Path, "state.json"),
            """
            {
              "runId": "test-run",
              "workspaceRoot": "C:\\workspace",
              "profile": "Text",
              "startedAt": "2026-09-03T00:00:00+00:00",
              "reviewRounds": 1,
              "reviewRoundCap": 5,
              "baselineHead": "",
              "approved": false,
              "tasksCompleted": 0,
              "builderSessionId": "",
              "builderVendor": "",
              "codeReviewRounds": 2,
              "codeReviewRoundCap": 3
            }
            """);

        var state = RunDirectory.Open(_workspace, runId).ReadState();

        Assert.Equal(0, state.GrantedReviewRounds);
        Assert.Equal(0, state.GrantedCodeReviewRounds);
        Assert.Equal(5, state.ReviewRoundCap);
        Assert.Equal(3, state.CodeReviewRoundCap);

        // Written before the server ran gates: nothing owed, nothing configured.
        Assert.Null(state.GateEnvironment);
        Assert.Null(state.BuilderRoots);
        Assert.Null(state.PendingGateFailure);
    }

    [Fact]
    public void The_gate_settings_round_trip_through_the_state_file()
    {
        var run = RunDirectory.Create(_workspace, "gated");
        run.WriteState(new RunState("gated", @"C:\workspace", "Text", DateTimeOffset.Now, 0, 5,
                                    GateEnvironment: new Dictionary<string, string> { ["CD_TEST_SQL_CONN"] = "Server=." },
                                    BuilderRoots: [@"C:\Dev\eShopOnContainers"],
                                    PendingGateFailure: "exited 3"));

        var state = run.ReadState();

        Assert.Equal("Server=.", state.GateEnvironment!["CD_TEST_SQL_CONN"]);
        Assert.Equal([@"C:\Dev\eShopOnContainers"], state.BuilderRoots);
        Assert.Equal("exited 3", state.PendingGateFailure);
    }
}
