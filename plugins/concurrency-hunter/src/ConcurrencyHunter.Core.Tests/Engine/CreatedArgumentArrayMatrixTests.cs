using System.Collections.Concurrent;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The array an unresolved call creates for its arguments is a base like any other for <c>unresolved-base</c> (R7, TD-124): a
/// controller action hands an opaque library one or two elements through each way C# creates such an array, and the counter is read
/// against the same action without the call. An element that should name heap storage and resolves to no region — an object the action
/// is handed by code the analysis does not see — counts the operation once; a resolved object, a string or a number, a struct copy and a
/// fresh object do not. The unknown effect starts from the elements, never from the array.</summary>
public sealed class CreatedArgumentArrayMatrixTests
{
    /// <summary>How the call creates the array of its arguments.</summary>
    public enum Creation
    {
        /// <summary><c>Lib.Array&lt;T&gt;(a, b)</c> on a <c>params T[]</c>.</summary>
        ParamsArray,

        /// <summary><c>Lib.Sequence&lt;T&gt;(a, b)</c> on a <c>params IEnumerable&lt;T&gt;</c>.</summary>
        ParamsCollection,

        /// <summary><c>Lib.Take&lt;T&gt;(new T[] { a, b })</c>.</summary>
        ExplicitArray,

        /// <summary><c>Lib.Take&lt;T&gt;([a, b])</c>.</summary>
        CollectionExpression
    }

    /// <summary>What an element is.</summary>
    public enum Element
    {
        /// <summary>No element: the call has one.</summary>
        None,

        /// <summary>An object the action is handed by code the analysis does not see: it should name heap storage and resolves to no
        /// region.</summary>
        Unresolved,

        /// <summary>An object a singleton holds.</summary>
        Resolved,

        /// <summary>A string or a number.</summary>
        StringOrNumber,

        /// <summary>A struct local, a copy.</summary>
        StructCopy,

        /// <summary><c>new Box()</c> in the argument.</summary>
        FreshObject
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Creation">How the array is created.</param>
    /// <param name="First">The first element.</param>
    /// <param name="Second">The second element, or none.</param>
    private sealed record Cell(Creation Creation, Element First, Element Second)
    {
        public override string ToString() => $"{Creation}/{First}/{Second}";
    }

    /// <summary>The cells whose outcome is known to be wider than the requirement asks: none.</summary>
    private static readonly Cell[] KnownWider = [];

    private static readonly ConcurrentDictionary<string, Lazy<EngineRun>> Runs = new(StringComparer.Ordinal);

    private static readonly Lazy<int> Empty = new(() => Run("").Counter(CoverageCounters.UNRESOLVED_BASE));

    private static IEnumerable<Cell> AllCells() =>
        from creation in Enum.GetValues<Creation>()
        from first in Enum.GetValues<Element>().Where(element => element != Element.None)
        from second in Enum.GetValues<Element>()
        select new Cell(creation, first, second);

    private static IEnumerable<Cell> Cells() => AllCells().Where(cell => Excluded(cell) is null);

    /// <summary>Why a cell is left out, or null: one array cannot hold an <c>S</c> and an object without boxing the <c>S</c>, and a box
    /// is a fresh object, which the FreshObject cells hold.</summary>
    /// <param name="cell">The cell.</param>
    private static string? Excluded(Cell cell) =>
        cell.Second != Element.None && (cell.First == Element.StructCopy) != (cell.Second == Element.StructCopy)
            ? "a struct copy beside an object is boxed: a FreshObject cell"
            : null;

    public static TheoryData<Creation, Element, Element> Rows()
    {
        var rows = new TheoryData<Creation, Element, Element>();
        foreach (var cell in Cells())
            rows.Add(cell.Creation, cell.First, cell.Second);
        return rows;
    }

    /// <summary>Whether the call grows <c>unresolved-base</c> by one (R7): exactly where an element is Unresolved.</summary>
    /// <param name="cell">The cell.</param>
    private static bool Expected(Cell cell) => cell.First == Element.Unresolved || cell.Second == Element.Unresolved;

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_cell_counts_as_expected(Creation creation, Element first, Element second)
    {
        var cell = new Cell(creation, first, second);
        Assert.Equal(Expected(cell) ? 1 : 0, Count(cell.Creation, Elements(cell)));
    }

    [Theory]
    [InlineData(Creation.ParamsArray)]
    [InlineData(Creation.ParamsCollection)]
    [InlineData(Creation.ExplicitArray)]
    [InlineData(Creation.CollectionExpression)]
    public void Two_unresolved_elements_count_once(Creation creation)
    {
        Assert.Equal(1, Count(creation, [Element.Unresolved, Element.Unresolved]));
        Assert.Equal(1, Count(creation, [Element.Unresolved, Element.Unresolved, Element.Unresolved]));
    }

