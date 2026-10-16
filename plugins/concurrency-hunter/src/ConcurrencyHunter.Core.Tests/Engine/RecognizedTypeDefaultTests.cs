using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A member of a recognized type that no recognizer models still has an effect (question 144). A collection's member the
/// table names has its table effects, the table now naming the bulk writers, the capacity members, the scans and the copies (ADR 0010,
/// amendment of 2026-10-16); any other member of a collection has an unknown effect on what the collection holds and on its arguments.
/// Any other member of a task, a thread, a timer or a lock has one on its arguments alone, other than the delegates, the object a
/// <c>Monitor</c> member locks and the family's own objects. None of them is a semantic gap.</summary>
public sealed class RecognizedTypeDefaultTests
{
    // ---- the table's new members ----

    [Theory]
    [InlineData("_state.Items.Reverse();")]
    [InlineData("_state.Items.Reverse(0, 1);")]
    [InlineData("_state.Items.Sort();")]
    [InlineData("_state.Items.Sort(_state.Comparer);")]
    [InlineData("_state.Items.Sort(0, 1, _state.Comparer);")]
    [InlineData("_state.Items.InsertRange(0, new[] { new Item() });")]
    [InlineData("_state.Items.RemoveRange(0, 1);")]
    [InlineData("_state.Items.Capacity = 10;")]
    [InlineData("_state.Items.TrimExcess();")]
    [InlineData("_state.Items.EnsureCapacity(10);")]
    public async Task Foreach_over_a_List_meets_a_bulk_or_capacity_writer_on_the_structure(string write)
    {
        var findings = await Findings("foreach (var x in _state.Items) { }", write);

        Assert.Contains(findings, finding => finding.Resource.AccessPath[0] == "Items" && finding.Resource.CollectionId is not null &&
                                             finding.Resource.Selector is null);
    }

    [Theory]
    [InlineData("_ = _state.Items.IndexOf(_state.First);")]
    [InlineData("_ = _state.Items.LastIndexOf(_state.First);")]
    [InlineData("_ = _state.Items.BinarySearch(_state.First, _state.Comparer);")]
    [InlineData("_ = _state.Items.Capacity;")]
    [InlineData("_ = _state.Items.ToArray();")]
    [InlineData("_ = _state.Items.GetRange(0, 1);")]
    public async Task A_scan_or_a_copy_of_a_List_meets_Add(string read)
    {
        var findings = await Findings(read, "_state.Items.Add(new Item());");

        Assert.Contains(findings, finding => finding.Resource.AccessPath[0] == "Items" && finding.Resource.CollectionId is not null);
    }

    [Theory]
    [InlineData("foreach (var x in _state.Items.ToArray()) { x.Value = 1; }")]
    [InlineData("foreach (var x in _state.Items.GetRange(0, 1)) { x.Value = 1; }")]
    [InlineData("foreach (var x in _state.Items.Slice(0, 1)) { x.Value = 1; }")]
    [InlineData("foreach (var x in _state.Queue.ToArray()) { x.Value = 1; }")]
    [InlineData("foreach (var x in _state.Stack.ToArray()) { x.Value = 1; }")]
    [InlineData("foreach (var x in _state.Line.ToArray()) { x.Value = 1; }")]
    [InlineData("foreach (var x in _state.Bag.ToArray()) { x.Value = 1; }")]
    public async Task A_copy_holds_the_objects_its_collection_holds(string write)
    {
        var finding = Assert.Single(await Findings("_ = _state.First.Value;", write));

        Assert.Equal(["Value"], finding.Resource.AccessPath);
    }

    [Theory]
    [InlineData("_state.Set.UnionWith(_state.Items);")]
    [InlineData("_state.Set.IntersectWith(_state.Items);")]
    [InlineData("_state.Set.ExceptWith(_state.Items);")]
    [InlineData("_state.Set.SymmetricExceptWith(_state.Items);")]
    [InlineData("_state.Set.TrimExcess();")]
    [InlineData("_state.Queue.TrimExcess();")]
    [InlineData("_state.Queue.EnsureCapacity(10);")]
    [InlineData("_state.Stack.TrimExcess();")]
    [InlineData("_state.Map.TrimExcess();")]
    [InlineData("_state.Map.EnsureCapacity(10);")]
    public async Task Foreach_over_a_set_queue_stack_or_dictionary_meets_its_bulk_or_capacity_writer(string write)
    {
        var collection = write[(write.IndexOf('.') + 1)..write.IndexOf('.', write.IndexOf('.') + 1)];
        var findings = await Findings($"foreach (var x in _state.{collection}) {{ }}", write);

        Assert.Contains(findings, finding => finding.Resource.AccessPath[0] == collection && finding.Resource.CollectionId is not null);
    }

    [Theory]
    [InlineData("_ = _state.Set.IsSubsetOf(_state.Items);")]
    [InlineData("_ = _state.Set.IsProperSupersetOf(_state.Items);")]
    [InlineData("_ = _state.Set.Overlaps(_state.Items);")]
    [InlineData("_ = _state.Set.SetEquals(_state.Items);")]
    [InlineData("_state.Set.IntersectWith(_state.Items);")]
    public async Task A_set_comparison_enumerates_its_argument(string read)
    {
        var findings = await Findings(read, "_state.Items.Add(new Item());");

        Assert.Contains(findings, finding => finding.Resource.AccessPath[0] == "Items" && finding.Resource.CollectionId is not null);
    }

