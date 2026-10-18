using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Which layer describes a library call, and whether the run generates its member now (R1, R2, R3, R8), over what the project
/// entries say, what the built-in entries say, what the generated file of the call's own target holds and where the member comes from.
/// The origin of a cell is not written by hand: it is what <see cref="GenerationTargets.Of"/> answers for a call built to be of that
/// origin, over a fake install, so the matrix covers the way from the reference to the decision. The expected answer of a cell is
/// written here from the requirement, not from the code.</summary>
public sealed class GeneratedLayerMatrixTests
{
    private const string PACKAGE = "Acme.Lib";
    private const string VERSION = "1.2.3";
    private const string API = "namespace Acme { public static class Api { public static void Run(System.Action callback) { } } }";
    private const string POINTER_API = "namespace Acme { public static unsafe class Api { public static void Run(delegate*<void> callback) { } } }";
    private const string SOURCE_API = "public static class SourceApi { public static void Run(System.Action callback) { } }";

    private static readonly IReadOnlyList<Cell> Cells = (from project in Enum.GetValues<ProjectEntry>()
                                                         from builtIn in Enum.GetValues<BuiltInEntry>()
                                                         from stored in Enum.GetValues<StoredAnswer>()
                                                         from origin in Enum.GetValues<TargetOrigin>()
                                                         select new Cell(project, builtIn, stored, origin))
        .Where(cell => !Excluded(cell))
        .ToArray();

    // What GenerationTargets.Of answers for each origin, asked once: building a call of an origin writes a fake install.
    private static readonly Lazy<IReadOnlyDictionary<TargetOrigin, GenerationTargetAnswer>> Probes =
        new(() => Enum.GetValues<TargetOrigin>().ToDictionary(origin => origin, Probe));

    /// <summary>What the project entries say about the member. <c>opaque</c> and two entries that disagree leave a call opaque the
    /// same way: a project match that is declared opaque and carries no effect.</summary>
    private enum ProjectEntry { None, Model, Opaque, Disagreeing }

    /// <summary>What the built-in entries say about the member.</summary>
    private enum BuiltInEntry { None, InRange, OutOfRange }

    /// <summary>What the generated file of the call's own target holds for the member in the call's compilation.</summary>
    private enum StoredAnswer { None, Model, ModelNotApplicable, Refusal }

    /// <summary>Where the call's member comes from.</summary>
    private enum TargetOrigin
    {
        Package,
        NetCoreApp,
        AspNetCoreApp,
        CoreLib,
        ForwardedToCoreLib,
        ForwardedToOtherAssembly,
        SourceMember,
        FunctionPointerMember,
        OtherPath,
        NoFrameworkPack,
        ImplementationUnavailable
    }

    /// <summary>One cell of the matrix.</summary>
    /// <param name="Project">What the project entries say.</param>
    /// <param name="BuiltIn">What the built-in entries say.</param>
    /// <param name="Stored">What the generated file holds.</param>
    /// <param name="Origin">Where the member comes from.</param>
    private sealed record Cell(ProjectEntry Project, BuiltInEntry BuiltIn, StoredAnswer Stored, TargetOrigin Origin)
    {
        public override string ToString() => $"project {Project} / built-in {BuiltIn} / stored {Stored} / origin {Origin}";
    }

    /// <summary>What a cell comes to.</summary>
    /// <param name="Layer">The layer that describes the call.</param>
    /// <param name="Generate">Whether the run generates the member now.</param>
    /// <param name="Listed">Whether the scope's coverage lists the member: a generation target that no layer describes and that the run
    /// does not generate now (R5). A member generated now is listed or not by what its generation answers, which no cell settles.</param>
    private sealed record Answer(DescribingLayer Layer, bool Generate, bool Listed);

