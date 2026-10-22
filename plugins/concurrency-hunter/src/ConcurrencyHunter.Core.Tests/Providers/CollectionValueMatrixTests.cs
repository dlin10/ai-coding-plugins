using System.Collections.Concurrent;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The value the driver builds for a concrete ADR 0010 collection at every place that asks for one: the class itself, never a
/// subclass, with two insertions of values built for each axis's own type; a witness's return or <c>out</c> and a seed field as before,
/// with one insertion of a seed value. The receiver of a collection's own member is no place: the engine recognizes those members, so
/// no driver is built for them.</summary>
public sealed class CollectionValueMatrixTests
{
    private enum Collection { List, Dictionary, ConcurrentDictionary, ConcurrentQueue, ConcurrentStack, ConcurrentBag, HashSet, Queue, Stack, LinkedList }
    private enum Place
    {
        Argument, NestedConstructorArgument, NestedFactoryArgument, BaseConstructorArgument, PairContent, TaskContent, TriggerArgument,
        LambdaReturn, WitnessReturn, WitnessOut, SeedField
    }

    /// <summary>The type of an element or of a dictionary's value: <c>Holding</c>, a non-sealed library class with an <c>object</c>
    /// field; <c>Immutable</c>, <c>int</c>.</summary>
    private enum Element { Holding, Immutable }

    /// <summary>The type of a dictionary's key, as <see cref="Element"/>; <c>None</c> for a collection without one.</summary>
    private enum Key { None, Holding, Immutable }

    private sealed record Cell(Collection Collection, Place Place, Element Element, Key Key);

    private static readonly Cell[] Cells = (from collection in Enum.GetValues<Collection>()
                                            from place in Enum.GetValues<Place>()
                                            from element in Enum.GetValues<Element>()
                                            from key in Enum.GetValues<Key>()
                                            where Allowed(collection, key)
                                            select new Cell(collection, place, element, key)).ToArray();

    private static readonly ConcurrentDictionary<Cell, Lazy<Driver>> Drivers = new();

