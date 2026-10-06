using PlanForge.Acts;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The gatekeeper runs a gate under the bound its caller names: a task's gate and a review fix's run-wide gates have
/// different bounds, so the bound has to travel with the call rather than live in the runner.
/// </summary>
public sealed class GatekeeperTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public GatekeeperTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task A_gate_runs_under_the_timeout_its_caller_gives_and_is_reported_as_a_timeout_past_it()
    {
        var reported = new BuildResult("done", ["a.cs"], new Verification("passed", "the checks ran"), "done");
        var state = new RunState("run", _workspace, "Text", DateTimeOffset.Now, 0, 5);

        var result = await Gatekeeper.CheckAsync(reported, [new GateCommand("G1", "Write-Output 'started'; Start-Sleep -Seconds 60")],
                                                 stated: true, killed: [], state, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal("gate_failed", result.Status);
        Assert.Equal("timeout", result.Gate?.Outcome);
        Assert.Contains("within 3 s", result.Gate?.Detail, StringComparison.Ordinal);
    }
}