    [Fact]
    public void No_cell_narrows()
    {
        var narrowed = new List<string>();
        foreach (var cell in Cells)
        {
            var (expected, actual) = (Expected(cell), Actual(cell));
            if (expected.Layer == DescribingLayer.None && actual.Layer != DescribingLayer.None)
                narrowed.Add($"{cell}: the {actual.Layer} layer describes a call that is left opaque");
            else if (expected.Layer == DescribingLayer.Project && actual.Layer is DescribingLayer.Generated or DescribingLayer.BuiltIn)
                narrowed.Add($"{cell}: the {actual.Layer} layer describes a call a project entry decides");
            else if (expected.Generate && !actual.Generate)
                narrowed.Add($"{cell}: a member the run generates is not generated");
        }

        Assert.True(narrowed.Count == 0, $"{narrowed.Count} of {Cells.Count} cells:\n" + string.Join('\n', narrowed.Take(20)));
    }

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        var wrong = new List<string>();
        foreach (var cell in Cells)
        {
            var (expected, actual) = (Expected(cell), Actual(cell));
            if (expected != actual)
                wrong.Add($"{cell}: expected {expected}, got {actual}");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {Cells.Count} cells:\n" + string.Join('\n', wrong.Take(20)));
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        // 4 project x 3 built-in x 4 stored x 11 origins, less the 7 origins that are no target with a stored answer (3 of the 4), and
        // less the function-pointer member with an entry of the project or built-in layer (11 of the 12 pairs, the stored answer none).
        Assert.Equal(4 * 3 * 4 * 11 - 7 * 4 * 3 * 3 - 11, Cells.Count);
        Assert.Equal(Cells.Count, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<ProjectEntry>().Order(), Cells.Select(cell => cell.Project).Distinct().Order());
        Assert.Equal(Enum.GetValues<BuiltInEntry>().Order(), Cells.Select(cell => cell.BuiltIn).Distinct().Order());
        Assert.Equal(Enum.GetValues<StoredAnswer>().Order(), Cells.Select(cell => cell.Stored).Distinct().Order());
        Assert.Equal(Enum.GetValues<TargetOrigin>().Order(), Cells.Select(cell => cell.Origin).Distinct().Order());

        // Every origin meets every entry its member can have, and an origin that can be written meets every stored answer.
        foreach (var origin in Enum.GetValues<TargetOrigin>())
        {
            var cells = Cells.Where(cell => cell.Origin == origin).ToArray();
            var named = origin != TargetOrigin.FunctionPointerMember;
            ProjectEntry[] projects = named ? Enum.GetValues<ProjectEntry>() : [ProjectEntry.None];
            BuiltInEntry[] builtIns = named ? Enum.GetValues<BuiltInEntry>() : [BuiltInEntry.None];
            StoredAnswer[] stored = IsNoTarget(origin) ? [StoredAnswer.None] : Enum.GetValues<StoredAnswer>();
            Assert.Equal(projects.Order(), cells.Select(cell => cell.Project).Distinct().Order());
            Assert.Equal(builtIns.Order(), cells.Select(cell => cell.BuiltIn).Distinct().Order());
            Assert.Equal(stored.Order(), cells.Select(cell => cell.Stored).Distinct().Order());
        }

        // Every answer the requirements name is somewhere in the matrix.
        Assert.Equal(Enum.GetValues<DescribingLayer>().Order(), Cells.Select(cell => Expected(cell).Layer).Distinct().Order());
        Assert.Equal(new[]
                     {
                         new Answer(DescribingLayer.Project, false, false),
                         new Answer(DescribingLayer.BuiltIn, false, false),
                         new Answer(DescribingLayer.Generated, false, false),
                         new Answer(DescribingLayer.None, false, false),
                         new Answer(DescribingLayer.None, false, true),
                         new Answer(DescribingLayer.None, true, false)
                     }.OrderBy(answer => answer.ToString(), StringComparer.Ordinal),
                     Cells.Select(Expected).Distinct().OrderBy(answer => answer.ToString(), StringComparer.Ordinal));

        // The origins are what they say: each call's own target resolution gives the answer its name names.
        var wrong = Enum.GetValues<TargetOrigin>().Select(origin => (Origin: origin, Why: ProbeMismatch(origin, Probes.Value[origin])))
                        .Where(probe => probe.Why is not null)
                        .Select(probe => $"{probe.Origin}: {probe.Why}")
                        .ToArray();
        Assert.True(wrong.Length == 0, string.Join('\n', wrong));
    }

