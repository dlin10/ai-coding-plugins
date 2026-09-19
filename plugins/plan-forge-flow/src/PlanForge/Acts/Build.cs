using System.Text;
using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// One task per call. Deliberately not a loop: this is where observability, survivability and the
/// chance to intervene matter, and one tool call per task is the only progress granularity any
/// host actually surfaces.
/// </summary>
internal sealed class Build
{
    private readonly IVendor _vendor;
    private readonly PromptLibrary _prompts;

    public Build(IVendor vendor, PromptLibrary prompts)
    {
        _vendor = vendor;
        _prompts = prompts;
    }

    public async Task<BuildOutcome> NextAsync(RunDirectory run, Selection selection, CancellationToken ct)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);

        var tasks = PlanTasks.Parse(run.ReadPlan());
        if (state.TasksCompleted >= tasks.Count)
            return new BuildOutcome(null, state.TasksCompleted, tasks.Count);

        var task = tasks[state.TasksCompleted];

        // Which session this turn belongs to is settled before the prompt is composed, because the
        // user's instructions go to a builder exactly once: the turn that starts a session carries
        // them, and a resumed one already has them in its history. See docs/adr/0019.
        var sameVendor = string.Equals(state.BuilderVendor, _vendor.Id, StringComparison.Ordinal);
        var resumeToken = sameVendor && state.BuilderSessionId is { Length: > 0 } token ? token : null;

        var prompt = Compose(task, tasks.Count, state.PendingGateFailure,
                             resumeToken is null ? state.BuilderInstructions : null);
        SensitiveInput.Guard(prompt, $"task {task.Number}");
        await using var session = await _vendor.StartAsync(new RoleSpec(VendorRole.Builder, _prompts.Load(_vendor.Id, VendorRole.Builder),
                                                                        state.BuilderRoots, WorkerTools.Effective(state.WorkerTools)),
                                                           selection,
                                                           resumeToken,
                                                           ct);

        BuildResult reported;
        try
        {
            reported = await BuilderTurn.RunAsync(session, state.WorkspaceRoot, prompt, ct);
        }
        // The call is gone, so nothing can be returned — but the turn happened, and what is known
        // of it goes down before the cancellation travels on. The task counter does not move: the
        // builder never answered, so nothing it did was verified and this stays the next task.
        catch (TurnCutShortException cutShort)
        {
            run.AppendFlowCutShort($"Task {task.Number} of {tasks.Count}", cutShort.FilesWritten);
            run.WriteState(Resumed(state, session, sameVendor) with
            {
                PendingGateFailure = Gatekeeper.CutShortBrief(cutShort.FilesWritten)
            });

            throw;
        }

        // The host's run of the task's gate, where the gate is a command, is what decides the task
        // — not the builder's account of the checks it ran. See docs/adr/0015.
        var gate = PlanGates.TaskGate(task.Text);
        var killed = session.KilledBackgroundTasks;
        var result = await Gatekeeper.CheckAsync(reported, gate is null ? [] : [gate], PlanGates.HasGate(task.Text), killed, state, ct);

        // A task the builder could not do, or whose gate failed, stays the next task, so the
        // following call retries it instead of stepping over it as if it had been built.
        var tasksCompleted = Gatekeeper.IsDone(result) ? state.TasksCompleted + 1 : state.TasksCompleted;

        run.AppendFlowBuild(task.Number, tasks.Count, result);
        run.WriteState(Resumed(state, session, sameVendor) with
        {
            TasksCompleted = tasksCompleted,
            PendingGateFailure = Gatekeeper.PendingFailure(result, killed, state.PendingGateFailure)
        });

        return new BuildOutcome(result, tasksCompleted, tasks.Count);
    }

    /// <summary>
    /// The builder's session as the run should remember it after a turn. A cut-short turn keeps its
    /// token like any other: the id arrives on the vendor's first stream line, long before the
    /// answer that never came, and without it the retry starts a builder with no memory of the
    /// attempt it is repeating.
    /// </summary>
    private RunState Resumed(RunState state, IVendorSession session, bool sameVendor) =>
        state with
        {
            BuilderSessionId = sameVendor
                ? session.ResumeToken ?? state.BuilderSessionId
                : session.ResumeToken ?? string.Empty,
            BuilderVendor = _vendor.Id
        };

    private static string Compose(PlanTask task, int total, string? pendingGateFailure, string? instructions)
    {
        var prompt = new StringBuilder().Append("# Task ").Append(task.Number).Append(" of ").Append(total).AppendLine()
                                        .AppendLine()
                                        .AppendLine(task.Text);
        Gatekeeper.AppendPendingFailure(prompt, pendingGateFailure);
        RunInstructions.Append(prompt, instructions);
        return prompt.ToString();
    }
}

internal sealed record BuildOutcome(BuildResult? Result, int TasksCompleted, int TaskCount);

internal sealed class NotApprovedException(string runId) : Exception($"run {runId} has no approved plan yet");
