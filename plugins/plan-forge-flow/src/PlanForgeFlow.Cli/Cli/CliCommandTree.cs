namespace PlanForgeFlow.Cli;

internal sealed record CliCommandDefinition(string Name, IReadOnlySet<string> Options, bool LoadsState);

internal static class CliCommands
{
    private static readonly HashSet<string> BooleanOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "cancel", "retry", "relock", "amendment", "full", "accept-risk", "purge-generated-agents", "verification-passed",
    };

    private static readonly IReadOnlyDictionary<string, CliCommandDefinition> Definitions =
        new Dictionary<string, CliCommandDefinition>(StringComparer.Ordinal)
        {
            ["plan lock"] = Define("plan lock", true, "workspace", "relock", "amendment"),
            ["plan materialize"] = Define("plan materialize", false, "workspace", "amendment"),
            ["agents install"] = Define("agents install", false, "workspace"),
            ["build dispatch"] = Define("build dispatch", true, "workspace", "stage", "task-number", "retry", "cancel", "dispatch-id", "model", "effort", "authorization-note", "accept-risk"),
            ["build complete"] = Define("build complete", true, "workspace", "task-number", "dispatch-id", "verification-passed", "authorization-note", "accept-risk"),
            ["build resolve"] = Define("build resolve", true, "workspace", "conflict", "dispatch-id"),
            ["build begin"] = Define("build begin", true, "workspace", "amendment", "relock"),
            ["review prepare"] = Define("review prepare", true, "workspace", "allow-paths", "full", "authorization-note"),
            ["review authorize-preexisting"] = Define("review authorize-preexisting", true, "workspace", "authorized-paths", "authorization-note", "accept-risk"),
            ["review verdict"] = Define("review verdict", true, "workspace", "stage", "critique-file", "accept-risk", "authorization-note"),
            ["session builder"] = Define("session builder", true, "workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["session reviewer"] = Define("session reviewer", true, "workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["run doctor"] = Define("run doctor", false, "workspace"),
            ["run status"] = Define("run status", false, "workspace"),
            ["run set"] = Define("run set", true, "workspace", "key", "value", "amendment", "accept-risk", "authorization-note"),
            ["run cleanup"] = Define("run cleanup", false, "workspace", "purge-generated-agents"),
        };

    public static IReadOnlyCollection<string> Names => Definitions.Keys.ToArray();

    public static bool TryGet(string name, out CliCommandDefinition definition) => Definitions.TryGetValue(name, out definition!);

    public static bool IsBoolean(string option) => BooleanOptions.Contains(option);

    private static CliCommandDefinition Define(string name, bool loadsState, params string[] options)
        => new(name, options.ToHashSet(StringComparer.Ordinal), loadsState);
}
