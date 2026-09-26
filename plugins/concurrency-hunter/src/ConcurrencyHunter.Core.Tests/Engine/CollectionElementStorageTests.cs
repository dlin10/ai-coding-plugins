using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The heap holds what a collection of ADR 0010 holds as it holds an array's cells (question 30, closed in the second run
/// of phase 5b): what an insertion or a replacement puts in is what every member handing a value out yields, and every rule that
/// follows an array's cells follows a collection's storage (R2, R5, R6).</summary>
public sealed class CollectionElementStorageTests
{
    private const string FIRST = "alloc:State..ctor()#Item";
    private const string SPARE = "alloc:State..ctor()#Spare";
    private const string LOOSE = "alloc:State..ctor()#Loose";

    // ---- what comes out is what went in ----

    [Fact]
    public void List_indexer_yields_the_inserted_object()
    {
        var run = Run("_state.Items[0].Hits = 1;", "_ = _state.First.Hits;");

        Assert.Single(Writes(run, FIRST));
        Assert.Contains(run.PairsOn("Hits"), pair => pair.Resource.Region == FIRST);
    }

    [Fact]
    public void Foreach_over_a_list_yields_the_inserted_object()
    {
        var run = Run("foreach (var item in _state.Items) item.Hits = 1;", "_ = _state.First.Hits;");

        Assert.Single(Writes(run, FIRST));
        Assert.Contains(run.PairsOn("Hits"), pair => pair.Resource.Region == FIRST);
    }

    [Fact]
    public void Dictionary_indexer_and_TryGetValue_yield_the_value()
    {
        var run = Run("_state.Map[\"a\"].Hits = 1; if (_state.Map.TryGetValue(\"a\", out var found)) found.Hits = 2;");

        Assert.Equal(2, Sites(Writes(run, FIRST)));
    }

    [Fact]
    public void Queue_hands_out_what_it_holds()
    {
        var run = Run("_state.Queue.Peek().Hits = 1; _state.Queue.Dequeue().Hits = 2; " +
                      "if (_state.Queue.TryPeek(out var peeked)) peeked.Hits = 3; if (_state.Queue.TryDequeue(out var taken)) taken.Hits = 4;");

        Assert.Equal(4, Sites(Writes(run, FIRST)));
    }

    [Fact]
    public void Stack_hands_out_what_it_holds()
    {
        var run = Run("_state.Stack.Peek().Hits = 1; _state.Stack.Pop().Hits = 2; " +
                      "if (_state.Stack.TryPeek(out var peeked)) peeked.Hits = 3; if (_state.Stack.TryPop(out var taken)) taken.Hits = 4;");

        Assert.Equal(4, Sites(Writes(run, FIRST)));
    }

    [Fact]
    public void Concurrent_collections_hand_out_what_they_hold()
    {
        var run = Run("if (_state.ConcurrentQueue.TryDequeue(out var a)) a.Hits = 1; if (_state.ConcurrentQueue.TryPeek(out var b)) b.Hits = 2; " +
                      "if (_state.ConcurrentStack.TryPop(out var c)) c.Hits = 3; if (_state.ConcurrentStack.TryPeek(out var d)) d.Hits = 4; " +
                      "if (_state.Bag.TryTake(out var e)) e.Hits = 5; if (_state.Bag.TryPeek(out var f)) f.Hits = 6; " +
                      "_state.Concurrent[\"a\"].Hits = 7; if (_state.Concurrent.TryGetValue(\"a\", out var g)) g.Hits = 8; " +
                      "_state.Concurrent.GetOrAdd(\"b\", new Item()).Hits = 9; _state.Concurrent.AddOrUpdate(\"c\", new Item(), (_, old) => old).Hits = 10;");

        Assert.Equal(10, Sites(Writes(run, FIRST)));
    }

    [Fact]
    public void Removal_hands_out_the_removed_value()
    {
        var run = Run("if (_state.Map.Remove(\"a\", out var removed)) removed.Hits = 1; " +
                      "if (_state.Concurrent.TryRemove(\"a\", out var gone)) gone.Hits = 2;");

        Assert.Equal(2, Sites(Writes(run, FIRST)));
    }

    [Fact]
    public void Linked_list_nodes_yield_their_values()
    {
        var run = Run("_state.Linked.First!.Value.Hits = 1; _state.Linked.Last!.Value.Hits = 2; " +
                      "_state.Linked.Find(_state.First)!.Value.Hits = 3; foreach (var item in _state.Linked) item.Hits = 4;");

        Assert.Equal(4, Sites(Writes(run, FIRST)));
    }

