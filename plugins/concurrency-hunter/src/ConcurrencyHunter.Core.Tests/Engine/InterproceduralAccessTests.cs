using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class InterproceduralAccessTests
{
    [Fact]
    public void Three_layer_finding_summarizes_each_body_once_and_lists_every_call()
    {
        var run = Analyze("""
            public sealed class Inventory { private int _reserved; public void Reserve(int count) => _reserved += count; }
            public sealed class OrderService(Inventory inventory) { public void Place() => inventory.Reserve(1); }
            public class OrdersController(OrderService service) : ControllerBase { public void Post() => service.Place(); }
            """ + Startup("services.AddSingleton<Inventory>(); services.AddSingleton<OrderService>();"));

        var access = Assert.Single(run.Accesses("_reserved"));
        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);
        Assert.Equal(["calls OrderService.Place() on di:OrderService@Singleton", "calls Inventory.Reserve(int) on di:Inventory@Singleton"],
                     access.CodeFlow.Where(step => step.Kind == "call").Select(step => step.Text));
        Assert.Single(run.PairsOn("_reserved"));
        var heap = run.Execution.Heap;
        Assert.Equal(heap.Heap.ReachableBodies.Count, heap.Summaries.Built);
    }

    [Fact]
    public void Lambda_calling_a_local_function_that_writes_a_field_accesses_the_singleton()
    {
        var run = Analyze("""
            public sealed class Ledger
            {
                private string? _last;
                public void Append(string entry)
                {
                    Action write = () => Store();
                    write();
                    void Store() => _last = entry;
                }
            }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post(string entry) => ledger.Append(entry); }
            """ + Startup("services.AddSingleton<Ledger>();"));

        var access = Assert.Single(run.Accesses("_last"));
        Assert.Equal("di:Ledger@Singleton", access.Resource.Region);
        Assert.Same(access, Assert.Single(run.PairsOn("_last")).First);
        Assert.Equal(0, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT));
    }

    [Fact]
    public void Lock_in_the_caller_protects_the_callee_access()
    {
        var run = Analyze("""
            public sealed class Ledger
            {
                private readonly object _gate = new();
                private object? _last;
                public void Write() { lock (_gate) { Store(); } }
                private void Store() => _last = new object();
            }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post() => ledger.Write(); }
            """ + Startup("services.AddSingleton<Ledger>();"));

        var store = Assert.Single(run.Accesses("_last"));
        Assert.Single(store.HeldProtectionIds);
        Assert.Empty(run.PairsOn("_last"));
        Assert.True(run.Pairs.Suppressed > 0);
    }

    [Fact]
    public void Lock_on_a_static_readonly_field_a_helper_created_suppresses()
    {
        var run = Analyze("""
            public static class State { public static int Value; }
            public static class Gates
            {
                public static readonly object Main = Create();
                private static object Create()
                {
                    object? gate = null;
                    for (var index = 0; index < 2; index++)
                        gate = new object();
                    return gate!;
                }
            }
            public class StateController : ControllerBase
            {
                public void First() { lock (Gates.Main) { State.Value = 1; } }
                public void Second() { lock (Gates.Main) { State.Value = 2; } }
            }
            """ + Startup());

        var writes = run.Accesses("Value");
        Assert.Equal(2, writes.Count);
        Assert.All(writes, write => Assert.Single(write.HeldProtectionIds));
        Assert.Single(writes.SelectMany(write => write.HeldProtectionIds).Distinct(StringComparer.Ordinal));
        Assert.Empty(run.PairsOn("Value"));
        Assert.True(run.Pairs.Suppressed > 0);
    }

    [Fact]
    public void Lock_on_a_readonly_field_of_the_same_singleton_one_call_down_suppresses()
    {
        var run = Analyze("""
            public sealed class Sequence { private readonly object _gate = new(); private int _next; public void Advance() { lock (_gate) { _next = _next + 1; } } }
            public sealed class Service(Sequence sequence) { public void Run() => sequence.Advance(); }
            public class SequenceController(Service service) : ControllerBase { public void Post() => service.Run(); }
            """ + Startup("services.AddSingleton<Sequence>(); services.AddSingleton<Service>();"));

        Assert.Empty(run.PairsOn("_next"));
        Assert.True(run.Pairs.Suppressed > 0);
    }

    [Fact]
    public void Two_fields_aliasing_one_lock_suppress()
    {
        var run = Analyze("""
            public sealed class Ledger
            {
                private readonly object _gate = new();
                private readonly object _second;
                private object? _lastEntry;
                public Ledger() => _second = _gate;
                public void WriteFirst() { lock (_gate) { _lastEntry = new object(); } }
                public void WriteSecond() { lock (_second) { _lastEntry = new object(); } }
            }
            public class LedgerController(Ledger ledger) : ControllerBase
            {
                public void First() => ledger.WriteFirst();
                public void Second() => ledger.WriteSecond();
            }
            """ + Startup("services.AddSingleton<Ledger>();"));

        Assert.Empty(run.PairsOn("_lastEntry"));
        Assert.All(run.Accesses("_lastEntry").Where(access => !access.IsConstructionLocal), access => Assert.Single(access.HeldProtectionIds));
    }

    [Fact]
    public void User_lock_type_protects_nothing()
    {
        var run = Analyze("""
            public sealed class SimpleLock { public void Lock() { } public void Unlock() { } }
            public sealed class Journal { private readonly SimpleLock _lock = new(); private object? _last; public void Append() { _lock.Lock(); _last = new object(); _lock.Unlock(); } }
            public class JournalController(Journal journal) : ControllerBase { public void Post() => journal.Append(); }
            """ + Startup("services.AddSingleton<Journal>();"));

        Assert.Equal(PairProtection.UNPROTECTED, Assert.Single(run.PairsOn("_last")).Protection);
    }

    [Fact]
    public void Property_increment_by_assignment_is_one_read_modify_write_without_a_separate_read()
    {
        var run = Analyze("""
            public sealed class Tallies { public int Views { get; set; } public void AddView() => Views = Views + 1; }
            public class TalliesController(Tallies tallies) : ControllerBase { public void Post() => tallies.AddView(); }
            """ + Startup("services.AddSingleton<Tallies>();"));

        Assert.Equal([AccessOperation.ReadModifyWrite], run.Accesses("Views").Select(access => access.Operation));
    }

    [Fact]
    public void Split_read_compute_write_is_one_read_modify_write()
    {
        var run = Analyze("""
            public sealed class Tallies { private int _split; public void AddSplit(int amount) { var current = _split; var next = current + amount; _split = next; } }
            public class TalliesController(Tallies tallies) : ControllerBase { public void Post(int amount) => tallies.AddSplit(amount); }
            """ + Startup("services.AddSingleton<Tallies>();"));

        Assert.Equal([AccessOperation.ReadModifyWrite], run.Accesses("_split").Select(access => access.Operation));
    }

    [Fact]
    public void Call_result_that_ignores_its_argument_is_a_plain_write_and_an_out_parameter_carries_its_read()
    {
        var run = Analyze("""
            public sealed class Tally
            {
                private int _count;
                public void Reset() => _count = Constant(_count);
                public void Bump() { Copy(out var current); _count = current + 1; }
                private static int Constant(int value) => 7;
                private void Copy(out int value) => value = _count;
            }
            public class TallyController(Tally tally) : ControllerBase { public void Post() { tally.Reset(); tally.Bump(); } }
            """ + Startup("services.AddSingleton<Tally>();"));

        var reset = Assert.Single(run.Accesses("_count"), access => access.Symbol == "Tally.Reset()" && access.Operation != AccessOperation.Read);
        Assert.Equal(AccessOperation.Write, reset.Operation);
        Assert.Empty(reset.ReadSources);
        var bump = Assert.Single(run.Accesses("_count"), access => access.Symbol == "Tally.Bump()");
        Assert.Equal(AccessOperation.ReadModifyWrite, bump.Operation);
        Assert.Equal("Tally.Copy(int)", Assert.Single(bump.ReadSources).Symbol);
    }

    [Fact]
    public void Only_the_instance_whose_load_feeds_the_write_loses_its_read()
    {
        var run = Analyze("""
            public sealed class Counter { public int Value; }
            public static class Helper { public static int Read(Counter counter) => counter.Value; }
            public sealed class Totals(Counter counter)
            {
                public int Seen;
                public void Bump() { counter.Value = Helper.Read(counter) + 1; Seen = Helper.Read(counter); }
            }
            public class TotalsController(Totals totals) : ControllerBase { public void Post() => totals.Bump(); }
            """ + Startup("services.AddSingleton<Totals>(); services.AddSingleton<Counter>();"));

        var accesses = run.Accesses("Value");
        var bump = Assert.Single(accesses, access => access.Operation == AccessOperation.ReadModifyWrite);
        Assert.Equal("Totals.Bump()", bump.Symbol);
        // The second call site is another instance of Helper.Read, whose load feeds nothing, so its read survives and pairs.
        var read = Assert.Single(accesses, access => access.Operation == AccessOperation.Read);
        Assert.Equal(bump.Resource.Identity, read.Resource.Identity);
        Assert.Contains(run.PairsOn("Value"), pair => ReferenceEquals(pair.First, read) || ReferenceEquals(pair.Second, read));
    }

    [Fact]
    public void Read_and_write_through_two_methods_is_a_read_modify_write_at_the_setter_store()
    {
        var run = Analyze(ThroughMethods);

        var access = Assert.Single(run.Accesses("_level"));
        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);
        Assert.Equal("Tallies.SetLevel(int)", access.Symbol);
    }

    [Fact]
    public void Stale_read_with_an_independent_write_gives_a_read_and_a_write()
    {
        var run = Analyze("""
            public sealed class Headline { private string? _text; public string? Replace(string text) { var previous = _text; _text = text; return previous; } }
            public class HeadlineController(Headline headline) : ControllerBase { public string? Put(string text) => headline.Replace(text); }
            """ + Startup("services.AddSingleton<Headline>();"));

        Assert.Equal([AccessOperation.Read, AccessOperation.Write], run.Accesses("_text").Select(access => access.Operation).Order());
    }

    [Fact]
    public void Virtual_override_without_an_allocation_produces_no_access()
    {
        var run = Analyze("""
            public static class Counters { public static object? Circle; public static object? Square; }
            public abstract class Shape { public abstract void Draw(); }
            public sealed class Circle : Shape { public override void Draw() => Counters.Circle = new object(); }
            public sealed class Square : Shape { public override void Draw() => Counters.Square = new object(); }
            public class ShapeController : ControllerBase { public void Post() { Shape shape = new Circle(); shape.Draw(); } }
            """ + Startup());

        Assert.Single(run.Accesses("Circle"));
        Assert.Empty(run.Accesses("Square"));
    }

    [Fact]
    public void Base_call_from_an_override_runs_the_base_body_at_the_call_site_and_never_an_override()
    {
        var run = Analyze("""
            public class Tally { protected int _base; public virtual void Bump() => _base = 1; }
            public class MidTally : Tally { protected int _mid; public override void Bump() { _mid = 1; base.Bump(); } }
            public sealed class TopTally : MidTally { public override void Bump() => base.Bump(); }
            public sealed class SideTally : Tally { private int _side; public override void Bump() => _side = 1; }
            public class TallyController(TopTally tally) : ControllerBase { public void Post() => tally.Bump(); }
            """ + Startup("services.AddSingleton<TopTally>();"));

        Assert.Equal(["calls TopTally.Bump() on di:TopTally@Singleton", "calls MidTally.Bump() on di:TopTally@Singleton",
                      "calls Tally.Bump() on di:TopTally@Singleton"],
                     Assert.Single(run.Accesses("_base")).CodeFlow.Where(step => step.Kind == "call").Select(step => step.Text));
        Assert.Single(run.Accesses("_mid"));
        var heap = run.Execution.Heap.Heap;
        Assert.DoesNotContain(heap.Edges, edge => heap.Instances[edge.CallerInstance].BodyId == heap.Instances[edge.CalleeInstance].BodyId);
        Assert.False(run.Execution.Heap.Program.Reaches("body:Fixture:M:SideTally.Bump"));
    }

    [Theory]
    [InlineData("Action bump = base.Bump; bump();")]
    [InlineData("Task.Run(base.Bump).Wait();")]
    public void Base_method_group_delegate_runs_the_base_body_and_never_an_override(string callBase)
    {
        var run = Analyze($$"""
            public class Tally { protected int _base; public virtual void Bump() => _base = 1; }
            public class MidTally : Tally { protected int _mid; public override void Bump() { _mid = 1; {{callBase}} } }
            public sealed class TopTally : MidTally { public override void Bump() { {{callBase}} } }
            public sealed class SideTally : Tally { private int _side; public override void Bump() => _side = 1; }
            public class TallyController(TopTally tally) : ControllerBase { public void Post() => tally.Bump(); }
            """ + Startup("services.AddSingleton<TopTally>();"));

        Assert.Equal("di:TopTally@Singleton", Assert.Single(run.Accesses("_base")).Resource.Region);
        Assert.Single(run.Accesses("_mid"));
        var heap = run.Execution.Heap.Heap;
        Assert.DoesNotContain(heap.Edges, edge => heap.Instances[edge.CallerInstance].BodyId == heap.Instances[edge.CalleeInstance].BodyId);
        // A spawned body is no call edge: it is one of the spawn's callees.
        Assert.DoesNotContain(heap.Spawns, spawn => spawn.Callees.Any(callee => heap.Instances[callee.InstanceId].BodyId == heap.Instances[spawn.CallerInstance].BodyId));
        Assert.False(run.Execution.Heap.Program.Reaches("body:Fixture:M:SideTally.Bump"));
    }

    [Fact]
    public void Base_ref_indexer_reaches_the_cell_the_base_accessor_returns_and_never_an_override()
    {
        var run = Analyze("""
            public class Cells { public int Cell; public virtual ref int this[int index] => ref Cell; }
            public class MidCells : Cells { public int Reads; public override ref int this[int index] { get { Reads++; return ref base[index]; } } }
            public sealed class TopCells : MidCells { public override ref int this[int index] => ref base[index]; }
            public sealed class SideCells : Cells { public int Side; public override ref int this[int index] => ref Side; }
            public class CellsController(TopCells cells) : ControllerBase { public void Post() => cells[0] = 1; }
            """ + Startup("services.AddSingleton<TopCells>();"));

        var write = Assert.Single(run.Accesses("Cell"));
        Assert.Equal(AccessOperation.Write, write.Operation);
        Assert.Equal("di:TopCells@Singleton", write.Resource.Region);
        Assert.Single(run.Accesses("Reads"));
        var heap = run.Execution.Heap.Heap;
        Assert.DoesNotContain(heap.Edges, edge => heap.Instances[edge.CallerInstance].BodyId == heap.Instances[edge.CalleeInstance].BodyId);
        Assert.False(run.Execution.Heap.Program.Reaches("body:Fixture:M:SideCells.get_Item(System.Int32)"));
    }

    [Fact]
    public void Wildcard_write_pairs_with_a_read_of_another_field_and_with_a_wildcard_read()
    {
        var run = Analyze(DeepChain(string.Empty) + """
            public class DeepController(DeepChain chain) : ControllerBase
            {
                public void Put(string value) => chain.Write(value);
                public string? Get() => chain.Read();
                public string? Label() => chain.Label;
            }
            """ + Startup("services.AddSingleton<DeepChain>();"));

        var wildcardPairs = run.Pairs.Pairs.Where(pair => pair.Resource.IsWildcard).ToArray();
        Assert.Contains(wildcardPairs, pair => new[] { pair.First, pair.Second }.Any(access => access.Resource.AccessPath.SequenceEqual(["Label"])));
        Assert.Contains(wildcardPairs, pair => pair.First.Resource.IsWildcard && pair.Second.Resource.IsWildcard &&
                                               new[] { pair.First, pair.Second }.Any(access => access.Operation == AccessOperation.Read));
        Assert.All(wildcardPairs, pair => Assert.Equal("di:DeepChain@Singleton", pair.Resource.Region));
        Assert.True(run.Counter(CoverageCounters.WILDCARD_ACCESS) > 0);
    }

    [Fact]
    public void Task_run_lambda_is_spawn_work_and_not_a_delegate_to_an_opaque_call()
    {
        var run = Analyze("""
            public static class Counters { public static object? Value; }
            public class JobsController : ControllerBase { public void Post() => Task.Run(() => Counters.Value = new object()); }
            """ + Startup());

        Assert.Equal(0, run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        var heap = run.Execution.Heap.Heap;
        var spawn = Assert.Single(heap.Spawns);
        Assert.Equal(IrSpawnKind.TaskRun, spawn.Kind);
        Assert.Equal("body:Fixture:M:JobsController.Post#lambda1", heap.Instances[Assert.Single(spawn.Callees).InstanceId].BodyId);
        Assert.DoesNotContain(heap.Edges, edge => edge.CalleeInstance == spawn.Callees[0].InstanceId);
        Assert.DoesNotContain("body:Fixture:M:JobsController.Post#lambda1", run.Collection.Coverage.LoweredNotReached);
    }

    [Fact]
    public void Element_store_gives_no_access_but_its_object_escapes()
    {
        var run = Analyze("""
            public sealed class Box { public object? Value; }
            public sealed class Board { public readonly object?[] Slots = new object?[4]; }
            public class BoardController(Board board) : ControllerBase { public void Post() { var box = new Box(); board.Slots[0] = box; box.Value = new object(); } }
            """ + Startup("services.AddSingleton<Board>();"));

        Assert.Empty(run.Accesses("[]"));
        Assert.Single(run.Accesses("Value"));
        var box = run.Execution.Heap.Region("alloc:BoardController.Post()#Box");
        Assert.Equal(OwnershipKind.Escaped, run.Execution.Analysis.Ownership[box.Identity].Kind);
        Assert.Equal(1, run.Counter(CoverageCounters.ELEMENT_OPERATION));
    }

    [Fact]
    public void Delegate_given_to_a_spawn_and_to_another_opaque_call_counts_for_the_other_call()
    {
        const string post = "body:Fixture:M:WorkController.Post";
        var run = Analyze("""
            public sealed class Marks { public int Value; }
            public class WorkController(Marks marks) : ControllerBase
            {
                public void Post() { Func<int> work = () => marks.Value = 1; Task.Run(work); GC.KeepAlive(new Lazy<int>(work)); }
            }
            """ + Startup("services.AddSingleton<Marks>();"));

        // The work runs as the spawn's and, handed to Lazy as well, in the unknown call of that delegate (R3).
        Assert.Equal([ExecutionKind.Spawn, ExecutionKind.UnknownDelegateCall],
                     run.Accesses("Value").Select(access => run.Execution.Analysis.Execution(access.ExecutionId).Kind).Order());
        Assert.Equal(1, run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        var reached = run.Execution.Heap.Program.Result.OpaqueCalls[post];
        Assert.Single(Assert.Single(reached, call => call.Callee.StartsWith("System.Lazy", StringComparison.Ordinal)).DelegateValues);
        Assert.Empty(Assert.Single(reached, call => call.Callee.StartsWith("System.Threading.Tasks.Task.Run", StringComparison.Ordinal)).DelegateValues);
        var summary = run.Execution.Heap.Heap.Instances.Values.First(instance => instance.BodyId == post).Summary;
        Assert.Single(Assert.Single(summary.OpaqueCalls, call => call.Callee.StartsWith("System.Lazy", StringComparison.Ordinal)).Delegates);
        Assert.Empty(Assert.Single(summary.OpaqueCalls, call => call.Callee.StartsWith("System.Threading.Tasks.Task.Run", StringComparison.Ordinal)).Delegates);
    }

    [Fact]
    public void Coverage_counters_add_up_on_a_mixed_fixture_with_a_spawn()
    {
        var run = Analyze("""
            public sealed class Rates { }
            public sealed class Payload { public void Touch() => GC.KeepAlive(this); }
            public class MixedController(Rates rates) : ControllerBase
            {
                public void Post(Payload payload)
                {
                    var slots = new object?[1];
                    slots[0] = rates;
                    Task.Run(() => { });
                    GC.KeepAlive(System.Linq.Enumerable.Aggregate(slots, 0, (total, slot) => total, total => total));
                    GC.KeepAlive(slots);
                    payload.Touch();
                }
            }
            """ + Startup("services.AddSingleton<Rates>(_ => new Rates());"));

        var coverage = run.Collection.Coverage;
        Assert.Equal(run.Execution.Heap.Heap.ReachableBodies.Count, run.Counter(CoverageCounters.REACHABLE_BODIES));
        Assert.Equal(1, run.Counter(CoverageCounters.ELEMENT_OPERATION));
        // The two delegates Aggregate takes: the counter counts the delegates, not the calls that take them, and Task.Run's work runs.
        Assert.Equal(2, run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.Equal(0, run.Counter(CoverageCounters.UNANALYSED_REGISTRATION));
        Assert.Equal(1, run.Counter(CoverageCounters.NO_RECEIVER_OBJECT));
        Assert.Equal(1, run.Counter(OrderingCounters.SPAWN_SITES));
        Assert.Equal(0, run.Counter(OrderingCounters.UNPROVEN_JOINS));
        Assert.Equal(0, run.Counter(CoverageCounters.MERGED_CONTEXT));
        Assert.Equal(run.Counter(CoverageCounters.OPAQUE_CALL), coverage.TopOpaqueCallees.Sum(callee => callee.Count));
        // Spawn work is reached, and since phase 5b a lambda handed to an opaque call is too: it runs in an unknown execution (R3).
        Assert.DoesNotContain("body:Fixture:M:MixedController.Post(Payload)#lambda1", coverage.LoweredNotReached);
        Assert.DoesNotContain("body:Fixture:M:MixedController.Post(Payload)#lambda2", coverage.LoweredNotReached);
        // Phase 5b puts GC.KeepAlive(Object) into the library table without effect (TD-034a): its three calls are known, not opaque.
        Assert.DoesNotContain(coverage.TopOpaqueCallees, callee => callee.Callee == "System.GC.KeepAlive(object)");
    }

    [Fact]
    public void Recursive_method_reached_by_two_roots_gives_one_access_per_execution()
    {
        var run = Analyze("""
            public static class Counters { public static int Depth; }
            public class WalkController : ControllerBase
            {
                public void Get() => Walk(3);
                public void Post() => Walk(3);
                private static void Walk(int depth) { Counters.Depth = depth; if (depth > 0) Walk(depth - 1); }
            }
            """ + Startup());

        var accesses = run.Accesses("Depth");
        Assert.Equal(2, accesses.Count);
        Assert.Equal(2, accesses.Select(access => access.ExecutionId).Distinct().Count());
    }

    [Fact]
    public void Lock_taken_in_a_caller_body_after_its_entry_protects_the_callee()
    {
        var run = Analyze("""
            public sealed class Ledger
            {
                private readonly object _gate = new();
                private object? _last;
                public void Outer() => Middle();
                private void Middle() { lock (_gate) { Inner(); } }
                private void Inner() => _last = new object();
            }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post() => ledger.Outer(); }
            """ + Startup("services.AddSingleton<Ledger>();"));

        var store = Assert.Single(run.Accesses("_last"));
        Assert.Single(store.HeldProtectionIds);
        Assert.Contains(store.CodeFlow, step => step.Kind == "acquire");
    }

    [Fact]
    public void Controller_constructor_static_write_is_collected_under_each_action_with_a_construction_step()
    {
        var run = Analyze("""
            public static class Counters { public static object? Created; }
            public class CounterController : ControllerBase { public CounterController() => Counters.Created = new object(); public void Get() { } public void Post() { } }
            """ + Startup());

        var accesses = run.Accesses("Created");
        Assert.Equal(2, accesses.Select(access => access.ExecutionId).Distinct().Count());
        Assert.All(accesses, access => Assert.Contains(access.CodeFlow, step => step.Kind == "construction"));
        Assert.All(accesses, access => Assert.NotNull(access.Root));
    }

    [Fact]
    public void Transient_constructed_for_a_lazy_singleton_belongs_to_the_singleton_construction()
    {
        var run = Analyze("""
            public static class Log { public static object? Last; }
            public sealed class Formatter { public Formatter() => Log.Last = new object(); }
            public sealed class Catalog { public readonly Formatter Formatter; public Catalog(Formatter formatter) => Formatter = formatter; }
            public class CatalogController(Catalog catalog) : ControllerBase { public void Get() => GC.KeepAlive(catalog); }
            """ + Startup("services.AddSingleton<Catalog>(); services.AddTransient<Formatter>();"));

        var access = Assert.Single(run.Accesses("Last"));
        Assert.Equal("construction of di:Catalog@Singleton", run.Execution.Analysis.Execution(access.ExecutionId).Display);
        Assert.Equal((access.ExecutionId, "construction"), (access.Root.RootId, access.Root.RootKind));
    }

    [Fact]
    public void Instance_reached_by_two_paths_shows_the_same_representative_code_flow_on_every_run()
    {
        const string SOURCE = """
            public sealed class Store { private object? _value; public void Write() => _value = new object(); }
            public sealed class Service(Store store) { public void A() => store.Write(); public void B() => store.Write(); public void Both() { B(); A(); } }
            public class StoreController(Service service) : ControllerBase { public void Post() => service.Both(); }
            """;
        var first = Assert.Single(Analyze(SOURCE + Startup("services.AddSingleton<Store>(); services.AddSingleton<Service>();")).Accesses("_value"));
        var second = Assert.Single(Analyze(SOURCE + Startup("services.AddSingleton<Store>(); services.AddSingleton<Service>();")).Accesses("_value"));

        Assert.Equal(first.CodeFlow, second.CodeFlow);
        Assert.Contains(first.CodeFlow, step => step.Text == "calls Service.B() on di:Service@Singleton");
    }

    [Fact]
    public void Read_through_a_getter_carries_a_read_source_with_the_getter_call()
    {
        var run = Analyze(ThroughMethods);

        var source = Assert.Single(Assert.Single(run.Accesses("_level")).ReadSources);
        Assert.Equal("Tallies.GetLevel()", source.Symbol);
        Assert.Contains(source.CodeFlow, step => step.Text.StartsWith("calls Tallies.GetLevel()", StringComparison.Ordinal));
    }

    [Fact]
    public void Lock_released_by_an_intermediate_method_before_a_helper_does_not_protect_the_helper()
    {
        var run = Analyze("""
            public sealed class Ledger
            {
                private readonly object _gate = new();
                private object? _last;
                public void Outer() { System.Threading.Monitor.Enter(_gate); Middle(); System.Threading.Monitor.Exit(_gate); }
                private void Middle() { System.Threading.Monitor.Exit(_gate); Helper(); System.Threading.Monitor.Enter(_gate); }
                private void Helper() => _last = new object();
            }
            public class LedgerController(Ledger ledger) : ControllerBase { public void Post() => ledger.Outer(); }
            """ + Startup("services.AddSingleton<Ledger>();"));

        Assert.Empty(Assert.Single(run.Accesses("_last")).HeldProtection);
        Assert.Equal(PairProtection.UNPROTECTED, Assert.Single(run.PairsOn("_last")).Protection);
    }

    [Fact]
    public void Wildcard_accesses_from_members_of_two_projects_pair_on_one_resource()
    {
        var solution = FixtureSolution.CreateProjects(
            new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
                ProjectReferences = [("App", "Lib")]
            },
            ("Lib", "Chain.cs", DeepChain(string.Empty)),
            ("App", "Program.cs", Usings + """
                System.Console.WriteLine();
                public class DeepController : ControllerBase
                {
                    public void Put([FromServices] DeepChain chain, string value) => chain.First.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value = value;
                    public string? Get([FromServices] DeepChain chain) => chain.Read();
                }
                """ + Startup("services.AddSingleton<DeepChain>();")));

        var run = AnalyzeScope(solution, "scope:App");

        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.IsWildcard &&
                                                 new[] { pair.First.Symbol, pair.Second.Symbol }.Order(StringComparer.Ordinal)
                                                     .SequenceEqual(["DeepChain.Read()", "DeepController.Put(DeepChain, string)"]));
    }

    [Fact]
    public void Opaque_call_in_an_override_the_heap_does_not_reach_is_not_counted()
    {
        var run = Analyze("""
            public abstract class Shape { public abstract void Draw(); }
            public sealed class Circle : Shape { public override void Draw() { } }
            public sealed class Square : Shape { public override void Draw() => GC.Collect(); }
            public class ShapeController : ControllerBase { public void Post() { Shape shape = new Circle(); shape.Draw(); } }
            """ + Startup());

        Assert.DoesNotContain(run.Collection.Coverage.TopOpaqueCallees, callee => callee.Callee == "System.GC.Collect()");
        Assert.Contains("body:Fixture:M:Square.Draw", run.Collection.Coverage.LoweredNotReached);
    }

    [Fact]
    public void Lambda_writing_a_captured_read_is_one_read_modify_write_with_its_read_in_the_creating_method()
    {
        var run = Analyze("""
            public sealed class Counter
            {
                private int _count;
                public void Bump() { var seen = _count; Action apply = () => _count = seen + 1; apply(); }
            }
            public class CounterController(Counter counter) : ControllerBase { public void Post() => counter.Bump(); }
            """ + Startup("services.AddSingleton<Counter>();"));

        var access = Assert.Single(run.Accesses("_count"));
        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);
        var source = Assert.Single(access.ReadSources);
        Assert.Equal("Counter.Bump()", source.Symbol);
        Assert.True(source.Source.StartLine < access.Source.StartLine || source.Source.StartColumn < access.Source.StartColumn);
    }

    [Fact]
    public void Root_scope_service_resolved_for_two_lazy_singletons_writes_once_in_its_construction()
    {
        var run = Analyze("""
            public static class Log { public static object? Last; }
            public sealed class Session { public Session() => Log.Last = new object(); }
            public sealed class First { public readonly Session Session; public First(Session session) => Session = session; }
            public sealed class Second { public readonly Session Session; public Second(Session session) => Session = session; }
            public class SessionController(First first, Second second) : ControllerBase { public void Post() { GC.KeepAlive(first); GC.KeepAlive(second); } }
            """ + Startup("services.AddSingleton<First>(); services.AddSingleton<Second>(); services.AddScoped<Session>();"));

        var access = Assert.Single(run.Accesses("Last"));
        Assert.Equal("construction of di:Session@Scoped", run.Execution.Analysis.Execution(access.ExecutionId).Display);
    }

    [Fact]
    public void Recursive_bump_depending_on_its_own_returned_load_is_one_read_modify_write()
    {
        var run = Analyze("""
            public sealed class Counter
            {
                private int _value;
                public int Bump(int depth) { if (depth == 0) return _value; var previous = Bump(depth - 1); _value = previous + 1; return previous; }
            }
            public class CounterController(Counter counter) : ControllerBase { public void Post() => counter.Bump(3); }
            """ + Startup("services.AddSingleton<Counter>();"));

        Assert.Equal([AccessOperation.ReadModifyWrite], run.Accesses("_value").Select(access => access.Operation));
    }

    [Fact]
    public void Merged_generic_write_pairs_with_a_closed_read_on_the_closed_region()
    {
        var run = Analyze("""
            public sealed class Order { }
            public sealed class Invoice { }
            public static class Cache<T> { public static object? Last; }
            public static class Access
            {
                public static void Store<T>(object value) => Cache<T>.Last = value;
                public static object? Load<T>() => Cache<T>.Last;
            }
            public class CacheController : ControllerBase
            {
                public void Post() { Access.Store<Order>(new object()); Access.Store<Invoice>(new object()); GC.KeepAlive(Access.Load<Order>()); }
            }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 1));

        var heap = run.Execution.Heap.Heap;
        var pair = Assert.Single(run.Pairs.Pairs, candidate => new[] { candidate.First, candidate.Second }.Any(access => heap.Regions[access.Resource.RegionId!].IsOpen) &&
                                                                 new[] { candidate.First, candidate.Second }.Any(access => access.Operation == AccessOperation.Read));
        Assert.Equal("static:Cache<Order>", pair.Resource.Region);
        Assert.NotEmpty(pair.Uncertainties);
    }

    private static string ThroughMethods => """
        public sealed class Tallies
        {
            private int _level;
            public void RaiseLevel() => SetLevel(GetLevel() + 1);
            private int GetLevel() => _level;
            private void SetLevel(int level) => _level = level;
        }
        public class TalliesController(Tallies tallies) : ControllerBase { public void Post() => tallies.RaiseLevel(); }
        """ + Startup("services.AddSingleton<Tallies>();");

    private static string DeepChain(string extra) =>
        string.Concat(Enumerable.Range(1, 11).Select(level => $"public sealed class Level{level} {{ public Level{level + 1} Next {{ get; }} = new(); }}\n")) + """
            public sealed class Level12 { public string? Value { get; set; } }
            public sealed class DeepChain
            {
                public Level1 First { get; } = new();
                public string? Label { get; set; }
                public void Write(string value) => First.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value = value;
                public string? Read() => First.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value;
            }
            """ + extra;
}
