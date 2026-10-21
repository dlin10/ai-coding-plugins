using System.Collections.Concurrent;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Generic method witnesses whose values may name a type parameter scoped to the witnessed method.</summary>
public sealed class GenericMethodWitnessMatrixTests
{
    private enum WitnessKind { InterfaceMethod, AbstractOverride, ExplicitImplementation }
    private enum ReturnKind { ByValue, Ref, RefReadonly }
    private enum ReturnShape { Void, TypeParameter, ConstructedInterface, ArrayOfParameter, TaskOfConstructed, ValueTaskOfConstructed, NestedInGeneric, Unrelated }
    private enum InputKind { None, Parameter, OutConstructed, OutArrayOfParameter, OutNestedInGeneric }
    private enum ConstraintKind { None, Class, Interface }

    private sealed record Cell(WitnessKind Witness, ReturnKind Return, ReturnShape Shape, InputKind Input, ConstraintKind Constraint);

    private static readonly Cell[] Cells = (from witness in Enum.GetValues<WitnessKind>()
                                            from @return in Enum.GetValues<ReturnKind>()
                                            from shape in Enum.GetValues<ReturnShape>()
                                            from input in Enum.GetValues<InputKind>()
                                            from constraint in Enum.GetValues<ConstraintKind>()
                                            where Allowed(@return, shape)
                                            select new Cell(witness, @return, shape, input, constraint)).ToArray();

    [Fact]
    public void Every_cell_compiles_binds_and_gets_its_expected_witness()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, cell =>
        {
            try
            {
                Check(cell);
            }
            catch (Exception error)
            {
                wrong.Enqueue($"{cell}: {error.Message}");
            }
        });

        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong.Take(20)));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        Assert.Equal(990, Cells.Length);
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<WitnessKind>(), Cells.Select(cell => cell.Witness).Distinct().Order());
        Assert.Equal(Enum.GetValues<ReturnKind>(), Cells.Select(cell => cell.Return).Distinct().Order());
        Assert.Equal(Enum.GetValues<ReturnShape>(), Cells.Select(cell => cell.Shape).Distinct().Order());
        Assert.Equal(Enum.GetValues<InputKind>(), Cells.Select(cell => cell.Input).Distinct().Order());
        Assert.Equal(Enum.GetValues<ConstraintKind>(), Cells.Select(cell => cell.Constraint).Distinct().Order());
    }

    // C# does not permit ref void or ref readonly void returns (CS1547).
    private static bool Allowed(ReturnKind kind, ReturnShape shape) => kind == ReturnKind.ByValue || shape != ReturnShape.Void;

    private static void Check(Cell cell)
    {
        var library = Compile(Source(cell));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.Empty(library.ExternMembers);
        const string MEMBER = "M:Lib.Api.Run(Lib.User)";
        var member = Assert.IsAssignableFrom<ISymbol>(DriverSynthesizer.FindMember(library.Compilation, MEMBER));
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, CancellationToken.None);
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        var driver = synthesis.Driver;
        Assert.Empty(driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var tree = driver.Compilation.SyntaxTrees.Single();
        var model = driver.Compilation.GetSemanticModel(tree);
        var call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                       .Single(invocation => invocation.Expression.ToString() == "global::Lib.Api.Run");
        Assert.Equal(MEMBER, model.GetSymbolInfo(call).Symbol?.GetDocumentationCommentId());
        var probe = driver.Compilation.GetTypeByMetadataName("Probe_user")!;
        var witness = probe.GetMembers().OfType<IMethodSymbol>().Single(method => method.Name.EndsWith("Make", StringComparison.Ordinal));
        var expectedExtern = cell.Input is InputKind.OutConstructed or InputKind.OutArrayOfParameter or InputKind.OutNestedInGeneric ||
                             cell.Return == ReturnKind.ByValue && cell.Shape is not (ReturnShape.Void or ReturnShape.Unrelated);
        Assert.Equal(expectedExtern, witness.IsExtern);
        Assert.Equal(cell.Witness == WitnessKind.AbstractOverride, witness.IsOverride);
        if (cell.Witness == WitnessKind.ExplicitImplementation)
            Assert.Equal("ISecond", Assert.Single(witness.ExplicitInterfaceImplementations).ContainingType.Name);
        var declaration = (MethodDeclarationSyntax)witness.DeclaringSyntaxReferences.Single().GetSyntax();
        Assert.Equal(!expectedExtern, declaration.Body is not null);
        if (expectedExtern)
            return;
        if (cell.Return != ReturnKind.ByValue)
            Assert.Contains("return ref WitnessedRef<", declaration.Body!.ToString());
        if (cell.Input == InputKind.Parameter)
        {
            var store = declaration.Body!.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single(assignment => assignment.Right.ToString() == "p0");
            Assert.Equal(SpecialType.System_Object, model.GetTypeInfo(store.Left).Type?.SpecialType);
        }
    }

    private static string Source(Cell cell)
    {
        var result = cell.Shape switch
        {
            ReturnShape.Void => "void",
            ReturnShape.TypeParameter => "T",
            ReturnShape.ConstructedInterface => "IQ<T>",
            ReturnShape.ArrayOfParameter => "T[]",
            ReturnShape.TaskOfConstructed => "Task<IQ<T>>",
            ReturnShape.ValueTaskOfConstructed => "ValueTask<IQ<T>>",
            ReturnShape.NestedInGeneric => "Outer<T>.Inner",
            _ => "Ready"
        };
        var input = cell.Input switch
        {
            InputKind.Parameter => "T x",
            InputKind.OutConstructed => "out IQ<T> x",
            InputKind.OutArrayOfParameter => "out T[] x",
            InputKind.OutNestedInGeneric => "out Outer<T>.Inner x",
            _ => ""
        };
        var constraint = cell.Constraint switch
        {
            ConstraintKind.Class => " where T : class",
            ConstraintKind.Interface => " where T : IComparable<T>",
            _ => ""
        };
        var prefix = cell.Return switch { ReturnKind.Ref => "ref ", ReturnKind.RefReadonly => "ref readonly ", _ => "" };
        var method = $"{prefix}{result} Make<T>({input}){constraint};";
        var user = cell.Witness switch
        {
            WitnessKind.InterfaceMethod => $"public interface User {{ {method} }}",
            WitnessKind.AbstractOverride => $"public abstract class User {{ public abstract {method} }}",
            _ => $"public interface ISecond {{ {method} }} public interface User : ISecond {{ void Other(); }}"
        };
        return "using System; using System.Threading.Tasks; namespace Lib { " +
               "public interface IQ<T> { } public class Outer<T> { public sealed class Inner { public Inner() { } } } " +
               "public sealed class Ready { public Ready() { } } " + user +
               " public static class Api { public static void Run(User user) { } } }";
    }
}