    [Theory]
    [InlineData(Creation.ParamsArray)]
    [InlineData(Creation.ParamsCollection)]
    [InlineData(Creation.ExplicitArray)]
    [InlineData(Creation.CollectionExpression)]
    public void An_unresolved_element_counts_beside_any_other(Creation creation)
    {
        foreach (var other in new[] { Element.Resolved, Element.StringOrNumber, Element.FreshObject })
        {
            Assert.True(Count(creation, [Element.Unresolved, other]) == 1, $"{creation}: Unresolved before {other}");
            Assert.True(Count(creation, [other, Element.Unresolved]) == 1, $"{creation}: Unresolved after {other}");
            Assert.True(Count(creation, [other, Element.Unresolved, other]) == 1, $"{creation}: Unresolved between {other}");
        }
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var all = AllCells().ToArray();
        var cells = Cells().ToArray();
        Assert.Empty(KnownWider);
        Assert.Equal(120, all.Length);
        Assert.Equal(32, all.Count(cell => Excluded(cell) is not null));
        Assert.Equal(88, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Creation>(), value => Assert.Contains(cells, cell => cell.Creation == value));
        Assert.All(Enum.GetValues<Element>().Where(value => value != Element.None), value => Assert.Contains(cells, cell => cell.First == value));
        Assert.All(Enum.GetValues<Element>(), value => Assert.Contains(cells, cell => cell.Second == value));
        // Both outcomes occur for every creation, and the struct element type with both elements a copy.
        Assert.All(Enum.GetValues<Creation>(), value => Assert.Contains(cells, cell => cell.Creation == value && Expected(cell)));
        Assert.All(Enum.GetValues<Creation>(), value => Assert.Contains(cells, cell => cell.Creation == value && !Expected(cell)));
        Assert.Contains(cells, cell => cell is { First: Element.StructCopy, Second: Element.StructCopy });
    }

    [Fact]
    public void No_cell_gives_the_effect_to_the_array()
    {
        foreach (var cell in Cells())
        {
            var elements = Elements(cell);
            // An unknown effect on an object the action owns, a fresh box, is a read (CONTEXT.md, Unknown effect).
            var effects = Run(Call(cell.Creation, elements)).Collection.Accesses
                                                            .Where(access => (access.Operation.IsUnknownEffect() || access.Operation == AccessOperation.Read) &&
                                                                             access.Symbol.StartsWith("CaseController.", StringComparison.Ordinal))
                                                            .ToArray();
            Assert.True(!effects.Any(access => access.Resource.Region.Contains("[]", StringComparison.Ordinal)),
                        $"{cell}: the unknown effect reaches the created array");
            // An element the heap names is where the effect starts: it reaches the box's field.
            if (elements.Any(element => element is Element.Resolved or Element.FreshObject))
                Assert.True(effects.Any(access => access.Resource.AccessPath.SequenceEqual(["N"])), $"{cell}: the unknown effect misses the element's field");
        }
    }

    private static Element[] Elements(Cell cell) => cell.Second == Element.None ? [cell.First] : [cell.First, cell.Second];

    /// <summary>What the call adds to <c>unresolved-base</c> beyond the same action without it.</summary>
    /// <param name="creation">How the array is created.</param>
    /// <param name="elements">The elements.</param>
    private static int Count(Creation creation, IReadOnlyList<Element> elements) =>
        Run(Call(creation, elements)).Counter(CoverageCounters.UNRESOLVED_BASE) - Empty.Value;

    /// <summary>The call statement: an array of <c>S</c> where every element is a struct copy, of <c>object</c> otherwise.</summary>
    /// <param name="creation">How the array is created.</param>
    /// <param name="elements">The elements, one variable of each kind per position.</param>
    private static string Call(Creation creation, IReadOnlyList<Element> elements)
    {
        var type = elements.All(element => element == Element.StructCopy) ? "S" : "object";
        var list = string.Join(", ", elements.Select((element, position) => element switch
        {
            Element.Unresolved => $"unseen{position}",
            Element.Resolved => $"_shared.Item{position}",
            Element.StringOrNumber => position % 2 == 0 ? "text" : "count",
            Element.StructCopy => $"s{position}",
            Element.FreshObject => "new Box()",
            _ => throw new ArgumentOutOfRangeException(nameof(elements), element, null)
        }));
        return creation switch
        {
            Creation.ParamsArray => $"Opaque.Lib.Array<{type}>({list});",
            Creation.ParamsCollection => $"Opaque.Lib.Sequence<{type}>({list});",
            Creation.ExplicitArray => $"Opaque.Lib.Take<{type}>(new {type}[] {{ {list} }});",
            Creation.CollectionExpression => $"Opaque.Lib.Take<{type}>([{list}]);",
            _ => throw new ArgumentOutOfRangeException(nameof(creation), creation, null)
        };
    }

    private static EngineRun Run(string call) =>
        Runs.GetOrAdd(call, _ => new Lazy<EngineRun>(() => AnalyzeScope(
                          FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", Source(call))),
                          "scope:Fixture"))).Value;

    /// <summary>A controller action that makes two struct locals and then makes <paramref name="call"/>; the objects its parameters hold
    /// come from code the analysis does not see, and a singleton holds three objects of its own.</summary>
    /// <param name="call">The call statement, empty for the action without it.</param>
    private static string Source(string call) => Usings + $$"""
        public sealed class Box { public int N; }

        public struct S { public int X; public Box Ref; }

        public sealed class Shared
        {
            public Box Item0 = new();
            public Box Item1 = new();
            public Box Item2 = new();
        }

        public class CaseController : ControllerBase
        {
            private readonly Shared _shared;
            public CaseController(Shared shared) { _shared = shared; }

            public void Post(Box unseen0, Box unseen1, Box unseen2, string text, int count)
            {
                var s0 = new S();
                var s1 = new S();
                {{call}}
            }
        }
        """ + Startup("services.AddSingleton<Shared>();");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            namespace Opaque;
            public static class Lib
            {
                public static void Array<T>(params T[] values) { }
                public static void Sequence<T>(params IEnumerable<T> values) { }
                public static void Take<T>(T[] values) { }
            }
            """, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))], StubAssemblies.PlatformWithout([]),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
