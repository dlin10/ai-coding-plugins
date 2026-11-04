using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The two rules that decide which copy of an assembly a library's reference set holds, each over every combination of what
/// decides it (SPEC TD-034b). The conflict cells (R3) cross the assembly version and the file version of a package's copy of an
/// assembly against the shared framework's copy, with the framework that holds it. The floor cells (R4) cross the standing of the
/// lowest admitted version of a dependency against a floor, a higher admitted version that does or does not reach the floor, the
/// assembly that references the dependency at the floor, and whether the lowest admitted version has a compatible lib folder. The
/// list of cells that stand for a broader set is empty: neither rule has a safe side, so every cell is exact. Several sources of a
/// floor, a source that leaves the set, new rounds and the bound of the rounds are <see cref="ReferenceFloorTests"/>', outside the
/// axes.</summary>
public sealed class ReferenceSetMatrixTests : IDisposable
{
    private const string NET8 = "net8.0";
    private const string ASPNETCORE_APP = "Microsoft.AspNetCore.App";

    /// <summary>The version with which the library, a package's assembly or a framework's assembly references the dependency in a floor
    /// cell: the floor.</summary>
    private const string FLOOR = "3.0.0.0";

    private enum Relation { Older, Equal, Newer }
    private enum FrameworkOfCopy { NetCoreApp, AspNetCoreApp }
    private enum Winner { Package, Framework }
    private enum Standing { Below, Equal, Above }
    private enum HigherVersion { Reaches, FallsShort }
    private enum FloorSource { Library, OtherPackage, Framework }
    private enum LibFolder { Compatible, Incompatible }

    /// <summary>A cell of the conflict matrix.</summary>
    /// <param name="AssemblyVersion">The package copy's assembly version against the framework copy's.</param>
    /// <param name="FileVersion">The package copy's file version against the framework copy's.</param>
    /// <param name="Framework">The shared framework that holds the framework copy.</param>
    private sealed record ConflictCell(Relation AssemblyVersion, Relation FileVersion, FrameworkOfCopy Framework);

    /// <summary>A cell of the floor matrix.</summary>
    /// <param name="Lowest">The assembly of the lowest admitted version of the dependency against the floor.</param>
    /// <param name="Higher">Whether a higher admitted version holds an assembly not older than the floor.</param>
    /// <param name="Source">What references the dependency at the floor.</param>
    /// <param name="Lib">Whether the lowest admitted version has a lib folder compatible with the platform.</param>
    private sealed record FloorCell(Standing Lowest, HigherVersion Higher, FloorSource Source, LibFolder Lib);

    /// <summary>What a floor cell resolves to.</summary>
    /// <param name="TakenFrom">The cached version of the dependency whose assembly is in the set, or <c>null</c> for none.</param>
    /// <param name="Missing">The entry of the missing dependencies, or <c>null</c> for none.</param>
    private sealed record FloorExpectation(string? TakenFrom, string? Missing);

    private static readonly ConflictCell[] Conflicts = (from assembly in Enum.GetValues<Relation>()
                                                        from file in Enum.GetValues<Relation>()
                                                        from framework in Enum.GetValues<FrameworkOfCopy>()
                                                        select new ConflictCell(assembly, file, framework)).ToArray();

    private static readonly FloorCell[] Floors = (from lowest in Enum.GetValues<Standing>()
                                                  from higher in Enum.GetValues<HigherVersion>()
                                                  from source in Enum.GetValues<FloorSource>()
                                                  from lib in Enum.GetValues<LibFolder>()
                                                  select new FloorCell(lowest, higher, source, lib)).ToArray();

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    public static IEnumerable<object[]> ConflictRows() =>
        Conflicts.Select(cell => new object[] { cell.AssemblyVersion.ToString(), cell.FileVersion.ToString(), cell.Framework.ToString() });

    public static IEnumerable<object[]> FloorRows() =>
        Floors.Select(cell => new object[] { cell.Lowest.ToString(), cell.Higher.ToString(), cell.Source.ToString(), cell.Lib.ToString() });

