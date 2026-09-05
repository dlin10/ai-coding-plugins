using System.Text;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// Decides what a builder turn is worth once the host has run its gate. The builder's
/// <c>status</c> and <c>verification</c> are its own word; the gate's exit code is the server's,
/// and where both exist the exit code wins — see docs/adr/0015.
/// </summary>
internal static class Gatekeeper
{
    private const string GateFailed = "gate_failed";

    /// <summary>
    /// Runs <paramref name="gates"/> when the builder reported <c>done</c>, and hands back the result
    /// with <see cref="BuildResult.Gate"/> filled in and the status rewritten to
    /// <c>gate_failed</c> when the command did not exit 0. A builder that reported <c>blocked</c>
    /// has nothing to gate; a gate that is a condition rather than a command leaves the self-report
    /// standing, and says so.
    /// </summary>
    /// <param name="result"></param>
    /// <param name="gates">The executable gate commands, run in order; empty when none is executable.</param>
    /// <param name="stated">Whether the plan states a gate at all, executable or not.</param>
    /// <param name="state"></param>
    /// <param name="ct"></param>
    public static async Task<BuildResult> CheckAsync(BuildResult result,
                                                     IReadOnlyList<GateCommand> gates,
                                                     bool stated,
                                                     RunState state,
                                                     CancellationToken ct)
    {
        var label = gates.Count == 0 ? "Gate" : Label(gates);

        if (!IsDone(result))
            return result with { Gate = new GateRun("not_run", label, null, null, null, null,
                                                     $"the builder reported {result.Status}, so the gate was not run") };

        if (gates.Count == 0)
            return result with { Gate = new GateRun("not_executable", label, null, null, null, null,
                                                     stated
                                                         ? "the gate is a condition rather than a command; the builder's verification stands"
                                                         : "the plan states no gate; the builder's verification stands") };

        var run = await GateRunner.RunAsync(Joined(gates), state.WorkspaceRoot, state.GateEnvironment, ct)
                                  .ConfigureAwait(false);

        return run.Outcome is "passed" or "not_run"
            ? result with { Gate = run }
            : result with { Gate = run, Status = GateFailed };
    }

    /// <summary>Only <c>done</c> is progress: a gate that failed leaves the task where it was.</summary>
    public static bool IsDone(BuildResult result) => string.Equals(result.Status, "done", StringComparison.Ordinal);

    /// <summary>
    /// What the next builder turn is told about a gate that failed, so the retry works against the
    /// host's evidence rather than its own recollection of a green run.
    /// </summary>
    public static string? PendingFailure(BuildResult result, string? previous)
    {
        if (result.Gate is null) return previous;

        return result.Gate.Outcome switch
        {
            "passed" => null,
            "failed" or "timeout" => Describe(result.Gate),
            _ => previous
        };
    }

    public static void AppendPendingFailure(StringBuilder prompt, string? pending)
    {
        if (pending is not { Length: > 0 }) return;

        prompt.AppendLine()
              .AppendLine("# The previous attempt did not pass its gate")
              .AppendLine()
              .AppendLine(pending)
              .AppendLine()
              .AppendLine("The same command runs again on the host after this turn, and the work is not counted "
                          + "until it exits 0. Make it pass; do not report `done` on the strength of a check you ran yourself.");
    }

    private static string Describe(GateRun gate)
    {
        var text = new StringBuilder();
        text.Append("The gate ");
        if (gate.Label != "Gate") text.Append('(').Append(gate.Label).Append(") ");

        text.Append(gate.Outcome == "timeout"
                        ? $"was killed after {gate.Seconds:0} s without finishing"
                        : gate.ExitCode is { } code
                            ? $"exited {code} after {gate.Seconds:0} s"
                            : $"could not run: {gate.Detail}")
            .AppendLine(" when the host ran it after your previous turn.")
            .AppendLine()
            .AppendLine("Command:")
            .AppendLine()
            .AppendLine("```")
            .AppendLine(gate.Command)
            .AppendLine("```");

        if (gate.Output is { Length: > 0 })
            text.AppendLine()
                .AppendLine("Its output ended with:")
                .AppendLine()
                .AppendLine("```text")
                .AppendLine(gate.Output)
                .AppendLine("```");

        return text.ToString().TrimEnd();
    }

    private static string Label(IReadOnlyList<GateCommand> gates) =>
        gates.Count == 1 ? gates[0].Label : string.Join(", ", gates.Select(gate => gate.Label));

    /// <summary>
    /// Several run-wide gates become one script, one gate per line: the runner stops at the first
    /// line that fails, so the report names the failing command's exit code and nothing after it ran.
    /// </summary>
    private static GateCommand Joined(IReadOnlyList<GateCommand> gates) =>
        gates.Count == 1 ? gates[0] : new GateCommand(Label(gates), string.Join('\n', gates.Select(gate => gate.Command)));
}
