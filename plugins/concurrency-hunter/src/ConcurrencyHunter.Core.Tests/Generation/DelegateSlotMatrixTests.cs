using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The delegate-slot matrix of task 11, rule (b): one compiled fixture, one class per slot/stored-value/channel cell, each
/// generated with <see cref="ModelGenerator"/>'s trace. A store into a delegate-typed field of the receiver or of a static is no state
/// store when every object its value may point to is a probe of the member's delegate parameter or the slot's own seed, and its value
/// comes from no origin the heap does not follow (TD-034a, R3, R5, R6).</summary>
public sealed class DelegateSlotMatrixTests
{
    private static readonly Lazy<IReadOnlyDictionary<Cell, CellResult>> Results = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Where the slot is.</summary>
    public enum Slot
    {
        ReceiverSlot,
        StaticSlot
    }

    /// <summary>What <c>Set</c> stores into the slot.</summary>
    public enum StoredValue
    {
        Parameter,
        HelperParameter,
        CombineWithSlot,
        RemoveFromSlot,
        Null,
        OtherSlot,
        SameFieldOtherObject,
        LibraryLambda,
        Unknown,
        Mixed,
        CombineWithUnknown,
        CompoundUnknown,
        RemoveUnknownRight,
        RemoveUnknownFromSlot,
        RemoveFromUnknown
    }

    /// <summary>Where <c>Set</c> makes the store.</summary>
    public enum Channel
    {
        Direct,
        HandedOff
    }

    /// <summary>One matrix cell.</summary>
    /// <param name="Slot">Where the slot is.</param>
    /// <param name="Value">What is stored into it.</param>
    /// <param name="Channel">Where the store is made.</param>
    public sealed record Cell(Slot Slot, StoredValue Value, Channel Channel)
    {
        public string Name => $"{Slot}/{Value}/{Channel}";

        public string Class => $"C_{Slot}_{Value}_{Channel}";

        public override string ToString() => Name;
    }

    /// <summary>What one cell's generation gave.</summary>
    /// <param name="Answer">The answer.</param>
    /// <param name="SlotStore">Whether the effect reading recorded a state store on the slot's object.</param>
    /// <param name="StateStores">Every state store the effect reading recorded, for failure messages.</param>
    private sealed record CellResult(GeneratedAnswer Answer, bool SlotStore, IReadOnlyList<string> StateStores);

    /// <summary>What rule (b) expects of a cell.</summary>
    /// <param name="SlotStore">Whether the store is a state store on the slot's object.</param>
    /// <param name="Fate">For a qualifying <see cref="Channel.Direct"/> cell, the fate of <c>value</c> in its entry; else
    /// <c>null</c>, the answer not asserted or no entry.</param>
    /// <param name="NoEntry">Whether the answer has no entry.</param>
    private sealed record Expectation(bool SlotStore, LibraryFate? Fate, bool NoEntry);

    private const string PRELUDE = """
        using System;

        namespace Lib
        {
            public static class Sink { public static extern void Run(Action action); }
            public static class Ext { public static extern Action Make(); }

        """;

    [Fact]
    public void Every_cell_records_the_expected_slot_store()
    {
        var failures = Results.Value.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal)
                              .Where(pair => pair.Value.SlotStore != Expected(pair.Key).SlotStore)
                              .Select(pair => $"{pair.Key.Name}: expected {(Expected(pair.Key).SlotStore ? "a" : "no")} state store on the slot's object; " +
                                              $"got [{string.Join(", ", pair.Value.StateStores)}] ({Show(pair.Value.Answer)})")
                              .ToArray();

