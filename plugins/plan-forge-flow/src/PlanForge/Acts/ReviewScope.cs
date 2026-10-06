using PlanForge.Run;

namespace PlanForge.Acts;

/// <summary>
/// What the orchestrator asked of one code-review round's window: paths it leaves out, and whether
/// untracked files reach the critic by path rather than by content. Neither outlives the call, so a
/// round that should keep them asks again. See docs/adr/0029.
/// </summary>
/// <param name="ExcludedPaths">Git pathspec patterns, relative to the workspace, the round leaves out.</param>
/// <param name="UntrackedByReference">Whether untracked files are listed for the critic to read instead of embedded.</param>
internal sealed record ReviewScope(IReadOnlyList<string> ExcludedPaths, bool UntrackedByReference)
{
    /// <summary>The whole window, every file embedded: what a round gets when it asks for nothing.</summary>
    public static ReviewScope Whole { get; } = new([], false);

    /// <summary>
    /// The scope a call's arguments ask for. A path opening with <c>:</c> is refused, because git
    /// would read it as pathspec magic and this call adds its own <c>:(exclude)</c> in front.
    /// </summary>
    /// <param name="excludePaths">The paths and patterns to leave out, or null for none.</param>
    /// <param name="untrackedByReference">Whether untracked files are given by path.</param>
    public static ReviewScope From(string[]? excludePaths, bool untrackedByReference)
    {
        foreach (var path in excludePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentRejectedException("excludePaths holds an empty path");
            if (path.StartsWith(':'))
                throw new ArgumentRejectedException($"excludePaths takes paths and patterns, not pathspec magic: '{path}'");
        }

        return new ReviewScope(excludePaths ?? [], untrackedByReference);
    }
}
