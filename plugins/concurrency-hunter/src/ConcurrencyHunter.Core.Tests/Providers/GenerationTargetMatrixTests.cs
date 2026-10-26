using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Which implementation and which generated file stand for a member a call reaches (R1, R2, R6, R8), over where the call's
/// reference comes from, whether the compilation belongs to the program or the generator, what of the implementation is on disk and
/// where the implementation sends the member's declaring type. The expected answer of a cell is written here from the requirement,
/// not from the code.</summary>
public sealed class GenerationTargetMatrixTests
{
    private const string MEMBER = "M:Acme.Api.Run(System.Action)";
    private const string PACKAGE = "Acme.Lib";
    private const string VERSION = "1.2.3";
    private const string API = "namespace Acme { public static class Api { public static void Run(System.Action callback) { } } }";
    private const string POINTER_API = "namespace Acme { public static unsafe class Api { public static void Run(delegate*<void> callback) { } } }";
    private const string SOURCE_API = "public static class SourceApi { public static void Run(System.Action callback) { } }";
    private static readonly Version ReferenceVersion = new(1, 0, 0, 0);

    private static readonly IReadOnlyList<Cell> Cells = Enum.GetValues<CompilationKind>()
        .SelectMany(compilation => Enum.GetValues<Origin>().SelectMany(origin => AvailabilitiesOf(origin)
            .SelectMany(availability => ForwardingsOf(origin).Select(forwarding => new Cell(compilation, origin, availability, forwarding)))))
        .Where(Allowed)
        .ToArray();

    private enum CompilationKind { Program, Generation }

    private enum Origin
    {
        PackageLib,
        PackageRef,
        PackageRuntimes,
        NetCoreAppPack,
        AspNetCoreAppPack,
        OtherPath,
        SourceMember,
        FunctionPointerMember,
        PackageWithoutFrameworkPack,
        NetCoreAppShared,
        AspNetCoreAppShared,
        OtherShared,
        CoreLib,
        GeneratedAssembly
    }

    private enum Availability
    {
        OnDisk,
        PackageVersionMissing,
        AssetForFrameworkMissing,
        SharedFrameworkMajorMissing,
        AspNetCoreAppMissing
    }

    private enum Forwarding { None, ToCoreLib, ToOtherAssembly }

    private enum Kind { NoTarget, Unavailable, Target }

    private sealed record Cell(CompilationKind Compilation, Origin Origin, Availability Availability, Forwarding Forwarding)
    {
        public override string ToString() => $"{Compilation} / {Origin} / {Availability} / {Forwarding}";
    }

    /// <summary>A cell built: the member, the compilation it is in, the resolver over the fake install and what the cell put on disk.</summary>
    /// <param name="Install">The fake install.</param>
    /// <param name="Compilation">The scope.</param>
    /// <param name="Method">The member a call names.</param>
    /// <param name="Resolver">The resolver over the install.</param>
    /// <param name="NuspecReads">The nuspecs the resolver read.</param>
    /// <param name="ImplementationPath">The implementation file the cell wrote, or <c>null</c> when it wrote none the resolver can use.</param>
    private sealed record Scenario(GenerationInstall Install, Compilation Compilation, IMethodSymbol Method, ImplementationAssemblies Resolver,
                                   List<string> NuspecReads, string? ImplementationPath) : IDisposable
    {
        public void Dispose() => Install.Dispose();
    }

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        var wrong = new List<string>();
        foreach (var cell in Cells)
        {
            using var scenario = Build(cell);
            var answer = GenerationTargets.Of(scenario.Method, scenario.Compilation, scenario.Resolver, CancellationToken.None, GenerationMajor(cell));
            if (Mismatch(cell, answer, scenario) is { } why)
                wrong.Add($"{cell}: {why}");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {Cells.Count} cells:\n" + string.Join('\n', wrong.Take(20)));
    }