        Assert.True(failures.Length == 0, $"{failures.Length} cell(s) off the table:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_qualifying_cell_gets_its_expected_fate()
    {
        var failures = new List<string>();
        foreach (var (cell, result) in Results.Value.OrderBy(pair => pair.Key.Name, StringComparer.Ordinal))
        {
            var expected = Expected(cell);
            if (expected.Fate is { } fate)
            {
                var got = result.Answer.Model?.Fates.FirstOrDefault(candidate => candidate.Parameter == "value");
                if (got != fate)
                    failures.Add($"{cell.Name}: expected an entry with {fate}; got {Show(result.Answer)}");
            }
            else if (expected.NoEntry && result.Answer.Model is not null)
                failures.Add($"{cell.Name}: expected no entry; got {Show(result.Answer)}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cell(s) off the table:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void The_matrix_has_exactly_the_allowed_cells()
    {
        var product = (from slot in Enum.GetValues<Slot>()
                       from value in Enum.GetValues<StoredValue>()
                       from channel in Enum.GetValues<Channel>()
                       select new Cell(slot, value, channel)).ToArray();
        var excluded = product.Where(cell => !Allowed(cell)).ToArray();

        // A static field has one object, so the same field of another object is no static cell.
        Assert.Equal(product.Where(cell => cell is { Slot: Slot.StaticSlot, Value: StoredValue.SameFieldOtherObject }), excluded);
        Assert.Equal(product.Except(excluded).Select(cell => cell.Name).Order(StringComparer.Ordinal),
                     Results.Value.Keys.Select(cell => cell.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>Rule (b) of task 11 for one cell. Under <see cref="Channel.Direct"/>, a plain store of the handler, through a helper's
    /// parameter, a combination with the slot's own value, a removal from it and <c>null</c> leave no state store; the first three give
    /// <c>value</c> the holder <c>this</c> under a receiver slot, which <c>Fire</c> runs, and <c>unknown-execution</c> under a static
    /// one, which has no holder; the removal and <c>null</c> give it <c>not-run</c>. A removal's right operand contributes nothing to
    /// its result (R3), so removing an unknown delegate qualifies too, its answer not asserted. Every other value — another slot's seed,
    /// the same field of another object, a library lambda, an unknown delegate, or a combination with one, and a removal from one —
    /// may point to an object that is neither, and leaves a state store. Under <see cref="Channel.HandedOff"/> a cell leaves a state
    /// store exactly when its direct twin does; its answer is not asserted.</summary>
    /// <param name="cell">The cell.</param>
    private static Expectation Expected(Cell cell)
    {
        if (cell.Channel == Channel.HandedOff)
            return Expected(cell with { Channel = Channel.Direct }) with { Fate = null, NoEntry = false };
        return cell.Value switch
        {
            StoredValue.Parameter or StoredValue.HelperParameter or StoredValue.CombineWithSlot =>
                new Expectation(false, cell.Slot == Slot.ReceiverSlot
                    ? new LibraryFate("value", LibraryFateKind.Holder, LibraryHolderKind.This, null)
                    : new LibraryFate("value", LibraryFateKind.UnknownExecution, null, null), false),
            StoredValue.RemoveFromSlot or StoredValue.Null => new Expectation(false, new LibraryFate("value", LibraryFateKind.NotRun, null, null), false),
            StoredValue.RemoveUnknownRight or StoredValue.RemoveUnknownFromSlot => new Expectation(false, null, false),
            StoredValue.CombineWithUnknown or StoredValue.CompoundUnknown or StoredValue.RemoveFromUnknown => new Expectation(true, null, false),
            _ => new Expectation(true, null, true)
        };
    }

    private static bool Allowed(Cell cell) => !(cell.Slot == Slot.StaticSlot && cell.Value == StoredValue.SameFieldOtherObject);

    private static IEnumerable<Cell> Cells() =>
        from slot in Enum.GetValues<Slot>()
        from value in Enum.GetValues<StoredValue>()
        from channel in Enum.GetValues<Channel>()
        let cell = new Cell(slot, value, channel)
        where Allowed(cell)
        select cell;

    /// <summary>Compiles the fixture once and generates every cell in parallel, as the model generator is used per member.</summary>
    private static IReadOnlyDictionary<Cell, CellResult> Run()
    {
        var cells = Cells().ToArray();
        var library = Compile(Source(cells));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var results = new ConcurrentDictionary<Cell, CellResult>();
        Parallel.ForEach(cells, cell =>
        {
            var request = new GenerationRequest(ASSEMBLY, "1.0", MethodId(library, $"Lib.{cell.Class}", "Set"), null, null);
            var trace = ModelGenerator.Trace(request, library, CancellationToken.None);
            var stores = trace.StateStores.Select(store => store.Region).ToArray();
            results[cell] = new CellResult(trace.Answer, stores.Any(region => IsSlotObject(trace, cell, region)), stores);
        });
        return results;
    }

    /// <summary>Whether a region is the object a cell's slot is in: the receiver the driver hands <c>Set</c>, or the static storage of
    /// the cell's class.</summary>
    /// <param name="trace">The cell's trace.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="region">The region.</param>
    private static bool IsSlotObject(GenerationTrace trace, Cell cell, string region) => cell.Slot == Slot.ReceiverSlot
        ? Fixtures.GenerationRuns.Slot(trace, DriverSynthesizer.DRIVER_TYPE, $"Recv_{DriverSynthesizer.CALL_VARIANT}").Contains(region)
        : trace.Run!.Heap!.Regions.TryGetValue(region, out var held) && held.Kind == HeapRegionKind.Static &&
          region.EndsWith($"Lib.{cell.Class}", StringComparison.Ordinal);

    private static string Source(IEnumerable<Cell> cells)
    {
        var source = new StringBuilder(PRELUDE);
        foreach (var cell in cells)
            source.Append(Declaration(cell));
        return source.Append("}\n").ToString();
    }

    private static string Declaration(Cell cell)
    {
        var receiver = cell.Slot == Slot.ReceiverSlot;
        var modifier = receiver ? "" : "static ";
        var (h, g, count) = receiver ? ("_h", "_g", "_count") : ("s_h", "s_g", "s_count");
        var parameters = new List<string> { "Action value" };
        if (cell.Value == StoredValue.Mixed)
            parameters.Add("bool flag");
        if (cell.Value == StoredValue.SameFieldOtherObject)
            parameters.Add($"{cell.Class} other");

        var store = cell.Value switch
        {
            StoredValue.Parameter => $"{h} = value;",
            StoredValue.HelperParameter => "Put(value);",
            StoredValue.CombineWithSlot => $"{h} += value;",
            StoredValue.RemoveFromSlot => $"{h} -= value;",
            StoredValue.Null => $"{h} = null;",
            StoredValue.OtherSlot => $"{h} = {g};",
            StoredValue.SameFieldOtherObject => "_h = other._h;",
            StoredValue.LibraryLambda => $"{h} = () => {count}++;",
            StoredValue.Unknown => $"{h} = Ext.Make();",
            StoredValue.Mixed => $"{h} = flag ? value : () => {count}++;",
            StoredValue.CombineWithUnknown => $"{h} = value + Ext.Make();",
            StoredValue.CompoundUnknown => $"{h} += Ext.Make();",
            StoredValue.RemoveUnknownRight => $"{h} = value - Ext.Make();",
            StoredValue.RemoveUnknownFromSlot => $"{h} -= Ext.Make();",
            StoredValue.RemoveFromUnknown => $"{h} = Ext.Make() - value;",
            _ => throw new InvalidOperationException($"Unknown stored value {cell.Value}.")
        };
        var body = cell.Channel == Channel.Direct ? store : "Sink.Run(() => { " + store + " });";

        var source = new StringBuilder().Append("    public ").Append(receiver ? "sealed" : "static").Append(" class ").Append(cell.Class).Append("\n    {\n");
        source.Append("        private ").Append(modifier).Append("Action ").Append(h).Append(";\n");
        source.Append("        private ").Append(modifier).Append("Action ").Append(g).Append(";\n");
        source.Append("        private ").Append(modifier).Append("int ").Append(count).Append(";\n");
        source.Append("        public ").Append(modifier).Append("void Set(").Append(string.Join(", ", parameters)).Append(") { ").Append(body).Append(" }\n");
        if (cell.Value == StoredValue.HelperParameter)
            source.Append("        private ").Append(modifier).Append("void Put(Action a) { ").Append(h).Append(" = a; }\n");
        source.Append("        public ").Append(modifier).Append("void Fire() { ").Append(h).Append("?.Invoke(); }\n");
        return source.Append("    }\n").ToString();
    }

    private static string Show(GeneratedAnswer answer) => answer.Model is { } model
        ? ModelEntryWriter.Write(model).ToJsonString()
        : answer.ModelReason ?? answer.Reason ?? "no answer";
}
