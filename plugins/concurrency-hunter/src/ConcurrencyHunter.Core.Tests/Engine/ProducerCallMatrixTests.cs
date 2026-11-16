using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ProducerCallMatrixTests
{
    private enum Position { Receiver, Argument }
    private enum Flow
    {
        Local, Phi, Conversion, Await, HelperReturn, CallerArgument, CapturedVariable, ConstructorField,
        FieldInitializer, AutoPropertyInitializer, CollectionElement, CollectionKey, KeeperStorage, StructCopy
    }
    private enum Producer { PackageBody, PackageConstructor, PackageDispatch, Framework }
    private enum Chain { Direct, ThroughProducer }
    private enum Earlier { No, Yes }
    private sealed record Cell(Position Position, Flow Flow, Producer Producer, Chain Chain, Earlier Earlier);
    private sealed record Source(string Work, string Members, string Extra, string ProducerMember);

    // Entries name complete cells, never a pattern that can silently exempt a new cell. A reason here is a run-record leftover.
    private static readonly IReadOnlyDictionary<Cell, string> WIDER = new Dictionary<Cell, string>();

    private const string LIBRARY = """
        using System;
        using System.Threading.Tasks;
        public class MatrixValue
        {
            public MatrixValue() { }
            public MatrixValue(int marker) { }
            public void Decide(Action action) { action(); }
            public void Earlier(Action action) { action(); }
            public MatrixValue Wrap() => this;
            public virtual MatrixValue Virtual() => this;
            public virtual Task<MatrixValue> VirtualAsync() => Task.FromResult(this);
        }
        public static class MatrixApi
        {
            public static MatrixValue Make() => new MatrixValue(1);
            public static Task<MatrixValue> MakeAsync() => Task.FromResult(Make());
            public static MatrixValue Factory() => new MatrixValue();
            public static MatrixValue Wrap(MatrixValue value) => value;
            public static void Decide(MatrixValue value, Action action) { action(); }
        }
        public class MatrixKeeper
        {
            private MatrixValue _value = null!;
            public virtual void Keep(MatrixValue value) { _value = value; }
            public virtual MatrixValue Take() => _value;
        }
        """;

    private const string MODELS = """
        {"schemaVersion":1,"assemblies":["CallSiteLibrary"],"models":[
          {"member":"M:MatrixKeeper.Keep(MatrixValue)","effects":{},"keeps":{"this":["arg:value"]}},
          {"member":"M:MatrixKeeper.Take","effects":{},"result":"[kept:this]"}
        ]}
        """;

    private static IEnumerable<Cell> Cells() =>
        from position in Enum.GetValues<Position>()
        from flow in Enum.GetValues<Flow>()
        from producer in Enum.GetValues<Producer>()
        where flow != Flow.Await || producer != Producer.PackageConstructor // An object creation is not awaited.
        from chain in Enum.GetValues<Chain>()
        from earlier in Enum.GetValues<Earlier>()
        where earlier == Earlier.No || producer is Producer.PackageBody or Producer.PackageConstructor // The other calls cannot be admitted.
        select new Cell(position, flow, producer, chain, earlier);

    [Fact]
    public void Every_value_of_every_axis_occurs_and_the_cells_are_counted()
    {
        var cells = Cells().ToArray();
        Assert.Equal(328, cells.Length);
        Assert.Equal(220, cells.Count(cell => cell.Earlier == Earlier.No));
        Assert.Equal(108, cells.Count(cell => cell.Earlier == Earlier.Yes));
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Position>(), cells.Select(cell => cell.Position).Distinct().Order());
        Assert.Equal(Enum.GetValues<Flow>(), cells.Select(cell => cell.Flow).Distinct().Order());
        Assert.Equal(Enum.GetValues<Producer>(), cells.Select(cell => cell.Producer).Distinct().Order());
        Assert.Equal(Enum.GetValues<Chain>(), cells.Select(cell => cell.Chain).Distinct().Order());
        Assert.Equal(Enum.GetValues<Earlier>(), cells.Select(cell => cell.Earlier).Distinct().Order());
        Assert.DoesNotContain(cells, cell => cell.Flow == Flow.Await && cell.Producer == Producer.PackageConstructor);
        Assert.DoesNotContain(cells, cell => cell.Earlier == Earlier.Yes && cell.Producer is Producer.PackageDispatch or Producer.Framework);
    }

    [Fact]
    public void Every_cell_is_exact_outside_the_wider_list()
    {
        var errors = new List<string>();
        foreach (var cell in Cells().Where(cell => !WIDER.ContainsKey(cell)))
        {
            try { Check(cell, wider: false); }
            catch (Exception error) { errors.Add($"{cell}: {error.Message}"); }
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Every_wider_cell_is_wider_and_names_a_cell()
    {
        var cells = Cells().ToHashSet();
        foreach (var (cell, reason) in WIDER)
        {
            Assert.Contains(cell, cells);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Check(cell, wider: true);
        }
    }

    [Fact]
    public void Package_producer_behind_a_framework_call_is_admitted()
    {
        var program = new CallSiteTestProgram(
            "var items = new System.Collections.Generic.List<MatrixValue>(); items.Add(MatrixApi.Make()); items[0].Decide(() => state.Value++);",
            library: LIBRARY);
        var run = program.Run();
        var deciding = Assert.Single(CallSiteTestProgram.Calls(run, "MatrixValue.Decide"));
        var factory = Assert.Single(CallSiteTestProgram.Calls(run, "MatrixApi.Make"));
        var framework = Assert.Single(CallSiteTestProgram.Calls(run, "get_Item"));
        var producers = ProducerCalls.Of(run.Heap!, deciding, IsProgram);
        Assert.Contains(factory, producers);
        Assert.Contains(framework, producers);
        using var compilations = new CallSiteCompilations();
        var rounds = program.Rounds(compilations);
        Assert.True(rounds.Grow(run, CallSiteTestProgram.Pairs(run), 1));
        Assert.Contains(factory, rounds.Admission.Calls!.Keys);
        Assert.DoesNotContain(framework, rounds.Admission.Calls.Keys);
        Assert.Contains(Place(factory), Assert.Single(rounds.Measurements).Producers);
    }

    [Fact]
    public void Ninth_hop_is_not_followed()
    {
        foreach (var hops in new[] { 8, 9 })
        {
            var program = new CallSiteTestProgram("MatrixApi.Make()" + string.Concat(Enumerable.Repeat(".Wrap()", hops)) +
                                                  ".Decide(() => state.Value++);", library: LIBRARY);
            var run = program.Run();
            var deciding = Assert.Single(CallSiteTestProgram.Calls(run, "MatrixValue.Decide"));
            var factory = Assert.Single(CallSiteTestProgram.Calls(run, "MatrixApi.Make"));
            var producers = ProducerCalls.Of(run.Heap!, deciding, IsProgram);
            Assert.Equal(hops == 8, producers.Contains(factory));
            using var compilations = new CallSiteCompilations();
            var rounds = program.Rounds(compilations);
            Assert.True(rounds.Grow(run, CallSiteTestProgram.Pairs(run), 1));
            Assert.Equal(hops == 8, rounds.Admission.Calls!.ContainsKey(factory));
            Assert.Equal(9, producers.Count);
        }
    }

    private static void Check(Cell cell, bool wider)
    {
        var source = Fixture(cell);
        var program = new CallSiteTestProgram(source.Work, source.Members, source.Extra, LIBRARY,
                                              models: MODELS);
        var run = program.Run();
        var primary = CallSiteTestProgram.Calls(run, source.ProducerMember);
        Assert.Equal(cell.Flow == Flow.Phi ? 2 : 1, primary.Count);
        var deciding = Assert.Single(CallSiteTestProgram.Calls(run, cell.Position == Position.Receiver ? "MatrixValue.Decide" : "MatrixApi.Decide"));
        using var compilations = new CallSiteCompilations();
        var selector = program.Selector(compilations);
        var rounds = program.Rounds(compilations);
        var round = 1;
        if (cell.Earlier == Earlier.Yes)
        {
            var earlier = Assert.Single(CallSiteTestProgram.Calls(run, "MatrixValue.Earlier"));
            var pairs = CallSiteTestProgram.Pairs(run);
            Assert.Contains(pairs.Pairs.SelectMany(pair => pair.DecidingCalls), decision => decision.Call == earlier);
            Assert.DoesNotContain(pairs.Pairs.SelectMany(pair => pair.DecidingCalls), decision => decision.Call == deciding);
            Assert.True(rounds.Grow(run, pairs, round++));
            Assert.All(primary, call => Assert.Contains(call, rounds.Admission.Calls!.Keys));
            Assert.All(primary, call => Assert.Contains(Place(call), Assert.Single(rounds.Measurements).Producers));
            run = program.Run(rounds.Admission);
            Assert.Contains(deciding, CallSiteTestProgram.Calls(run, "Decide"));
        }

        var producers = ProducerCalls.Of(run.Heap!, deciding, IsProgram);
        Assert.All(primary, call => Assert.Equal(!wider, producers.Contains(call)));
        var pairsNow = CallSiteTestProgram.Pairs(run);
        Assert.Contains(pairsNow.Pairs.SelectMany(pair => pair.DecidingCalls), decision => decision.Call == deciding);
        Assert.True(rounds.Grow(run, pairsNow, round));
        var eligible = cell.Producer is Producer.PackageBody or Producer.PackageConstructor;
        Assert.All(primary, call =>
        {
            Assert.Equal(cell.Producer == Producer.Framework ? "framework" : eligible ? null : "no-body",
                         selector.Target(run, call, [program.Project]).Outcome);
            Assert.Equal(eligible && (!wider || cell.Earlier == Earlier.Yes), rounds.Admission.Calls!.ContainsKey(call));
        });
        if (cell.Chain == Chain.ThroughProducer)
        {
            var wrapper = Assert.Single(CallSiteTestProgram.Calls(run, cell.Position == Position.Receiver ? "MatrixValue.Wrap" : "MatrixApi.Wrap"));
            Assert.Contains(wrapper, producers);
            Assert.Contains(wrapper, rounds.Admission.Calls!.Keys);
        }

        var measurement = Assert.Single(rounds.Measurements, measurement => measurement.Round == round && measurement.Callee.Contains("Decide", StringComparison.Ordinal));
        Assert.All(primary, call => Assert.Equal(!wider, measurement.Producers.Contains(Place(call))));
        var entries = producers.Append(deciding).Where(call => selector.Target(run, call, [program.Project]).Outcome is null).ToArray();
        var admission = CallSiteTestProgram.Admit(entries);
        var counted = program.Run(admission, countOnly: true);
        Assert.Null(counted.Heap); // The bound is measured in the call graph, before summaries and the heap.
        Assert.Equal(counted.Reachable.ReachedBodies.Keys.Count(admission.IsLibraryBody), measurement.BodyCount);
        if (eligible && !wider)
        {
            Assert.All(primary, call => Assert.Contains(call.Target, counted.Reachable.ReachedBodies.Keys));
            var without = CallSiteTestProgram.Admit(entries.Except(primary).ToArray());
            var fewer = program.Run(without, countOnly: true).Reachable.ReachedBodies.Keys.Count(admission.IsLibraryBody);
            Assert.True(measurement.BodyCount > fewer, "The deciding call's count must include even a producer admitted in an earlier round.");
        }
        var last = program.Run(rounds.Admission);
        Assert.All(primary, call => Assert.Contains(call, CallSiteTestProgram.Calls(last, source.ProducerMember)));
    }

    private static bool IsProgram(string body) => body.StartsWith("body:Fixture:", StringComparison.Ordinal);
    private static string Place(CallIdentity call) => $"{call.Span.Path}:{call.Span.StartLine}";

    private static Source Fixture(Cell cell)
    {
        var awaited = cell.Flow == Flow.Await;
        var expression = cell.Producer switch
        {
            Producer.PackageBody => awaited ? "MatrixApi.MakeAsync()" : "MatrixApi.Make()",
            Producer.PackageConstructor => "new MatrixValue(1)",
            Producer.PackageDispatch => awaited ? "MatrixApi.Factory().VirtualAsync()" : "MatrixApi.Factory().Virtual()",
            _ => awaited ? "Task.FromResult<MatrixValue>(null!)" : "Activator.CreateInstance<MatrixValue>()"
        };
        var member = cell.Producer switch
        {
            Producer.PackageBody => awaited ? "MatrixApi.MakeAsync" : "MatrixApi.Make",
            Producer.PackageConstructor => "MatrixValue.#ctor(System.Int32)",
            Producer.PackageDispatch => awaited ? "MatrixValue.VirtualAsync" : "MatrixValue.Virtual",
            _ => awaited ? "Task.FromResult" : "Activator.CreateInstance"
        };
        var input = cell.Chain == Chain.Direct ? "value" : cell.Position == Position.Receiver ? "value.Wrap()" : "MatrixApi.Wrap(value)";
        var action = cell.Earlier == Earlier.No ? "state.Value++" : "value.Earlier(() => state.Value++)";
        var consume = cell.Position == Position.Receiver ? $"{input}.Decide(() => {action});" : $"MatrixApi.Decide({input}, () => {action});";
        var members = string.Empty;
        var extra = string.Empty;
        var work = $"var value = {expression}; {consume}";
        switch (cell.Flow)
        {
            case Flow.Phi:
                work = $"MatrixValue value; if (state.Value > 0) value = {expression}; else value = {expression}; {consume}";
                break;
            case Flow.Conversion:
                work = $"object stored = {expression}; var value = (MatrixValue)stored; {consume}";
                break;
            case Flow.Await:
                members = $"private async Task<MatrixValue> ProduceAsync() => await {expression};";
                work = $"var value = ProduceAsync().GetAwaiter().GetResult(); {consume}";
                break;
            case Flow.HelperReturn:
                members = $"private MatrixValue Produce() => {expression};";
                work = $"var value = Produce(); {consume}";
                break;
            case Flow.CallerArgument:
                members = $"private void Consume(MatrixValue value) {{ {consume} }}";
                work = $"Consume({expression});";
                break;
            case Flow.CapturedVariable:
                work = $"var value = {expression}; Action consume = () => {{ {consume} }}; consume();";
                break;
            case Flow.ConstructorField:
                extra = $"public sealed class Holder {{ public MatrixValue Value; public Holder() {{ Value = {expression}; }} }}";
                work = $"var holder = new Holder(); var value = holder.Value; {consume}";
                break;
            case Flow.FieldInitializer:
                members = $"private MatrixValue _value = {expression};";
                work = $"var value = _value; {consume}";
                break;
            case Flow.AutoPropertyInitializer:
                extra = $"public sealed class Holder {{ public MatrixValue Value {{ get; }} = {expression}; }}";
                work = $"var holder = new Holder(); var value = holder.Value; {consume}";
                break;
            case Flow.CollectionElement:
                work = $"var items = new System.Collections.Generic.List<MatrixValue>(); items.Add({expression}); var value = items[0]; {consume}";
                break;
            case Flow.CollectionKey:
                work = $"var seed = {expression}; var items = new System.Collections.Generic.Dictionary<MatrixValue, int>(); items.Add(seed, 0); foreach (var value in items.Keys) {{ {consume.Replace("value.Earlier", "seed.Earlier", StringComparison.Ordinal)} }}";
                break;
            case Flow.KeeperStorage:
                work = $"var keeper = new MatrixKeeper(); keeper.Keep({expression}); var value = keeper.Take(); {consume}";
                break;
            case Flow.StructCopy:
                extra = "public struct Carrier { public MatrixValue Value; }";
                work = $"var original = new Carrier {{ Value = {expression} }}; var copy = original; var value = copy.Value; {consume}";
                break;
        }
        return new(work.Replace("; ", ";\n", StringComparison.Ordinal), members, extra, member);
    }
}
