namespace ConcurrencyHunter.Providers;

public sealed class ProviderRegistrationException(string message) : Exception(message);

public sealed class ProviderRegistry
{
    static ProviderRegistry()
    {
        using var outside = WorkCancellation.Outside();
        BuiltIn = new(BuiltInProviders.Create());
    }

    public ProviderRegistry(IEnumerable<IExecutionRootProvider> providers)
    {
        var registered = new List<IExecutionRootProvider>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            WorkCancellation.Check();
            if (string.IsNullOrWhiteSpace(provider.ProviderId))
                throw new ProviderRegistrationException($"Provider {provider.GetType().FullName} has an empty id.");
            if (!ids.Add(provider.ProviderId))
                throw new ProviderRegistrationException($"Provider id '{provider.ProviderId}' is registered twice.");
            if (provider.SupportedAssemblyVersions is not { Count: > 0 })
                throw new ProviderRegistrationException($"Provider '{provider.ProviderId}' declares no supported assembly versions.");

            foreach (var range in provider.SupportedAssemblyVersions)
            {
                WorkCancellation.Check();
                if (string.IsNullOrWhiteSpace(range.AssemblyName))
                    throw new ProviderRegistrationException($"Provider '{provider.ProviderId}' declares a version range with no assembly name.");
                if (range.Minimum >= range.MaximumExclusive)
                {
                    throw new ProviderRegistrationException(
                        $"Provider '{provider.ProviderId}' declares {range.AssemblyName} {range.Minimum} up to " +
                        $"{range.MaximumExclusive}, whose minimum is not below its exclusive maximum.");
                }
            }

            registered.Add(provider);
        }

        Providers = registered;
    }

    public static ProviderRegistry BuiltIn { get; }

    public IReadOnlyList<IExecutionRootProvider> Providers { get; }
}

/// <summary>The composition root: the one place built-in providers are registered.</summary>
internal static class BuiltInProviders
{
    internal static IReadOnlyList<IExecutionRootProvider> Create() => [new AspNetCoreRootProvider(), new HostingRootProvider()];
}
