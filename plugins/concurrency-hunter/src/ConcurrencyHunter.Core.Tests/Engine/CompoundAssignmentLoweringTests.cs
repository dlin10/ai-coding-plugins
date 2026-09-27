using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A compound assignment or an increment is its long form <c>t = t op v</c> (question 33): the receiver and every index are
/// evaluated once, then the target is read, the right-hand side is evaluated, and the target is written. An array element is an
/// element load and store, a property the lowering calls is its getter and its setter, and a target the lowering does not model
/// still lowers its operands into its unknown.</summary>
public sealed class CompoundAssignmentLoweringTests
{
    [Fact]
    public async Task Array_element_increment_lowers_to_element_load_compute_and_store()
    {
        var body = await Lower("class C { int[] A = new int[4]; void M(int i) { A[i]++; } }");

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadElementOperation>());
        var compute = Assert.Single(operations.OfType<IrComputeOperation>());
        var store = Assert.Single(operations.OfType<IrStoreElementOperation>());
        Assert.Single(operations.OfType<IrLoadFieldOperation>());
        Assert.DoesNotContain(operations, operation => operation is IrUnknownOperation);
        Assert.Equal("Add", compute.Operator);
        Assert.Equal(load.ResultValue, compute.OperandValues[0]);
        Assert.Equal(compute.ResultValue, store.Value);
        Assert.Equal(load.ReceiverValue, store.ReceiverValue);
        Assert.Equal(load.IndexValues, store.IndexValues);
        Assert.True(IndexOf(operations, load) < IndexOf(operations, compute));
        Assert.True(IndexOf(operations, compute) < IndexOf(operations, store));
    }

    [Fact]
    public async Task Array_element_compound_assignment_lowers_its_right_side_between_load_and_store()
    {
        var body = await Lower("class C { int[] A = new int[4]; int F() => 1; void M(int i) { A[i] += F(); } }");

        var operations = Operations(body);
        var load = Assert.Single(operations.OfType<IrLoadElementOperation>());
        var call = Assert.Single(operations.OfType<IrCallOperation>(), operation => operation.Method.Contains(".F(", StringComparison.Ordinal));
        var compute = Assert.Single(operations.OfType<IrComputeOperation>());
        var store = Assert.Single(operations.OfType<IrStoreElementOperation>());
        Assert.Equal([load.ResultValue, call.ResultValue!.Value], compute.OperandValues);
        Assert.True(IndexOf(operations, load) < IndexOf(operations, call));
        Assert.True(IndexOf(operations, call) < IndexOf(operations, store));
        Assert.Equal(compute.ResultValue, store.Value);
    }

    [Fact]
    public async Task Multidimensional_array_element_increment_names_every_index()
    {
        var body = await Lower("class C { int[,] G = new int[4, 4]; void M(int i, int j) { G[i, j]--; } }");

        var load = Assert.Single(Operations<IrLoadElementOperation>(body));
        var store = Assert.Single(Operations<IrStoreElementOperation>(body));
        Assert.Equal(2, load.IndexValues.Count);
        Assert.Equal(load.IndexValues, store.IndexValues);
        Assert.Equal("Subtract", Assert.Single(Operations<IrComputeOperation>(body)).Operator);
    }

    [Fact]
    public async Task Receiver_and_index_are_evaluated_once()
    {
        var body = await Lower("""
            using System.Collections.Generic;
            class C
            {
                int[] _cells = new int[4];
                List<int> _items = new();
                int[] Cells() => _cells;
                List<int> Items() => _items;
                int Index() => 0;
                void M() { Cells()[Index()] += 2; Items()[Index()]++; }
            }
            """);

        var calls = Operations<IrCallOperation>(body);
        Assert.Single(calls, call => call.Method.Contains(".Cells(", StringComparison.Ordinal));
        Assert.Single(calls, call => call.Method.Contains(".Items(", StringComparison.Ordinal));
        Assert.Equal(2, calls.Count(call => call.Method.Contains(".Index(", StringComparison.Ordinal)));
        var getter = Assert.Single(calls, call => call.Method.Contains(".get_Item(", StringComparison.Ordinal));
        var setter = Assert.Single(calls, call => call.Method.Contains(".set_Item(", StringComparison.Ordinal));
        Assert.Equal(getter.ReceiverValue, setter.ReceiverValue);
        Assert.Equal(getter.ArgumentValues[0], setter.ArgumentValues[0]);
    }

    [Fact]
    public void Calls_in_the_index_and_the_right_side_keep_their_accesses()
    {
        var run = Analyze(ActionSource("Cells[Next()] += Read();"));

        Assert.Contains(run.Collection.Accesses, access => Path(access) == "_cursor");
        Assert.Contains(run.Collection.Accesses, access => Path(access) == "_source");
        Assert.Contains(run.Collection.Accesses, access => Path(access).StartsWith("Cells.[", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Postfix_yields_the_value_read_and_prefix_the_value_written()
    {
        var body = await Lower("""
            class C
            {
                int[] A = new int[4];
                int _level;
                int Level { get { return _level; } set { _level = value; } }
                int Sink;
                void M(int i)
                {
                    Sink = A[i]++;
                    Sink = ++A[i];
                    Sink = Level--;
                    Sink = Level += 3;
                }
            }
            """);

        var operations = Operations(body);
        var sinks = operations.OfType<IrStoreFieldOperation>().Where(store => store.Field.Name == "Sink").ToArray();
        var loads = operations.OfType<IrLoadElementOperation>().ToArray();
        var computes = operations.OfType<IrComputeOperation>().ToArray();
        var getters = operations.OfType<IrCallOperation>().Where(call => call.Method.Contains(".get_Level(", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, sinks.Length);
        Assert.Equal(4, computes.Length);
        Assert.Equal(loads[0].ResultValue, sinks[0].Value);
        Assert.Equal(computes[1].ResultValue, sinks[1].Value);
        Assert.Equal(getters[0].ResultValue, sinks[2].Value);
        Assert.Equal(computes[3].ResultValue, sinks[3].Value);
    }

    [Fact]
    public async Task Property_with_accessor_bodies_increment_calls_the_getter_then_the_setter()
    {
        var body = await Lower("""
            class C
            {
                int _level;
                int Level { get { return _level; } set { _level = value; } }
                void M() { Level++; }
            }
            """);

        var operations = Operations(body);
        var getter = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains(".get_Level(", StringComparison.Ordinal));
        var setter = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains(".set_Level(", StringComparison.Ordinal));
        var compute = Assert.Single(operations.OfType<IrComputeOperation>());
        Assert.True(IndexOf(operations, getter) < IndexOf(operations, compute));
        Assert.True(IndexOf(operations, compute) < IndexOf(operations, setter));
        Assert.Equal(getter.ResultValue, compute.OperandValues[0]);
        Assert.Equal([compute.ResultValue], setter.ArgumentValues);
        Assert.Equal(getter.ReceiverValue, setter.ReceiverValue);
        Assert.DoesNotContain(operations, operation => operation is IrUnknownOperation);
    }

    /// <summary>A virtual or interface property dispatches both accessors, and <c>base.</c> calls the base accessors themselves, as
    /// its long form does.</summary>
    [Fact]
    public async Task Virtual_and_interface_property_compound_assignment_calls_both_accessors()
    {
        var body = await Lower("""
            interface IGauge { int Value { get; set; } }
            class Base { public virtual int Level { get; set; } }
            class C : Base
            {
                IGauge _gauge;
                Base _other;
                public override int Level { get => 0; set { } }
                void M() { _gauge.Value |= 4; _other.Level -= 1; base.Level += 2; }
            }
            """);

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal(["get_Value", "set_Value", "get_Level", "set_Level", "get_Level", "set_Level"],
                     calls.Select(call => AccessorName(call.Method)));
        Assert.Equal(calls[0].ReceiverValue, calls[1].ReceiverValue);
        Assert.Equal(calls[2].ReceiverValue, calls[3].ReceiverValue);
        Assert.Equal(IrCallKind.Virtual, calls[2].CallKind);
        Assert.Equal(IrCallKind.Virtual, calls[3].CallKind);
        Assert.Equal(IrCallKind.Instance, calls[4].CallKind);
        Assert.Equal(IrCallKind.Instance, calls[5].CallKind);
    }

    [Fact]
    public async Task Static_computed_property_increment_calls_both_accessors()
    {
        var body = await Lower("""
            class C
            {
                static int _count;
                static int Count { get => _count; set => _count = value; }
                void M() { Count++; }
            }
            """);

        var calls = Operations<IrCallOperation>(body);
        Assert.Equal(["get_Count", "set_Count"], calls.Select(call => AccessorName(call.Method)));
        Assert.All(calls, call => Assert.Null(call.ReceiverValue));
        Assert.Equal([Assert.Single(Operations<IrComputeOperation>(body)).ResultValue], calls[1].ArgumentValues);
    }

    [Fact]
    public async Task Source_indexer_compound_assignment_calls_get_and_set_with_the_same_arguments()
    {
        var body = await Lower("""
            class Grid { public int this[int row, int column] { get => 0; set { } } }
            class C
            {
                Grid _grid = new();
                void M(int row) { _grid[row, 2] += 5; }
            }
            """);

        var calls = Operations<IrCallOperation>(body);
        var getter = Assert.Single(calls, call => call.Method.Contains(".get_Item(", StringComparison.Ordinal));
        var setter = Assert.Single(calls, call => call.Method.Contains(".set_Item(", StringComparison.Ordinal));
        var compute = Assert.Single(Operations<IrComputeOperation>(body));
        Assert.Single(Operations<IrLoadFieldOperation>(body), load => load.Field.Name == "_grid");
        Assert.Equal(getter.ReceiverValue, setter.ReceiverValue);
        Assert.Equal([.. getter.ArgumentValues, compute.ResultValue], setter.ArgumentValues);
        Assert.Equal([0, 1, 2], setter.ArgumentParameterOrdinals);
    }

    [Fact]
    public async Task List_indexer_increment_makes_what_its_long_form_makes() =>
        await AssertSameAsLongForm("Items[0]++;", "var items = Items; items[0] = items[0] + 1;");

    [Fact]
    public async Task Dictionary_indexer_compound_assignment_makes_what_its_long_form_makes()
    {
        await AssertSameAsLongForm("Map[\"a\"] += 1;", "var map = Map; map[\"a\"] = map[\"a\"] + 1;");
        await AssertSameAsLongForm("Safe[\"a\"] += 1;", "var safe = Safe; safe[\"a\"] = safe[\"a\"] + 1;");
    }

    /// <summary>A right-hand side that branches has the compiler take the target before the branch; the target is still read there and
    /// written after it through the same receiver and index, as its long form does (review F-0034).</summary>
    [Fact]
    public async Task Compound_assignment_with_a_branching_right_side_makes_what_its_long_form_makes()
    {
        const string right = "var right = Flag ? Read() : 1;";
        await AssertSameAsLongForm("Level += Flag ? Read() : 1;", $"var level = Level; {right} Level = level + right;");
        await AssertSameAsLongForm("Cells[0] += Flag ? Read() : 1;", $"var cells = Cells; var cell = cells[0]; {right} cells[0] = cell + right;");
        await AssertSameAsLongForm("Items[0] += Flag ? Read() : 1;", $"var items = Items; var item = items[0]; {right} items[0] = item + right;");
        await AssertSameAsLongForm("Index += Flag ? Read() : 1;", $"var index = Index; {right} Index = index + right;");
    }

    /// <summary>Every other target the compiler takes before a branching right-hand side — a field, an automatic property, a local, a
    /// parameter, a ref parameter and a member of a <c>dynamic</c> receiver — is read where it is taken and written after the branch
    /// with what was computed, as its long form is, and none lowers to an unknown compound assignment (review F-0038).</summary>
    [Fact]
    public async Task Compound_assignment_with_a_branching_right_side_writes_every_kind_of_target()
    {
        var body = await Lower("""
            class C
            {
                int _count;
                int Auto { get; set; }
                dynamic _d;
                bool Flag;
                int Sink;
                void M(int p, ref int r)
                {
                    int local = 0;
                    _count += Flag ? 1 : 2;
                    Auto += Flag ? 1 : 2;
                    local += Flag ? 1 : 2;
                    p += Flag ? 1 : 2;
                    r += Flag ? 1 : 2;
                    _d.X += Flag ? 1 : 2;
                    Sink = local + p;
                }
            }
            """);

        var operations = Operations(body);
        var computes = operations.OfType<IrComputeOperation>().ToArray();
        Assert.Equal(7, computes.Length);
        Assert.DoesNotContain(operations.OfType<IrUnknownOperation>(),
                              unknown => unknown.OperationKind == "CompoundAssignment" && unknown.DynamicCallee is null);
        var stores = operations.OfType<IrStoreFieldOperation>().ToDictionary(store => store.Field.Name);
        Assert.Equal(computes[0].ResultValue, stores["_count"].Value);
        Assert.Equal(computes[1].ResultValue, stores["Auto"].Value);
        Assert.Equal(computes[4].ResultValue, Assert.Single(operations.OfType<IrStoreReferenceOperation>()).Value);
        var dynamicSet = Assert.Single(operations.OfType<IrUnknownOperation>(), unknown => unknown.DynamicCallee == "dynamic set X");
        Assert.Equal(computes[5].ResultValue, dynamicSet.OperandValues[^1]);
        var sum = computes[6];
        Assert.Equal(sum.ResultValue, stores["Sink"].Value);
        Assert.Equal([ValueOf(operations, computes[2].ResultValue), ValueOf(operations, computes[3].ResultValue)], sum.OperandValues);
    }

    /// <summary>A target taken before a branching right-hand side evaluates its receiver and its index once, where it is taken, and is
    /// written through the receiver that read it: a field, an automatic property, a field of an array's element, and the cell a
    /// ref-returning call hands back (review F-0042).</summary>
    [Fact]
    public async Task Compound_assignment_with_a_branching_right_side_evaluates_its_receiver_once()
    {
        var body = await Lower("""
            class Holder { public int Value; public int Auto { get; set; } }
            struct Slot { public int Value; }
            class C
            {
                Holder _holder = new();
                Slot[] A = new Slot[4];
                int _cell;
                bool Flag;
                Holder Get() => _holder;
                int Index() => 0;
                ref int Cell() => ref _cell;
                void M()
                {
                    Get().Value += Flag ? 1 : 2;
                    Get().Auto += Flag ? 1 : 2;
                    A[Index()].Value += Flag ? 1 : 2;
                    Cell() += Flag ? 1 : 2;
                }
            }
            """);

        var operations = Operations(body);
        var calls = operations.OfType<IrCallOperation>().ToArray();
        Assert.Equal(2, calls.Count(call => call.Method.Contains(".Get(", StringComparison.Ordinal)));
        Assert.Single(calls, call => call.Method.Contains(".Index(", StringComparison.Ordinal));
        Assert.Single(calls, call => call.Method.Contains(".Cell(", StringComparison.Ordinal));
        var loads = operations.OfType<IrLoadFieldOperation>().Where(load => load.Field.Name is "Value" || load.Field.Name.Contains("Auto", StringComparison.Ordinal)).ToArray();
        var stores = operations.OfType<IrStoreFieldOperation>().ToArray();
        Assert.Equal(3, loads.Length);
        Assert.Equal(loads.Select(load => load.ReceiverValue), stores.Select(store => store.ReceiverValue));
        var reference = Assert.Single(operations.OfType<IrStoreReferenceOperation>());
        Assert.Equal(Assert.Single(calls, call => call.Method.Contains(".Cell(", StringComparison.Ordinal)).ResultValue, reference.AddressValue);
    }

    /// <summary>The value a local or a parameter holds after it was assigned <paramref name="value"/>.</summary>
    private static int ValueOf(IrOperation[] operations, int value) =>
        operations.OfType<IrAssignOperation>().Single(assign => assign.SourceValue == value).TargetValue;

    [Fact]
    public async Task ConcurrentDictionary_indexer_increment_is_DCA1004_on_the_cell() =>
        Assert.Contains("DCA1004 Safe.[\"a\"]", await Findings(ActionSource("Safe[\"a\"]++;")));

    [Fact]
    public async Task Array_element_increment_in_two_executions_is_DCA1002()
    {
        var findings = await Findings(WorkerSource("Cells[Index]++;", "Cells[Index]++;"));

        Assert.Contains(findings, finding => finding.StartsWith("DCA1002 Cells.[", StringComparison.Ordinal));
        Assert.Equal(await Findings(WorkerSource("var cells = Cells; var index = Index; cells[index] = cells[index] + 1;",
                                                 "var cells = Cells; var index = Index; cells[index] = cells[index] + 1;")),
                     findings);
    }

    [Fact]
    public async Task Property_increment_in_two_executions_is_DCA1002_on_its_backing_field() =>
        Assert.Contains("DCA1002 _level", await Findings(WorkerSource("Level++;", "Level++;")));

    [Fact]
    public async Task Struct_element_field_increment_is_one_read_modify_write()
    {
        var body = await Lower("""
            struct Slot { public int Value; }
            class C { Slot[] Slots = new Slot[4]; void M(int i) { Slots[i].Value++; } }
            """);

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body), operation => operation.Field.Name == "Value");
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal("Value", store.Field.Name);
        Assert.Equal(load.Id, store.ReadModifyWriteOf);
        Assert.Equal(load.ReceiverValue, store.ReceiverValue);
    }

    /// <summary>A property that returns a reference and has no setter is a target the lowering does not model: its receiver and the
    /// right-hand side are still lowered, their calls kept, and handed to its unknown.</summary>
    [Fact]
    public async Task Unmodelled_compound_target_still_lowers_its_operands()
    {
        var body = await Lower("""
            class Holder { int _value; public ref int Slot => ref _value; }
            class C
            {
                Holder _holder = new();
                Holder Get() => _holder;
                int Read() => 1;
                void M() { Get().Slot += Read(); Get().Slot++; }
            }
            """);

        var calls = Operations<IrCallOperation>(body);
        var unknowns = Operations<IrUnknownOperation>(body).Where(unknown => unknown.Reason == "unsupported").ToArray();
        var holders = calls.Where(call => call.Method.Contains(".Get(", StringComparison.Ordinal)).Select(call => call.ResultValue!.Value).ToArray();
        var read = Assert.Single(calls, call => call.Method.Contains(".Read(", StringComparison.Ordinal));
        Assert.Equal(2, holders.Length);
        Assert.Equal(2, unknowns.Length);
        Assert.Equal([holders[0], read.ResultValue!.Value], unknowns[0].OperandValues);
        Assert.Equal([holders[1]], unknowns[1].OperandValues);
    }

    /// <summary>The findings and the accesses of the member are those of its long form with the receiver and the index taken into
    /// locals once.</summary>
    private static async Task AssertSameAsLongForm(string compound, string longForm)
    {
        var findings = await Findings(ActionSource(compound));
        Assert.NotEmpty(findings);
        Assert.Equal(await Findings(ActionSource(longForm)), findings);
        Assert.Equal(Accesses(Analyze(ActionSource(longForm))), Accesses(Analyze(ActionSource(compound))));
    }

    private static IReadOnlyList<string> Accesses(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Symbol == "Store.Act()" && !access.IsConstructionLocal)
           .Select(access => $"{access.Resource.Identity} {access.Operation.ToWireName()}")
           .Order(StringComparer.Ordinal)
           .ToArray();

    private static string AccessorName(string method)
    {
        var name = method[..method.IndexOf('(', StringComparison.Ordinal)];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    private static int IndexOf(IrOperation[] operations, IrOperation operation) => Array.IndexOf(operations, operation);

    private static string Path(Access access) => string.Join(".", access.Resource.AccessPath);

    private const string Fields = """
            public readonly int[] Cells = new int[4];
            public readonly List<int> Items = new() { 0, 0 };
            public readonly Dictionary<string, int> Map = new() { ["a"] = 0 };
            public readonly ConcurrentDictionary<string, int> Safe = new();
            public int Index = Environment.ProcessorCount % 4;
            public bool Flag = Environment.ProcessorCount > 1;
            private int _level;
            private int _cursor;
            private int _source;

            public int Level { get { return _level; } set { _level = value; } }

            public int Next() => _cursor++;

            public int Read() => _source;
        """;

    private const string CollectionUsings = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        """;

    /// <summary>Two workers of one singleton, each running its own member of it.</summary>
    private static string WorkerSource(string first, string second) => CollectionUsings + $$"""
        public sealed class Store
        {
        {{Fields}}

            public void First()
            {
                {{first}}
            }

            public void Second()
            {
                {{second}}
            }
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly Store _store;
            public FirstWorker(Store store) => _store = store;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _store.First();
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly Store _store;
            public SecondWorker(Store store) => _store = store;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _store.Second();
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Store>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");

    /// <summary>One action of a controller, which may run against itself.</summary>
    private static string ActionSource(string body) => CollectionUsings + $$"""
        public sealed class Store
        {
        {{Fields}}

            public void Act()
            {
                {{body}}
            }
        }

        public class ActionController : ControllerBase
        {
            private readonly Store _store;
            public ActionController(Store store) => _store = store;
            public void Post() => _store.Act();
        }
        """ + Startup("services.AddSingleton<Store>();");

    /// <summary>The findings of a fixture as rule and resource, run with the solver as the analyzer runs it.</summary>
    private static async Task<IReadOnlyList<string>> Findings(string source)
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY,
                                                         ProviderRegistry.BuiltIn, CancellationToken.None);
        return result.Findings.Select(finding => $"{finding.RuleId} {string.Join(".", finding.Resource.AccessPath)}")
                     .Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<IrBody> Lower(string source)
    {
        var solution = FixtureSolution.Create(("Case.cs", source));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, ROOT_DIRECTORY, CancellationToken.None).Body;
    }

    private static IrOperation[] Operations(IrBody body) =>
        body.Blocks.SelectMany(block => block.Operations).ToArray();

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        Operations(body).OfType<T>().ToArray();
}
