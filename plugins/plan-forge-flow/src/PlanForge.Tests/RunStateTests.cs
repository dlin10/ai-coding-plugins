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
    }
}
