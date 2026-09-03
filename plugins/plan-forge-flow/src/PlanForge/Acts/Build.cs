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
        if (state.TasksCompleted >= tasks.Count) return new BuildOutcome(null, state.TasksCompleted, tasks.Count);

        var task = tasks[state.TasksCompleted];
        var prompt = Compose(task, tasks.Count);
        SensitiveInput.Guard(prompt, $"task {task.Number}");

        var sameVendor = string.Equals(state.BuilderVendor, _vendor.Id, StringComparison.Ordinal);
        var resumeToken = sameVendor && state.BuilderSessionId is { Length: > 0 } token ? token : null;
        await using var session = await _vendor.StartAsync(
            new RoleSpec(VendorRole.Builder, _prompts.Load(_vendor.Id, VendorRole.Builder)),
            selection, resumeToken, ct);

        var result = await BuilderTurn.RunAsync(session, state.WorkspaceRoot, prompt, ct);

        run.AppendFlowBuild(task.Number, tasks.Count, result);
        run.WriteState(state with
        {
            TasksCompleted = state.TasksCompleted + 1,
            BuilderSessionId = sameVendor
                ? session.ResumeToken ?? state.BuilderSessionId
                : session.ResumeToken ?? string.Empty,
            BuilderVendor = _vendor.Id
        });

        return new BuildOutcome(result, state.TasksCompleted + 1, tasks.Count);
    }

    private static string Compose(PlanTask task, int total) =>
        new StringBuilder()
            .Append("# Task ").Append(task.Number).Append(" of ").Append(total).AppendLine()
            .AppendLine()
            .AppendLine(task.Text)
            .ToString();
}

internal sealed record BuildOutcome(BuildResult? Result, int TasksCompleted, int TaskCount);

internal sealed class NotApprovedException(string runId)
    : Exception($"run {runId} has no approved plan yet");
