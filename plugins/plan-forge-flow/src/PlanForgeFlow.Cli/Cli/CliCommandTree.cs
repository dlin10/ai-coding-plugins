namespace PlanForgeFlow.Cli;

internal sealed record CliCommandDefinition(string Name, IReadOnlySet<string> Options, bool LoadsState);

internal static class CliCommands
{
    private static readonly HashSet<string> BooleanOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "cancel", "retry", "relock", "amendment", "full", "accept-risk", "purge-generated-agents", "verification-passed", "legacy",
    };

    private static readonly IReadOnlyDictionary<string, CliCommandDefinition> Definitions =
        new Dictionary<string, CliCommandDefinition>(StringComparer.Ordinal)
        {
            ["plan lock"] = Define("plan lock", true, "workspace", "relock", "amendment"),
            ["plan materialize"] = Define("plan materialize", false, "workspace", "amendment", "run-id", "plan-file"),
            ["plan stage"] = Define("plan stage", false, "workspace", "run-id", "model", "effort", "cursor-version", "observed-model", "waiver-reason", "accept-risk", "authorization-note", "provider", "requested-model", "resolved-model", "models-used"),
            ["plan finalize"] = Define("plan finalize", false, "workspace", "run-id", "model", "effort", "cursor-version", "observed-model", "waiver-reason", "provider", "requested-model", "resolved-model", "models-used", "builder-hold-id"),
            ["plan abandon"] = Define("plan abandon", false, "workspace", "run-id"),
            ["plan invalidate"] = Define("plan invalidate", false, "workspace", "run-id", "reason"),
            ["agents install"] = Define("agents install", false, "workspace"),
            ["build dispatch"] = Define("build dispatch", true, "workspace", "stage", "task-number", "retry", "cancel", "dispatch-id", "model", "effort", "authorization-note", "accept-risk"),
            ["build complete"] = Define("build complete", true, "workspace", "task-number", "dispatch-id", "verification-passed", "authorization-note", "accept-risk"),
            ["build resolve"] = Define("build resolve", true, "workspace", "conflict", "dispatch-id"),
            ["build begin"] = Define("build begin", true, "workspace", "amendment", "relock"),
            ["review prepare"] = Define("review prepare", true, "workspace", "allow-paths", "full", "authorization-note"),
            ["review authorize-preexisting"] = Define("review authorize-preexisting", true, "workspace", "authorized-paths", "authorization-note", "accept-risk"),
            ["review verdict"] = Define("review verdict", true, "workspace", "stage", "critique-file", "accept-risk", "authorization-note"),
            ["review record-response"] = Define("review record-response", false, "workspace", "run-id", "dispatch-id", "stage"),
            ["session builder"] = Define("session builder", true, "workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["session reviewer"] = Define("session reviewer", true, "workspace", "id", "dispatch-id", "model", "effort", "authorization-note"),
            ["session start"] = Define("session start", false, "workspace", "role", "model", "effort", "thread-id"),
            ["session status"] = Define("session status", false, "workspace", "session-id"),
            ["session result"] = Define("session result", false, "workspace", "session-id"),
            ["session cancel"] = Define("session cancel", false, "workspace", "session-id"),
            ["session worker"] = Define("session worker", false, "workspace", "session-id"),
            ["run doctor"] = Define("run doctor", false, "workspace"),
            ["run status"] = Define("run status", false, "workspace"),
            ["run set"] = Define("run set", true, "workspace", "key", "value", "amendment", "accept-risk", "authorization-note"),
            ["run cleanup"] = Define("run cleanup", false, "workspace", "purge-generated-agents", "legacy"),
        };

    public static IReadOnlyCollection<string> Names => Definitions.Keys.ToArray();

    public static bool TryGet(string name, out CliCommandDefinition definition) => Definitions.TryGetValue(name, out definition!);

    public static bool IsBoolean(string option) => BooleanOptions.Contains(option);

    private static CliCommandDefinition Define(string name, bool loadsState, params string[] options)
        => new(name, options.Append("host").ToHashSet(StringComparer.Ordinal), loadsState);
}
