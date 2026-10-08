using ModelContextProtocol.Protocol;
using PlanForge.Acts;
using PlanForge.Mcp;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class FixGatePolicyMatrixTests
{
    private static readonly string?[] Modes = [null, "full", "targeted", "Targeted", "", "   ", "unknown"];
    private static readonly string?[] Timings = [null, "beforeNextRound", "final", "BeforeNextRound", "", "   ", "unknown"];
    private static readonly string?[] HostOutcomes = ["passed", "failed", "timeout", "not_executable", "not_run", null];
    private static readonly string[] Statuses = ["done", "blocked", "gate_failed", "background_killed"];
    private static readonly string[] SelfOutcomes = ["passed", "failed", "unavailable"];
    private static readonly string[] Plans =
    [
        "## Gates\n1. **G1.** `full command`\n**Fix gate:** `short command` (R2)",
        "## Gates\n1. **G1.** `full command`",
        "## Gates\n1. **G1.** `full command`\n**Fix gate:** `short command`\n2. **Fix gate** prose",
        "## Gates\n1. **G1.** `full command`\n**Fix gate:** condition `short command`",
        "## Gates\n**G1.** `full command`\n**Fix gate:** `short command`"
    ];

    public static IEnumerable<object?[]> SelectionCells()
    {
        for (var mode = 0; mode < Modes.Length; mode++)
        for (var plan = 0; plan < Plans.Length; plan++)
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            // R1/R2/R8: full does not require a Fix gate; targeted requires the unique
            // executable command and a real numbered G entry; other mode inputs are refused.
            var expected = mode switch
            {
                0 or 1 => plan == 4 ? "empty" : "full",
                2 when plan == 0 => "targeted",
                _ => "refused"
            };
            yield return [Modes[mode], plan, newline, expected];
        }
    }

    [Theory]
    [MemberData(nameof(SelectionCells))]
    public void Selection_is_exact_for_every_mode_plan_and_newline(string? mode, int plan, string newline, string expected)
    {
        var text = Plans[plan].Replace("\n", newline, StringComparison.Ordinal);
        if (expected == "refused")
        {
            Assert.Throws<ArgumentRejectedException>(() => FixGatePolicy.SelectGates(mode, text));
            return;
        }

        var gates = FixGatePolicy.SelectGates(mode, text);
        if (expected == "empty") Assert.Empty(gates);
        else
        {
            var gate = Assert.Single(gates);
            Assert.Equal(expected == "full" ? "G1" : "Fix gate", gate.Label);
            Assert.Equal(expected == "full" ? "full command" : "short command", gate.Command);
        }
        Assert.Equal(mode ?? "full", FixGatePolicy.GateMode(mode));
    }

    public static IEnumerable<object?[]> CompletionCells()
    {
        for (var mode = 0; mode < Modes.Length; mode++)
        foreach (var host in HostOutcomes)
        foreach (var status in Statuses)
        foreach (var self in SelfOutcomes)
        {
            // R3/R6: host success decides irrespective of the Builder's own report.
            // R1 retains the existing full condition-gate closure, and R8 refuses bad modes.
            var expected = mode switch
            {
                >= 3 => "refused",
                2 when host == "passed" => "pending",
                0 or 1 when host == "passed" => "closed",
                0 or 1 when host == "not_executable" && status == "done" && self == "passed" => "closed",
                _ => "unverified"
            };
            yield return [Modes[mode], host, status, self, expected];
        }
    }

    [Theory]
    [MemberData(nameof(CompletionCells))]
    public void Completion_is_exact_for_every_mode_host_status_and_self_report(string? mode, string? host,
                                                                              string status, string self, string expected)
    {
        var result = new BuildResult(status, [], new Verification(self, "self evidence"), "summary",
            host is null ? null : new GateRun(host, "gate", null, null, null, null, null));
        if (expected == "refused")
        {
            Assert.Throws<ArgumentRejectedException>(() => FixGatePolicy.Completion(mode, result));
            return;
        }

        var actual = FixGatePolicy.Completion(mode, result);
        Assert.Equal(expected switch
        {
            "closed" => FixCompletion.Closed,
            "pending" => FixCompletion.PendingFullGate,
            _ => FixCompletion.Unverified
        }, actual);
    }

    public static IEnumerable<object?[]> ReadinessCells()
    {
        for (var timing = 0; timing < Timings.Length; timing++)
        foreach (var pending in new[] { false, true })
        {
            // R4/R8: final admits pending fixes; default/beforeNextRound requires full
            // verification first. Invalid timing is rejected even when nothing is pending.
            var expected = timing >= 3 ? "invalid" : timing != 2 && pending ? "pending" : "ready";
            yield return [Timings[timing], pending, expected];
        }
    }

    [Theory]
    [MemberData(nameof(ReadinessCells))]
    public void Review_readiness_is_exact_for_every_timing_and_pending_state(string? timing, bool pending, string expected)
    {
        if (expected != "ready")
        {
            var error = Assert.Throws<ArgumentRejectedException>(() => FixGatePolicy.RequireReviewReady(timing, pending));
            Assert.Contains(expected == "invalid" ? "fullGate" : "pending full host verification", error.Message);
            return;
        }
        FixGatePolicy.RequireReviewReady(timing, pending);
        Assert.Equal(timing ?? "beforeNextRound", FixGatePolicy.FullGateTiming(timing));
    }

    [Fact]
    public void Matrices_cover_every_axis_without_duplicate_or_missing_cells()
    {
        var selection = SelectionCells().ToArray();
        var completion = CompletionCells().ToArray();
        var readiness = ReadinessCells().ToArray();
        Assert.Equal(70, selection.Length);
        Assert.Equal(504, completion.Length);
        Assert.Equal(14, readiness.Length);
        Assert.Equal(70, selection.Select(row => (row[0], row[1], row[2])).Distinct().Count());
        Assert.Equal(504, completion.Select(row => (row[0], row[1], row[2], row[3])).Distinct().Count());
        Assert.Equal(14, readiness.Select(row => (row[0], row[1])).Distinct().Count());
        Assert.Equal(Modes, selection.Select(row => (string?)row[0]).Distinct());
        Assert.Equal(Enumerable.Range(0, 5), selection.Select(row => (int)row[1]!).Distinct());
        Assert.Equal(["\n", "\r\n"], selection.Select(row => (string)row[2]!).Distinct());
        Assert.Equal(Modes, completion.Select(row => (string?)row[0]).Distinct());
        Assert.Equal(HostOutcomes, completion.Select(row => (string?)row[1]).Distinct());
        Assert.Equal(Statuses, completion.Select(row => (string)row[2]!).Distinct());
        Assert.Equal(SelfOutcomes, completion.Select(row => (string)row[3]!).Distinct());
        Assert.Equal(Timings, readiness.Select(row => (string?)row[0]).Distinct());
        Assert.Equal([false, true], readiness.Select(row => (bool)row[1]!).Distinct());
    }

    [Fact]
    public async Task Policy_refusals_keep_their_reason_through_the_real_Mcp_error_filter()
    {
        var refusals = new List<(Action Action, string Reason)>();
        foreach (var mode in Modes.Skip(3))
            refusals.Add((() => FixGatePolicy.GateMode(mode), "gate must be exactly"));
        foreach (var timing in Timings.Skip(3))
            refusals.Add((() => FixGatePolicy.FullGateTiming(timing), "fullGate must be exactly"));
        refusals.Add((() => FixGatePolicy.SelectGates("targeted", Plans[1]), "none was found"));
        refusals.Add((() => FixGatePolicy.SelectGates("targeted", Plans[2]), "duplicate labels"));
        refusals.Add((() => FixGatePolicy.SelectGates("targeted", Plans[3]), "executable Fix gate"));
        refusals.Add((() => FixGatePolicy.SelectGates("targeted", Plans[4]), "numbered **G<n>**"));
        refusals.Add((() => FixGatePolicy.RequireReviewReady(null, true), "pending full host verification"));

        foreach (var (action, reason) in refusals)
        {
            var result = await ToolErrors.Surfaced((_, _) =>
            {
                action();
                return ValueTask.FromResult(new CallToolResult());
            })(null!, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Contains(reason, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }
    }
}