    [Fact]
    public void Value_of_a_node_created_on_its_own_reaches_the_list_it_is_added_to()
    {
        var created = Run("var node = new LinkedListNode<Item>(_state.Spare); _state.Linked.AddFirst(node); _state.Linked.First!.Value.Hits = 1;");
        var given = Run("var node = new LinkedListNode<Item>(new Item()); node.Value = _state.Spare; _state.Linked.AddLast(node); " +
                        "_state.Linked.Last!.Value.Hits = 1;");

        foreach (var run in new[] { created, given })
        {
            Assert.Single(Writes(run, SPARE));
            var heap = run.Execution.Heap.Heap;
            var lists = heap.PointsTo(run.Execution.Heap.Region("di:State@Singleton").Identity, "Fixture:State.Linked");
            var held = Assert.Single(lists, list => heap.PointsTo(list, PathValue.ELEMENT).Contains(run.Execution.Heap.Region(SPARE).Identity));
            // The list holds what the node holds, and never the node itself.
            Assert.DoesNotContain(heap.PointsTo(held, PathValue.ELEMENT),
                                  region => heap.Regions[region].TypeKey?.Contains("LinkedListNode", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void Value_given_to_an_added_node_is_what_the_list_and_the_node_both_hold()
    {
        // Once added, the node and its list's cell are one storage: a value given through the node is what the list hands out, and
        // one given through the list's node is what the node hands out.
        var throughNode = Run("var node = new LinkedListNode<Item>(new Item()); _state.Linked.AddFirst(node); node.Value = _state.Spare; " +
                              "foreach (var item in _state.Linked) item.Hits = 1;");
        var throughList = Run("var node = new LinkedListNode<Item>(new Item()); _state.Linked.AddFirst(node); _state.Linked.First!.Value = _state.Spare; " +
                              "node.Value.Hits = 1;");

        Assert.Single(Writes(throughNode, SPARE));
        Assert.Single(Writes(throughList, SPARE));
    }

    [Fact]
    public void Node_added_by_a_helper_is_a_cell_of_the_list_it_was_added_to()
    {
        // The node is the worker's own, but a helper adds it to the singleton's list: what the heap knows of it, and not the body that
        // uses it, makes its value that list's cell and its neighbours that list's structure.
        const string work = "var node = new LinkedListNode<Item>(new Item()); _state.Attach(node);\n        _ = node.Value;\n" +
                            "        node.Value = _state.Spare;\n        _ = node.Next;";
        var run = Run(work, "foreach (var item in _state.Linked) { }");

        var list = Assert.Single(run.Execution.Heap.Heap.PointsTo(run.Execution.Heap.Region("di:State@Singleton").Identity, "Fixture:State.Linked"));
        IReadOnlyList<Access> At(string text) =>
            run.Collection.Accesses.Where(access => access.Resource.CollectionId == list && access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) &&
                                                    access.Source.StartLine == Line(Source(work, ""), text))
               .ToArray();

        Assert.Equal([(AccessOperation.Read, true)], At("_ = node.Value;").Select(access => (access.Operation, access.Resource.Selector is not null)).Distinct());
        var write = Assert.Single(At("node.Value = _state.Spare;"));
        Assert.Equal((AccessOperation.Write, ElementSelector.Unknown), (write.Operation, write.Resource.Selector));
        Assert.Equal([(AccessOperation.Read, false)], At("_ = node.Next;").Select(access => (access.Operation, access.Resource.Selector is not null)).Distinct());
        Assert.Contains(run.Pairs.Pairs, pair => ReferenceEquals(pair.First, write) || ReferenceEquals(pair.Second, write));
    }

    [Fact]
    public void Replacement_puts_the_new_object_in()
    {
        foreach (var work in new[]
                 {
                     "_state.Items[0] = _state.Spare; _state.Items[0].Hits = 1;",
                     "_state.Concurrent.TryUpdate(\"a\", _state.Spare, _state.First); _state.Concurrent[\"a\"].Hits = 1;",
                     "_state.Linked.First!.Value = _state.Spare; _state.Linked.First!.Value.Hits = 1;"
                 })
            Assert.Single(Writes(Run(work), SPARE));
    }

    // ---- escape, ownership and publication ----

    [Fact]
    public void Element_of_a_shared_list_is_escaped_through_the_insertion_site()
    {
        var run = Run("_state.Items[1].Hits = 1;");

        var ownership = run.Execution.Ownership(LOOSE);
        Assert.Equal(OwnershipKind.Escaped, ownership.Kind);
        Assert.Contains(ownership.Evidence, hop => hop.Contains("is stored into [] of", StringComparison.Ordinal) &&
                                                   hop.EndsWith($"at Case.cs:{Line(Source("", ""), "Items.Add(new Loose());")}.", StringComparison.Ordinal));
    }

    [Fact]
    public void Element_of_a_list_of_one_execution_stays_owned()
    {
        var run = Run("var local = new List<Item>(); local.Add(new Item()); local[0].Hits = 1;", "_state.Items[0].Hits = 2;");

        var write = Assert.Single(run.Of("Hits"), access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal));
        Assert.StartsWith("alloc:Worker.ExecuteAsync(", write.Resource.Region, StringComparison.Ordinal);
        Assert.Equal(OwnershipKind.ThreadConfined, write.Ownership);
        Assert.DoesNotContain(run.PairsOn("Hits"), pair => pair.Resource.Region == write.Resource.Region);
    }

    [Fact]
    public void Object_adding_itself_to_a_shared_list_in_its_constructor_is_published()
    {
        var run = Analyze("""
            using System.Collections.Generic;

            public sealed class Registry { public readonly List<Member> Members = new(); }
            public sealed class Member
            {
                public int Hits;
                public Member(Registry registry) { registry.Members.Add(this); Hits = 1; }
            }

            public sealed class Joiner(Registry registry) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { _ = new Member(registry); return Task.CompletedTask; }
            }

            public sealed class Counter(Registry registry) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    foreach (var member in registry.Members) _ = member.Hits;
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Registry>(); services.AddHostedService<Joiner>(); services.AddHostedService<Counter>();"));

        var write = Assert.Single(run.Accesses("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.False(write.IsConstructionLocal);
        Assert.Contains(run.PairsOn("Hits"), pair => ReferenceEquals(pair.First, write) || ReferenceEquals(pair.Second, write));
    }

    [Fact]
    public void Unknown_effect_on_an_element_of_a_shared_list_writes()
    {
        var run = Run("Opaque.Lib.Touch(_state.Items[1]);");

        Assert.Contains(run.Of("Hits"), access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Resource.Region == LOOSE &&
                                                  access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void List_handed_to_an_unresolved_call_hands_over_its_delegates()
    {
        var run = Run("var actions = new List<Action>(); actions.Add(() => _state.First.Hits = 1); Opaque.Lib.Touch(actions);");

        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write &&
                                                  run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.UnknownDelegateCall);
    }

    // ---- ordering and gaps ----

    [Fact]
    public void Handle_stored_in_a_list_is_no_quiet_event_proof()
    {
        const string timer = "var timer = new Timer(_ => state.F(), null, 0, 1000); var done = new ManualResetEvent(false); ";
        const string wait = "timer.Dispose(done); done.WaitOne(); state.P1();";

        // The proof holds for a handle nothing else touches, and fails once the handle is stored anywhere: an array cell, or a list.
        Assert.False(Overlap(Analyze(TimerWorker(timer + wait)), "P1", "F"));
        Assert.True(Overlap(Analyze(TimerWorker(timer + "var cells = new WaitHandle[1]; cells[0] = done; " + wait)), "P1", "F"));
        Assert.True(Overlap(Analyze(TimerWorker(timer + "var handles = new List<WaitHandle>(); handles.Add(done); " + wait)), "P1", "F"));
    }

    [Fact]
    public async Task Gap_result_stored_in_a_list_and_read_back_is_followed()
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution("""
            using System.Collections.Generic;

            public sealed class Work { public int Count; public readonly List<object> Gates = new(); }
            public sealed class Locker(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    work.Gates.Add(Opaque.Lib.Gate(work)); lock (work.Gates[0]) { work.Count = 1; }
                    return Task.CompletedTask;
                }
            }
            public sealed class Writer(Work work) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { work.Count = 2; return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<Work>(); services.AddHostedService<Locker>(); services.AddHostedService<Writer>();")),
                                                         ROOT_DIRECTORY, CancellationToken.None);

        // The lock is on what the list holds, which the gap's result went into, so the gap decides the protection as it does for
        // a lock on an array cell it went into.
        var finding = Assert.Single(result.Findings, finding => string.Join(".", finding.Resource.AccessPath) == "Count" &&
                                                                !finding.AccessA.Operation.IsUnknownEffect() && !finding.AccessB.Operation.IsUnknownEffect());
        Assert.Equal(10, finding.Confidence.Components.Protection);
    }

    [Fact]
    public void Task_read_back_from_a_list_is_no_proven_join_handle()
    {
        // A handle is proven where it is read straight back; read out of an array cell or a list, it may be any task put there.
        Assert.False(Overlap(Analyze(TimerWorker("var task = Task.Run(state.F); await task; state.P1();")), "P1", "F"));
        Assert.True(Overlap(Analyze(TimerWorker("var tasks = new Task[1]; tasks[0] = Task.Run(state.F); await tasks[0]; state.P1();")), "P1", "F"));
        Assert.True(Overlap(Analyze(TimerWorker("var tasks = new List<Task>(); tasks.Add(Task.Run(state.F)); await tasks[0]; state.P1();")),
                            "P1", "F"));
    }

    // ---- what stays as it was ----

    [Fact]
    public void Element_operation_counter_counts_array_operations_only()
    {
        var collections = Run("_state.Items.Add(_state.First); _ = _state.Items[0]; _state.Items[0] = _state.Spare; " +
                              "foreach (var item in _state.Items) { }");
        var arrays = Run("var cells = new Item[1]; cells[0] = _state.First; _ = cells[0];");

        Assert.Equal(0, collections.Counter(CoverageCounters.ELEMENT_OPERATION));
        Assert.Equal(2, arrays.Counter(CoverageCounters.ELEMENT_OPERATION));
    }

    [Fact]
    public void Member_result_stays_its_read_for_a_check_then_act()
    {
        var run = Run("var current = _state.Map[\"a\"]; _state.Map[\"a\"] = current;");

        // The value the indexer handed out is the First the map holds, and it is still the indexer's read that decides the store.
        Assert.Contains(run.Collection.Accesses, access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) &&
                                                           access.Resource.AccessPath[0] == "Map" &&
                                                           access.Operation == AccessOperation.CompoundOperation);
    }

    // ---- helpers ----

    private static IReadOnlyList<Access> Writes(EngineRun run, string region) =>
        run.Of("Hits").Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Resource.Region == region &&
                                       access.Operation == AccessOperation.Write)
           .ToArray();