    /// <summary>R1, R2, R3, R5 and R8, in the order they apply: a project entry decides; then a built-in entry in range; then a member
    /// that is no generation target is left as it is; then the generated file's answer settles the member whether or not its
    /// implementation is on disk; then, with no answer, a member whose implementation is not on disk is listed and one whose is on disk is
    /// generated.</summary>
    /// <param name="cell">The cell.</param>
    private static Answer Expected(Cell cell)
    {
        // R1: a project entry is the layer over the others. A model describes the call; opaque, and two entries that disagree, leave
        // the member opaque, and nothing is generated for it.
        if (cell.Project != ProjectEntry.None)
            return new Answer(DescribingLayer.Project, false, false);
        // R1: a built-in entry in range describes the call; one out of range does not.
        if (cell.BuiltIn == BuiltInEntry.InRange)
            return new Answer(DescribingLayer.BuiltIn, false, false);
        // R1, R8: CoreLib, a forwarded type, a member with source, a function-pointer member, another path and no framework pack are no
        // generation targets: never generated, never listed.
        if (IsNoTarget(cell.Origin))
            return new Answer(DescribingLayer.None, false, false);
        // R2: a model the call's compilation accepts describes the call, and a member with a valid answer is not generated again.
        if (cell.Stored == StoredAnswer.Model)
            return new Answer(DescribingLayer.Generated, false, false);
        // R2, R5: a model the compilation does not accept stays in the file, is not generated again and is listed.
        if (cell.Stored == StoredAnswer.ModelNotApplicable)
            return new Answer(DescribingLayer.None, false, true);
        // R2, R3, R5: a refusal is the generator's answer, kept; the member is not generated again and is listed under its reason.
        if (cell.Stored == StoredAnswer.Refusal)
            return new Answer(DescribingLayer.None, false, true);
        // R3, R5: with no answer, an implementation that is not on disk cannot be generated from; the member is listed and tried again
        // the next time.
        if (cell.Origin == TargetOrigin.ImplementationUnavailable)
            return new Answer(DescribingLayer.None, false, true);
        // R1: no layer describes the member, it is a target and its implementation is on disk: it is left opaque in this analysis and
        // generated.
        return new Answer(DescribingLayer.None, true, false);
    }

    /// <summary>What <see cref="GeneratedLayer.Decide"/> answers for a cell, the origin being the one the cell's call really has.</summary>
    /// <param name="cell">The cell.</param>
    private static Answer Actual(Cell cell)
    {
        var origin = GeneratedLayer.OriginOf(Probes.Value[cell.Origin]);
        var decision = GeneratedLayer.Decide(ProjectMatch(cell.Project), BuiltInMatch(cell.BuiltIn), StoredOf(cell.Stored), origin);
        // R5: the coverage lists a target that no layer describes and that is not generated now.
        var listed = origin != GenerationOrigin.NotATarget && decision.Layer == DescribingLayer.None && !decision.Generate;
        return new Answer(decision.Layer, decision.Generate, listed);
    }

    /// <summary>R1, R8: the origins no layer's file answers for and the run never generates.</summary>
    /// <param name="origin">The origin.</param>
    private static bool IsNoTarget(TargetOrigin origin) => origin is TargetOrigin.CoreLib or TargetOrigin.ForwardedToCoreLib or
        TargetOrigin.ForwardedToOtherAssembly or TargetOrigin.SourceMember or TargetOrigin.FunctionPointerMember or TargetOrigin.OtherPath or
        TargetOrigin.NoFrameworkPack;

    /// <summary>The cells nothing can reach: a member that is no target has no stored answer, because nothing writes one (R1, R3), and a
    /// function-pointer member has no entry of any layer, because no declaration id names it (R8).</summary>
    /// <param name="cell">The cell.</param>
    private static bool Excluded(Cell cell) =>
        IsNoTarget(cell.Origin) && cell.Stored != StoredAnswer.None ||
        cell.Origin == TargetOrigin.FunctionPointerMember && (cell.Project != ProjectEntry.None || cell.BuiltIn != BuiltInEntry.None);

