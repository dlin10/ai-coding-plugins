using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Claude;

internal static class ClaudeCommandHandlers
{
    public static PendingRun Stage(CommandContext context, TextReader input)
        => PendingRuns.StageClaude(RequireRepository(context), input.ReadToEnd(), context.Args.GetRequired("run-id"), Selection(context));

    public static PendingRun Finalize(CommandContext context)
        => PendingRuns.FinalizeClaude(RequireRepository(context), context.Args.GetRequired("run-id"), Selection(context),
                                      context.Args.GetRequired("builder-hold-id"));

    public static PendingRun RecordResponse(CommandContext context, TextReader input)
    {
        if (context.Args.GetRequired("stage") != "plan") throw new CliFailure("usage", "Claude pending review stage must be plan");
        return PendingRuns.RecordClaude(RequireRepository(context), context.Args.GetRequired("run-id"),
                                        context.Args.GetRequired("dispatch-id"), input.ReadToEnd());
    }

    public static PendingRun Invalidate(CommandContext context)
        => PendingRuns.InvalidateClaude(RequireRepository(context), context.Args.GetRequired("run-id"), context.Args.GetRequired("reason"));

    public static PendingRun Abandon(CommandContext context)
        => PendingRuns.AbandonClaude(RequireRepository(context), context.Args.GetRequired("run-id"));

    private static ProviderSelection Selection(CommandContext context)
        => ProviderSelections.Parse(context.Args.GetRequired("provider"),
                                    context.Args.GetRequired("requested-model"),
                                    context.Args.GetRequired("resolved-model"),
                                    context.Args.Get("models-used"),
                                    context.Args.GetRequired("effort"));

    private static RepositoryIdentity RequireRepository(CommandContext context)
    {
        if (context.Host != HostKind.Claude) throw new CliFailure("usage", "this command requires --host claude");
        return RepositoryPaths.Identify(context.Workspace);
    }
}
