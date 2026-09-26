using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The four collections phase 5b adds to the table of ADR 0010 (R7): <c>HashSet&lt;T&gt;</c>, <c>Queue&lt;T&gt;</c>,
/// <c>Stack&lt;T&gt;</c> and <c>LinkedList&lt;T&gt;</c> with its nodes, each modelled by its members with a structure and cells, none
/// of them atomically, and holding what it was given.</summary>
public sealed class CollectionTableExtensionTests
{
    // ---- insertions against insertions ----

    [Fact]
    public void HashSet_insertions_from_two_roots_conflict_on_the_structure() =>
        AssertStructureWrites(Run("_state.Set.Add(new Item());", "_state.Set.Add(new Item());"), "Set");

    [Fact]
    public void Queue_insertions_from_two_roots_conflict_on_the_structure() =>
        AssertStructureWrites(Run("_state.Queue.Enqueue(new Item());", "_state.Queue.Enqueue(new Item());"), "Queue");

    [Fact]
    public void Stack_insertions_from_two_roots_conflict_on_the_structure() =>
        AssertStructureWrites(Run("_state.Stack.Push(new Item());", "_state.Stack.Push(new Item());"), "Stack");

    [Fact]
    public void LinkedList_insertions_from_two_roots_conflict_on_the_structure() =>
        AssertStructureWrites(Run("_state.List.AddLast(new Item());", "_state.List.AddFirst(new Item());"), "List");

    // ---- reads against an insertion ----

