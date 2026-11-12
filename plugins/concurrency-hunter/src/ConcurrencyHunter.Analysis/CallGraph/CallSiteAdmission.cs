namespace ConcurrencyHunter.CallGraph;

public enum AdmissionRoute { Direct, Dispatch, Delegate, Work, Di, Root, Startup, Receiverless, TypeInitializer, Comparer }

/// <summary>The library bodies that project calls may enter. Every edge is decided independently of earlier reaches.</summary>
/// <param name="AssemblyName">The admitted library's assembly name.</param>
/// <param name="BodyIds">The members with executable bodies admitted as direct entries.</param>
public sealed record CallSiteAdmission(string AssemblyName, IReadOnlySet<string> BodyIds)
{
    /// <summary>Whether a body belongs to the admitted library, including its nested bodies.</summary>
    /// <param name="bodyId">The body, or null for an entry without an origin.</param>
    public bool IsLibraryBody(string? bodyId) => bodyId?.StartsWith($"body:{AssemblyName}:", StringComparison.Ordinal) == true;

    /// <summary>Whether a type belongs to the admitted library.</summary>
    /// <param name="typeKey">The assembly-qualified type key.</param>
    public bool IsLibraryType(string typeKey) => typeKey.StartsWith($"{AssemblyName}:", StringComparison.Ordinal);

    /// <summary>Whether this edge may enter the target's body.</summary>
    /// <param name="origin">The reaching body, or null for roots, startup and DI.</param>
    /// <param name="target">The body being entered.</param>
    /// <param name="route">How the target is reached.</param>
    public bool Admits(string? origin, string target, AdmissionRoute route) =>
        !IsLibraryBody(target) || IsLibraryBody(origin) ||
        origin is not null && route == AdmissionRoute.Direct && BodyIds.Contains(target);
}
