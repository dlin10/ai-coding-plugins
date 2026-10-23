using System.Collections.Concurrent;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>A trigger's <c>Span&lt;T&gt;</c> or <c>ReadOnlySpan&lt;T&gt;</c> argument, passed in any way, is built in the call from a
/// static field of <c>T[]</c> seeded as a <c>T[]</c> argument in its place would be, and the trigger covers its member; any other
/// ref-like or pointer argument leaves its member uncovered.</summary>
public sealed class SpanTriggerArgumentMatrixTests
{
    private enum SpanKind { Span, ReadOnlySpan }
    private enum Pass { Value, In, RefReadOnly, Ref }
    private enum Element { Immutable, Holding, TypeParameter }
    private enum Await { Sync, Async }

    private sealed record Cell(SpanKind Span, Pass Pass, Element Element, Await Await);

    private const string MEMBER = "M:Lib.Holder`1.#ctor(System.Action)";
    private const string TAKE = "M:Lib.Holder`1.Take(";

    /// <summary>The span is the trigger's second parameter, after an <c>int</c>.</summary>
    private const int ORDINAL = 1;

    private static readonly Cell[] Cells = (from span in Enum.GetValues<SpanKind>()
                                            from pass in Enum.GetValues<Pass>()
                                            from element in Enum.GetValues<Element>()
                                            from waits in Enum.GetValues<Await>()
                                            where Allowed(span, pass, element, waits)
                                            select new Cell(span, pass, element, waits)).ToArray();