    [Theory]
    [InlineData("Set", "_state.Set.Add(new Item());")]
    [InlineData("Queue", "_state.Queue.Enqueue(new Item());")]
    [InlineData("Stack", "_state.Stack.Push(new Item());")]
    [InlineData("List", "_state.List.AddLast(new Item());")]
    public void Count_against_an_insertion_pairs_on_the_structure_and_reads_no_cell(string field, string insert)
    {
        var run = Run($"_ = _state.{field}.Count;", insert);

        Assert.Contains(StructurePairs(run, field), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
        Assert.DoesNotContain(Cells(run, field), access => access.Operation == AccessOperation.Read);
    }

    [Theory]
    [InlineData("Set", "_state.Set.Add(new Item());")]
    [InlineData("Queue", "_state.Queue.Enqueue(new Item());")]
    [InlineData("Stack", "_state.Stack.Push(new Item());")]
    [InlineData("List", "_state.List.AddLast(new Item());")]
    public void Contains_against_an_insertion_pairs_on_the_structure_and_the_cells(string field, string insert)
    {
        var run = Run($"_ = _state.{field}.Contains(_state.First);", insert);

        Assert.Contains(StructurePairs(run, field), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
        Assert.Contains(CellPairs(run, field), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    [Theory]
    [InlineData("Set", "_state.Set.Add(new Item());")]
    [InlineData("Queue", "_state.Queue.Enqueue(new Item());")]
    [InlineData("Stack", "_state.Stack.Push(new Item());")]
    [InlineData("List", "_state.List.AddLast(new Item());")]
    public void Enumeration_against_an_insertion_pairs_on_the_structure_and_the_cells(string field, string insert)
    {
        var run = Run($"foreach (var item in _state.{field}) {{ }}", insert);

        Assert.Contains(StructurePairs(run, field), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
        Assert.Contains(CellPairs(run, field), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    // ---- removals, peeks and clearing ----

    [Theory]
    [InlineData("Queue", "_ = _state.Queue.Dequeue();")]
    [InlineData("Stack", "_ = _state.Stack.Pop();")]
    [InlineData("List", "_state.List.RemoveFirst();")]
    public void Removal_writes_the_structure_and_a_cell(string field, string remove)
    {
        var run = Run(remove, "");

        Assert.Contains(Structure(run, field), access => access.Operation == AccessOperation.Write);
        Assert.Contains(Cells(run, field), access => access.Operation == AccessOperation.Write);
    }

    [Theory]
    [InlineData("Queue")]
    [InlineData("Stack")]
    public void Peek_reads_the_structure_and_a_cell(string field)
    {
        var run = Run($"_ = _state.{field}.Peek();", "");

        Assert.Equal([AccessOperation.Read], Structure(run, field).Select(access => access.Operation).Distinct());
        Assert.Equal([AccessOperation.Read], Cells(run, field).Select(access => access.Operation).Distinct());
    }

    [Theory]
    [InlineData("Set", "_ = _state.Set.Contains(_state.First);")]
    [InlineData("Queue", "_ = _state.Queue.Peek();")]
    [InlineData("Stack", "_ = _state.Stack.Peek();")]
    [InlineData("List", "_ = _state.List.First!.Value;")]
    public void Clear_against_a_read_of_a_cell_pairs_on_the_cells(string field, string read)
    {
        var run = Run($"_state.{field}.Clear();", read);

        Assert.Contains(CellPairs(run, field), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    // ---- what the collection holds ----

    [Theory]
    [InlineData("Set")]
    [InlineData("Queue")]
    [InlineData("Stack")]
    [InlineData("List")]
    public void Deep_read_of_the_collection_reaches_the_fields_of_an_inserted_object(string field)
    {
        var run = Run($"_ = System.Text.Json.JsonSerializer.Serialize(_state.{field});", "_state.First.Value = 2;");

        Assert.Contains(run.PairsOn("Value"), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    [Fact]
    public void Result_of_Dequeue_points_to_the_object_the_queue_holds()
    {
        var run = Run("var item = _state.Queue.Dequeue(); item.Value = 3;", "");

        // The queue holds First, put in at construction, so the write lands on it as a write through an array cell would.
        var write = Assert.Single(run.Of("Value"), access => access.Operation == AccessOperation.Write);
        Assert.Equal("alloc:State..ctor()#Item", write.Resource.Region);
    }

    // ---- the nodes of a linked list ----

    [Fact]
    public void Node_value_write_is_a_cell_write_without_the_structure()
    {
        var run = Run("_state.List.First!.Value = new Item();", "");

        Assert.Contains(Cells(run, "List"), access => access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(Structure(run, "List"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void Node_value_write_against_a_read_of_a_cell_pairs()
    {
        var run = Run("var node = _state.List.First!; node.Value = new Item();", "_ = _state.List.Last!.Value;");

        Assert.Contains(CellPairs(run, "List"), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    [Fact]
    public void Node_value_write_against_Count_does_not_pair()
    {
        var run = Run("_state.List.First!.Value = new Item();", "_ = _state.List.Count;");

        Assert.Empty(StructurePairs(run, "List"));
        Assert.Empty(CellPairs(run, "List"));
    }

    [Fact]
    public void Object_set_as_a_node_value_is_held_by_the_list()
    {
        // The node stands for its list, so the list holds what is set as its value and a deep read of the list reaches it.
        var run = Run("_state.List.First!.Value = _state.Spare; _ = System.Text.Json.JsonSerializer.Serialize(_state.List);", "_state.Spare.Value = 2;");

        Assert.Contains(run.PairsOn("Value"), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    [Fact]
    public void Value_of_a_node_created_and_added_in_the_body_is_a_cell_of_its_list()
    {
        var run = Run("var node = new LinkedListNode<Item>(_state.Spare); _state.List.AddLast(node); node.Value = _state.First;",
                      "_ = _state.List.Last!.Value;");

        // Two writes of a cell: the insertion, and the node's value set after it.
        Assert.Equal(2, Cells(run, "List").Where(access => access.Operation == AccessOperation.Write).Select(access => access.Source.StartColumn).Distinct().Count());
        Assert.Contains(CellPairs(run, "List"), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    [Fact]
    public void Value_a_created_node_holds_is_held_by_the_list_it_is_added_to()
    {
        var run = Run("var node = new LinkedListNode<Item>(_state.Spare); _state.List.AddLast(node); " +
                      "_ = System.Text.Json.JsonSerializer.Serialize(_state.List);", "_state.Spare.Value = 2;");

        Assert.Contains(run.PairsOn("Value"), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    [Fact]
    public void Value_of_a_node_a_field_holds_is_a_cell_of_the_list_it_was_added_to()
    {
        var run = Run("_state.List.AddLast(_state.Node); _state.Node.Value = _state.Spare;", "_ = _state.List.First!.Value;");

        // The node's own field names the write, the list the read: one collection all the same.
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.CollectionId is not null && pair.Resource.Selector is not null &&
                                                 new[] { pair.First, pair.Second }.Any(access => access.Resource.AccessPath[0] == "Node" &&
                                                                                                 access.Operation == AccessOperation.Write) &&
                                                 new[] { pair.First, pair.Second }.Any(access => access.Resource.AccessPath[0] == "List" &&
                                                                                                 access.Operation == AccessOperation.Read));
    }

    [Fact]
    public void Value_AddAfter_puts_next_to_a_node_is_held_by_the_list()
    {
        var run = Run("_state.List.AddAfter(_state.List.First!, _state.Spare); _ = System.Text.Json.JsonSerializer.Serialize(_state.List);",
                      "_state.Spare.Value = 2;");

        Assert.Contains(run.PairsOn("Value"), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.Write));
    }

    [Fact]
    public void AddAfter_changes_the_structure()
    {
        // The node it adds after is read from the same list, so the insertion is the change of a compound sequence (ADR 0010).
        var run = Run("_state.List.AddAfter(_state.List.First!, _state.First);", "_ = _state.List.Count;");

        Assert.Contains(Structure(run, "List"), access => access.Operation == AccessOperation.CompoundOperation);
        Assert.Contains(StructurePairs(run, "List"), pair => HasOperations(pair, AccessOperation.Read, AccessOperation.CompoundOperation));
    }

    // ---- no unresolved call ----

    [Fact]
    public void Members_of_the_four_collections_are_modelled_with_no_unknown_effect_and_no_gap()
    {
        var run = Run("_state.Set.Add(_state.First); _state.Queue.Enqueue(_state.First); _state.Stack.Push(_state.First); " +
                      "_state.List.AddLast(_state.First).Value = _state.First; _ = _state.Queue.Peek(); _state.Set.Clear(); " +
                      // Members the table leaves out are still the recognizer's (R1): no unknown effect on what the collections hold.
                      "_ = _state.List.First!.List; _state.Set.TrimExcess(); _state.Queue.TrimExcess();", "");

        var calls = run.Execution.Heap.Heap.Instances.Values.Where(instance => instance.BodyId.Contains("Worker.ExecuteAsync", StringComparison.Ordinal))
                       .SelectMany(instance => instance.Summary.OpaqueCalls)
                       .Where(call => call.Callee.StartsWith("System.Collections.Generic.", StringComparison.Ordinal))
                       .ToArray();
        Assert.NotEmpty(calls);
        // The table models the listed members; the rest are the recognizer's all the same.
        Assert.All(calls, call => Assert.True(call.Collection is not null || call.IsRecognized, call.Callee));
        Assert.Contains(calls, call => call.Collection is null);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.DoesNotContain(run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("System.Collections.Generic.", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static void AssertStructureWrites(EngineRun run, string field) =>
        Assert.Contains(StructurePairs(run, field), pair => HasOperations(pair, AccessOperation.Write, AccessOperation.Write));

    private static bool HasOperations(AccessPair pair, AccessOperation first, AccessOperation second) =>
        new[] { pair.First.Operation, pair.Second.Operation }.Order().SequenceEqual(new[] { first, second }.Order());

    /// <summary>The worker's accesses to the structure: the state's constructor puts an item into each collection too.</summary>
    private static Access[] Structure(EngineRun run, string field) =>
        run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) &&
                                                access.Resource.CollectionId is not null && access.Resource.Selector is null &&
                                                access.Resource.AccessPath.SequenceEqual([field]))
           .ToArray();

    private static Access[] Cells(EngineRun run, string field) =>
        run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) &&
                                                access.Resource.CollectionId is not null && access.Resource.Selector is not null &&
                                                access.Resource.AccessPath[0] == field)
           .ToArray();

    private static AccessPair[] StructurePairs(EngineRun run, string field) =>
        run.Pairs.Pairs.Where(pair => pair.Resource.Selector is null && pair.Resource.CollectionId is not null &&
                                      pair.Resource.AccessPath.SequenceEqual([field]))
           .ToArray();

    private static AccessPair[] CellPairs(EngineRun run, string field) =>
        run.Pairs.Pairs.Where(pair => pair.Resource.Selector is not null && pair.Resource.CollectionId is not null && pair.Resource.AccessPath[0] == field)
           .ToArray();

    private static EngineRun Run(string work, string other) => Analyze(Source(work, other));

    /// <summary>A singleton <c>State</c> holding one of each collection, each with <c>First</c> put into it at construction, which one
    /// worker does <paramref name="work"/> on while another does <paramref name="other"/>.</summary>
    private static string Source(string work, string other) => $$"""
        using System.Collections.Generic;

        public sealed class Item { public int Value; }

        public sealed class State
        {
            public readonly Item First = new();
            public readonly Item Spare = new();
            public readonly HashSet<Item> Set = new();
            public readonly Queue<Item> Queue = new();
            public readonly Stack<Item> Stack = new();
            public readonly LinkedList<Item> List = new();
            public readonly LinkedListNode<Item> Node = new(new Item());

            public State()
            {
                Set.Add(First);
                Queue.Enqueue(First);
                Stack.Push(First);
                List.AddLast(First);
            }
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
}
