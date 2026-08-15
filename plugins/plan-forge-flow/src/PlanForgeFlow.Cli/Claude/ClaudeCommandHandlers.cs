using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Claude;

internal static class ClaudeCommandHandlers
{
    private static readonly HashSet<string> ExecutionCommands = new(StringComparer.Ordinal)
    {
        "build dispatch", "build complete", "build resolve", "review prepare", "review authorize-preexisting", "review verdict",
        "session builder", "session reviewer", "run set",
    };

    public static PendingRun Stage(CommandContext context, TextReader input)
    {
        var repository = RequireActive(context);
        return PendingRuns.StageClaude(repository, input.ReadToEnd(), context.Args.GetRequired("run-id"), Selection(context));
    }

    public static PendingRun Finalize(CommandContext context)
    {
        var repository = RequireActive(context);
        return PendingRuns.FinalizeClaude(repository, context.Args.GetRequired("run-id"), Selection(context),
                                          context.Args.GetRequired("builder-hold-id"));
    }

    public static PendingRun RecordResponse(CommandContext context, TextReader input)
    {
        if (context.Args.GetRequired("stage") != "plan") throw new CliFailure("usage", "Claude pending review stage must be plan");
        return PendingRuns.RecordClaude(RequireActive(context), context.Args.GetRequired("run-id"),
                                        context.Args.GetRequired("dispatch-id"), input.ReadToEnd());
    }

    public static PendingRun Invalidate(CommandContext context)
        => PendingRuns.InvalidateClaude(RequireActive(context), context.Args.GetRequired("run-id"), context.Args.GetRequired("reason"));

    public static PendingRun Abandon(CommandContext context)
    {
        var repository = RequireRepository(context);
        var activation = ClaudeActivations.Abandon(repository,
                                                   context.Args.GetRequired("run-id"),
                                                   ClaudeActivations.CurrentSessionId(),
                                                   context.Args.Has("accept-risk"),
                                                   context.Args.Get("authorization-note"));
        return PendingRuns.TryLoadForRun(repository, activation.RunId, HostKind.Claude) ??
               new PendingRun
               {
                   Host = HostKind.Claude,
                   Workspace = activation.Workspace,
                   ScopeId = activation.ScopeId,
                   RunId = activation.RunId,
                   Phase = PendingRunPhase.Abandoned,
                   CreatedAt = activation.CreatedAt,
                   UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
               };
    }

    public static ClaudeActivation Begin(CommandContext context)
        => ClaudeActivations.Begin(RequireRepository(context), ClaudeActivations.CurrentSessionId());

    public static ClaudeActivation AbandonRun(CommandContext context)
        => ClaudeActivations.Abandon(RequireRepository(context),
                                     context.Args.GetRequired("run-id"),
                                     ClaudeActivations.CurrentSessionId(),
                                     context.Args.Has("accept-risk"),
                                     context.Args.Get("authorization-note"));

    public static MaterializeData Materialize(CommandContext context)
    {
        var repository = RequireRepository(context);
        var runId = context.Args.GetRequired("run-id");
        var pending = PendingRuns.Load(repository, runId, HostKind.Claude);
        if (pending.Phase is not (PendingRunPhase.Ready or PendingRunPhase.Materializing or PendingRunPhase.Consumed))
            throw new CliFailure("state", $"Claude materialization requires ready, materializing, or consumed pending state (current: {PendingRuns.PhaseName(pending.Phase)})", 3);
        ClaudeActivations.RequireMaterialization(repository, runId, ClaudeActivations.CurrentSessionId());
        return Workflow.Planning.ForgeWorkflow.MaterializeClaudePlan(context);
    }

    public static void RequireExecutionForCommand(CommandContext context)
    {
        if (context.Host != HostKind.Claude || !ExecutionCommands.Contains(context.Command)) return;
        ClaudeActivations.RequireExecution(RequireRepository(context), context.RequireState(), ClaudeActivations.CurrentSessionId());
    }

    public static CleanupData Cleanup(CommandContext context)
    {
        var repository = RequireRepository(context);
        var purgeAgents = context.Args.Has("purge-generated-agents");
        if (context.Args.Has("legacy"))
        {
            ClaudeActivations.RequireNoActivation(repository);
            Workflow.Planning.Materializer.CleanupLegacy(context.Workspace, purgeAgents, HostKind.Claude);
            return new CleanupData(true, purgeAgents);
        }

        var activation = ClaudeActivations.BeginCleanup(repository,
                                                        ClaudeActivations.CurrentSessionId(),
                                                        context.Args.Get("run-id"),
                                                        context.Args.Has("accept-risk"),
                                                        context.Args.Get("authorization-note"));
        Workflow.ForgeWorkflow.Cleanup(context.Workspace, purgeAgents, HostKind.Claude);
        ClaudeActivations.CompleteCleanup(activation);
        return new CleanupData(true, purgeAgents);
    }

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

    private static RepositoryIdentity RequireActive(CommandContext context)
    {
        var repository = RequireRepository(context);
        ClaudeActivations.RequirePlanning(repository, context.Args.GetRequired("run-id"), ClaudeActivations.CurrentSessionId());
        return repository;
    }
}
