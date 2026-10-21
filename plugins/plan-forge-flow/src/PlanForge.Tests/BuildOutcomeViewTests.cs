using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class BuildOutcomeViewTests
{
    private static readonly GateRun Passed = new("passed", "Gate", "./build/check.ps1 -Task 3", 0, "tests pass", 42.7, null);

    /// <summary>
    /// A clean pass is settled by its outcomes and counters; the builder's account, evidence and
    /// files and the gate's command and output are in the Flow log, and nothing acts on them.
    /// </summary>
    [Fact]
    public void A_clean_pass_goes_out_as_its_verdicts_and_counters()
    {
        var view = BuildOutcomeView.Of(new BuildOutcome(Clean(), 3, 10));

        Assert.Equal("""{"result":{"status":"done","verification":{"outcome":"passed"},"gate":{"outcome":"passed","label":"Gate","exitCode":0,"seconds":42.7,"detail":null}},"tasksCompleted":3,"taskCount":10}""",
                     view.ToJsonString());
    }

    /// <summary>
    /// Everything short of a clean pass is a moment the builder's words are read: the build
    /// reference says to read the verification of a pass the builder could not check before
    /// narrating it, and a failed, unrun or blocked task is decided from them.
    /// </summary>
    [Theory]
    [InlineData("done", "unavailable", "passed")]
    [InlineData("gate_failed", "passed", "failed")]
    [InlineData("done", "unavailable", "not_executable")]
    [InlineData("blocked", "failed", "not_run")]
    public void Any_other_result_goes_out_whole(string status, string verification, string gate)
    {
        var result = Clean() with
        {
            Status = status,
            Verification = new Verification(verification, "ran the targeted tests"),
            Gate = Passed with { Outcome = gate }
        };

        var view = BuildOutcomeView.Of(new BuildOutcome(result, 3, 10))["result"]!;

        Assert.Equal("Задача 3 готова", view["summary"]!.GetValue<string>());
        Assert.Equal("ran the targeted tests", view["verification"]!["evidence"]!.GetValue<string>());
        Assert.Equal("src/A.cs", view["filesChanged"]![0]!.GetValue<string>());
        Assert.Equal("tests pass", view["gate"]!["output"]!.GetValue<string>());
        Assert.Equal("./build/check.ps1 -Task 3", view["gate"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void A_build_with_every_task_done_has_no_result_to_trim()
    {
        Assert.Equal("""{"result":null,"tasksCompleted":10,"taskCount":10}""",
                     BuildOutcomeView.Of(new BuildOutcome(null, 10, 10)).ToJsonString());
    }

    private static BuildResult Clean() =>
        new("done", ["src/A.cs"], new Verification("passed", "ran the targeted tests"), "Задача 3 готова", Passed);
}