    [Theory]
    [MemberData(nameof(ConflictRows))]
    public void Every_conflict_cell_is_exact(string assemblyVersion, string fileVersion, string framework)
    {
        var cell = new ConflictCell(Enum.Parse<Relation>(assemblyVersion), Enum.Parse<Relation>(fileVersion), Enum.Parse<FrameworkOfCopy>(framework));
        // The framework's copy is at 2.0.0.0 in both versions; the package's copy is older, equal or newer than that.
        var frameworkCopy = new Dictionary<string, byte[]> { ["Shared"] = EmittedAssemblies.Image("Shared", version: "2.0.0.0", fileVersion: "2.0.0.0") };
        var web = cell.Framework == FrameworkOfCopy.AspNetCoreApp;
        string frameworkDirectory;
        if (web)
        {
            _install.SharedFramework("8.0.1");
            frameworkDirectory = _install.AspNetCoreFramework("8.0.1", images: frameworkCopy);
        }
        else
            frameworkDirectory = _install.SharedFramework("8.0.1", images: frameworkCopy);
        var package = _install.Package("Foo", "1.0.0", web ? GenerationInstall.FrameworkReferences(NET8, ASPNETCORE_APP) : "",
                                       new Dictionary<string, byte[]>
                                       {
                                           [$"{NET8}/Foo"] = EmittedAssemblies.Image("Foo"),
                                           [$"{NET8}/Shared"] = EmittedAssemblies.Image("Shared", version: Compared(cell.AssemblyVersion),
                                                                                        fileVersion: Compared(cell.FileVersion))
                                       });

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        var taken = Expected(cell) == Winner.Package ? Lib(package, "Shared") : Path.Combine(frameworkDirectory, "Shared.dll");
        Assert.Equal(new[] { taken }, found.References.Where(reference => Path.GetFileName(reference) == "Shared.dll").ToArray());
        Assert.Empty(found.MissingDependencies);
    }

    [Theory]
    [MemberData(nameof(FloorRows))]
    public void Every_floor_cell_is_exact(string lowest, string higher, string source, string lib)
    {
        var cell = new FloorCell(Enum.Parse<Standing>(lowest), Enum.Parse<HigherVersion>(higher), Enum.Parse<FloorSource>(source), Enum.Parse<LibFolder>(lib));
        var floor = Assembly("A", FLOOR);
        _install.SharedFramework("8.0.1", images: cell.Source == FloorSource.Framework
                                     ? new Dictionary<string, byte[]> { ["Fw"] = Assembly("Fw", "1.0.0.0", floor) }
                                     : null);
        if (cell.Source == FloorSource.OtherPackage)
            _install.Package("D", "1.0.0", "", new Dictionary<string, byte[]> { [$"{NET8}/D"] = Assembly("D", "1.0.0.0", floor) });
        // The higher version holds an assembly below the floor in a cell that falls short, so that taking the highest version differs
        // from taking the lowest one that reaches the floor.
        var versions = new Dictionary<string, string>
        {
            ["1.0.0"] = _install.Package("A", "1.0.0", "", new Dictionary<string, byte[]>
                                                           {
                                                               [$"{(cell.Lib == LibFolder.Compatible ? NET8 : "net461")}/A"] = Assembly("A", Held(cell.Lowest))
                                                           }),
            ["2.0.0"] = _install.Package("A", "2.0.0", "", new Dictionary<string, byte[]>
                                                           {
                                                               [$"{NET8}/A"] = Assembly("A", cell.Higher == HigherVersion.Reaches ? "5.0.0.0" : "2.0.0.0")
                                                           })
        };
        _install.Package("Foo", "1.0.0", cell.Source == FloorSource.OtherPackage ? Dependencies(("A", "[1.0.0, )"), ("D", "1.0.0"))
                                                                                    : Dependencies(("A", "[1.0.0, )")),
                         new Dictionary<string, byte[]>
                         {
                             [$"{NET8}/Foo"] = EmittedAssemblies.Image("Foo", references: cell.Source == FloorSource.Library ? new[] { floor } : [])
                         });

        var found = Found(_install.Resolver().Resolve("Foo", "1.0.0", "Foo", NET8));

        var expected = Expected(cell);
        Assert.Equal(new[] { expected.Missing }.OfType<string>().ToArray(), found.MissingDependencies.ToArray());
        Assert.Equal(new[] { expected.TakenFrom }.OfType<string>().Select(version => Lib(versions[version], "A")).ToArray(),
                     found.References.Where(reference => Path.GetFileName(reference) == "A.dll").ToArray());
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        Assert.Equal(18, Conflicts.Length);
        Assert.Equal(18, Conflicts.Distinct().Count());
        Assert.Equal(18, ConflictRows().Count());
        Assert.Equal(Enum.GetValues<Relation>(), Conflicts.Select(cell => cell.AssemblyVersion).Distinct().Order());
        Assert.Equal(Enum.GetValues<Relation>(), Conflicts.Select(cell => cell.FileVersion).Distinct().Order());
        Assert.Equal(Enum.GetValues<FrameworkOfCopy>(), Conflicts.Select(cell => cell.Framework).Distinct().Order());
        Assert.Equal(36, Floors.Length);
        Assert.Equal(36, Floors.Distinct().Count());
        Assert.Equal(36, FloorRows().Count());
        Assert.Equal(Enum.GetValues<Standing>(), Floors.Select(cell => cell.Lowest).Distinct().Order());
        Assert.Equal(Enum.GetValues<HigherVersion>(), Floors.Select(cell => cell.Higher).Distinct().Order());
        Assert.Equal(Enum.GetValues<FloorSource>(), Floors.Select(cell => cell.Source).Distinct().Order());
        Assert.Equal(Enum.GetValues<LibFolder>(), Floors.Select(cell => cell.Lib).Distinct().Order());
    }