    [Fact]
    public async Task UnionWith_holds_what_it_enumerates()
    {
        var finding = Assert.Single(await Findings("_ = _state.First.Value;",
                                                   "var set = new System.Collections.Generic.HashSet<Item>(); set.UnionWith(_state.Items); foreach (var x in set) { x.Value = 1; }"));

        Assert.Equal(["Value"], finding.Resource.AccessPath);
    }

    [Fact]
    public async Task ContainsValue_scans_the_dictionary()
    {
        var findings = await Findings("_ = _state.Map.ContainsValue(_state.First);", "_state.Map[2] = new Item();");

        Assert.Contains(findings, finding => finding.Resource.AccessPath[0] == "Map" && finding.Resource.CollectionId is not null);
    }

    [Fact]
    public async Task A_thread_safe_collection_reads_IsEmpty_and_writes_PushRange_atomically()
    {
        var result = await Analyze("_ = _state.Bag.IsEmpty;", "_state.Pile.PushRange(new[] { new Item() });");

        Assert.Contains(result.Accesses, access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal) &&
                                                   access.Resource.CollectionId is not null && access.Operation == AccessOperation.AtomicRead);
        Assert.Contains(result.Accesses, access => access.Symbol.StartsWith("Writer.", StringComparison.Ordinal) &&
                                                   access.Resource.CollectionId is not null && access.Operation == AccessOperation.AtomicWrite);
    }

    // ---- the default of a collection ----

    [Theory]
    [InlineData("_state.Items.CopyTo(_state.Target);", "_ = _state.Target[0];", "Target")]
    [InlineData("_ = _state.Items.AsReadOnly();", "foreach (var x in _state.Items) { }", "Items")]
    [InlineData("_state.Pile.TryPopRange(_state.Target);", "_ = _state.Target[0];", "Target")]
    [InlineData("_state.Items.Sort(_state.Comparer);", "_ = _state.Comparer.Calls;", "Calls")]
    public async Task A_member_outside_the_table_has_an_unknown_effect_and_is_no_gap(string write, string read, string touched)
    {
        var result = await Analyze(read, write);

        Assert.Contains(result.Findings, finding => finding.Resource.AccessPath.Contains(touched) && finding.AccessB.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(Gaps(result));
    }

    // ---- the default of a task, a thread, a timer or a lock ----

    [Fact]
    public async Task SetException_has_an_unknown_effect_on_the_exception_and_is_no_gap()
    {
        var result = await Analyze("_ = _state.Error.Code;", "_state.Source.SetException(_state.Error);");

        Assert.Contains(result.Findings, finding => finding.Resource.AccessPath.Contains("Code"));
        Assert.Empty(Gaps(result));
    }

    [Theory]
    [InlineData("System.Threading.Monitor.Pulse(_state.First);")]
    [InlineData("_ = System.Threading.Monitor.IsEntered(_state.First);")]
    [InlineData("_ = _state.Work.IsCompleted;")]
    [InlineData("_ = _state.Work.Status;")]
    [InlineData("_ = Task.WaitAny(_state.Tasks);")]
    public async Task What_a_lock_or_a_task_holds_is_not_touched_by_its_other_members(string call)
    {
        Assert.Empty(await Findings("_ = _state.First.Value;", call));
    }

    // ---- helpers ----

    private static IEnumerable<SemanticGap> Gaps(AnalysisResult result) => result.Coverage.SelectMany(scope => scope.Gaps);

    private static async Task<IReadOnlyList<Finding>> Findings(string read, string write) => (await Analyze(read, write)).Findings;

    private static Task<AnalysisResult> Analyze(string read, string write) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Source(read, write))), ROOT_DIRECTORY, CancellationToken.None);

    /// <summary>A singleton <c>State</c> one worker reads while another changes it. <c>First</c> is held by every collection and is
    /// what <c>Work</c> completes with.</summary>
    private static string Source(string read, string write) => Usings + $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public sealed class Item { public int Value; }

        public sealed class Failure : Exception { public int Code; }

        public sealed class ItemComparer : IComparer<Item>
        {
            public int Calls;
            public int Compare(Item x, Item y) => 0;
        }

        public sealed class State
        {
            public Item First = new();
            public List<Item> Items = new();
            public Item[] Target = new Item[4];
            public HashSet<Item> Set = new();
            public Queue<Item> Queue = new();
            public Stack<Item> Stack = new();
            public Dictionary<int, Item> Map = new();
            public ConcurrentStack<Item> Pile = new();
            public ConcurrentBag<Item> Bag = new();
            public ConcurrentQueue<Item> Line = new();
            public ItemComparer Comparer = new();
            public TaskCompletionSource<Item> Source = new();
            public Failure Error = new();
            public Task<Item> Work;
            public Task<Item>[] Tasks;

            public State()
            {
                Items.Add(First);
                Set.Add(First);
                Queue.Enqueue(First);
                Stack.Push(First);
                Map[1] = First;
                Pile.Push(First);
                Bag.Add(First);
                Line.Enqueue(First);
                Work = Task.FromResult(First);
                Tasks = new[] { Work };
            }
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            public Reader(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{read}}
                return Task.CompletedTask;
            }
        }

        public sealed class Writer : BackgroundService
        {
            private readonly State _state;
            public Writer(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{write}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Reader>(); services.AddHostedService<Writer>();");
}
