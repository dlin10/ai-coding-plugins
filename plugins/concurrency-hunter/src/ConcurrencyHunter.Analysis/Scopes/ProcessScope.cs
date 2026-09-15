namespace ConcurrencyHunter.Scopes;

/// <summary>One application as it runs in one OS process (ADR 0005): an executable project and every project it
/// transitively references, or, in a solution without an executable, every C# project under the id
/// <see cref="SOLUTION_SCOPE_ID"/>.</summary>
public sealed record ProcessScope(string Id, string? ExecutableProject, IReadOnlyList<string> Projects)
{
    public const string SOLUTION_SCOPE_ID = "solution";
    public const string NO_EXECUTABLE_DIAGNOSTIC =
        "No executable project was found; the whole solution is analyzed as one process scope.";
}