    [Fact]
    public void No_cell_is_a_target_R1_excludes()
    {
        var leaked = new List<string>();
        foreach (var cell in Cells.Where(cell => Expected(cell) == Kind.NoTarget))
        {
            using var scenario = Build(cell);
            var answer = GenerationTargets.Of(scenario.Method, scenario.Compilation, scenario.Resolver, CancellationToken.None, GenerationMajor(cell));
            if (answer is IdentifiedGenerationTarget identified)
                leaked.Add($"{cell}: {identified.GetType().Name} for {identified.MemberId}");
            // What the reference alone rules out is decided before any resolution.
            else if (DecidedByReference(cell) && scenario.NuspecReads.Count != 0)
                leaked.Add($"{cell}: the resolver was asked ({scenario.NuspecReads.Count} nuspec reads)");
        }

        Assert.True(leaked.Count == 0, $"{leaked.Count} cells:\n" + string.Join('\n', leaked.Take(20)));
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        // Each compilation: three package origins x 5 x 3, two NETCore origins x 2 x 3, two AspNetCore origins x 3 x 3, six others once.
        // The generated assembly itself occurs only in a generator compilation.
        Assert.Equal(2 * (3 * 15 + 2 * 2 * 3 + 2 * 3 * 3 + 6) + 1, Cells.Count);
        Assert.Equal(Cells.Count, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<CompilationKind>().Order(), Cells.Select(cell => cell.Compilation).Distinct().Order());
        Assert.Equal(Enum.GetValues<Origin>().Order(), Cells.Select(cell => cell.Origin).Distinct().Order());
        Assert.Equal(Enum.GetValues<Availability>().Order(), Cells.Select(cell => cell.Availability).Distinct().Order());
        Assert.Equal(Enum.GetValues<Forwarding>().Order(), Cells.Select(cell => cell.Forwarding).Distinct().Order());
        Assert.Equal(Enum.GetValues<Kind>().Order(), Cells.Select(Expected).Distinct().Order());
        Assert.Equal(new[] { NoTargetReason.OtherPath, NoTargetReason.Source, NoTargetReason.FunctionPointer, NoTargetReason.NoFrameworkPack,
                            NoTargetReason.CoreLib, NoTargetReason.Forwarded }.Order(),
                     Cells.Where(cell => Expected(cell) == Kind.NoTarget).Select(ExpectedReason).Distinct().Order());
        // Every availability meets every forwarding for a package, and the forwardings meet the frameworks of the shared frameworks.
        Assert.All(new[] { Origin.PackageLib, Origin.PackageRef, Origin.PackageWithoutFrameworkPack }, origin =>
            Assert.Equal(2 * Enum.GetValues<Availability>().Length * Enum.GetValues<Forwarding>().Length, Cells.Count(cell => cell.Origin == origin)));
        Assert.All(new[] { Origin.NetCoreAppPack, Origin.AspNetCoreAppPack, Origin.NetCoreAppShared, Origin.AspNetCoreAppShared }, origin =>
            Assert.Equal(Enum.GetValues<Forwarding>().Order(), Cells.Where(cell => cell.Origin == origin).Select(cell => cell.Forwarding).Distinct().Order()));
        Assert.Contains(Cells, cell => cell.Origin == Origin.AspNetCoreAppPack && cell.Availability == Availability.AspNetCoreAppMissing);
        Assert.All(Enum.GetValues<Origin>().Where(origin => origin != Origin.GeneratedAssembly), origin =>
            Assert.Equal(Enum.GetValues<CompilationKind>().Order(), Cells.Where(cell => cell.Origin == origin).Select(cell => cell.Compilation).Distinct().Order()));
    }

    private static bool Allowed(Cell cell) =>
        // A program compilation is never the generator's own library copy.
        cell.Origin != Origin.GeneratedAssembly || cell.Compilation == CompilationKind.Generation;

    private static int? GenerationMajor(Cell cell) => cell.Compilation == CompilationKind.Generation
        ? IsPackage(cell.Origin) && cell.Availability == Availability.SharedFrameworkMajorMissing ? 9 : 8
        : null;

    /// <summary>The availabilities that apply to an origin: a package meets all of them, a shared framework's references the ones that
    /// are theirs, every other origin is decided from the reference alone.</summary>
    /// <param name="origin">The origin.</param>
    private static IEnumerable<Availability> AvailabilitiesOf(Origin origin) => origin switch
    {
        Origin.PackageLib or Origin.PackageRef or Origin.PackageWithoutFrameworkPack => Enum.GetValues<Availability>(),
        Origin.NetCoreAppPack or Origin.NetCoreAppShared => [Availability.OnDisk, Availability.SharedFrameworkMajorMissing],
        Origin.AspNetCoreAppPack or Origin.AspNetCoreAppShared => [Availability.OnDisk, Availability.SharedFrameworkMajorMissing, Availability.AspNetCoreAppMissing],
        _ => [Availability.OnDisk]
    };

