namespace PlanForge.Repo;

internal static class GitPathspec
{
    // These exclusions match by name at any depth because no caller declares where the
    // documentation lives. `plugins/plan-forge-flow/` in this repository is why a root-anchored
    // rule would not do.
    internal static readonly string[] WithoutDocumentation =
    [
        ".",
        ":(exclude)CONTEXT.md",
        ":(exclude)**/CONTEXT.md",
        ":(exclude)docs/adr/**",
        ":(exclude)**/docs/adr/**"
    ];
}