    [Fact]
    public void Every_cell_gets_its_expected_trigger()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, cell =>
        {
            try { Check(cell); }
            catch (Exception error) { wrong.Enqueue($"{cell}: {error.Message}"); }
        });
        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong.Take(20)));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<SpanKind>(), Cells.Select(cell => cell.Span).Distinct().Order());
        Assert.Equal(Enum.GetValues<Pass>(), Cells.Select(cell => cell.Pass).Distinct().Order());
        Assert.Equal(Enum.GetValues<Element>(), Cells.Select(cell => cell.Element).Distinct().Order());
        Assert.Equal(Enum.GetValues<Await>(), Cells.Select(cell => cell.Await).Distinct().Order());
    }

    [Fact]
    public void No_cell_is_left_out_by_Allowed()
    {
        Assert.Equal(2 * 4 * 3 * 2, Cells.Length);
    }

    [Fact]
    public void A_ref_struct_other_than_a_span_leaves_its_member_uncovered()
    {
        // The library's own ref struct, and a framework one declared inside Span<T>, are not spans: the driver builds neither, while
        // the span member beside them is a trigger.
        var driver = Synthesize("""
            public ref struct Cursor { public int Position; }
            public sealed class Holder<T>
            {
                private Action _a;
                public Holder(Action a) { _a = a; }
                public void Scan(Cursor c) { }
                public void Walk(Span<byte>.Enumerator e) { }
                public void Take(int n, Span<byte> s) { }
            }
            """);

        var uncovered = driver.Uncovered[FateClassifier.RESULT];
        Assert.Contains(uncovered, why => why.StartsWith("M:Lib.Holder`1.Scan(Lib.Cursor): ", StringComparison.Ordinal) &&
                                          why.EndsWith("needs a value of Lib.Cursor, a ref-like or pointer type", StringComparison.Ordinal));
        Assert.Contains(uncovered, why => why.StartsWith("M:Lib.Holder`1.Walk(System.Span{System.Byte}.Enumerator): ", StringComparison.Ordinal) &&
                                          why.EndsWith(", a ref-like or pointer type", StringComparison.Ordinal));
        Assert.DoesNotContain(driver.Triggers, trigger => trigger.Member.StartsWith("M:Lib.Holder`1.Scan(", StringComparison.Ordinal) ||
                                                          trigger.Member.StartsWith("M:Lib.Holder`1.Walk(", StringComparison.Ordinal));
        Assert.DoesNotContain(uncovered, why => why.StartsWith(TAKE, StringComparison.Ordinal));
        Assert.Single(driver.Triggers, trigger => trigger.Member.StartsWith(TAKE, StringComparison.Ordinal));
        Assert.False(driver.Covers(FateClassifier.RESULT));
    }

    [Fact]
    public void A_namesake_span_leaves_its_member_uncovered()
    {
        // Spans named as the runtime's but nested in a type of the namespace System, or declared by the library itself in that
        // namespace, are not spans: the driver builds none of them, while the runtime's span member beside the nested ones is a trigger.
        const string HOLDER = """
            namespace Lib
            {
                public sealed class Holder<T>
                {
                    private Action _a;
                    public Holder(Action a) { _a = a; }
                    public void TakeSpan(int n, PREFIXSpan<byte> s) { }
                    public void TakeRead(int n, PREFIXReadOnlySpan<byte> s) { }
                    EXTRA
                }
            }
            """;
        const string NAMESAKES = """
            public ref struct Span<T> { public Span(T[] items) { } }
            public ref struct ReadOnlySpan<T> { public ReadOnlySpan(T[] items) { } }
            """;
        var nested = SynthesizeSource("using System;\n\nnamespace System { public static class Other { " + NAMESAKES + " } }\n" +
                                      HOLDER.Replace("PREFIX", "Other.").Replace("EXTRA", "public void Take(int n, Span<byte> s) { }"));
        var own = SynthesizeSource("using System;\n\nnamespace System { " + NAMESAKES + " }\n" + HOLDER.Replace("PREFIX", "").Replace("EXTRA", ""));

        // bounded: the two libraries above.
        foreach (var (driver, type) in new[] { (nested, "System.Other."), (own, "System.") })
        {
            var uncovered = driver.Uncovered[FateClassifier.RESULT];
            // bounded: the two namesakes.
            foreach (var (member, span) in new[] { ("TakeSpan", "Span"), ("TakeRead", "ReadOnlySpan") })
            {
                var id = $"M:Lib.Holder`1.{member}(System.Int32,{type}{span}{{System.Byte}})";
                Assert.Contains(uncovered, why => why.StartsWith(id + ": ", StringComparison.Ordinal) &&
                                                  why.EndsWith(", a ref-like or pointer type", StringComparison.Ordinal));
                Assert.DoesNotContain(driver.Triggers, trigger => trigger.Member == id);
            }
            Assert.False(driver.Covers(FateClassifier.RESULT));
        }
        Assert.DoesNotContain(nested.Uncovered[FateClassifier.RESULT], why => why.StartsWith(TAKE, StringComparison.Ordinal));
        Assert.Single(nested.Triggers, trigger => trigger.Member.StartsWith(TAKE, StringComparison.Ordinal));
    }

    [Fact]
    public void A_pointer_argument_leaves_its_member_uncovered()
    {
        var driver = Synthesize("""
            public sealed class Holder<T>
            {
                private Action _a;
                public Holder(Action a) { _a = a; }
                public unsafe void Point(int* p) { }
            }
            """);

        var uncovered = driver.Uncovered[FateClassifier.RESULT];
        Assert.Contains(uncovered, why => why.StartsWith("M:Lib.Holder`1.Point(System.Int32*): ", StringComparison.Ordinal) &&
                                          why.EndsWith(", a ref-like or pointer type", StringComparison.Ordinal));
        Assert.DoesNotContain(driver.Triggers, trigger => trigger.Member.StartsWith("M:Lib.Holder`1.Point(", StringComparison.Ordinal));
        Assert.False(driver.Covers(FateClassifier.RESULT));
    }

    // Every combination is one C# admits: a span's element may be any of the three, a span may be passed by value, in, ref readonly
    // or ref to a method returning a task, and an async action may declare a span local that does not live across its await.
    private static bool Allowed(SpanKind span, Pass pass, Element element, Await waits) => true;

    private static void Check(Cell cell)
    {
        var driver = Synthesize(Types(cell, control: false));
        var control = Synthesize(Types(cell, control: true));

        // The member is a trigger, and the holder is covered.
        Assert.DoesNotContain(driver.Uncovered[FateClassifier.RESULT], why => why.StartsWith(TAKE, StringComparison.Ordinal));
        var trigger = Assert.Single(driver.Triggers, trigger => trigger.Holder == FateClassifier.RESULT && trigger.Member.StartsWith(TAKE, StringComparison.Ordinal));
        Assert.True(driver.Covers(FateClassifier.RESULT), string.Join("; ", driver.Uncovered[FateClassifier.RESULT]));
        var controlTrigger = Assert.Single(control.Triggers, other => other.Holder == FateClassifier.RESULT && other.Member.StartsWith(TAKE, StringComparison.Ordinal));
        Assert.Equal(controlTrigger.Action, trigger.Action);

        // The field is a T[] of two elements, and everything but the trigger's action is what the member taking T[] gets.
        var field = $"TArg_{trigger.Action}_{ORDINAL}";
        var driverType = driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE)!;
        var fieldSymbol = Assert.Single(driverType.GetMembers(field).OfType<IFieldSymbol>());
        var array = Assert.IsAssignableFrom<IArrayTypeSymbol>(fieldSymbol.Type);
        Assert.Equal(1, array.Rank);
        Assert.Equal(control.Compilation.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE)!.GetMembers(field).OfType<IFieldSymbol>().Single().Type.ToDisplayString(),
                     array.ToDisplayString());
        Assert.Equal(WithoutAction(control, trigger.Action), WithoutAction(driver, trigger.Action));

        var tree = driver.Compilation.SyntaxTrees.Single();
        var model = driver.Compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
        var setup = model.GetOperation(methods.Single(method => method.Identifier.Text == DriverSynthesizer.SETUP))!;
        var seed = Assert.Single(setup.Descendants().OfType<ISimpleAssignmentOperation>(),
                                 assignment => assignment.Target is IFieldReferenceOperation reference && reference.Field.Name == field);
        var creation = Assert.IsAssignableFrom<IArrayCreationOperation>(Unconverted(seed.Value));
        Assert.Equal(2, creation.Initializer!.ElementValues.Length);

        // The call passes a span built from the field, in the way the member takes it, awaited when it returns a task.
        var action = model.GetOperation(methods.Single(method => method.Identifier.Text == trigger.Action))!;
        var call = Assert.Single(action.Descendants().OfType<IInvocationOperation>(), invocation => invocation.TargetMethod.Name == "Take");
        Assert.Equal(cell.Await == Await.Async, call.Parent is IAwaitOperation);
        var argument = call.Arguments.Single(argument => argument.Parameter!.Ordinal == ORDINAL);
        Assert.Equal(cell.Pass switch { Pass.Value => RefKind.None, Pass.In => RefKind.In, Pass.RefReadOnly => RefKind.RefReadOnlyParameter, _ => RefKind.Ref },
                     argument.Parameter!.RefKind);
        var expectedKeyword = cell.Pass switch { Pass.Value => "", Pass.Ref => "ref", _ => "in" };
        Assert.Equal(expectedKeyword, ((ArgumentSyntax)argument.Syntax).RefKindKeyword.Text);
        var span = cell.Pass == Pass.Value
            ? Unconverted(argument.Value)
            : Assert.Single(action.Descendants().OfType<IVariableDeclaratorOperation>(),
                            declarator => SymbolEqualityComparer.Default.Equals(declarator.Symbol, ((ILocalReferenceOperation)argument.Value).Local)).Initializer!.Value;
        var spanCreation = Assert.IsAssignableFrom<IObjectCreationOperation>(span);
        Assert.Equal(cell.Span == SpanKind.Span ? "Span`1" : "ReadOnlySpan`1", spanCreation.Type!.OriginalDefinition.MetadataName);
        var source = Assert.IsAssignableFrom<IFieldReferenceOperation>(Unconverted(Assert.Single(spanCreation.Arguments).Value));
        Assert.Equal(field, source.Field.Name);
    }

    /// <summary>A cell's library: a generic holder made by its constructor, whose <c>Take</c> takes an <c>int</c> and then the span
    /// of the cell, or for the control, an array of its element in the span's place.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="control">Whether <c>Take</c> takes the array in the span's place.</param>
    private static string Types(Cell cell, bool control)
    {
        var element = cell.Element switch { Element.Immutable => "byte", Element.Holding => "Holding", _ => "T" };
        var type = control ? $"{element}[]" : $"{cell.Span}<{element}>";
        var pass = cell.Pass switch { Pass.In => "in ", Pass.RefReadOnly => "ref readonly ", Pass.Ref => "ref ", _ => "" };
        var take = cell.Await == Await.Async ? $"public Task Take(int n, {pass}{type} s) => Task.CompletedTask;" : $"public void Take(int n, {pass}{type} s) {{ }}";
        return $$"""
            public class Holding { public object Held; public Holding() { } }
            public sealed class Holder<T>
            {
                private Action _a;
                public Holder(Action a) { _a = a; }
                {{take}}
            }
            """;
    }

    /// <summary>The driver's source without one action's method.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="action">The action.</param>
    private static string WithoutAction(Driver driver, string action)
    {
        var root = driver.Compilation.SyntaxTrees.Single().GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(declaration => declaration.Identifier.Text == action);
        return root.RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
    }

    private static IOperation Unconverted(IOperation operation) =>
        operation is IConversionOperation { IsImplicit: true } conversion ? Unconverted(conversion.Operand) : operation;

    private static Driver Synthesize(string types) =>
        SynthesizeSource("using System;\nusing System.Threading.Tasks;\n\nnamespace Lib\n{\n" + types + "\n}\n");

    /// <summary>The driver of <see cref="MEMBER"/> in a library compiled from a whole source.</summary>
    /// <param name="source">The library's source.</param>
    private static Driver SynthesizeSource(string source)
    {
        var library = Compile(source);
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var member = Assert.IsAssignableFrom<ISymbol>(DriverSynthesizer.FindMember(library.Compilation, MEMBER));
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Empty(synthesis.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return synthesis.Driver;
    }
}
