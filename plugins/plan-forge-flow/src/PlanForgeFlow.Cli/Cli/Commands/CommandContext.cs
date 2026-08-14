using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Cli.Commands;

internal sealed record CommandContext(string Command, string Workspace, ParsedArgs Args, ForgeState? State, HostKind Host = HostKind.Codex)
{
    public ForgeState RequireState() => State ?? throw new InvalidOperationException($"{Command} does not load Forge state");

    public static CommandContext Create(CliCommandDefinition command, ParsedArgs args)
    {
        var workspace = RepositoryPaths.CanonicalWorkspaceRoot(args.Get("workspace") ?? Directory.GetCurrentDirectory());
        var host = args.Get("host")?.ToLowerInvariant() switch
        {
            null or "codex" => HostKind.Codex,
            "cursor" => HostKind.Cursor,
            "claude" => HostKind.Claude,
            _ => throw new CliFailure("usage", "--host must be codex|cursor|claude"),
        };
        var state = command.LoadsState ? StateStore.Load(workspace) : null;
        if (state is not null && state.Host != host) throw new CliFailure("state", "Forge state host does not match --host", 3);
        if (state is not null && state.RepositoryScopeId != RepositoryPaths.ScopeId(RepositoryPaths.Identify(workspace))) throw new CliFailure("state", "Forge state repository scope does not match this workspace", 3);
        return new CommandContext(command.Name, workspace, args, state, host);
    }
}
