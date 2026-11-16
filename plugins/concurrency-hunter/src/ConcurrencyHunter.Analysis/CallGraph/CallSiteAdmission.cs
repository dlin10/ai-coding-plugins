using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.CallGraph;

public enum AdmissionRoute { Direct, Dispatch, Delegate, Work, Di, Root, Startup, Receiverless, TypeInitializer, Comparer }

/// <summary>The library bodies that project calls may enter. Every edge is decided independently of earlier reaches.</summary>
/// <param name="AssemblyName">The admitted library's assembly name.</param>
/// <param name="BodyIds">The members with executable bodies admitted as direct entries.</param>
public sealed record CallSiteAdmission(string AssemblyName, IReadOnlySet<string> BodyIds)
{
    /// <summary>The admitted program calls and the target body of each. Null retains the assembly-wide probe input.</summary>
    public IReadOnlyDictionary<CallIdentity, string>? Calls { get; init; }

    /// <summary>Creates admission for individual calls across several libraries.</summary>
    /// <param name="calls">The selected calls with their executable target bodies.</param>
    public CallSiteAdmission(IReadOnlyDictionary<CallIdentity, string> calls)
        : this(string.Empty, calls.Values.ToHashSet(StringComparer.Ordinal)) => Calls = calls;

    private IEnumerable<string> Assemblies => AssemblyName.Length != 0 ? [AssemblyName] : BodyIds.Select(AssemblyOf).Distinct(StringComparer.Ordinal);

    private static string AssemblyOf(string bodyId) => bodyId.Split(':', 3)[1];

    /// <summary>The admission of a root member and its nested bodies, for the lowering cache.</summary>
    /// <param name="bodyId">The root member.</param>
    public string CacheKey(string bodyId) => Calls is null ? $"assembly:{AssemblyName}" :
        string.Join("|", Calls.Where(pair => pair.Key.BodyId == bodyId || pair.Key.BodyId.StartsWith(bodyId + "#", StringComparison.Ordinal))
                              .Select(pair => $"{pair.Key.BodyId}:{pair.Key.Span}:{pair.Key.Target}:{pair.Key.Ordinal}:{pair.Value}")
                              .Order(StringComparer.Ordinal));

    /// <summary>Whether a body belongs to the admitted library, including its nested bodies.</summary>
    /// <param name="bodyId">The body, or null for an entry without an origin.</param>
    public bool IsLibraryBody(string? bodyId) => bodyId is not null && Assemblies.Any(assembly => bodyId.StartsWith($"body:{assembly}:", StringComparison.Ordinal));

    /// <summary>Whether a type belongs to the admitted library.</summary>
    /// <param name="typeKey">The assembly-qualified type key.</param>
    public bool IsLibraryType(string typeKey) => Assemblies.Any(assembly => typeKey.StartsWith($"{assembly}:", StringComparison.Ordinal));

    /// <summary>Whether this edge may enter the target's body.</summary>
    /// <param name="origin">The reaching body, or null for roots, startup and DI.</param>
    /// <param name="target">The body being entered.</param>
    /// <param name="route">How the target is reached.</param>
    /// <param name="call">The reaching call, when the edge is from a call operation.</param>
    public bool Admits(string? origin, string target, AdmissionRoute route, CallIdentity? call = null) =>
        !IsLibraryBody(target) || IsLibraryBody(origin) && AssemblyOf(origin!) == AssemblyOf(target) ||
        origin is not null && route == AdmissionRoute.Direct &&
        (Calls is null ? BodyIds.Contains(target) : call is not null && call.BodyId == origin && Calls.GetValueOrDefault(call) == target);
}
