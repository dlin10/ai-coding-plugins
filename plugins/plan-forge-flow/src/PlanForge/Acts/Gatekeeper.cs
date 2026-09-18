using System.Text;
using PlanForge.Diagnostics;
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
    private const string BACKGROUND_KILLED = "background_killed";
    private const string UNAVAILABLE = "unavailable";

    // How a kill is briefed, and how the brief is told apart from a failed gate once it is only a
    // string in the run state.
    private const string KILLED_BRIEF = "Your previous turn ended with work still running in the background";

    // The same, for a turn the host took away before the builder answered.
    private const string CUT_SHORT_BRIEF = "Your previous turn was cut short: the host took the call away before you answered";

    /// <summary>
    /// Runs <paramref name="gates"/> when the turn is worth checking, and hands back the result with
    /// <see cref="BuildResult.Gate"/> filled in and the status rewritten to what the exit code says:
    /// <c>done</c> where the command exits 0, <c>gate_failed</c> where it does not. A gate that is a
    /// condition rather than a command leaves the self-report standing, and says so. A turn that
    /// left work running in the background is <c>background_killed</c> and runs no gate: by the
    /// builder's own account the work was still in progress — see docs/adr/0018.
    /// </summary>
    /// <param name="result"></param>
    /// <param name="gates">The executable gate commands, run in order; empty when none is executable.</param>
    /// <param name="stated">Whether the plan states a gate at all, executable or not.</param>
    /// <param name="killed">What the vendor killed when the turn ended; empty when nothing was left running.</param>
    /// <param name="state"></param>
    /// <param name="ct"></param>
    public static async Task<BuildResult> CheckAsync(BuildResult result,
                                                     IReadOnlyList<GateCommand> gates,
                                                     bool stated,
                                                     IReadOnlyList<string> killed,
                                                     RunState state,
                                                     CancellationToken ct)
    {
        var label = gates.Count == 0 ? "Gate" : Label(gates);

        if (killed.Count > 0)
        {
            RunLog.Current?.Write("warn", "builder", "builder.background-killed", ("tasks", string.Join("; ", killed)));
            return result with
            {
                Status = BACKGROUND_KILLED,
                Gate = new GateRun("not_run", label, null, null, null, null,
                                   $"the turn ended while {Quoted(killed)} still ran in the background, so the session killed "
                                   + "it and the gate was not run")
            };
        }

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
    public static string? PendingFailure(BuildResult result, IReadOnlyList<string> killed, string? previous)
    {
        if (killed.Count > 0) return KilledBrief(killed);

        // How the previous turn ended is about the turn that followed it and nothing later:
        // without this, a task whose gate is a condition would carry the brief on to the next task.
        if (Transient(previous)) previous = null;
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

        if (Transient(pending))
        {
            prompt.AppendLine()
                  .AppendLine("# The previous attempt was cut short")
                  .AppendLine()
                  .AppendLine(pending);
            return;
        }

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

    /// <summary>
    /// What the retry is told about a kill. The builder of task 8 in run 20260916-134641-21e3d5 said
    /// it would continue when its grid finished, and believed a time limit had forced it out; the
    /// brief says what actually ended the turn.
    /// </summary>
    private static string KilledBrief(IReadOnlyList<string> killed)
    {
        var text = new StringBuilder().Append(KILLED_BRIEF)
                                      .AppendLine(", and the session ended with the turn, killing it before it finished:")
                                      .AppendLine();

        foreach (var task in killed)
            text.Append("- `").Append(task).AppendLine("`");

        return text.AppendLine()
                   .Append("Nothing it would have produced exists, and the task was not counted. Your session ends at your ")
                   .Append("final answer and nothing runs after it: run what you need in the foreground, with a timeout ")
                   .Append("long enough for it, and wait for it before you answer.")
                   .ToString();
    }

    /// <summary>
    /// What the next builder turn is told about a turn the host cut short. The files are the whole
    /// of what is known — the builder never answered, so there is no status, no verification and no
    /// gate — and naming them is what stops the retry starting over on work already on disk.
    /// </summary>
    /// <remarks>
    /// The closing paragraph is aimed at the cause rather than the symptom. The builder of round 5
    /// in run 20260917-111319-20e672 finished its work and its gates with twenty-three minutes to
    /// spare, then spent every one of them on a check it had not been asked for, and the hour ran
    /// out while that check hung.
    /// </remarks>
    public static string CutShortBrief(IReadOnlyList<string> filesWritten)
    {
        var text = new StringBuilder().Append(CUT_SHORT_BRIEF)
                                      .AppendLine(", and the session was killed with it.")
                                      .AppendLine();

        if (filesWritten.Count > 0)
        {
            text.AppendLine("What you had already written is still on disk:").AppendLine();
            foreach (var file in filesWritten) text.Append("- `").Append(file).AppendLine("`");
            text.AppendLine();
        }

        return text.Append("Nothing was verified: no gate ran and the work was not counted. Read what you changed ")
                   .Append("before you change more. The call ends at a fixed deadline you cannot see and nothing you ")
                   .Append("do after your final answer survives it, so spend the turn on the work you were asked for ")
                   .Append("and answer as soon as it is done — not on optional extra checks.")
                   .ToString();
    }

    // A brief about how the previous turn ended, as opposed to a gate that failed: it is spent by
    // the turn that reads it, while a gate failure stands until a gate passes.
    private static bool Transient(string? brief) =>
        brief is not null && (brief.StartsWith(KILLED_BRIEF, StringComparison.Ordinal)
                              || brief.StartsWith(CUT_SHORT_BRIEF, StringComparison.Ordinal));

    private static string Quoted(IReadOnlyList<string> killed) => string.Join(", ", killed.Select(task => $"`{task}`"));

    private static string Label(IReadOnlyList<GateCommand> gates) =>
        gates.Count == 1 ? gates[0].Label : string.Join(", ", gates.Select(gate => gate.Label));

    /// <summary>
    /// Several run-wide gates become one script, one gate per line: the runner stops at the first
    /// line that fails, so the report names the failing command's exit code and nothing after it ran.
    /// </summary>
    private static GateCommand Joined(IReadOnlyList<GateCommand> gates) =>
        gates.Count == 1 ? gates[0] : new GateCommand(Label(gates), string.Join('\n', gates.Select(gate => gate.Command)));
}
