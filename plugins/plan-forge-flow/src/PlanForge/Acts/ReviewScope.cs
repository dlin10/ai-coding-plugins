using PlanForge.Run;

namespace PlanForge.Acts;

/// <summary>
/// The paths the orchestrator asked one code-review round to leave out of its window. They do not
/// outlive the call, so a round that should keep them asks again. How the window travels is the
/// server's choice, not the scope's. See docs/adr/0029 and docs/adr/0034.
/// </summary>
/// <param name="ExcludedPaths">Git pathspec patterns, relative to the workspace, the round leaves out.</param>
internal sealed record ReviewScope(IReadOnlyList<string> ExcludedPaths)
{
    /// <summary>The whole window: what a round gets when it asks for nothing.</summary>
    public static ReviewScope Whole { get; } = new([]);

    /// <summary>
    /// The scope a call's arguments ask for. A path opening with <c>:</c> is refused, because git
    /// would read it as pathspec magic and this call adds its own <c>:(exclude)</c> in front.
    /// </summary>
    /// <param name="excludePaths">The paths and patterns to leave out, or null for none.</param>
    public static ReviewScope From(string[]? excludePaths)
    {
        foreach (var path in excludePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentRejectedException("excludePaths holds an empty path");
            if (path.StartsWith(':'))
                throw new ArgumentRejectedException($"excludePaths takes paths and patterns, not pathspec magic: '{path}'");
        }

        return new ReviewScope(excludePaths ?? []);
    }
}