    /// <summary>How many distinct places in the source the accesses stand at.</summary>
    private static int Sites(IEnumerable<Access> accesses) =>
        accesses.Select(access => (access.Source.StartLine, access.Source.StartColumn)).Distinct().Count();

    private static int Line(string source, string text) =>
        Array.FindIndex((Usings + source).Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1;

    private static bool Overlap(EngineRun run, string one, string other)
    {
        Assert.Contains(run.Accesses("Value"), access => Is(access, one));
        Assert.Contains(run.Accesses("Value"), access => Is(access, other));
        return run.PairsOn("Value").Any(pair => Is(pair.First, one) && Is(pair.Second, other) || Is(pair.First, other) && Is(pair.Second, one));
    }

    private static bool Is(Access access, string helper) => access.Symbol.EndsWith($".{helper}()", StringComparison.Ordinal);

    private static EngineRun Run(string work, string other = "") => Analyze(Source(work, other));

    private static EngineRun Analyze(string source) => AnalyzeScope(Solution(source), "scope:Fixture");

    private static Solution Solution(string source) =>
        FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", Usings + source));

    /// <summary>A singleton <c>State</c> holding one of each collection of the table, each with <c>First</c> put in at construction and
    /// the list with a <c>Loose</c> object nothing else holds, which one worker does <paramref name="work"/> on while another does
    /// <paramref name="other"/>.</summary>
    private static string Source(string work, string other) => $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public class Item { public int Hits; }
        public sealed class Spare : Item { }
        public sealed class Loose : Item { }

        public sealed class State
        {
            public readonly Item First = new Item();
            public readonly Spare Spare = new Spare();
            public readonly List<Item> Items = new();
            public readonly Dictionary<string, Item> Map = new();
            public readonly ConcurrentDictionary<string, Item> Concurrent = new();
            public readonly Queue<Item> Queue = new();
            public readonly Stack<Item> Stack = new();
            public readonly ConcurrentQueue<Item> ConcurrentQueue = new();
            public readonly ConcurrentStack<Item> ConcurrentStack = new();
            public readonly ConcurrentBag<Item> Bag = new();
            public readonly LinkedList<Item> Linked = new();

            public State()
            {
                Items.Add(First);
                Items.Add(new Loose());
                Map.Add("a", First);
                Concurrent.TryAdd("a", First);
                Queue.Enqueue(First);
                Stack.Push(First);
                ConcurrentQueue.Enqueue(First);
                ConcurrentStack.Push(First);
                Bag.Add(First);
                Linked.AddLast(First);
            }

            public void Attach(LinkedListNode<Item> node) => Linked.AddLast(node);
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");

    /// <summary>A worker over a singleton whose helpers each write <c>Value</c>, as the ordering tests of timers and joins name them.</summary>
    private static string TimerWorker(string body) => $$"""
        using System.Collections.Generic;

        public sealed class State
        {
            public int Value;
            public void P1() => Value = 1;
            public void F() => Value = 3;
        }

        public sealed class Worker(State state) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Touch(object value) { }
                public static object Gate(object owner) => new();
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
