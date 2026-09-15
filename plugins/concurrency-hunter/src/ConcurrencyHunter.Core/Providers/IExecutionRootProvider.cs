using ConcurrencyHunter.Di;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers;

/// <summary>The one extension boundary for discovering framework execution roots. A provider reads the
/// context and returns data; it creates no findings and assigns no severity or confidence.</summary>
public interface IExecutionRootProvider
{
    string ProviderId { get; }
    IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions { get; }
    RootDiscoveryResult Discover(RootDiscoveryContext context);
}

/// <summary>An assembly name with the versions a provider's semantics hold for, maximum exclusive.</summary>
public sealed record SupportedAssemblyVersion(string AssemblyName, Version Minimum, Version MaximumExclusive)
{
    private static readonly Version FRAMEWORK_MINIMUM = new(8, 0, 0, 0);
    private static readonly Version FRAMEWORK_MAXIMUM_EXCLUSIVE = new(11, 0, 0, 0);

    /// <summary>The range every built-in framework provider supports: 8.0.0.0 up to 11.0.0.0 exclusive.</summary>
    public static SupportedAssemblyVersion Framework(string assemblyName) =>
        new(assemblyName, FRAMEWORK_MINIMUM, FRAMEWORK_MAXIMUM_EXCLUSIVE);

    public bool Contains(Version version) => version >= Minimum && version < MaximumExclusive;
}

public sealed record RootDiscoveryContext(string ScopeId, IReadOnlyList<Compilation> Compilations, string RootDirectory,
                                          DiIndex DiIndex, CancellationToken CancellationToken);
