using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>A dependency is taken at the lowest cached version that every range admits and whose assemblies are not older than their
/// floor: the highest version with which an assembly of the set — the library, the assemblies of the dependencies taken and of the
/// shared frameworks — references them (SPEC TD-034b). The floors only rise across the rounds of one resolution.</summary>
public sealed class ReferenceFloorTests : IDisposable
{
    private const string NET8 = "net8.0";

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void A_floor_lifts_a_dependency_and_the_closure_follows_the_version_lifted_to()
    {
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("A", "[1.0.0, )")), Assembly("A", "2.0.0.0"));
        Cache("A", "1.0.0", Dependencies(("X", "1.0.0")), ("A", Assembly("A", "1.0.0.0")));
        var lifted = Cache("A", "2.0.0", Dependencies(("Y", "1.0.0")), ("A", Assembly("A", "2.0.0.0")));
        Cache("X", "1.0.0", "", ("X", Assembly("X", "1.0.0.0")));
        var y = Cache("Y", "1.0.0", "", ("Y", Assembly("Y", "1.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(lifted, "A"), Lib(y, "Y")], found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Theory]
    [InlineData("0.0.0.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("2.0.0.0", "2.0.0")]
    public void A_reference_of_version_zero_raises_no_floor_and_the_lowest_version_stays(string referenced, string taken)
    {
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("A", "[1.0.0, )")), Assembly("A", referenced));
        var versions = Versions("A", "1.0.0", "2.0.0");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(versions[taken], "A")], found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Theory]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("3.0.0.0", "9.0.0")]
    public void A_copy_that_lost_the_duplicate_to_the_framework_gives_no_floor_and_the_copy_that_won_does(string packageCopy, string taken)
    {
        // D carries Shared and so does the framework, at 2.0.0.0: a package copy below it loses and one above it wins. Only the copy
        // that is taken gives its reference to Z 9 — 9.0.0.0 — as a floor.
        var shared = _install.SharedFramework("8.0.1", images: new Dictionary<string, byte[]> { ["Shared"] = Assembly("Shared", "2.0.0.0") });
        Foo(Dependencies(("D", "1.0.0"), ("Z", "[1.0.0, )")));
        var d = Cache("D", "1.0.0", "", ("D", Assembly("D", "1.0.0.0")), ("Shared", Assembly("Shared", packageCopy, Assembly("Z", "9.0.0.0"))));
        var versions = Versions("Z", "1.0.0", "9.0.0");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Contains(Lib(versions[taken], "Z"), found.References);
        Assert.Single(found.References, reference => Path.GetFileName(reference) == "Z.dll");
        Assert.Equal(packageCopy == "3.0.0.0", found.References.Contains(Lib(d, "Shared")));
        Assert.Equal(packageCopy != "3.0.0.0", found.References.Contains(Path.Combine(shared, "Shared.dll")));
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void Two_sources_of_different_versions_set_the_floor_by_the_higher()
    {
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("A", "[1.0.0, )"), ("D", "1.0.0")), Assembly("A", "2.0.0.0"));
        Cache("D", "1.0.0", "", ("D", Assembly("D", "1.0.0.0", Assembly("A", "3.0.0.0"))));
        var versions = Versions("A", "1.0.0", "2.0.0", "3.0.0", "4.0.0");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Single(found.References, reference => Path.GetFileName(reference) == "A.dll");
        Assert.Contains(Lib(versions["3.0.0"], "A"), found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_floor_stays_raised_when_the_assembly_that_raised_it_leaves_the_set()
    {
        // The library references A 2 and B 1. The lowest A, 1, references B 5, so B is lifted to 5; lifting A to 2 drops that reference,
        // and the floor of B stays at 5 — the set of A 2 and B 1 would be the first round's again.
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("A", "[1.0.0, )"), ("B", "[1.0.0, )")), Assembly("A", "2.0.0.0"), Assembly("B", "1.0.0.0"));
        Cache("A", "1.0.0", "", ("A", Assembly("A", "1.0.0.0", Assembly("B", "5.0.0.0"))));
        var a2 = Cache("A", "2.0.0", "", ("A", Assembly("A", "2.0.0.0", Assembly("B", "1.0.0.0"))));
        var versions = Versions("B", "1.0.0", "3.0.0", "5.0.0");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(a2, "A"), Lib(versions["5.0.0"], "B")], found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_floor_raised_by_a_name_no_dependency_holds_takes_one_round_and_reads_the_same_nuspecs()
    {
        _install.SharedFramework("8.0.1");
        var dependencies = Dependencies(("Bar", "1.0.0"), ("Baz", "1.0.0"));
        Foo(dependencies, Assembly("Unheld", "5.0.0.0"));
        _install.Package("Plain", "1.0.0", dependencies, new Dictionary<string, byte[]> { [$"{NET8}/Plain"] = EmittedAssemblies.Image("Plain") });
        Cache("Bar", "1.0.0", "", ("Bar", Assembly("Bar", "1.0.0.0")));
        Cache("Baz", "1.0.0", Dependencies(("Qux", "1.0.0")), ("Baz", Assembly("Baz", "1.0.0.0")));
        Cache("Qux", "1.0.0", "", ("Qux", Assembly("Qux", "1.0.0.0")));
        var raised = new List<string>();
        var plain = new List<string>();

        var found = Found(_install.Resolver(nuspecRead: raised.Add).Resolve("Foo", "1.0.0", "Foo", NET8));
        Found(_install.Resolver(nuspecRead: plain.Add).Resolve("Plain", "1.0.0", "Plain", NET8));

        Assert.Equal(4, raised.Count);
        Assert.Equal(plain.Count, raised.Count);
        Assert.Equal(plain.Skip(1).Select(Path.GetFileName), raised.Skip(1).Select(Path.GetFileName));
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_chain_of_lifts_past_the_round_bound_stops_at_the_last_round_and_names_the_dependency_below_the_floor()
    {
        // Version k of D holds A at k and B, which references A at k + 1: each round lifts D one version, because the floor of A rises
        // to the version the set just taken references. Rounds run out at CLOSURE_ROUNDS, with the set of the last one.
        const int LAST = ImplementationAssemblies.CLOSURE_ROUNDS;
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("D", "[1.0.0, )")));
        var versions = Enumerable.Range(1, LAST + 2)
                                 .ToDictionary(version => version,
                                               version => Cache("D", $"{version}.0.0", "", ("A", Assembly("A", $"{version}.0.0.0")),
                                                                ("B", Assembly("B", "1.0.0.0", Assembly("A", $"{version + 1}.0.0.0")))));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([$"D@{LAST}.0.0: A older than {LAST + 1}.0.0.0"], found.MissingDependencies);
        Assert.Contains(Lib(versions[LAST], "A"), found.References);
        Assert.DoesNotContain(Lib(versions[LAST + 1], "A"), found.References);
        Assert.Single(found.References, reference => Path.GetFileName(reference) == "A.dll");
    }

    [Fact]
    public void When_no_cached_version_reaches_the_floor_the_lowest_admitted_stays_in_the_set_and_is_named_with_it()
    {
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("A", "[1.0.0, )")), Assembly("A", "3.0.0.0"));
        var a1 = Cache("A", "1.0.0", "", ("A", Assembly("A", "1.0.0.0")));
        Cache("A", "2.0.0", "", ("A", Assembly("A", "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal(["A@1.0.0: A older than 3.0.0.0"], found.MissingDependencies);
        Assert.Equal([Lib(a1, "A")], found.References);
    }

    [Fact]
    public void A_version_outside_the_range_is_never_taken_to_reach_a_floor()
    {
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("A", "[1.0.0, 2.0.0)")), Assembly("A", "2.0.0.0"));
        var a1 = Cache("A", "1.0.0", "", ("A", Assembly("A", "1.0.0.0")));
        Cache("A", "2.0.0", "", ("A", Assembly("A", "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal(["A@1.0.0: A older than 2.0.0.0"], found.MissingDependencies);
        Assert.Equal([Lib(a1, "A")], found.References);
    }

    [Fact]
    public void A_version_without_a_compatible_lib_folder_reaches_every_floor()
    {
        _install.SharedFramework("8.0.1");
        Foo(Dependencies(("A", "[1.0.0, )")), Assembly("A", "2.0.0.0"));
        Cache("A", "1.0.0", "", ("A", Assembly("A", "1.0.0.0")));
        _install.Package("A", "2.0.0", "", "net461/A");

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal(["A@2.0.0: no compatible lib folder"], found.MissingDependencies);
        Assert.DoesNotContain(found.References, reference => Path.GetFileName(reference) == "A.dll");
    }

    /// <summary>An emitted assembly of a version, referencing the given assemblies.</summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="version">Its assembly version.</param>
    /// <param name="references">The assemblies it references, each with the version it is emitted at.</param>
    private static byte[] Assembly(string name, string version, params byte[][] references) =>
        EmittedAssemblies.Image(name, version: version, references: references);

    /// <summary>The package Foo 1.0.0 whose assembly references the given assemblies.</summary>
    /// <param name="dependencies">The nuspec's <c>&lt;dependencies&gt;</c> element, or empty.</param>
    /// <param name="references">The assemblies Foo references.</param>
    private void Foo(string dependencies, params byte[][] references) =>
        _install.Package("Foo", "1.0.0", dependencies,
                         new Dictionary<string, byte[]> { [$"{NET8}/Foo"] = EmittedAssemblies.Image("Foo", references: references) });

    /// <summary>A cached package whose lib folder for net8.0 holds the given assemblies.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version folder.</param>
    /// <param name="dependencies">The nuspec's <c>&lt;dependencies&gt;</c> element, or empty.</param>
    /// <param name="assemblies">The assemblies of the lib folder, by name.</param>
    private string Cache(string id, string version, string dependencies, params (string Name, byte[] Image)[] assemblies) =>
        _install.Package(id, version, dependencies, assemblies.ToDictionary(item => $"{NET8}/{item.Name}", item => item.Image));

    /// <summary>Cached versions of a package, each with a net8.0 lib folder holding one assembly of the package's name at the version
    /// of the folder.</summary>
    /// <param name="id">The package id, which is also the assembly name.</param>
    /// <param name="versions">The version folders, three numeric parts each.</param>
    private Dictionary<string, string> Versions(string id, params string[] versions) =>
        versions.ToDictionary(version => version, version => Cache(id, version, "", (id, Assembly(id, version + ".0"))));

    /// <summary>A nuspec <c>&lt;dependencies&gt;</c> element.</summary>
    /// <param name="items">The dependencies, each with its id and version range.</param>
    private static string Dependencies(params (string Id, string Range)[] items) =>
        "<dependencies>" + string.Concat(items.Select(item => $"""<dependency id="{item.Id}" version="{item.Range}" />""")) + "</dependencies>";

    private static string Lib(string package, string assembly) => Path.Combine(package, "lib", NET8, assembly + ".dll");

    private static ImplementationAssembly Found(ImplementationResolution resolution)
    {
        Assert.True(resolution.Reason is null, $"{resolution.Reason}: {resolution.Detail}");
        return resolution.Assembly!;
    }
}
