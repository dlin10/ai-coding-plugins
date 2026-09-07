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
    private const string DONE = "done";
    private const string GATE_FAILED = "gate_failed";
    private const string UNAVAILABLE = "unavailable";

    /// <summary>
    /// Runs <paramref name="gates"/> when the turn is worth checking, and hands back the result with
    /// <see cref="BuildResult.Gate"/> filled in and the status rewritten to what the exit code says:
    /// <c>done</c> where the command exits 0, <c>gate_failed</c> where it does not. A gate that is a
    /// condition rather than a command leaves the self-report standing, and says so.
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

        if (!Gatable(result))
            return result with { Gate = new GateRun("not_run", label, null, null, null, null,
                                                     $"the builder reported {result.Status}, so the gate was not run") };

        if (gates.Count == 0)
            return result with { Gate = new GateRun("not_executable", label, null, null, null, null,
                                                     stated
                                                         ? "the gate is a condition rather than a command; the builder's verification stands"
                                                         : "the plan states no gate; the builder's verification stands") };

        var run = await GateRunner.RunAsync(Joined(gates), state.WorkspaceRoot, state.GateEnvironment, ct)
                                  .ConfigureAwait(false);

        // The exit code decides the status in both directions. Upward matters as much as downward:
        // a builder that did the work and could not prove it reported `blocked`, and the gate that
        // passes is the proof it was missing.
        return run.Outcome switch
        {
            "passed" => result with { Gate = run, Status = DONE },
            "not_run" => result with { Gate = run },
            _ => result with { Gate = run, Status = GATE_FAILED }
        };
    }

    /// <summary>Only <c>done</c> is progress: a gate that failed leaves the task where it was.</summary>
    public static bool IsDone(BuildResult result) => string.Equals(result.Status, DONE, StringComparison.Ordinal);

    /// <summary>
    /// Whether the host has a reason to run the gate at all. <c>done</c> is the obvious one;
    /// <c>blocked</c> with a verification of <c>unavailable</c> is a builder saying it did the work
    /// and could not prove it, which is exactly what a host holding the environment can settle.
    /// </summary>
    /// <remarks>
    /// Task 13 of run 20260905-144900-e42174 deadlocked on the two rules this joins. Its first
    /// attempt failed the gate, so the failure was stored; its second fixed the cause but answered
    /// <c>blocked</c>, because the codex sandbox cannot reach SQL Express and three of the nine
    /// gated tests need it. No gate ran for a blocked turn, and <see cref="PendingFailure"/> clears
    /// only on a gate that passes, so every later attempt was handed the superseded failure,
    /// reasoned that only the host could verify, and blocked again — the last of them changing no
    /// files at all. The gate an orchestrator eventually ran by hand passed. A verification of
    /// <c>failed</c> is deliberately left alone: there the builder ran the check itself and watched
    /// it fail, and nothing about its word is in doubt.
    /// </remarks>
    private static bool Gatable(BuildResult result) =>
        IsDone(result) || string.Equals(result.Verification.Outcome, UNAVAILABLE, StringComparison.Ordinal);

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
