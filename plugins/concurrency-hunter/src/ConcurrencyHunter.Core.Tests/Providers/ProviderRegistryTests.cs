using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class ProviderRegistryTests
{
    private static readonly SupportedAssemblyVersion Range = SupportedAssemblyVersion.Framework("Microsoft.AspNetCore.Mvc.Core");

    [Fact]
    public void Built_in_registry_comes_from_the_composition_root_with_aspnetcore_then_hosting()
    {
        Assert.Same(ProviderRegistry.BuiltIn, ProviderRegistry.BuiltIn);
        Assert.Equal(["aspnetcore", "hosting"], ProviderRegistry.BuiltIn.Providers.Select(provider => provider.ProviderId));
    }

    [Fact]
    public void Valid_providers_are_registered_in_order()
    {
        var first = new StubProvider("first", Range);
        var second = new StubProvider("second", Range, new SupportedAssemblyVersion("Other", new Version(1, 0), new Version(2, 0)));

        var registry = new ProviderRegistry([first, second]);

        Assert.Equal([first, second], registry.Providers);
    }

    [Fact]
    public void Empty_or_blank_provider_id_is_refused()
    {
        Assert.Throws<ProviderRegistrationException>(() => new ProviderRegistry([new StubProvider("", Range)]));
        Assert.Throws<ProviderRegistrationException>(() => new ProviderRegistry([new StubProvider("  ", Range)]));
    }

    [Fact]
    public void Duplicate_provider_id_is_refused()
    {
        var exception = Assert.Throws<ProviderRegistrationException>(() =>
            new ProviderRegistry([new StubProvider("aspnetcore", Range), new StubProvider("aspnetcore", Range)]));

        Assert.Contains("aspnetcore", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_with_no_version_ranges_is_refused()
    {
        Assert.Throws<ProviderRegistrationException>(() => new ProviderRegistry([new StubProvider("hosting")]));
    }

    [Fact]
    public void Version_range_with_an_empty_assembly_name_is_refused()
    {
        Assert.Throws<ProviderRegistrationException>(() =>
            new ProviderRegistry([new StubProvider("hosting", Range with { AssemblyName = "" })]));
    }

    [Fact]
    public void Version_range_whose_minimum_is_not_below_its_exclusive_maximum_is_refused()
    {
        Assert.Throws<ProviderRegistrationException>(() =>
            new ProviderRegistry([new StubProvider("equal", Range with { Minimum = Range.MaximumExclusive })]));
        Assert.Throws<ProviderRegistrationException>(() =>
            new ProviderRegistry([new StubProvider("inverted", Range with { Minimum = new Version(12, 0, 0, 0) })]));
    }

    [Fact]
    public void Framework_range_is_8_up_to_11_exclusive()
    {
        Assert.Equal(new Version(8, 0, 0, 0), Range.Minimum);
        Assert.Equal(new Version(11, 0, 0, 0), Range.MaximumExclusive);
        Assert.False(Range.Contains(new Version(7, 0, 0, 0)));
        Assert.True(Range.Contains(new Version(8, 0, 0, 0)));
        Assert.True(Range.Contains(new Version(10, 0, 0, 0)));
        Assert.False(Range.Contains(new Version(11, 0, 0, 0)));
    }

    private sealed class StubProvider(string providerId, params SupportedAssemblyVersion[] ranges) : IExecutionRootProvider
    {
        public string ProviderId => providerId;
        public IReadOnlyList<SupportedAssemblyVersion> SupportedAssemblyVersions => ranges;
        public RootDiscoveryResult Discover(RootDiscoveryContext context) => new(RootDiscoveryStatus.Checked, [], []);
    }
}