    /// <summary>The forwardings that apply to an origin: wherever an implementation can be read, all of them.</summary>
    /// <param name="origin">The origin.</param>
    private static IEnumerable<Forwarding> ForwardingsOf(Origin origin) =>
        origin is Origin.PackageLib or Origin.PackageRef or Origin.PackageWithoutFrameworkPack or Origin.NetCoreAppPack or Origin.AspNetCoreAppPack or
                  Origin.NetCoreAppShared or Origin.AspNetCoreAppShared ? Enum.GetValues<Forwarding>() : [Forwarding.None];

    private static bool DecidedByReference(Cell cell) =>
        cell.Origin is Origin.PackageRuntimes or Origin.OtherPath or Origin.OtherShared or Origin.SourceMember or Origin.FunctionPointerMember or
                       Origin.CoreLib or Origin.GeneratedAssembly ||
        cell.Compilation == CompilationKind.Program && cell.Origin is Origin.PackageWithoutFrameworkPack or Origin.NetCoreAppShared or Origin.AspNetCoreAppShared;

    /// <summary>R1, R6 and R8, in the order they apply: what the reference alone rules out is no target; otherwise anything of the
    /// implementation missing makes it unavailable; otherwise a declaring type the implementation forwards is no target; otherwise it is.</summary>
    /// <param name="cell">The cell.</param>
    private static Kind Expected(Cell cell) =>
        DecidedByReference(cell) ? Kind.NoTarget :
        cell.Availability != Availability.OnDisk ? Kind.Unavailable :
        cell.Forwarding != Forwarding.None ? Kind.NoTarget : Kind.Target;

    private static NoTargetReason ExpectedReason(Cell cell) => cell.Origin switch
    {
        Origin.SourceMember or Origin.GeneratedAssembly => NoTargetReason.Source,
        Origin.CoreLib => NoTargetReason.CoreLib,
        Origin.FunctionPointerMember => NoTargetReason.FunctionPointer,
        Origin.PackageWithoutFrameworkPack when cell.Compilation == CompilationKind.Program => NoTargetReason.NoFrameworkPack,
        Origin.PackageRuntimes or Origin.OtherPath or Origin.OtherShared => NoTargetReason.OtherPath,
        Origin.NetCoreAppShared or Origin.AspNetCoreAppShared when cell.Compilation == CompilationKind.Program => NoTargetReason.OtherPath,
        _ => NoTargetReason.Forwarded
    };

    private static bool IsPackage(Origin origin) =>
        origin is Origin.PackageLib or Origin.PackageRef or Origin.PackageRuntimes or Origin.FunctionPointerMember or Origin.PackageWithoutFrameworkPack;

    private static string AssemblyOf(Origin origin) => origin switch
    {
        Origin.NetCoreAppPack or Origin.NetCoreAppShared => "System.Fixture",
        Origin.AspNetCoreAppPack or Origin.AspNetCoreAppShared => "Microsoft.AspNetCore.Fixture",
        Origin.OtherPath or Origin.OtherShared => "Elsewhere.Lib",
        Origin.CoreLib => "System.Private.CoreLib",
        _ => PACKAGE
    };