    private static LibraryMatch? ProjectMatch(ProjectEntry entry) => entry switch
    {
        ProjectEntry.None => null,
        ProjectEntry.Model => Match(LibraryMatchKind.Known, ModelLayer.Project, declaredOpaque: false),
        ProjectEntry.Opaque or ProjectEntry.Disagreeing => Match(LibraryMatchKind.Opaque, ModelLayer.Project, declaredOpaque: true),
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "Not a project entry of the matrix.")
    };

    private static LibraryMatch? BuiltInMatch(BuiltInEntry entry) => entry switch
    {
        BuiltInEntry.None => null,
        BuiltInEntry.InRange => Match(LibraryMatchKind.Known, ModelLayer.BuiltIn, declaredOpaque: false),
        BuiltInEntry.OutOfRange => Match(LibraryMatchKind.OutOfRange, ModelLayer.BuiltIn, declaredOpaque: false),
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, "Not a built-in entry of the matrix.")
    };

    private static LibraryMatch Match(LibraryMatchKind kind, ModelLayer layer, bool declaredOpaque) =>
        new(kind, "M:A.B.C", [], new AssemblyIdentity("A"), SupportedAssemblyVersion.Framework("A"), layer, declaredOpaque);

    private static StoredGeneratedAnswer StoredOf(StoredAnswer stored) => stored switch
    {
        StoredAnswer.None => StoredGeneratedAnswer.Absent,
        StoredAnswer.Model => new StoredGeneratedAnswer(StoredGeneratedKind.Model, new LibraryModel("M:A.B.C", [SupportedAssemblyVersion.Framework("A")], [])),
        StoredAnswer.ModelNotApplicable => new StoredGeneratedAnswer(StoredGeneratedKind.ModelNotApplicable),
        StoredAnswer.Refusal => new StoredGeneratedAnswer(StoredGeneratedKind.Refusal),
        _ => throw new ArgumentOutOfRangeException(nameof(stored), stored, "Not a stored answer of the matrix.")
    };

    /// <summary>The first way the target resolution of an origin's call is not the answer the origin's name says, or <c>null</c>.</summary>
    /// <param name="origin">The origin.</param>
    /// <param name="answer">What <see cref="GenerationTargets.Of"/> answered.</param>
    private static string? ProbeMismatch(TargetOrigin origin, GenerationTargetAnswer answer)
    {
        NoTargetReason? reason = origin switch
        {
            TargetOrigin.CoreLib => NoTargetReason.CoreLib,
            TargetOrigin.ForwardedToCoreLib or TargetOrigin.ForwardedToOtherAssembly => NoTargetReason.Forwarded,
            TargetOrigin.SourceMember => NoTargetReason.Source,
            TargetOrigin.FunctionPointerMember => NoTargetReason.FunctionPointer,
            TargetOrigin.OtherPath => NoTargetReason.OtherPath,
            TargetOrigin.NoFrameworkPack => NoTargetReason.NoFrameworkPack,
            _ => null
        };
        return (reason, origin) switch
        {
            ({ } expected, _) => answer is NoGenerationTarget none && none.Reason == expected ? null : $"expected no target for {expected}, got {answer}",
            (_, TargetOrigin.ImplementationUnavailable) => answer is UnavailableGenerationTarget ? null : $"expected an unavailable target, got {answer}",
            _ => answer is GenerationTarget ? null : $"expected a target, got {answer}"
        };
    }

    /// <summary>What <see cref="GenerationTargets.Of"/> answers for the call of <c>Acme.Api.Run</c> (or the member the origin names)
    /// built to come from an origin, over a fake install that holds what the origin says is on disk.</summary>
    /// <param name="origin">The origin.</param>
    private static GenerationTargetAnswer Probe(TargetOrigin origin)
    {
        using var install = new GenerationInstall();
        var core = install.SharedFramework("8.0.5");
        var (compilation, method) = origin switch
        {
            TargetOrigin.CoreLib => CoreLibCall(install),
            TargetOrigin.SourceMember => SourceCall(install),
            _ => ReferencedCall(install, core, origin)
        };
        return GenerationTargets.Of(method, compilation, install.Resolver(), CancellationToken.None);
    }

    /// <summary>A call of <c>object.ToString</c> in a compilation whose corelib is referenced from the <c>Microsoft.NETCore.App.Ref</c>
    /// pack, as no real reference pack does.</summary>
    /// <param name="install">The fake install whose dotnet root holds the pack.</param>
    private static (Compilation Compilation, IMethodSymbol Method) CoreLibCall(GenerationInstall install)
    {
        var corelib = ReferenceScopes.Reference(File.ReadAllBytes(typeof(object).Assembly.Location),
                                                ReferenceScopes.PackFile(install, 8, "Microsoft.NETCore.App.Ref", "System.Private.CoreLib.dll"));
        var compilation = ReferenceScopes.Compile("public static class App { }", [corelib, EmittedAssemblies.RuntimeReferences[1]]);
        return (compilation, compilation.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().Single());
    }

    /// <summary>A call of a member declared in the scope's own source.</summary>
    /// <param name="install">The fake install whose dotnet root holds the pack.</param>
    private static (Compilation Compilation, IMethodSymbol Method) SourceCall(GenerationInstall install)
    {
        var compilation = ReferenceScopes.Compile(SOURCE_API, ReferenceScopes.Runtime(install, 8));
        return (compilation, compilation.GetTypeByMetadataName("SourceApi")!.GetMembers("Run").OfType<IMethodSymbol>().Single());
    }

    /// <summary>A call of a member of an assembly the compilation references from the path of an origin, whose implementation the origin
    /// puts on disk, forwards or leaves out.</summary>
    /// <param name="install">The fake install.</param>
    /// <param name="core">The <c>Microsoft.NETCore.App</c> folder of the install.</param>
    /// <param name="origin">The origin: a package, a shared framework, another path, or a package a scope cannot choose an asset for.</param>
    private static (Compilation Compilation, IMethodSymbol Method) ReferencedCall(GenerationInstall install, string core, TargetOrigin origin)
    {
        var assembly = origin switch
        {
            TargetOrigin.NetCoreApp => "System.Fixture",
            TargetOrigin.AspNetCoreApp => "Microsoft.AspNetCore.Fixture",
            TargetOrigin.OtherPath => "Elsewhere.Lib",
            _ => PACKAGE
        };
        var source = origin == TargetOrigin.FunctionPointerMember ? POINTER_API : API;
        var reference = EmittedAssemblies.Image(assembly, ReferenceScopes.VERSION_ATTRIBUTE + source);
        var implementation = origin switch
        {
            TargetOrigin.ForwardedToCoreLib => ForwardingAssemblies.Image(assembly, ("Acme", "Api", "System.Private.CoreLib")),
            TargetOrigin.ForwardedToOtherAssembly => ForwardingAssemblies.Image(assembly, ("Acme", "Api", "System.Collections")),
            _ => EmittedAssemblies.Image(assembly, ReferenceScopes.VERSION_ATTRIBUTE + source + "\n// the implementation, which is not the reference")
        };

        string referencePath;
        switch (origin)
        {
            case TargetOrigin.NetCoreApp:
                referencePath = ReferenceScopes.PackFile(install, 8, "Microsoft.NETCore.App.Ref", assembly + ".dll");
                File.WriteAllBytes(Path.Combine(core, assembly + ".dll"), implementation);
                break;
            case TargetOrigin.AspNetCoreApp:
                referencePath = ReferenceScopes.PackFile(install, 8, "Microsoft.AspNetCore.App.Ref", assembly + ".dll");
                File.WriteAllBytes(Path.Combine(install.AspNetCoreFramework("8.0.5"), assembly + ".dll"), implementation);
                break;
            case TargetOrigin.OtherPath:
                referencePath = ReferenceScopes.Join(install.Root, "elsewhere", assembly + ".dll");
                break;
            default:
                referencePath = ReferenceScopes.PackagePath(install, PACKAGE, VERSION, "lib", "net8.0", assembly);
                if (origin != TargetOrigin.ImplementationUnavailable)
                    ReferenceScopes.AddAsset(install, PACKAGE, VERSION, "net8.0", assembly, implementation);
                break;
        }

        var references = ReferenceScopes.Runtime(install, origin == TargetOrigin.NoFrameworkPack ? null : 8).ToList();
        references.Add(ReferenceScopes.Reference(reference, referencePath));
        var compilation = ReferenceScopes.Compile("public static class App { }", references);
        return (compilation, compilation.GetTypeByMetadataName("Acme.Api")!.GetMembers("Run").OfType<IMethodSymbol>().Single());
    }
}
