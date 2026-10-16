using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The generator on struct storage (R4): one compiled fixture, one member per storage/route/action cell, each answer written
/// from the storage the write finally reaches, whatever route reaches it. A writable reference to the storage answers as the storage
/// itself; a readonly reference answers a member that is not <c>readonly</c> as a defensive copy, and a route that copies the struct
/// out answers as the copy a by-value parameter is; a write into the object the struct's reference field holds answers by the storage,
/// on every route, since a copy holds the same objects — except through a <c>Nullable</c>'s or a pair's contents, a struct TD-034b
/// leaves unseeded, where the read of the reference field gets no model (<c>incomplete</c>). The class twin of a cell on library storage is the same member with the struct
/// declared as a class kept in the same place; a store of a whole struct holding <c>p</c> has the twin of a store of <c>p</c> into that
/// place.</summary>
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

    /// <summary>How the member reaches the storage: a writable reference, a readonly reference or a copy (R1, R2).</summary>
    public enum Route
    {
        Directly,
        HelperRefParameter,
        HelperRefFieldParameter,
        RefLocal,
        HelperRefReturn,
        RefProperty,
        RefIndexer,
        RefMethod,
        Span,
        ConditionalRef,
        GenericRef,
        RefForeach,
        LambdaOnStorage,
        RefLocalLoop,
        OutHelper,
        ReadonlyRefProperty,
        ReadonlyRefIndexer,
        ReadonlyRefMethod,
        ReadOnlySpan,
        ReadonlyRefForeach,
        InHelper,
        GenericIn,
        ReadonlyRefLocal,
        ReadonlyConditionalRef,
        GenericByValue,
        With,
        CapturedCopy,
        NullableValue,
        NullableDefault,
        PairValue,
        PairDeconstruct,
        CapturedParameter,
        Box,
        PatternBox,
        ForeachByValue,
        MethodGroupOnStorage
    }

    /// <summary>What the member does to the storage. A mutating call stands for every member that is not <c>readonly</c> — a setter, an
    /// indexer's setter, an event's add accessor, a writing <c>Deconstruct</c> — since the model names the effect, not the member.</summary>
    public enum Action
    {
        Increment,
        StoreObject,
        MutatingCall,
        StoreWholeStruct,
        TouchReference
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
        using System.Collections.Generic;

        namespace Lib
        {
            public class Item { public object Value; }
            public interface IBump { void Bump(); Item Held { get; } }
            public struct S : IBump { public int Count; public Item Ref; public void Bump() { Count++; } public readonly Item Held => Ref; }
            public struct Outer { public S Inner; }
            public struct OuterN { public S? Inner; }
            public struct OuterP { public KeyValuePair<int, S> Inner; }
            public sealed class K : IBump { public int Count; public Item Ref; public void Bump() { Count++; } public Item Held => Ref; }
            public sealed class OuterK { public K Inner = new K(); }

            public static class Ops
            {
                public static void IncrementField(ref int x) { x++; }
                public static void KeepField(ref Item x, Item p) { x = p; }
                public static void Increment(ref S x) { x.Count++; }
                public static void Keep(ref S x, Item p) { x.Ref = p; }
                public static void Bump(ref S x) { x.Bump(); }
                public static void Assign(ref S x, S w) { x = w; }
                public static void Touch(ref S x, Item p) { x.Held.Value = p; }
                public static ref S Identity(ref S x) => ref x;
                public static void Then(out S x, S w) { x = w; }
                public static void BumpIn(in S x) { x.Bump(); }
                public static void TouchIn(in S x, Item p) { x.Held.Value = p; }
                public static void Act<T>(ref T x) where T : struct, IBump { x.Bump(); }
                public static void TouchRef<T>(ref T x, Item p) where T : struct, IBump { x.Held.Value = p; }
                public static void ActIn<T>(in T x) where T : struct, IBump { x.Bump(); }
                public static void TouchRefIn<T>(in T x, Item p) where T : struct, IBump { x.Held.Value = p; }
                public static void ActByValue<T>(T x) where T : struct, IBump { x.Bump(); }
                public static void TouchByValue<T>(T x, Item p) where T : struct, IBump { x.Held.Value = p; }
                public static void Increment(ref K x) { x.Count++; }
                public static void Keep(ref K x, Item p) { x.Ref = p; }
                public static void Bump(ref K x) { x.Bump(); }
                public static void Touch(ref K x, Item p) { x.Held.Value = p; }
                public static ref K Identity(ref K x) => ref x;
            }

        """;

    /// <summary>A helper receiving a struct field by reference has the direct field write's answer, including array and nested storage.</summary>
    /// <param name="storage">The storage containing the struct.</param>
    /// <param name="action">The write forwarded to the helper.</param>
    [Theory]
    [InlineData(Storage.ReceiverArrayCell, Action.Increment)]
    [InlineData(Storage.ReceiverArrayCell, Action.StoreObject)]
    [InlineData(Storage.ReceiverField, Action.Increment)]
    [InlineData(Storage.ReceiverField, Action.StoreObject)]
    [InlineData(Storage.ReceiverNestedField, Action.Increment)]
    [InlineData(Storage.ReceiverNestedField, Action.StoreObject)]
    public void A_helper_reference_keeps_the_struct_field_location(Storage storage, Action action)
    {
        var cell = new Cell(storage, Route.HelperRefFieldParameter, action);
        Assert.True(Compare(cell, Results.Answers[cell]).IsExact, $"{cell.Name}: {Show(Results.Answers[cell])}");
        Assert.Equal(Shape(Results.Answers[new Cell(storage, Route.Directly, action)]), Shape(Results.Answers[cell]));
    }

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
        var failures = Cells().Where(cell => IsWritableReference(cell.Route))
                              .GroupBy(cell => (cell.Storage, cell.Action))
                              .Where(group => group.Select(cell => Shape(Results.Answers[cell])).Distinct(StringComparer.Ordinal).Count() > 1)
                              .Select(group => $"{group.Key.Storage}/{group.Key.Action}: " +
                                               string.Join("; ", group.Select(cell => $"{cell.Route} {Show(Results.Answers[cell])}")))
                              .ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} storage(s) answered by route:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_copying_route_answers_as_a_copy()
    {
        var copying = Cells().Where(IsCopying).ToArray();
        var failures = copying.Where(cell => Shape(Results.Answers[cell]) != Shape(Results.Answers[Copy(cell.Action)]))
                              .Select(cell => $"{cell.Name}: {Show(Results.Answers[cell])}; the copy {Show(Results.Answers[Copy(cell.Action)])}")
                              .ToArray();

        Assert.True(copying.Length > 0);
        Assert.True(failures.Length == 0, $"{failures.Length} copying cell(s) not answered as a copy:\n" + string.Join("\n", failures));
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
                              .Select(cell => (Cell: cell, Comparison: Compare(cell, Results.Answers[cell with { Twin = true }])))
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

        Assert.Equal(Product().Count(cell => Excluded(cell) is null), cells.Length);
        Assert.True(missing.Length == 0, "No cell has " + string.Join(", ", missing));
        Assert.True(refused.Length == 0, $"{refused.Length} cell(s) got no classification:\n" + string.Join("\n", refused));
    }

    /// <summary>Compares an answer with a cell's expectation, the cause it must name included.</summary>
    /// <param name="cell">The cell whose expectation the answer is held to.</param>
    /// <param name="answer">The answer: the cell's own, or its class twin's.</param>
    private static EntryComparison Compare(Cell cell, GeneratedAnswer answer)
    {
        var comparison = EntryComparator.Compare(answer, Expected(cell), requireSameModelReason: true);
        return ExpectedCause(cell) is { } code && comparison.IsExact && answer.Causes.All(cause => cause.Code != code)
            ? comparison with { IsExact = false, Detail = $"expected the cause {code}" }
            : comparison;
    }

    /// <summary>R4: a write into the object the struct's reference field holds answers by the storage on every route but a contents
    /// route, whose struct TD-034b leaves unseeded behind a property or a <c>readonly</c> field, so the read of its reference field gets
    /// no model; otherwise a writable reference answers as its storage, and a readonly reference or a copying route as a by-value
    /// parameter's copy.</summary>
    /// <param name="cell">The cell.</param>
    private static EntryTruth Expected(Cell cell) => cell.Action == Action.TouchReference
        ? IsContentsRoute(cell.Route) ? EntryTruth.NoEntry(ModelReasons.INCOMPLETE) : ExpectedTouch(cell.Storage)
        : ExpectedOn(IsWritableReference(cell.Route) ? cell.Storage : Storage.ByValueParameter, cell.Action);

    /// <summary>R7, by the storage the write reaches: library storage — the receiver's field, cell and nested struct, a static field or a
    /// static array's cell — is library state for a scalar write and a mutating call, and for a store of <c>p</c> or of a whole struct
    /// holding it what the same store into a class kept there gives; the caller's storage — the struct receiver's own field, what a
    /// <c>ref</c> or <c>out</c> parameter refers to — is <c>vocabulary</c>; a by-value parameter's copy is no effect.</summary>
    /// <param name="storage">The storage the write reaches.</param>
    /// <param name="action">The action on it.</param>
    private static EntryTruth ExpectedOn(Storage storage, Action action) => storage switch
    {
        // The receiver keeps p wherever in its own storage it is stored, as it does in a class it holds.
        Storage.ReceiverField or Storage.ReceiverArrayCell or Storage.ReceiverNestedField when action is Action.StoreObject or Action.StoreWholeStruct =>
            EntryTruth.Entry(new LibraryModel("M:Lib.Cell.M", [], [])
            {
                Keeps = new Dictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal) { ["this"] = [LibraryValue.Parse("arg:p")!] }
            }),
        Storage.ReceiverField or Storage.ReceiverArrayCell or Storage.ReceiverNestedField or Storage.StaticField or Storage.StaticArrayCell =>
            EntryTruth.NoEntry(ModelReasons.LIBRARY_STATE),
        Storage.StructReceiverField or Storage.RefParameter or Storage.OutParameter => EntryTruth.NoEntry(ModelReasons.VOCABULARY),
        Storage.ByValueParameter => EntryTruth.Entry(new LibraryModel("M:Lib.Cell.M", [], [])),
        _ => throw new InvalidOperationException($"Unknown storage {storage}.")
    };

    /// <summary>A write into the object the struct's reference field holds, fixed per storage (R4): an object the receiver holds is
    /// inexpressible (TD-034b, <c>receiver-object-write</c>); one a static holds is library state; one the caller's struct holds — the
    /// struct receiver's own field, a <c>ref</c> or by-value parameter — is <c>vocabulary</c>, since <c>writes-arg</c> writes only the
    /// argument's own regions one level deep (TD-034a) and a struct has no region of its own (TD-041): the object its reference field
    /// holds is merely reachable from it.</summary>
    /// <param name="storage">The storage whose struct holds the object.</param>
    private static EntryTruth ExpectedTouch(Storage storage) => storage switch
    {
        Storage.ReceiverField or Storage.ReceiverArrayCell or Storage.ReceiverNestedField => EntryTruth.NoEntry(ModelReasons.VOCABULARY),
        Storage.StaticField or Storage.StaticArrayCell => EntryTruth.NoEntry(ModelReasons.LIBRARY_STATE),
        Storage.StructReceiverField or Storage.RefParameter or Storage.ByValueParameter => EntryTruth.NoEntry(ModelReasons.VOCABULARY),
        _ => throw new InvalidOperationException($"No touch of a reference through {storage}.")
    };

    /// <summary>The cause a cell's answer must name besides its model reason: the unseeded read of a contents route's reference field,
    /// or a write into an object the receiver holds.</summary>
    /// <param name="cell">The cell.</param>
    private static string? ExpectedCause(Cell cell) => cell switch
    {
        { Action: Action.TouchReference } when IsContentsRoute(cell.Route) => ModelCauses.UNSEEDED_READ,
        { Action: Action.TouchReference, Storage: Storage.ReceiverField or Storage.ReceiverArrayCell or Storage.ReceiverNestedField } =>
            ModelCauses.RECEIVER_OBJECT_WRITE,
        _ => null
    };

    /// <summary>Whether a route takes the struct out of a <c>Nullable</c> or a <c>KeyValuePair</c>, behind a property or a
    /// <c>readonly</c> field.</summary>
    /// <param name="route">The route.</param>
    private static bool IsContentsRoute(Route route) =>
        route is Route.NullableValue or Route.NullableDefault or Route.PairValue or Route.PairDeconstruct;

    private static bool IsReadonlyReference(Route route) => route is Route.ReadonlyRefProperty or Route.ReadonlyRefIndexer or
        Route.ReadonlyRefMethod or Route.ReadOnlySpan or Route.ReadonlyRefForeach or Route.InHelper or Route.GenericIn or
        Route.ReadonlyRefLocal or Route.ReadonlyConditionalRef;

    private static bool IsCopyRoute(Route route) => route is Route.GenericByValue or Route.With or Route.CapturedCopy or
        Route.NullableValue or Route.NullableDefault or Route.PairValue or Route.PairDeconstruct or Route.CapturedParameter or Route.Box or
        Route.PatternBox or Route.ForeachByValue or Route.MethodGroupOnStorage;

    private static bool IsWritableReference(Route route) => !IsReadonlyReference(route) && !IsCopyRoute(route);

    /// <summary>Whether a cell works on a copy: a route of R2 for every action but a touch of the reference, and a readonly reference's
    /// mutating call, the defensive copy of R3.</summary>
    /// <param name="cell">The cell.</param>
    private static bool IsCopying(Cell cell) =>
        IsCopyRoute(cell.Route) && cell.Action != Action.TouchReference || IsReadonlyReference(cell.Route) && cell.Action == Action.MutatingCall;

    /// <summary>The cell a copy answers as: the same action on a by-value parameter.</summary>
    /// <param name="action">The action.</param>
    private static Cell Copy(Action action) => new(Storage.ByValueParameter, Route.Directly, action);

    /// <summary>Whether a cell's storage is library storage, where a class can stand in the same place, on a route a class takes: no
    /// generic route (the constraint is <c>struct</c>), and no <c>out</c> helper, whose only action is the whole write, whose twin — a store
    /// of <c>p</c> into the class kept there — no <c>out</c> argument reaches.</summary>
    /// <param name="cell">The cell.</param>
    private static bool HasTwin(Cell cell) => !cell.Twin && IsLibrary(cell.Storage) && IsWritableReference(cell.Route) &&
                                              cell.Route is not (Route.GenericRef or Route.OutHelper);

    private static bool IsLibrary(Storage storage) => storage is Storage.ReceiverField or Storage.ReceiverArrayCell or Storage.ReceiverNestedField or
        Storage.StaticField or Storage.StaticArrayCell;

    private static bool IsArray(Storage storage) => storage is Storage.ReceiverArrayCell or Storage.StaticArrayCell;

    /// <summary>Why a cell of the product is no cell, or <c>null</c> when it is one.</summary>
    /// <param name="cell">The cell.</param>
    private static string? Excluded(Cell cell)
    {
        var (storage, route, action) = (cell.Storage, cell.Route, cell.Action);
        if (route == Route.HelperRefFieldParameter && action is not (Action.Increment or Action.StoreObject))
            return "the helper writes a scalar or object field";
        if (route is Route.Span or Route.ReadOnlySpan or Route.RefForeach or Route.ReadonlyRefForeach or Route.ForeachByValue && !IsArray(storage))
            return "the route needs an array";
        if (route is Route.RefProperty or Route.RefIndexer or Route.RefMethod or Route.ReadonlyRefProperty or Route.ReadonlyRefIndexer or
                Route.ReadonlyRefMethod && !IsLibrary(storage))
            return "C# returns no reference to a parameter by value, and a struct's member no writable reference to its own field";
        if ((IsReadonlyReference(route) || route is Route.Box or Route.PatternBox or Route.NullableValue or Route.PairValue) &&
            action is Action.Increment or Action.StoreObject or Action.StoreWholeStruct)
            return "C# rejects a write through a readonly reference or into a value";
        if (route == Route.CapturedParameter && storage != Storage.ByValueParameter)
            return "the route captures the member's own by-value parameter";
        if (route == Route.OutHelper && action != Action.StoreWholeStruct)
            return "the helper's whole write of w is the action";
        if (route == Route.MethodGroupOnStorage && action != Action.MutatingCall)
            return "a method group invokes a member";
        if (action == Action.TouchReference && storage == Storage.OutParameter)
            return "the member assigns the whole struct first, so the object it touches is one it created";
        // The assignment C# demands first is itself a whole write of the caller's storage, which StoreWholeStruct's cell covers.
        if (!IsWritableReference(route) && storage == Storage.OutParameter)
            return "CS0269: use of unassigned out parameter; a copy or readonly reference of it needs a whole write of it first";
        if (route is Route.GenericRef or Route.GenericIn or Route.GenericByValue && action is not (Action.MutatingCall or Action.TouchReference))
            return "a type parameter shows only the interface's members";
        if (route == Route.LambdaOnStorage && storage is Storage.RefParameter or Storage.OutParameter)
            return "CS1628: a ref, out or in parameter cannot be used inside a lambda";
        if (route == Route.LambdaOnStorage && storage == Storage.StructReceiverField)
            return "CS1673: a lambda inside a struct cannot access instance members of this";
        if (route == Route.NullableDefault && action is Action.Increment or Action.StoreObject)
            return "CS1612: cannot modify the return value of GetValueOrDefault because it is not a variable";
        if (route == Route.NullableDefault && action == Action.StoreWholeStruct)
            return "CS0131: the left-hand side of an assignment must be a variable, property or indexer";
        if (route == Route.ForeachByValue && action is Action.Increment or Action.StoreWholeStruct)
            return "CS1656: cannot assign to a foreach iteration variable";
        if (route == Route.ForeachByValue && action == Action.StoreObject)
            return "CS1654: cannot modify members of a foreach iteration variable";
        return null;
    }

    private static IEnumerable<Cell> Product() =>
        from storage in Enum.GetValues<Storage>()
        from route in Enum.GetValues<Route>()
        from action in Enum.GetValues<Action>()
        select new Cell(storage, route, action);

    private static IEnumerable<Cell> Cells() => Product().Where(cell => Excluded(cell) is null);

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
        // The contents routes keep the struct inside a Nullable or a pair in the storage.
        var held = cell.Route switch
        {
            Route.NullableValue or Route.NullableDefault => "S?",
            Route.PairValue or Route.PairDeconstruct => "KeyValuePair<int, S>",
            _ => type
        };
        var outer = cell.Twin ? "OuterK" : cell.Route switch
        {
            Route.NullableValue or Route.NullableDefault => "OuterN",
            Route.PairValue or Route.PairDeconstruct => "OuterP",
            _ => "Outer"
        };
        var second = cell.Route is Route.ConditionalRef or Route.ReadonlyConditionalRef or Route.RefLocalLoop;
        var parameters = new List<string> { "Item p", $"{type} w" };
        var parameterKind = cell.Storage switch
        {
            Storage.RefParameter => "ref ",
            Storage.OutParameter => "out ",
            Storage.ByValueParameter => "",
            _ => null
        };
        if (parameterKind is not null)
            parameters.Add($"{parameterKind}{held} value");
        if (cell.Route is Route.ConditionalRef or Route.ReadonlyConditionalRef)
            parameters.Add("bool flag");
        if (second && parameterKind is not null)
            parameters.Add($"{parameterKind}{held} other");

        var initialized = cell.Twin ? " = new K()" : "";
        var (fields, place, secondPlace) = cell.Storage switch
        {
            Storage.ReceiverField => ($"private {held} _s{initialized};" + (second ? $" private {held} _t{initialized};" : ""), "_s", "_t"),
            Storage.ReceiverArrayCell => (cell.Twin ? $"private K[] _cells = {{ new K(){(second ? ", new K()" : "")} }};" : $"private {held}[] _cells = new {held}[4];",
                                          "_cells[0]", "_cells[1]"),
            Storage.ReceiverNestedField => ($"private {outer} _o{(cell.Twin ? " = new OuterK()" : "")};" +
                                            (second ? $" private {outer} _q{(cell.Twin ? " = new OuterK()" : "")};" : ""), "_o.Inner", "_q.Inner"),
            Storage.StaticField => ($"private static {held} s_s{initialized};" + (second ? $" private static {held} s_t{initialized};" : ""), "s_s", "s_t"),
            Storage.StaticArrayCell => (cell.Twin ? $"private static K[] s_cells = {{ new K(){(second ? ", new K()" : "")} }};"
                                                  : $"private static {held}[] s_cells = new {held}[4];", "s_cells[0]", "s_cells[1]"),
            Storage.StructReceiverField => ($"private {held} _s;" + (second ? $" private {held} _t;" : ""), "_s", "_t"),
            _ => ("", "value", "other")
        };
        var array = cell.Storage == Storage.StaticArrayCell ? "s_cells" : "_cells";

        var body = new List<string>();
        if (cell.Storage == Storage.OutParameter)
            body.Add(second ? "value = default; other = default;" : "value = default;");
        // The twin of a store of a whole struct holding p is the store of p into the class kept there.
        var action = cell.Twin && cell.Action == Action.StoreWholeStruct ? Action.StoreObject : cell.Action;
        // The whole struct stored is the member's own argument w, whose reference field the member sets to p first: at the store it holds
        // p, which the store carries into the place as any store does (R7).
        if (action == Action.StoreWholeStruct)
            body.Add("w.Ref = p;");
        body.AddRange(Act(cell, action, type, place, secondPlace, array));

        var kind = cell.Storage == Storage.StructReceiverField ? "struct" : "sealed class";
        var source = new StringBuilder().Append("    public ").Append(kind).Append(' ').Append(cell.Type).Append("\n    {\n");
        if (fields.Length != 0)
            source.Append("        ").Append(fields).Append('\n');
        if (RouteMember(cell.Route, type, place) is { } member)
            source.Append("        ").Append(member).Append('\n');
        source.Append("        public void M(").Append(string.Join(", ", parameters)).Append(")\n        {\n");
        foreach (var statement in body)
            source.Append("            ").Append(statement).Append('\n');
        return source.Append("        }\n    }\n").ToString();
    }

    /// <summary>The member of the cell's own type a route reaches the storage through: a reference-returning property, indexer or
    /// method; <c>null</c> for every other route.</summary>
    /// <param name="route">The route.</param>
    /// <param name="type">The struct, or its class twin.</param>
    /// <param name="place">The storage, an expression that is a variable.</param>
    private static string? RouteMember(Route route, string type, string place) => route switch
    {
        Route.RefProperty => $"private ref {type} Slot => ref {place};",
        Route.RefIndexer => $"private ref {type} this[int i] => ref {place};",
        Route.RefMethod => $"private ref {type} Get() => ref {place};",
        Route.ReadonlyRefProperty => $"private ref readonly {type} Slot => ref {place};",
        Route.ReadonlyRefIndexer => $"private ref readonly {type} this[int i] => ref {place};",
        Route.ReadonlyRefMethod => $"private ref readonly {type} Get() => ref {place};",
        _ => null
    };

    /// <summary>The statements acting on a place by a route.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="action">The action, the twin's own for a twin.</param>
    /// <param name="type">The struct, or its class twin.</param>
    /// <param name="place">The place, an expression that is a variable.</param>
    /// <param name="second">A second place of the same kind.</param>
    /// <param name="array">The array the place is a cell of, for an array's storage.</param>
    private static IReadOnlyList<string> Act(Cell cell, Action action, string type, string place, string second, string array)
    {
        var generic = action == Action.MutatingCall;
        return cell.Route switch
        {
            Route.Directly => [Direct(action, place)],
            Route.HelperRefParameter => [action switch
            {
                Action.Increment => $"Ops.Increment(ref {place});",
                Action.StoreObject => $"Ops.Keep(ref {place}, p);",
                Action.MutatingCall => $"Ops.Bump(ref {place});",
                Action.TouchReference => $"Ops.Touch(ref {place}, p);",
                _ => $"Ops.Assign(ref {place}, w);"
            }],
            Route.HelperRefFieldParameter => [action == Action.Increment ? $"Ops.IncrementField(ref {place}.Count);"
                                                                         : $"Ops.KeepField(ref {place}.Ref, p);"],
            Route.RefLocal => [$"ref var x = ref {place};", Direct(action, "x")],
            Route.HelperRefReturn => [Direct(action, $"Ops.Identity(ref {place})")],
            Route.RefProperty or Route.ReadonlyRefProperty => [Direct(action, "Slot")],
            Route.RefIndexer or Route.ReadonlyRefIndexer => [Direct(action, "this[0]")],
            Route.RefMethod or Route.ReadonlyRefMethod => [Direct(action, "Get()")],
            Route.Span => [$"Span<{type}> span = {array};", Direct(action, "span[0]")],
            Route.ReadOnlySpan => [$"ReadOnlySpan<{type}> span = {array};", Direct(action, "span[0]")],
            Route.ConditionalRef => [Direct(action, $"(flag ? ref {place} : ref {second})")],
            Route.ReadonlyConditionalRef => [$"ref readonly {type} r = ref (flag ? ref {place} : ref {second});", Direct(action, "r")],
            Route.GenericRef => [generic ? $"Ops.Act(ref {place});" : $"Ops.TouchRef(ref {place}, p);"],
            Route.GenericIn => [generic ? $"Ops.ActIn(in {place});" : $"Ops.TouchRefIn(in {place}, p);"],
            Route.GenericByValue => [generic ? $"Ops.ActByValue({place});" : $"Ops.TouchByValue({place}, p);"],
            Route.RefForeach => [$"foreach (ref var x in (Span<{type}>){array}) {Direct(action, "x")}"],
            Route.ReadonlyRefForeach => [$"foreach (ref readonly var x in (ReadOnlySpan<{type}>){array}) {Direct(action, "x")}"],
            // A captured w would be a capture cell holding the argument as given as well as after the write of p, which the heap does not
            // tell apart; the lambda takes the whole struct from a variable assigned once, after the write.
            Route.LambdaOnStorage when action == Action.StoreWholeStruct => ["var whole = w;", $"Action a = () => {{ {place} = whole; }};", "a();"],
            Route.LambdaOnStorage => [$"Action a = () => {{ {Direct(action, place)} }};", "a();"],
            Route.RefLocalLoop => [$"ref var r = ref {place};", "for (var turn = 0; turn < 2; turn++)", "{", $"    {Direct(action, "r")}",
                                   $"    r = ref {second};", "}"],
            Route.OutHelper => [$"Ops.Then(out {place}, w);"],
            Route.InHelper => [generic ? $"Ops.BumpIn(in {place});" : $"Ops.TouchIn(in {place}, p);"],
            Route.ReadonlyRefLocal => [$"ref readonly {type} r = ref {place};", Direct(action, "r")],
            Route.With => [$"var c = {place} with {{ Count = 1 }};", Direct(action, "c")],
            Route.CapturedCopy => [$"var c = {place};", $"Action a = () => {{ {Direct(action, "c")} }};", "a();"],
            Route.NullableValue or Route.PairValue => [Direct(action, $"{place}.Value")],
            Route.NullableDefault => [Direct(action, $"{place}.GetValueOrDefault()")],
            Route.PairDeconstruct => [$"var (k, v) = {place};", Direct(action, "v")],
            Route.CapturedParameter => [$"Action a = () => {{ {Direct(action, "value")} }};", "a();"],
            Route.Box => [$"object o = {place};", Direct(action, "((IBump)o)")],
            Route.PatternBox => [$"if ({place} is IBump b) {Direct(action, "b")}"],
            Route.ForeachByValue => [$"foreach (var x in {array}) {Direct(action, "x")}"],
            Route.MethodGroupOnStorage => [$"Action a = {place}.Bump;", "a();"],
            _ => throw new InvalidOperationException($"Unknown route {cell.Route}.")
        };
    }

    private static string Direct(Action action, string place) => action switch
    {
        Action.Increment => $"{place}.Count++;",
        Action.StoreObject => $"{place}.Ref = p;",
        Action.MutatingCall => $"{place}.Bump();",
        Action.TouchReference => $"{place}.Held.Value = p;",
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
        : $"{answer.ModelReason ?? answer.Reason}: {answer.Detail} [{string.Join("; ", answer.Causes)}]";
}