    /// <summary>What the cell's answer must be, or the first way it is not.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="answer">The answer.</param>
    /// <param name="scenario">The cell built.</param>
    private static string? Mismatch(Cell cell, GenerationTargetAnswer answer, Scenario scenario)
    {
        var kind = answer switch
        {
            NoGenerationTarget => Kind.NoTarget,
            UnavailableGenerationTarget => Kind.Unavailable,
            GenerationTarget => Kind.Target,
            _ => throw new InvalidOperationException($"Unknown answer {answer}.")
        };
        if (kind != Expected(cell))
            return $"expected {Expected(cell)}, got {answer}";
        if (answer is NoGenerationTarget none)
            return none.Reason == ExpectedReason(cell) ? null : $"expected the reason {ExpectedReason(cell)}, got {none.Reason}";

        var identified = (IdentifiedGenerationTarget)answer;
        if (identified.MemberId != MEMBER || identified.LookupId != MEMBER)
            return $"ids {identified.MemberId} / {identified.LookupId}, expected {MEMBER} for both (a void method)";
        // The key names the MVID of the asset the compilation's framework selects whenever that asset is on disk, also when the target is
        // unavailable for a shared framework it needs; only a missing package version or asset leaves it out.
        var mvid = IsPackage(cell.Origin) && scenario.ImplementationPath is { } asset ? AssemblyFile.Read(asset)!.Mvid : (Guid?)null;
        var major = cell.Availability == Availability.SharedFrameworkMajorMissing ? 9 : 8;
        var assembly = AssemblyOf(cell.Origin);
        // A package that is not on disk has no nuspec to spell its id: the folder's spelling stands.
        var package = cell.Availability == Availability.PackageVersionMissing ? PACKAGE.ToLowerInvariant() : PACKAGE;
        var expectedKey = IsPackage(cell.Origin)
            ? GeneratedFileKey.ForPackage(assembly, ReferenceVersion, package, VERSION, mvid)
            : GeneratedFileKey.ForFramework(assembly, ReferenceVersion, cell.Origin is Origin.NetCoreAppPack or Origin.NetCoreAppShared ? "Microsoft.NETCore.App" : "Microsoft.AspNetCore.App", major);
        if (identified.Key != expectedKey)
            return $"key {Describe(identified.Key)}, expected {Describe(expectedKey)}";
        if (answer is UnavailableGenerationTarget unavailable)
            return unavailable.Detail.Length > 0 ? null : "no detail says what is missing";

        var request = ((GenerationTarget)answer).Request;
        var expectedRequest = IsPackage(cell.Origin)
            ? new GenerationRequest(assembly, VERSION, MEMBER, PACKAGE, "net8.0")
            : new GenerationRequest(assembly, $"{major}.0", MEMBER, null, null);
        return request == expectedRequest ? null : $"request {request}, expected {expectedRequest}";
    }

    private static string Describe(GeneratedFileKey key) =>
        $"{key.AssemblyName} {key.AssemblyVersion} package {key.Package}/{key.PackageVersion}/{key.Mvid} framework {key.Framework}/{key.Major}";