    /// <summary>R3 for a conflict cell: which copy of the assembly the set holds.</summary>
    /// <param name="cell">The cell.</param>
    private static Winner Expected(ConflictCell cell)
    {
        // R3: the copy of the higher assembly version is taken whatever its file version, and whichever shared framework holds the other.
        if (cell.AssemblyVersion != Relation.Equal)
            return cell.AssemblyVersion == Relation.Newer ? Winner.Package : Winner.Framework;
        // R3: equal assembly versions go to the higher file version; equal file versions too go to the framework's copy.
        return cell.FileVersion == Relation.Newer ? Winner.Package : Winner.Framework;
    }

    /// <summary>R4 and R5 for a floor cell: which version of the dependency has its assembly in the set, and what the missing
    /// dependencies name.</summary>
    /// <param name="cell">The cell.</param>
    private static FloorExpectation Expected(FloorCell cell)
    {
        // R4: a version without a compatible lib folder satisfies every floor: it is the lowest, named "no compatible lib folder", and
        // gives the set no assembly. R5: the library's own reference to the assembly, which the set then lacks, is given by the lowest
        // cached version whose compatible lib folder holds the assembly not older than the reference.
        if (cell.Lib == LibFolder.Incompatible)
            return new(cell.Source == FloorSource.Library && cell.Higher == HigherVersion.Reaches ? "2.0.0" : null, "A@1.0.0: no compatible lib folder");
        // R4: an assembly of the lowest version that is equal to the floor or above it reaches it: that version stays, with no entry,
        // whatever the versions above it.
        if (cell.Lowest != Standing.Below)
            return new("1.0.0", null);
        // R4: an assembly below the floor gives way to the lowest higher version whose assembly is not below it, with no entry.
        if (cell.Higher == HigherVersion.Reaches)
            return new("2.0.0", null);
        // R4: when no admitted version reaches the floor the lowest stays in the set and is named with the floor.
        return new("1.0.0", $"A@1.0.0: A older than {FLOOR}");
    }

    /// <summary>The version of the package copy that stands in a relation to the framework copy's, 2.0.0.0.</summary>
    /// <param name="relation">The package copy against the framework copy.</param>
    private static string Compared(Relation relation) => relation switch
    {
        Relation.Older => "1.0.0.0",
        Relation.Equal => "2.0.0.0",
        Relation.Newer => "3.0.0.0",
        _ => throw new ArgumentOutOfRangeException(nameof(relation))
    };

    /// <summary>The assembly version of the lowest admitted version of the dependency that stands in a relation to the floor.</summary>
    /// <param name="standing">The assembly against the floor.</param>
    private static string Held(Standing standing) => standing switch
    {
        Standing.Below => "1.0.0.0",
        Standing.Equal => FLOOR,
        Standing.Above => "4.0.0.0",
        _ => throw new ArgumentOutOfRangeException(nameof(standing))
    };

    /// <summary>An emitted assembly of a version, referencing the given assemblies.</summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="version">Its assembly version.</param>
    /// <param name="references">The assemblies it references, each with the version it is emitted at.</param>
    private static byte[] Assembly(string name, string version, params byte[][] references) =>
        EmittedAssemblies.Image(name, version: version, references: references);

    /// <summary>A nuspec <c>&lt;dependencies&gt;</c> element.</summary>
    /// <param name="items">The dependencies, each with its id and version range.</param>
    private static string Dependencies(params (string Id, string Range)[] items) =>
        "<dependencies>" + string.Concat(items.Select(item => $"""<dependency id="{item.Id}" version="{item.Range}" />""")) + "</dependencies>";

    /// <summary>The file of an assembly in the net8.0 lib folder of a package.</summary>
    /// <param name="package">The package's version folder.</param>
    /// <param name="assembly">The assembly name.</param>
    private static string Lib(string package, string assembly) => Path.Combine(package, "lib", NET8, assembly + ".dll");

    /// <summary>The assembly of a resolution that found one.</summary>
    /// <param name="resolution">The resolution.</param>
    private static ImplementationAssembly Found(ImplementationResolution resolution)
    {
        Assert.True(resolution.Reason is null, $"{resolution.Reason}: {resolution.Detail}");
        return resolution.Assembly!;
    }
}
