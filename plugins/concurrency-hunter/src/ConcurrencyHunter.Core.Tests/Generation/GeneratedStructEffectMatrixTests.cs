using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The generator on struct storage (R7): one compiled fixture, one member per storage/route/action cell, each answer written
/// from the storage the write finally reaches, whatever route reaches it. The class twin of a cell on library storage is the same member
/// with the struct declared as a class kept in the same place; a store of a whole struct holding <c>p</c> has the twin of a store of
/// <c>p</c> into that place.</summary>
/// <param name="cache">The class fixture that keeps the matrix's answers for every test of the class.</param>
public sealed class GeneratedStructEffectMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private Matrix Results => cache.Get("results", Run);

    /// <summary>The storage the write finally reaches.</summary>
    public enum Storage
    {
        ReceiverField,
        ReceiverArrayCell,
        ReceiverNestedField,
        StaticField,
        StaticArrayCell,
        StructReceiverField,
        RefParameter,
        OutParameter,
        ByValueParameter
    }

    /// <summary>How the member reaches the storage.</summary>
    public enum Route
    {
        Directly,
        HelperRefParameter,
        RefLocal,
        HelperRefReturn
    }

    /// <summary>What the member does to the storage.</summary>
    public enum Action
    {
        Increment,
        StoreObject,
        MutatingCall,
        StoreWholeStruct
    }

    /// <summary>One matrix cell.</summary>
    /// <param name="Storage">The storage the write reaches.</param>
    /// <param name="Route">The route to it.</param>
    /// <param name="Action">The action on it.</param>
    /// <param name="Twin">Whether the cell is the class twin: the struct declared as a class in the same place.</param>
    public sealed record Cell(Storage Storage, Route Route, Action Action, bool Twin = false)
    {
        public string Name => $"{Storage}/{Route}/{Action}{(Twin ? "/twin" : "")}";

        public string Type => $"C_{Storage}_{Route}_{Action}{(Twin ? "_Twin" : "")}";

        public override string ToString() => Name;
    }

    /// <summary>The generated answers of every cell and every twin.</summary>
    /// <param name="Answers">The answer by cell.</param>
    private sealed record Matrix(IReadOnlyDictionary<Cell, GeneratedAnswer> Answers);

    private const string PRELUDE = """
        using System;

        namespace Lib
        {
            public sealed class Item { public object Value; }
            public struct S { public int Count; public object Ref; public void Bump() { Count++; } }
            public struct Outer { public S Inner; }
            public sealed class K { public int Count; public object Ref; public void Bump() { Count++; } }
            public sealed class OuterK { public K Inner = new K(); }

            public static class Ops
            {
                public static void Increment(ref S x) { x.Count++; }
                public static void Keep(ref S x, Item p) { x.Ref = p; }
                public static void Bump(ref S x) { x.Bump(); }
                public static void Assign(ref S x, S w) { x = w; }
                public static ref S Identity(ref S x) => ref x;
                public static void Increment(ref K x) { x.Count++; }
                public static void Keep(ref K x, Item p) { x.Ref = p; }
                public static void Bump(ref K x) { x.Bump(); }
                public static ref K Identity(ref K x) => ref x;
            }

        """;

    [Fact]
    public void Every_cell_answers_as_expected()
    {
        var failures = Cells().Select(cell => (Cell: cell, Comparison: Compare(cell, Results.Answers[cell])))
                              .Where(pair => !pair.Comparison.IsExact)
                              .Select(pair => $"{pair.Cell.Name}: {pair.Comparison.Detail}; got {Show(Results.Answers[pair.Cell])}")
                              .ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} cell(s) off the table:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_route_answers_as_its_storage()
    {
        var failures = Cells().GroupBy(cell => (cell.Storage, cell.Action))
                              .Where(group => group.Select(cell => Shape(Results.Answers[cell])).Distinct(StringComparer.Ordinal).Count() > 1)
                              .Select(group => $"{group.Key.Storage}/{group.Key.Action}: " +
                                               string.Join("; ", group.Select(cell => $"{cell.Route} {Show(Results.Answers[cell])}")))
                              .ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} storage(s) answered by route:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void No_cell_gets_a_model_that_misses_its_effect()
    {
        // A model is right only where it describes the struct-storage effect: where the truth has no entry there is none, and where it
        // has one the model names at least what the write did there.
        var failures = Cells().Where(cell => Results.Answers[cell].Model is not null)
                              .Select(cell => (Cell: cell, Comparison: Compare(cell, Results.Answers[cell])))
                              .Where(pair => pair.Comparison.IsUnsafeNarrowing)
                              .Select(pair => $"{pair.Cell.Name}: {pair.Comparison.Detail}; got {Show(Results.Answers[pair.Cell])}")
                              .ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} cell(s) got a model that misses the effect:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Each_expectation_matches_its_class_twin()
    {
        var failures = Cells().Where(HasTwin)
                              .Select(cell => (Cell: cell, Comparison: EntryComparator.Compare(Results.Answers[cell with { Twin = true }], Expected(cell),
                                                                                                requireSameModelReason: true)))
                              .Where(pair => !pair.Comparison.IsExact)
                              .Select(pair => $"{pair.Cell.Name}: the twin {pair.Comparison.Detail}; got {Show(Results.Answers[pair.Cell with { Twin = true }])}")
                              .ToArray();

        Assert.True(Cells().Count(HasTwin) > 0);
        Assert.True(failures.Length == 0, $"{failures.Length} expectation(s) the class twin does not give:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();
        var missing = Enum.GetValues<Storage>().Where(value => cells.All(cell => cell.Storage != value)).Select(value => $"storage {value}")
                          .Concat(Enum.GetValues<Route>().Where(value => cells.All(cell => cell.Route != value)).Select(value => $"route {value}"))
                          .Concat(Enum.GetValues<Action>().Where(value => cells.All(cell => cell.Action != value)).Select(value => $"action {value}"))
                          .ToArray();
        var refused = Results.Answers.Where(pair => pair.Value.Classified is null)
                             .Select(pair => $"{pair.Key.Name}: {pair.Value.Reason} {pair.Value.Detail}").ToArray();

        Assert.Equal(Enum.GetValues<Storage>().Length * Enum.GetValues<Route>().Length * Enum.GetValues<Action>().Length, cells.Length);
        Assert.True(missing.Length == 0, "No cell has " + string.Join(", ", missing));
        Assert.True(refused.Length == 0, $"{refused.Length} cell(s) got no classification:\n" + string.Join("\n", refused));
    }

    private static EntryComparison Compare(Cell cell, GeneratedAnswer answer) =>
        EntryComparator.Compare(answer, Expected(cell), requireSameModelReason: true);

    /// <summary>R7, by the storage the write reaches: library storage — the receiver's field, cell and nested struct, a static field or a
    /// static array's cell — is library state for a scalar write and a mutating call, and for a store of <c>p</c> or of a whole struct
    /// holding it what the same store into a class kept there gives; the caller's storage — the struct receiver's own field, what a
    /// <c>ref</c> or <c>out</c> parameter refers to — is <c>vocabulary</c>; a by-value parameter's copy is no effect.</summary>
    /// <param name="cell">The cell.</param>
    private static EntryTruth Expected(Cell cell) => cell.Storage switch
    {
        // The receiver keeps p wherever in its own storage it is stored, as it does in a class it holds.
        Storage.ReceiverField or Storage.ReceiverArrayCell or Storage.ReceiverNestedField when cell.Action is Action.StoreObject or Action.StoreWholeStruct =>
            EntryTruth.Entry(new LibraryModel("M:Lib.Cell.M", [], [])
            {
                Keeps = new Dictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal) { ["this"] = [LibraryValue.Parse("arg:p")!] }
            }),
        Storage.ReceiverField or Storage.ReceiverArrayCell or Storage.ReceiverNestedField or Storage.StaticField or Storage.StaticArrayCell =>
            EntryTruth.NoEntry(ModelReasons.LIBRARY_STATE),
        Storage.StructReceiverField or Storage.RefParameter or Storage.OutParameter => EntryTruth.NoEntry(ModelReasons.VOCABULARY),
        Storage.ByValueParameter => EntryTruth.Entry(new LibraryModel("M:Lib.Cell.M", [], [])),
        _ => throw new InvalidOperationException($"Unknown storage {cell.Storage}.")
    };

    /// <summary>Whether a cell's storage is library storage, where a class can stand in the same place.</summary>
    /// <param name="cell">The cell.</param>
    private static bool HasTwin(Cell cell) => !cell.Twin && cell.Storage is Storage.ReceiverField or Storage.ReceiverArrayCell or
        Storage.ReceiverNestedField or Storage.StaticField or Storage.StaticArrayCell;

    private static IEnumerable<Cell> Cells() =>
        from storage in Enum.GetValues<Storage>()
        from route in Enum.GetValues<Route>()
        from action in Enum.GetValues<Action>()
        select new Cell(storage, route, action);

    /// <summary>Compiles the fixture once and generates every cell and twin in parallel, as the model generator is used per member.</summary>
    private static Matrix Run()
    {
        var cells = Cells().Concat(Cells().Where(HasTwin).Select(cell => cell with { Twin = true })).ToArray();
        var library = Compile(Source(cells));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.Equal(0, library.ExternBodies);
        var answers = new ConcurrentDictionary<Cell, GeneratedAnswer>();
        Parallel.ForEach(cells, CellParallelism, cell =>
        {
            var request = new GenerationRequest(ASSEMBLY, "1.0", MemberId(library, cell), null, null);
            answers[cell] = ModelGenerator.Trace(request, library, CancellationToken.None).Answer;
        });
        return new Matrix(answers);
    }

    private static string Source(IEnumerable<Cell> cells)
    {
        var source = new StringBuilder(PRELUDE);
        foreach (var cell in cells)
            source.Append(Declaration(cell));
        return source.Append("}\n").ToString();
    }

    private static string Declaration(Cell cell)
    {
        var type = cell.Twin ? "K" : "S";
        var parameters = new List<string> { "Item p", $"{type} w" };
        parameters.AddRange(cell.Storage switch
        {
            Storage.RefParameter => [$"ref {type} value"],
            Storage.OutParameter => [$"out {type} value"],
            Storage.ByValueParameter => [$"{type} value"],
            _ => []
        });

        var (fields, place) = cell.Storage switch
        {
            Storage.ReceiverField => (cell.Twin ? "private K _s = new K();" : "private S _s;", "_s"),
            Storage.ReceiverArrayCell => (cell.Twin ? "private K[] _cells = { new K() };" : "private S[] _cells = new S[4];", "_cells[0]"),
            Storage.ReceiverNestedField => (cell.Twin ? "private OuterK _o = new OuterK();" : "private Outer _o;", "_o.Inner"),
            Storage.StaticField => (cell.Twin ? "private static K s_s = new K();" : "private static S s_s;", "s_s"),
            Storage.StaticArrayCell => (cell.Twin ? "private static K[] s_cells = { new K() };" : "private static S[] s_cells = new S[4];", "s_cells[0]"),
            Storage.StructReceiverField => ("private S _s;", "_s"),
            _ => ("", "value")
        };

        var body = new List<string>();
        if (cell.Storage == Storage.OutParameter)
            body.Add("value = default;");
        // The twin of a store of a whole struct holding p is the store of p into the class kept there.
        var action = cell.Twin && cell.Action == Action.StoreWholeStruct ? Action.StoreObject : cell.Action;
        // The whole struct stored is the member's own argument w, whose reference field the member sets to p first: at the store it holds
        // p, which the store carries into the place as any store does (R7).
        if (action == Action.StoreWholeStruct)
            body.Add("w.Ref = p;");
        body.AddRange(Act(cell.Route, action, place));

        var kind = cell.Storage == Storage.StructReceiverField ? "struct" : "sealed class";
        var source = new StringBuilder().Append("    public ").Append(kind).Append(' ').Append(cell.Type).Append("\n    {\n");
        if (fields.Length != 0)
            source.Append("        ").Append(fields).Append('\n');
        source.Append("        public void M(").Append(string.Join(", ", parameters)).Append(")\n        {\n");
        foreach (var statement in body)
            source.Append("            ").Append(statement).Append('\n');
        return source.Append("        }\n    }\n").ToString();
    }

    /// <summary>The statements acting on a place by a route.</summary>
    /// <param name="route">The route.</param>
    /// <param name="action">The action.</param>
    /// <param name="place">The place, an expression that is a variable.</param>
    private static IReadOnlyList<string> Act(Route route, Action action, string place) => route switch
    {
        Route.Directly => [Direct(action, place)],
        Route.HelperRefParameter => [action switch
        {
            Action.Increment => $"Ops.Increment(ref {place});",
            Action.StoreObject => $"Ops.Keep(ref {place}, p);",
            Action.MutatingCall => $"Ops.Bump(ref {place});",
            _ => $"Ops.Assign(ref {place}, w);"
        }],
        Route.RefLocal => [$"ref var x = ref {place};", Direct(action, "x")],
        Route.HelperRefReturn => [Direct(action, $"Ops.Identity(ref {place})")],
        _ => throw new InvalidOperationException($"Unknown route {route}.")
    };

    private static string Direct(Action action, string place) => action switch
    {
        Action.Increment => $"{place}.Count++;",
        Action.StoreObject => $"{place}.Ref = p;",
        Action.MutatingCall => $"{place}.Bump();",
        _ => $"{place} = w;"
    };

    private static string MemberId(LibraryCompilationResult library, Cell cell) =>
        library.Compilation!.GetTypeByMetadataName($"Lib.{cell.Type}")!.GetMembers("M").OfType<IMethodSymbol>().Single().GetDocumentationCommentId()!;

    /// <summary>An answer without what names its member: the entry, or the model reason.</summary>
    /// <param name="answer">The answer.</param>
    private static string Shape(GeneratedAnswer answer) => answer.Model is { } model
        ? ModelEntryWriter.Write(model with { Id = "M" }).ToJsonString()
        : answer.ModelReason ?? answer.Reason ?? "none";

    private static string Show(GeneratedAnswer answer) => answer.Model is { } model
        ? ModelEntryWriter.Write(model).ToJsonString()
        : $"{answer.ModelReason ?? answer.Reason}: {answer.Detail}";
}
