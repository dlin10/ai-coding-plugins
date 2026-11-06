using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The rank of a generation target's implementation assembly among the files it is compiled against (ADR 0032), over emitted
/// assemblies in a fake shared framework and packages folder.</summary>
public sealed class AssemblyRanksTests : IDisposable
{
    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void An_assembly_ranks_one_above_the_highest_assembly_it_references()
    {
        var c = EmittedAssemblies.Image("Rank.C");
        var b = EmittedAssemblies.Image("Rank.B", references: [c]);
        var a = EmittedAssemblies.Image("Rank.A", references: [b, c]);
        _install.SharedFramework("8.0.5", images: new Dictionary<string, byte[]> { ["Rank.A"] = a, ["Rank.B"] = b, ["Rank.C"] = c });
        var ranks = new AssemblyRanks(_install.Resolver());

        // Rank.C references only System.Runtime, which the framework folder does not hold: no edge.
        Assert.Equal((2, 1, 0), (Of(ranks, "Rank.A"), Of(ranks, "Rank.B"), Of(ranks, "Rank.C")));
    }

    [Fact]
    public void Assemblies_that_reference_each_other_share_one_rank()
    {
        var c = EmittedAssemblies.Image("Cycle.C");
        var stub = EmittedAssemblies.Image("Cycle.B");
        var a = EmittedAssemblies.Image("Cycle.A", references: [stub]);
        var b = EmittedAssemblies.Image("Cycle.B", references: [a, c]);
        var d = EmittedAssemblies.Image("Cycle.D", references: [a]);
        _install.SharedFramework("8.0.5", images: new Dictionary<string, byte[]> { ["Cycle.A"] = a, ["Cycle.B"] = b, ["Cycle.C"] = c, ["Cycle.D"] = d });
        var ranks = new AssemblyRanks(_install.Resolver());

        // Cycle.A and Cycle.B reference each other and Cycle.C below them; Cycle.D references the cycle. Asked from either end, the cycle has
        // the same rank.
        Assert.Equal(2, Of(ranks, "Cycle.D"));
        Assert.Equal((1, 1, 0), (Of(ranks, "Cycle.B"), Of(ranks, "Cycle.A"), Of(ranks, "Cycle.C")));
    }

    [Fact]
    public void A_package_ranks_above_the_framework_assembly_it_references()
    {
        var framework = EmittedAssemblies.Image("Ranked.Framework");
        _install.SharedFramework("8.0.5", images: new Dictionary<string, byte[]> { ["Ranked.Framework"] = framework });
        _install.Package("Ranked.Package", "1.0.0", "", new Dictionary<string, byte[]>
        {
            ["net8.0/Ranked.Package"] = EmittedAssemblies.Image("Ranked.Package", references: [framework])
        });
        var ranks = new AssemblyRanks(_install.Resolver());

        Assert.Equal(1, ranks.Of(new GenerationRequest("Ranked.Package", "1.0.0", "M:X.Y", "Ranked.Package", "net8.0"), CancellationToken.None));
        Assert.Equal(0, Of(ranks, "Ranked.Framework"));
    }

    [Fact]
    public void An_implementation_the_resolver_does_not_open_ranks_zero()
    {
        _install.SharedFramework("8.0.5", ["Present"]);
        var ranks = new AssemblyRanks(_install.Resolver());

        Assert.Equal(0, Of(ranks, "Absent"));
        Assert.Equal(0, ranks.Of(new GenerationRequest("System.Private.CoreLib", "8.0", "M:X.Y", null, null), CancellationToken.None));
    }

    /// <summary>The rank of a framework assembly of .NET 8.</summary>
    /// <param name="ranks">The ranks.</param>
    /// <param name="assembly">The assembly name.</param>
    private static int Of(AssemblyRanks ranks, string assembly) =>
        ranks.Of(new GenerationRequest(assembly, "8.0", "M:X.Y", null, null), CancellationToken.None);
}
