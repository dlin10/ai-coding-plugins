using System.Collections.Concurrent;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class RecipeValueMatrixTests
{
    private enum Source { PublicConstructor, Factory, InternalConstructor, Implementation, LibrarySharedStatic, OtherAssemblySharedStatic }
    private enum Argument { None, Default, ConcreteLibrary, ConcreteWithVirtuals, InterfaceWithImplementation, AbstractWithImplementation,
                            OtherAssemblyShared, CannedLibraryStatic, InterfaceWithoutImplementation, Delegate, Expression, Container,
                            StructWithReferences, StructWithReferencesDefaultOnly }
    private enum Depth { Top, Nested }
    private sealed record Cell(Source Source, Argument Argument, Depth Depth);

    private static IEnumerable<Cell> Cells() =>
        (from source in Enum.GetValues<Source>() from argument in Enum.GetValues<Argument>() from depth in Enum.GetValues<Depth>()
         select new Cell(source, argument, depth)).Where(cell => Allowed(cell) is null);

    private static string? Allowed(Cell cell)
    {
        if (cell.Argument == Argument.None && cell.Depth == Depth.Nested)
            return "There is no nested argument when the constructor takes no argument.";
        if (cell.Source is Source.LibrarySharedStatic or Source.OtherAssemblySharedStatic && cell.Argument != Argument.None)
            return "A shared static value takes no constructor argument.";
        return null;
    }

    private static bool Built(Cell cell) => cell.Source != Source.LibrarySharedStatic && cell.Argument is
        Argument.None or Argument.Default or Argument.ConcreteLibrary or Argument.ConcreteWithVirtuals or Argument.InterfaceWithImplementation or
        Argument.AbstractWithImplementation or Argument.OtherAssemblyShared or Argument.StructWithReferences;

    [Fact]
    public void Every_cell_gets_its_expected_recipe_value() => CheckCells(cell =>
    {
        var driver = Synthesize(cell, false, out var library);
        Assert.Equal(Built(cell), driver.RecipeStatements.Count == 1);
        Assert.Equal(!Built(cell), driver.UnseededRecipeStatics.Count == 1);
        Assert.Empty(driver.SeedStatements);
        if (!Built(cell))
            return;
        var text = Assert.Single(driver.RecipeStatements).Operation.Syntax.ToString();
        var source = cell.Source switch
        {
            Source.Factory => "global::Lib.V.Make(",
            Source.Implementation => "new global::Lib.Implementation(",
            Source.OtherAssemblySharedStatic => "global::System.StringComparer.",
            _ => "new global::Lib.V("
        };
        Assert.Contains(source, text);
        if (cell.Argument == Argument.StructWithReferences)
        {
            Assert.Contains("new global::Lib.Pair(new global::Lib.Leaf())", text);
            Assert.DoesNotContain("default(global::Lib.Pair)", text);
        }
        foreach (var forbidden in new[] { "Probe_", "Sub_", "=>", "Expression", "List<", "Keep.K", ".Shared" })
            Assert.DoesNotContain(forbidden, text);
        if (cell.Source == Source.OtherAssemblySharedStatic)
        {
            var trace = ModelGenerator.Trace(new GenerationRequest("System.Private.Uri", "10.0", "M:Lib.Api.Run(System.Action)", null, null),
                library, CancellationToken.None, initializers: TypeInitializerTreatment.Off);
            Assert.NotNull(trace.Answer.Model);
            Assert.Empty(trace.Answer.Causes);
            Assert.Empty(trace.Driver!.UnseededRecipeStatics);
            Assert.Contains("global::System.StringComparer.InvariantCulture", trace.Driver.Source);
            const string STRUCT_SOURCE = """
                using System;
                namespace Lib {
                public struct Pair { public StringComparer Value; public Pair(StringComparer value) { Value = value; } }
                public static class Api {
                    private static Pair State = new Pair(StringComparer.Ordinal);
                    public static void Run(Action a) { a(); _ = State.Value.Equals(null, null); }
                } }
                """;
            var structure = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null),
                Compile(STRUCT_SOURCE), CancellationToken.None, initializers: TypeInitializerTreatment.Off);
            Assert.NotNull(structure.Answer.Model);
            Assert.Empty(structure.Answer.Causes);
            Assert.Empty(structure.Driver!.UnseededRecipeStatics);
            Assert.Contains("global::System.StringComparer.InvariantCulture", structure.Driver.Source);
        }
    });

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Source>(), cells.Select(cell => cell.Source).Distinct().Order());
        Assert.Equal(Enum.GetValues<Argument>(), cells.Select(cell => cell.Argument).Distinct().Order());
        Assert.Equal(Enum.GetValues<Depth>(), cells.Select(cell => cell.Depth).Distinct().Order());
    }

    [Fact]
    public void No_cell_leaves_a_rejected_candidate_behind() => CheckCells(cell =>
    {
        var ordinary = Synthesize(cell);
        var rejected = Synthesize(cell, rejectedFirst: true);
        Assert.Equal(ordinary.Source, rejected.Source);
        Assert.Equal(ordinary.Unseeded, rejected.Unseeded);
        Assert.Equal(ordinary.ProbeTypes, rejected.ProbeTypes);
        Assert.Equal(ordinary.SeedFactories, rejected.SeedFactories);
    });

    [Fact]
    public void A_static_without_a_recipe_value_leaves_the_driver_synthesized()
    {
        var driver = Synthesize(new Cell(Source.PublicConstructor, Argument.Delegate, Depth.Nested));
        Assert.Empty(driver.RecipeStatements);
        Assert.Single(driver.UnseededRecipeStatics);
        Assert.Contains("V_Call", driver.Source);
    }

    private static void CheckCells(Action<Cell> check)
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Cells(), CellParallelism, cell =>
        {
            try { check(cell); }
            catch (Exception error) { failures.Add($"{cell}: {error}"); }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order().Take(20)));
    }

    private static Driver Synthesize(Cell cell, bool rejectedFirst = false) => Synthesize(cell, rejectedFirst, out _);

    private static Driver Synthesize(Cell cell, bool rejectedFirst, out LibraryCompilationResult library)
    {
        var argument = cell.Argument switch
        {
            Argument.None => "", Argument.Default => "int number, string text", Argument.ConcreteLibrary => "Leaf value",
            Argument.ConcreteWithVirtuals => "Virtual value", Argument.InterfaceWithImplementation => "IBox value",
            Argument.AbstractWithImplementation => "Abstract value", Argument.OtherAssemblyShared => "System.StringComparer value",
            Argument.CannedLibraryStatic => "System.Uri value", Argument.InterfaceWithoutImplementation => "IMissing value",
            Argument.Delegate => "System.Action value", Argument.Expression => "System.Linq.Expressions.Expression<System.Func<Leaf>> value",
            Argument.Container => "System.Collections.Generic.List<Leaf> value", Argument.StructWithReferences => "Pair value",
            _ => "DefaultPair value"
        };
        var nested = cell.Depth == Depth.Nested ? "public sealed class Inner { public Inner(" + argument + ") { } }" : "";
        if (cell.Depth == Depth.Nested)
            argument = "Inner value";
        var rejected = rejectedFirst ? "public V(System.Func<int> rejected) { }" : "";
        // The rejected overload must never become a usable source, including when the selected source is an implementation.
        var declaration = cell.Source switch
        {
            Source.PublicConstructor => "public sealed class V { " + rejected + " public V(" + argument + ") { } }",
            Source.Factory => "public sealed class V { " + rejected + " private V() { } public static V Make(" + argument + ") => new V(); }",
            Source.InternalConstructor => "public sealed class V { " + rejected + " internal V(" + argument + ") { } }",
            Source.Implementation => "public interface V { } public sealed class Implementation : V { " +
                (rejectedFirst ? "public Implementation(System.Func<int> rejected) { } " : "") + "public Implementation(" + argument + ") { } }",
            Source.LibrarySharedStatic => "public sealed class V { " + rejected + " private V() { } public static V Shared; }",
            _ => ""
        };
        var valueType = cell.Source == Source.OtherAssemblySharedStatic ? "System.StringComparer" : "V";
        var source = """
            [assembly: System.Reflection.AssemblyVersion("10.0.0.0")]
            namespace System { public sealed class Uri { private Uri() { } public static Uri Shared; } }
            namespace Lib {
                public sealed class Leaf { }
                public class Virtual { public virtual void Touch() { } }
                public interface IBox { }
                public sealed class ConcreteBox : IBox { }
                public abstract class Abstract { }
                public sealed class ConcreteAbstract : Abstract { }
                public interface IMissing { }
                public struct Pair { public Leaf Value; public Pair(Leaf value) { Value = value; } }
                public struct DefaultPair { public Leaf Value; }
            """ + nested + declaration + "public static class Api { private static " + valueType +
            " State; public static void Run(System.Action action) { action(); " +
            (cell.Source == Source.OtherAssemblySharedStatic ? "_ = State.Equals(null, null);" : "") + " } } }";
        var shared = LibraryCompilation.CompileTrees("System.Private.Uri",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, path: "Recipe/Library.cs")],
            EmittedAssemblies.RuntimeReferences.Append(MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location)).ToArray(),
            CancellationToken.None);
        Assert.True(shared.Compilation is not null, $"{shared.Reason}: {string.Join("; ", shared.Errors)}");
        library = shared;
        Assert.Equal(ParameterKind.Canned, ParameterKinds.Of(shared.Compilation.GetTypeByMetadataName("System.Uri")!));
        var copy = LibraryCompilation.RecipeCopy(shared, new GenerationChecks(CancellationToken.None));
        var member = DriverSynthesizer.FindMember(copy.Compilation!, "M:Lib.Api.Run(System.Action)")!;
        var synthesis = DriverSynthesizer.Synthesize(copy.Compilation!, member, copy.ExternMembers, copy.OpenedFields, new HashSet<(string, string)>(),
            new ComparisonWitnesses(), new GenerationChecks(CancellationToken.None), copy.ReplacedMembers,
            new HashSet<(string, string)> { ("System.Private.Uri:Lib.Api", "State") });
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        return synthesis.Driver;
    }
}