    [Fact]
    public void Every_cell_gets_its_expected_value()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, cell =>
        {
            try { Check(cell); }
            catch (Exception error) { wrong.Enqueue($"{cell}: {error.Message}"); }
        });
        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong.Take(30)));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Collection>(), Cells.Select(cell => cell.Collection).Distinct().Order());
        Assert.Equal(Enum.GetValues<Place>(), Cells.Select(cell => cell.Place).Distinct().Order());
        Assert.Equal(Enum.GetValues<Element>(), Cells.Select(cell => cell.Element).Distinct().Order());
        Assert.Equal(Enum.GetValues<Key>(), Cells.Select(cell => cell.Key).Distinct().Order());
        // A dictionary's mixed pairs occur both ways round.
        Assert.Contains(Cells, cell => cell is { Key: Key.Holding, Element: Element.Immutable });
        Assert.Contains(Cells, cell => cell is { Key: Key.Immutable, Element: Element.Holding });
    }

    [Fact]
    public void No_cell_declares_a_subclass_of_its_collection()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, cell =>
        {
            try
            {
                var driver = Synthesize(cell);
                var definition = driver.Compilation.GetTypeByMetadataName(MetadataName(cell.Collection))!;
                var tree = driver.Compilation.SyntaxTrees.Single();
                var model = driver.Compilation.GetSemanticModel(tree);
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    for (var type = (model.GetDeclaredSymbol(declaration) as INamedTypeSymbol)?.BaseType; type is not null; type = type.BaseType)
                        Assert.False(SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, definition), $"{declaration.Identifier.Text} derives from {type.ToDisplayString()}");
                }
            }
            catch (Exception error) { wrong.Enqueue($"{cell}: {error.Message}"); }
        });
        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong.Take(30)));
    }

    /// <summary>A collection without a key has none, and a dictionary always has one.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="key">The key's type.</param>
    private static bool Allowed(Collection collection, Key key) => IsDictionary(collection) == (key != Key.None);

    private static void Check(Cell cell)
    {
        var driver = Synthesize(cell);
        var tree = driver.Compilation.SyntaxTrees.Single();
        var model = driver.Compilation.GetSemanticModel(tree);
        var definition = driver.Compilation.GetTypeByMetadataName(MetadataName(cell.Collection))!;
        var operations = tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
                             .Select(declaration => model.GetOperation(declaration)).OfType<IOperation>()
                             .SelectMany(operation => operation.DescendantsAndSelf()).ToArray();

        // The value is an instance of the class itself.
        var creation = Assert.Single(operations.OfType<IObjectCreationOperation>(),
                                     created => SymbolEqualityComparer.Default.Equals(created.Type?.OriginalDefinition, definition));
        Assert.Equal(DisplayName(cell), creation.Type!.ToDisplayString());

        // Each insertion's values, built for each side by its own axis: Value's in a value, a seed where a seed is due.
        var witnessed = cell.Place is Place.WitnessReturn or Place.WitnessOut or Place.SeedField;
        var holding = cell.Element == Element.Holding || cell.Key == Key.Holding;
        var insertions = Insertions(operations, definition, cell.Collection).ToArray();
        var expected = !witnessed ? 2 : holding || cell.Place == Place.SeedField ? 1 : 0;
        Assert.Equal(expected, insertions.Length);
        var sides = IsDictionary(cell.Collection) ? new[] { cell.Key == Key.Holding, cell.Element == Element.Holding } : [cell.Element == Element.Holding];
        foreach (var values in insertions)
        {
            Assert.Equal(sides.Length, values.Length);
            for (var side = 0; side < sides.Length; side++)
            {
                var text = Resolve(values[side], operations).Syntax.ToString();
                if (!sides[side])
                    Assert.Equal("default(global::System.Int32)", text);
                else
                    Assert.StartsWith(witnessed ? "new Seed_" : "new Sub_", text);
            }
        }

        // A holding side of an argument has its element's field seeded through the argument's elements, a dictionary's keys or values.
        if (cell.Place != Place.Argument)
            return;
        var enumerated = IsDictionary(cell.Collection)
            ? new[] { (cell.Key == Key.Holding, "Arg_value_Call.Keys"), (cell.Element == Element.Holding, "Arg_value_Call.Values") }
            : [(cell.Element == Element.Holding, "Arg_value_Call")];
        foreach (var (seeded, elements) in enumerated)
        {
            var seeds = driver.SeedStatements.Where(seed => seed.Path == "arg:value/F:Lib.Item.Child" &&
                                                            seed.Operation.Syntax.Ancestors().OfType<ForEachStatementSyntax>().FirstOrDefault()?.Expression.ToString() == elements)
                              .ToArray();
            Assert.Equal(seeded ? 1 : 0, seeds.Length);
            if (seeded)
                Assert.Equal("Child", Assert.IsAssignableFrom<IFieldReferenceOperation>(Assert.IsAssignableFrom<ISimpleAssignmentOperation>(seeds[0].Operation).Target).Field.Name);
        }
    }

    /// <summary>The values of every insertion into a collection of the definition through its table member: an element, or a
    /// dictionary's key and value.</summary>
    /// <param name="operations">Every operation of the driver.</param>
    /// <param name="definition">The collection's definition.</param>
    /// <param name="collection">The collection.</param>
    private static IEnumerable<IOperation[]> Insertions(IOperation[] operations, INamedTypeSymbol definition, Collection collection)
    {
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case IInvocationOperation invocation when invocation.TargetMethod.Name == InsertionMember(collection) &&
                                                          SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType.OriginalDefinition, definition):
                    yield return invocation.Arguments.Select(argument => argument.Value).ToArray();
                    break;
                case ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.IsIndexer: true } indexer } assignment
                    when collection == Collection.Dictionary && SymbolEqualityComparer.Default.Equals(indexer.Property.ContainingType.OriginalDefinition, definition):
                    yield return [indexer.Arguments.Single().Value, assignment.Value];
                    break;
            }
        }
    }

    /// <summary>The expression a value comes from: past its implicit conversions, and from a factory's parameter to the argument its
    /// one call passes.</summary>
    /// <param name="value">The value.</param>
    /// <param name="operations">Every operation of the driver.</param>
    private static IOperation Resolve(IOperation value, IOperation[] operations)
    {
        while (value is IConversionOperation { IsImplicit: true } conversion)
            value = conversion.Operand;
        if (value is not IParameterReferenceOperation { Parameter: { ContainingSymbol: IMethodSymbol factory } parameter })
            return value;
        var call = Assert.Single(operations.OfType<IInvocationOperation>(), invocation => SymbolEqualityComparer.Default.Equals(invocation.TargetMethod, factory));
        return Resolve(call.Arguments.Single(argument => argument.Parameter?.Ordinal == parameter.Ordinal).Value, operations);
    }

    private static Driver Synthesize(Cell cell) => Drivers.GetOrAdd(cell, key => new Lazy<Driver>(() =>
    {
        // Stack<T> and the concurrent collections live in assemblies of their own, outside the fixtures' runtime references.
        var library = LibraryCompilation.CompileTrees(ASSEMBLY, [CSharpSyntaxTree.ParseText(LibrarySource(key), new CSharpParseOptions(LanguageVersion.Preview), $"{ASSEMBLY}/Library.cs")],
                                                      EmittedAssemblies.RuntimeReferences.Concat(new[] { typeof(Stack<>), typeof(ConcurrentBag<>) }
                                                                                                     .Select(type => MetadataReference.CreateFromFile(type.Assembly.Location)))
                                                                         .ToArray(),
                                                      CancellationToken.None);
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var member = library.Compilation.GetTypeByMetadataName("Lib.Api")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Empty(synthesis.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return synthesis.Driver;
    })).Value;

    private static string LibrarySource(Cell cell)
    {
        var type = TypeText(cell);
        var route = cell.Place switch
        {
            Place.Argument => $"public static class Api {{ public static void Run({type} value) {{ }} }}",
            Place.NestedConstructorArgument => $"public sealed class Wrap {{ public Wrap({type} value) {{ }} }} public static class Api {{ public static void Run(Wrap value) {{ }} }}",
            Place.NestedFactoryArgument => $"public sealed class Wrap {{ private Wrap() {{ }} public static Wrap Create({type} value) => throw null; }} public static class Api {{ public static void Run(Wrap value) {{ }} }}",
            Place.BaseConstructorArgument => $"public abstract class User {{ protected User({type} value) {{ }} }} public static class Api {{ public static void Run(User user) {{ }} }}",
            Place.PairContent => $"public static class Api {{ public static void Run(KeyValuePair<string, {type}> value) {{ }} }}",
            Place.TaskContent => $"public static class Api {{ public static void Run(Task<{type}> value) {{ }} }}",
            Place.TriggerArgument => $"public sealed class Holder {{ public void Fire({type} value) {{ }} }} public static class Api {{ public static Holder Run(Action done) => new Holder(); }}",
            Place.LambdaReturn => $"public static class Api {{ public static void Run(Func<{type}> make) {{ }} }}",
            Place.WitnessReturn => $"public interface User {{ {type} Make(); }} public static class Api {{ public static void Run(User user) {{ }} }}",
            Place.WitnessOut => $"public interface User {{ void Make(out {type} value); }} public static class Api {{ public static void Run(User user) {{ }} }}",
            _ => $"public sealed class Holder {{ public {type} Values; }} public static class Api {{ public static void Run(Holder holder) {{ }} }}"
        };
        return "using System; using System.Collections.Concurrent; using System.Collections.Generic; using System.Threading.Tasks; namespace Lib { " +
               "public class Item { public object Child; } " + route + " }";
    }

    private static bool IsDictionary(Collection collection) => collection is Collection.Dictionary or Collection.ConcurrentDictionary;

    private static string Namespace(Collection collection) =>
        collection.ToString().StartsWith("Concurrent", StringComparison.Ordinal) ? "System.Collections.Concurrent" : "System.Collections.Generic";

    private static string MetadataName(Collection collection) => $"{Namespace(collection)}.{collection}`{(IsDictionary(collection) ? 2 : 1)}";

    private static string InsertionMember(Collection collection) => collection switch
    {
        Collection.ConcurrentDictionary => "TryAdd",
        Collection.ConcurrentQueue or Collection.Queue => "Enqueue",
        Collection.ConcurrentStack or Collection.Stack => "Push",
        Collection.LinkedList => "AddLast",
        _ => "Add"
    };

    private static string TypeText(Cell cell) =>
        $"{cell.Collection}<{(IsDictionary(cell.Collection) ? Side(cell.Key == Key.Holding) + ", " : "")}{Side(cell.Element == Element.Holding)}>";

    private static string DisplayName(Cell cell) => $"{Namespace(cell.Collection)}.{TypeText(cell).Replace("Item", "Lib.Item", StringComparison.Ordinal)}";

    private static string Side(bool holding) => holding ? "Item" : "int";
}
