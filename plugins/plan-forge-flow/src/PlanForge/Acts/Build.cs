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

    /// <summary>Set when the task's Fast turn was served at standard speed for part of it.</summary>
    internal string? SpeedWarning { get; private set; }

    public Build(IVendor vendor, PromptLibrary prompts)
    {
        _vendor = vendor;
        _prompts = prompts;
    }

    public async Task<BuildOutcome> NextAsync(RunDirectory run, Selection selection, CancellationToken ct)
    {
        var state = run.ReadState();
        if (!state.Approved) throw new NotApprovedException(run.RunId);

        var plan = run.ReadPlan();
        var tasks = PlanTasks.Parse(plan);
        if (state.TasksCompleted >= tasks.Count)
            return new BuildOutcome(null, state.TasksCompleted, tasks.Count);

        var task = tasks[state.TasksCompleted];

        // Which session this turn belongs to is settled before the prompt is composed, because the
        // user's instructions go to a builder exactly once: the turn that starts a session carries
        // them, and a resumed one already has them in its history. See docs/adr/0019. A session
        // covers this task, so only a retry of it resumes; see docs/adr/0026.
        var scope = BuilderSession.TaskScope(task.Number);
        var resumeToken = BuilderSession.ResumeToken(state, _vendor.Id, scope);

        var prompt = Compose(task, tasks.Count, state.PendingGateFailure,
                             resumeToken is null ? state.BuilderInstructions : null,
                             resumeToken is null ? state.TaskChanges : null);
        SensitiveInput.Guard(prompt, $"task {task.Number}");
        if (resumeToken is null)
            prompt = BuilderBrief.Prepend(prompt, plan);

        await using var session = await _vendor.StartAsync(new RoleSpec(VendorRole.Builder, _prompts.Load(_vendor.Id, VendorRole.Builder),
                                                                        state.BuilderRoots, WorkerTools.Effective(state.WorkerTools),
                                                                        new WorkerTelemetryContext(run.TelemetryPath, run.Log, "build",
                                                                                                   TaskNumber: task.Number,
                                                                                                   TaskCount: tasks.Count)),
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
            run.WriteState(BuilderSession.Record(state, session, _vendor.Id, scope, resumeToken) with
            {
                PendingGateFailure = Gatekeeper.CutShortBrief(cutShort.FilesWritten)
            });

            throw;
        }

        // The host's run of the task's gate, where the gate is a command, is what decides the task
        // — not the builder's account of the checks it ran. See docs/adr/0015.
        var gate = PlanGates.TaskGate(task.Text);
        var killed = session.KilledBackgroundTasks;
        SpeedWarning = session.SpeedWarning;
        var result = await Gatekeeper.CheckAsync(reported, gate is null ? [] : [gate], PlanGates.HasGate(task.Text), killed, state, ct);

        // A task the builder could not do, or whose gate failed, stays the next task, so the
        // following call retries it instead of stepping over it as if it had been built.
        var done = Gatekeeper.IsDone(result);
        var tasksCompleted = done ? state.TasksCompleted + 1 : state.TasksCompleted;

        run.AppendFlowBuild(task.Number, tasks.Count, result);
        run.WriteState(BuilderSession.Record(state, session, _vendor.Id, scope, resumeToken) with
        {
            TasksCompleted = tasksCompleted,
            PendingGateFailure = Gatekeeper.PendingFailure(result, killed, state.PendingGateFailure),
            TaskChanges = done
                ? [.. (state.TaskChanges ?? []).Where(change => change.TaskNumber != task.Number),
                   new TaskChange(task.Number, result.FilesChanged)]
                : state.TaskChanges
        });

        return new BuildOutcome(result, tasksCompleted, tasks.Count);
    }

    /// <summary>
    /// The builder's act prompt for one task, before the Builder Brief is put in front of it: what
    /// earlier tasks changed, the task itself, what the last attempt left owing, and the user's
    /// instructions last.
    /// </summary>
    /// <param name="task">The task to build.</param>
    /// <param name="total">How many tasks the plan has, for the task's heading.</param>
    /// <param name="pendingGateFailure">What the last gate or turn left owing, or null when nothing is.</param>
    /// <param name="instructions">The user's builder instructions, for a fresh session only.</param>
    /// <param name="earlier">
    /// What the tasks before this one changed, for a fresh session only: it starts with no memory of
    /// them, and the files on disk are their result.
    /// </param>
    /// <returns>The prompt text.</returns>
    private static string Compose(PlanTask task, int total, string? pendingGateFailure, string? instructions,
                                  IReadOnlyList<TaskChange>? earlier)
    {
        var prompt = new StringBuilder();
        if (earlier is { Count: > 0 })
        {
            prompt.AppendLine("# Earlier tasks")
                  .AppendLine()
                  .AppendLine("Earlier tasks of this plan changed these files; their work is on disk.")
                  .AppendLine();
            foreach (var change in earlier.OrderBy(change => change.TaskNumber))
                prompt.Append("- Task ").Append(change.TaskNumber).Append(": ")
                      .AppendLine(change.FilesChanged.Count == 0 ? "no files" : string.Join(", ", change.FilesChanged));
            prompt.AppendLine();
        }

        prompt.Append("# Task ").Append(task.Number).Append(" of ").Append(total).AppendLine()
              .AppendLine()
              .AppendLine(task.Text);
        Gatekeeper.AppendPendingFailure(prompt, pendingGateFailure);
        RunInstructions.Append(prompt, instructions);
        return prompt.ToString();
    }
}

internal sealed record BuildOutcome(BuildResult? Result, int TasksCompleted, int TaskCount);

internal sealed class NotApprovedException(string runId) : Exception($"run {runId} has no approved plan yet");