    /// <summary>Puts a cell on disk and in a compilation.</summary>
    /// <param name="cell">The cell.</param>
    private static Scenario Build(Cell cell)
    {
        var install = new GenerationInstall();
        var reads = new List<string>();
        if (cell.Origin == Origin.CoreLib)
        {
            var corelib = ReferenceScopes.Reference(File.ReadAllBytes(typeof(object).Assembly.Location),
                cell.Compilation == CompilationKind.Program
                    ? ReferenceScopes.PackFile(install, 8, "Microsoft.NETCore.App.Ref", "System.Private.CoreLib.dll")
                    : ReferenceScopes.Join(install.DotnetRoot, "shared", "Microsoft.NETCore.App", "8.0.5", "System.Private.CoreLib.dll"));
            var scope = ReferenceScopes.Compile("public static class App { }", [corelib, EmittedAssemblies.RuntimeReferences[1]]);
            var toString = scope.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().Single();
            return new Scenario(install, scope, toString, install.Resolver(nuspecRead: reads.Add), reads, null);
        }
        var major = cell.Availability == Availability.SharedFrameworkMajorMissing ? 9 : 8;
        var core = install.SharedFramework("8.0.5");
        var assembly = AssemblyOf(cell.Origin);
        var source = cell.Origin == Origin.FunctionPointerMember ? POINTER_API : API;
        var referenceImage = EmittedAssemblies.Image(assembly, ReferenceScopes.VERSION_ATTRIBUTE + source);
        var implementation = cell.Forwarding switch
        {
            Forwarding.ToCoreLib => ForwardingAssemblies.Image(assembly, ("Acme", "Api", "System.Private.CoreLib")),
            Forwarding.ToOtherAssembly => ForwardingAssemblies.Image(assembly, ("Acme", "Api", "System.Collections")),
            _ => EmittedAssemblies.Image(assembly, ReferenceScopes.VERSION_ATTRIBUTE + source + "\n// the implementation, which is not the reference")
        };

        string? referencePath = null, implementationPath = null;
        switch (cell.Origin)
        {
            case Origin.NetCoreAppPack:
            case Origin.NetCoreAppShared:
                referencePath = cell.Origin == Origin.NetCoreAppPack
                    ? ReferenceScopes.PackFile(install, major, "Microsoft.NETCore.App.Ref", assembly + ".dll")
                    : ReferenceScopes.Join(install.DotnetRoot, "shared", "Microsoft.NETCore.App", $"{major}.0.5", assembly + ".dll");
                File.WriteAllBytes(implementationPath = Path.Combine(core, assembly + ".dll"), implementation);
                break;
            case Origin.AspNetCoreAppPack:
            case Origin.AspNetCoreAppShared:
                referencePath = cell.Origin == Origin.AspNetCoreAppPack
                    ? ReferenceScopes.PackFile(install, major, "Microsoft.AspNetCore.App.Ref", assembly + ".dll")
                    : ReferenceScopes.Join(install.DotnetRoot, "shared", "Microsoft.AspNetCore.App", $"{major}.0.5", assembly + ".dll");
                if (cell.Availability != Availability.AspNetCoreAppMissing)
                    File.WriteAllBytes(implementationPath = Path.Combine(install.AspNetCoreFramework("8.0.5"), assembly + ".dll"), implementation);
                break;
            case Origin.OtherPath:
                referencePath = ReferenceScopes.Join(install.Root, "elsewhere", assembly + ".dll");
                break;
            case Origin.OtherShared:
                referencePath = ReferenceScopes.Join(install.DotnetRoot, "shared", "Other.Framework", "8.0.5", assembly + ".dll");
                break;
            case Origin.SourceMember:
            case Origin.GeneratedAssembly:
                break;
            default:
                referencePath = cell.Origin switch
                {
                    Origin.PackageRef => ReferenceScopes.PackagePath(install, PACKAGE, VERSION, "ref", "net8.0", assembly),
                    Origin.PackageRuntimes => ReferenceScopes.PackagePath(install, PACKAGE, VERSION, "runtimes/win/lib", "net8.0", assembly),
                    _ => ReferenceScopes.PackagePath(install, PACKAGE, VERSION, "lib", "net8.0", assembly)
                };
                implementationPath = PutPackage(install, cell.Availability, assembly, implementation);
                break;
        }

        var references = ReferenceScopes.Runtime(install, cell.Compilation == CompilationKind.Generation || cell.Origin == Origin.PackageWithoutFrameworkPack ? null : major).ToList();
        if (referencePath is not null)
            references.Add(ReferenceScopes.Reference(referenceImage, referencePath));
        var compilation = ReferenceScopes.Compile(cell.Origin switch
        {
            Origin.SourceMember => SOURCE_API,
            Origin.GeneratedAssembly => API,
            _ => "public static class App { }"
        }, references);
        if (cell.Origin == Origin.GeneratedAssembly)
            compilation = compilation.WithAssemblyName(assembly);
        var type = compilation.GetTypeByMetadataName(cell.Origin == Origin.SourceMember ? "SourceApi" : "Acme.Api")!;
        var method = type.GetMembers("Run").OfType<IMethodSymbol>().Single();
        return new Scenario(install, compilation, method, install.Resolver(nuspecRead: reads.Add), reads, implementationPath);
    }

    /// <summary>Puts a package on disk as an availability says: whole, not at all, without an asset for net8.0, or whole but naming the
    /// <c>Microsoft.AspNetCore.App</c> framework, which no cell of a package installs.</summary>
    /// <param name="install">The fake install.</param>
    /// <param name="availability">The availability.</param>
    /// <param name="assembly">The assembly name.</param>
    /// <param name="implementation">The implementation image.</param>
    /// <returns>The path of the asset the resolver finds, or <c>null</c> when it finds none.</returns>
    private static string? PutPackage(GenerationInstall install, Availability availability, string assembly, byte[] implementation)
    {
        switch (availability)
        {
            case Availability.PackageVersionMissing:
                return null;
            case Availability.AssetForFrameworkMissing:
                // Only a .NET Framework asset, which no net8.0 scope takes.
                ReferenceScopes.AddAsset(install, PACKAGE, VERSION, "net462", assembly, implementation);
                return null;
            case Availability.AspNetCoreAppMissing:
                return ReferenceScopes.AddAsset(install, PACKAGE, VERSION, "net8.0", assembly, implementation,
                                       GenerationInstall.FrameworkReferences("net8.0", "Microsoft.AspNetCore.App"));
            default:
                return ReferenceScopes.AddAsset(install, PACKAGE, VERSION, "net8.0", assembly, implementation);
        }
    }
}
