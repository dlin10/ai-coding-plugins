namespace PlanForgeFlow;

internal sealed record CommandContext(string Command, string Workspace, ParsedArgs Args, ForgeState? State)
{
    public ForgeState RequireState() => State ?? throw new InvalidOperationException($"{Command} does not load Forge state");

    public static CommandContext Create(CliCommandDefinition command, ParsedArgs args)
    {
        var workspace = RepositoryPaths.CanonicalWorkspaceRoot(args.Get("workspace") ?? Directory.GetCurrentDirectory());
        return new CommandContext(command.Name, workspace, args, command.LoadsState ? StateStore.Load(workspace) : null);
    }
}
