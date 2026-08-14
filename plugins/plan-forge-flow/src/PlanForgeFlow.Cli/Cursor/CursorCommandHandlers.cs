using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Review;
using PlanForgeFlow.Workflow.State;
using PlanForgeFlow.Pending;

namespace PlanForgeFlow.Cursor;

internal static class CursorCommandHandlers
{
    public static PendingRun Stage(CommandContext context, TextReader input)
    {
        var repository = RequireRepository(context);
        return PendingRuns.Stage(repository, input.ReadToEnd(), context.Args.GetRequired("run-id"), Waiver(context, "reviewer"), context.Args.Has("accept-risk"), context.Args.Get("authorization-note"));
    }

    public static PendingRun Finalize(CommandContext context)
        => PendingRuns.Finalize(RequireRepository(context), context.Args.GetRequired("run-id"), Waiver(context, "builder"));

    public static PendingRun Abandon(CommandContext context)
        => PendingRuns.Abandon(RequireRepository(context), context.Args.GetRequired("run-id"));

    public static PendingRun Invalidate(CommandContext context)
        => PendingRuns.Invalidate(RequireRepository(context), context.Args.GetRequired("run-id"), context.Args.GetRequired("reason"));

    public static PendingRun RecordResponse(CommandContext context, TextReader input)
    {
        var repository = RequireRepository(context);
        var dispatchId = context.Args.GetRequired("dispatch-id");
        var stage = context.Args.GetRequired("stage");
        var response = input.ReadToEnd();
        if (stage != "plan") return CursorReviewEvidence.Record(repository, dispatchId, stage, response);
        var run = context.Args.Get("run-id") is { } runId ? PendingRuns.Load(repository, runId) : PendingRuns.ResolvePlanDispatch(repository, dispatchId);
        return PendingRuns.Record(repository, run.RunId, dispatchId, stage, response);
    }

    private static RepositoryIdentity RequireRepository(CommandContext context)
    {
        if (context.Host != HostKind.Cursor) throw new CliFailure("usage", "this command requires --host cursor");
        return RepositoryPaths.Identify(context.Workspace);
    }

    private static CursorModelWaiver Waiver(CommandContext context, string role)
        => new(role, context.Args.GetRequired("model"), context.Args.GetRequired("effort"), context.Args.GetRequired("cursor-version"), context.Args.GetRequired("observed-model"), context.Args.GetRequired("waiver-reason"), DateTimeOffset.UtcNow.ToString("O"));
}
