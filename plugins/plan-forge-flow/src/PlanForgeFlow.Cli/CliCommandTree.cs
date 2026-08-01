namespace PlanForgeFlow;

internal sealed record CliCommandDefinition(string Name, IReadOnlySet<string> Options, string? RequiredMode, bool LoadsState);

internal static class CliCommands
{
    private static readonly IReadOnlySet<string> BooleanOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "cancel", "retry", "relock", "amendment", "full", "accept-risk", "delete-owned-artifacts", "purge-generated-agents", "verification-passed",
    };

    private static readonly IReadOnlyDictionary<string, CliCommandDefinition> Definitions =
        new Dictionary<string, CliCommandDefinition>(StringComparer.Ordinal)
        {
            ["plan start"] = Define("plan start", "plan", false, "workspace", "session-context"),
            ["plan lock"] = Define("plan lock", "default", true, "workspace", "relock", "amendment"),
            ["approval issue"] = Define("approval issue", "plan", false, "workspace"),
            ["approval resume"] = Define("approval resume", "default", false, "workspace"),
            ["agents install"] = Define("agents install", "default", false, "workspace"),
            ["build dispatch"] = Define("build dispatch", "default", true, "workspace", "stage", "task-number", "retry", "cancel", "dispatch-id", "model", "effort", "plan-sha256", "authorization-note", "accept-risk"),
            ["build complete"] = Define("build complete", "default", true, "workspace", "task-number", "dispatch-id", "verification-passed", "authorization-note", "accept-risk"),
            ["build resolve"] = Define("build resolve", "default", true, "workspace", "conflict", "dispatch-id"),
            ["build begin"] = Define("build begin", "default", true, "workspace", "amendment", "relock"),
            ["review prepare"] = Define("review prepare", "default", true, "workspace", "allow-paths", "full", "plan-sha256", "authorization-note"),
            ["review authorize-preexisting"] = Define("review authorize-preexisting", "default", true, "workspace", "authorized-paths", "authorization-note", "accept-risk"),
            ["review verdict"] = Define("review verdict", "default", true, "workspace", "stage", "critique-file", "accept-risk", "authorization-note"),
            ["session builder"] = Define("session builder", "default", true, "workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["session reviewer"] = Define("session reviewer", "default", true, "workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["run doctor"] = Define("run doctor", null, false, "workspace"),
            ["run status"] = Define("run status", null, false, "workspace"),
            ["run set"] = Define("run set", "default", true, "workspace", "key", "value", "amendment", "accept-risk", "authorization-note"),
            ["run cleanup"] = Define("run cleanup", "default", false, "workspace", "delete-owned-artifacts", "purge-generated-agents"),
        };

    public static IReadOnlyCollection<string> Names => Definitions.Keys.ToArray();

    public static bool TryGet(string name, out CliCommandDefinition definition) => Definitions.TryGetValue(name, out definition!);

    public static bool IsBoolean(string option) => BooleanOptions.Contains(option);

    private static CliCommandDefinition Define(string name, string? mode, bool loadsState, params string[] options)
        => new(name, options.ToHashSet(StringComparer.Ordinal), mode, loadsState);
}
