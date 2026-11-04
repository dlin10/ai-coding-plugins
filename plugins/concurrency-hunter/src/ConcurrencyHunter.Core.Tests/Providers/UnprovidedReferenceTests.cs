using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>An assembly the library itself references, which neither a package of the set nor the shared framework gives, is taken
/// from the packages folder: the package whose id is the assembly's name, at the lowest cached version whose lib folder for the
/// platform holds the assembly at a version not below the reference, as that one file (SPEC TD-034b). Nothing is downloaded, and a
/// reference nothing satisfies is left out without a name.</summary>
public sealed class UnprovidedReferenceTests : IDisposable
{
    private const string NET8 = "net8.0";
    private const string EXTRA = "Extra.Lib";

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void Of_three_cached_versions_the_lowest_not_below_the_reference_is_taken_and_only_that_one_file()
    {
        _install.SharedFramework("8.0.1");
        Foo("", Assembly(EXTRA, "2.0.0.0"));
        Cache(EXTRA, "1.0.0", (EXTRA, Assembly(EXTRA, "1.0.0.0")), ("Extra.Other", Assembly("Extra.Other", "1.0.0.0")));
        var taken = Cache(EXTRA, "2.0.0", (EXTRA, Assembly(EXTRA, "2.0.0.0")), ("Extra.Other", Assembly("Extra.Other", "2.0.0.0")));
        Cache(EXTRA, "3.0.0", (EXTRA, Assembly(EXTRA, "3.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(taken, EXTRA)], found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_reference_no_cached_version_reaches_leaves_the_set_without_it_and_names_nothing()
    {
        _install.SharedFramework("8.0.1");
        Foo("", Assembly(EXTRA, "5.0.0.0"));
        Cache(EXTRA, "1.0.0", (EXTRA, Assembly(EXTRA, "1.0.0.0")));
        Cache(EXTRA, "2.0.0", (EXTRA, Assembly(EXTRA, "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Empty(found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_reference_with_no_package_folder_leaves_the_set_without_it_and_names_nothing()
    {
        _install.SharedFramework("8.0.1");
        Foo("", Assembly(EXTRA, "2.0.0.0"));
        Cache("Other.Package", "2.0.0", ("Other.Package", Assembly("Other.Package", "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Empty(found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_name_a_package_dependency_gives_is_not_looked_up()
    {
        // The reference is 1 and the dependency is held at exactly 2: the lookup, were it made, would take the 1 of the folder.
        _install.SharedFramework("8.0.1");
        Foo(Dependency(EXTRA, "[2.0.0]"), Assembly(EXTRA, "1.0.0.0"));
        Cache(EXTRA, "1.0.0", (EXTRA, Assembly(EXTRA, "1.0.0.0")));
        var given = Cache(EXTRA, "2.0.0", (EXTRA, Assembly(EXTRA, "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(given, EXTRA)], found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_name_the_shared_framework_gives_is_not_looked_up()
    {
        var shared = _install.SharedFramework("8.0.1", images: new Dictionary<string, byte[]> { [EXTRA] = Assembly(EXTRA, "2.0.0.0") });
        Foo("", Assembly(EXTRA, "1.0.0.0"));
        Cache(EXTRA, "1.0.0", (EXTRA, Assembly(EXTRA, "1.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Path.Combine(shared, EXTRA + ".dll")], found.References);
    }

    [Fact]
    public void A_lib_folder_of_a_version_without_the_file_or_for_another_platform_passes_to_the_next_version()
    {
        _install.SharedFramework("8.0.1");
        Foo("", Assembly(EXTRA, "2.0.0.0"));
        Cache(EXTRA, "1.0.0", ("Extra.Other", Assembly("Extra.Other", "9.0.0.0")));
        _install.Package(EXTRA, "1.5.0", "", new Dictionary<string, byte[]> { ["net9.0/" + EXTRA] = Assembly(EXTRA, "9.0.0.0") });
        Cache(EXTRA, "1.8.0", (EXTRA, Assembly(EXTRA, "1.0.0.0")));
        var taken = Cache(EXTRA, "2.0.0", (EXTRA, Assembly(EXTRA, "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(taken, EXTRA)], found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_reference_the_dependency_makes_and_not_the_library_is_not_looked_up()
    {
        _install.SharedFramework("8.0.1");
        Foo(Dependency("Dep", "1.0.0"));
        var dep = Cache("Dep", "1.0.0", ("Dep", Assembly("Dep", "1.0.0.0", Assembly(EXTRA, "2.0.0.0"))));
        Cache(EXTRA, "2.0.0", (EXTRA, Assembly(EXTRA, "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(dep, "Dep")], found.References);
        Assert.Empty(found.MissingDependencies);
    }

    [Fact]
    public void A_reference_of_version_zero_takes_the_lowest_cached_version_that_holds_the_file()
    {
        _install.SharedFramework("8.0.1");
        Foo("", Assembly(EXTRA, "0.0.0.0"));
        var taken = Cache(EXTRA, "1.0.0", (EXTRA, Assembly(EXTRA, "1.0.0.0")));
        Cache(EXTRA, "2.0.0", (EXTRA, Assembly(EXTRA, "2.0.0.0")));

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        Assert.Equal([Lib(taken, EXTRA)], found.References);
    }

    [Fact]
    public void The_lookup_checks_for_cancellation_before_it_reads_each_cached_version()
    {
        // Foo and Plain differ in one reference: Foo's, to Extra.Lib 9, no cached version of three reaches.
        _install.SharedFramework("8.0.1");
        Foo("", Assembly(EXTRA, "9.0.0.0"));
        _install.Package("Plain", "1.0.0", "", new Dictionary<string, byte[]> { [$"{NET8}/Plain"] = EmittedAssemblies.Image("Plain") });
        foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
            Cache(EXTRA, version, (EXTRA, Assembly(EXTRA, version + ".0")));
        var withLookup = new List<int>();
        var withoutLookup = new List<int>();

        Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8, new GenerationChecks(CancellationToken.None, (_, ordinal) => withLookup.Add(ordinal))));
        Found(_install.Resolver().Resolve("Plain", "1.0.0", "Plain", NET8, new GenerationChecks(CancellationToken.None, (_, ordinal) => withoutLookup.Add(ordinal))));

        Assert.Equal(withoutLookup.Count + 3, withLookup.Count);
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
    /// <param name="assemblies">The assemblies of the lib folder, by name.</param>
    private string Cache(string id, string version, params (string Name, byte[] Image)[] assemblies) =>
        _install.Package(id, version, "", assemblies.ToDictionary(item => $"{NET8}/{item.Name}", item => item.Image));

    /// <summary>A nuspec <c>&lt;dependencies&gt;</c> element with one dependency.</summary>
    /// <param name="id">The dependency id.</param>
    /// <param name="range">Its version range.</param>
    private static string Dependency(string id, string range) => $"""<dependencies><dependency id="{id}" version="{range}" /></dependencies>""";

    private static string Lib(string package, string assembly) => Path.Combine(package, "lib", NET8, assembly + ".dll");

    private static ImplementationAssembly Found(ImplementationResolution resolution)
    {
        Assert.True(resolution.Reason is null, $"{resolution.Reason}: {resolution.Detail}");
        return resolution.Assembly!;
    }
}
