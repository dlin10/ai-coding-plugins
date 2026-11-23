using System.Reflection;
using System.Runtime.CompilerServices;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The completion proof of a pass reads the joins of a parent's visits once per parent (#475): every later start of the parent
/// takes the joins whose handle names its execution from the record the first one made, and gets what a start that proves every join
/// of the parent anew gets, proving no handle itself. The proofs of a handle find a call's targets, the edges into an instance, the
/// starts an execution makes and the executions a handle names in one tree through indexes that give what a pass over the whole list
/// gives, in its order.</summary>
public sealed class ParentJoinRecordTests
{
    private const BindingFlags NON_PUBLIC = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly Type ProofType = typeof(HappensBefore).GetNestedType("PassCompletionProof", BindingFlags.NonPublic)!;

    /// <summary>The cases the record must keep apart: a handle stored in a field, proven but not the pass's own; one child waited for
    /// twice; two children of one parent; a task forwarder; a helper whose task one root and another start, so that its handle names
    /// executions in two trees; a retry loop around a blocking wait.</summary>
    private static readonly Lazy<EngineRun[]> Runs = new(() =>
    [
        Analyze(Worker("for (var pass = 0; pass < 2; pass++) { _task = Task.Run(Write); await _task; }", "private Task? _task;")),
        Analyze(Worker("for (var pass = 0; pass < 2; pass++) { var own = Task.Run(Write); own.Wait(); await own; }")),
        Analyze(Worker("for (var pass = 0; pass < 2; pass++) { var a = Task.Run(Write); var b = Task.Run(Write); a.Wait(); b.Wait(); } " +
                       "await Task.CompletedTask;")),
        Analyze(Worker("for (var pass = 0; pass < 2; pass++) await Forward();",
                       "private Task Forward() => DrainAsync(); private async Task DrainAsync() { await Task.Run(Write); }")),
        Analyze(Startup("services.AddHostedService<Worker>(); services.AddSingleton<Drainer>();") + """

            public sealed class Drainer
            {
                public static int Value;
                public async Task Drain() { var own = Task.Run(() => Value = 1); await own; }
            }
            public sealed class Worker(Drainer drainer) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    for (var pass = 0; pass < 2; pass++) await drainer.Drain();
                }
            }
            public sealed class DrainController(Drainer drainer) : ControllerBase
            {
                public async Task Post() { await drainer.Drain(); await drainer.Drain(); }
            }
            """),
        Analyze(Worker("for (var attempt = 0; attempt < 3; attempt++) { try { var own = Task.Run(Write); own.Wait(); break; } " +
                       "catch (AggregateException) { } } await Task.CompletedTask;"))
    ]);

    /// <summary>On the fixtures, after their pairing, every start reads from the record what a start proving every join of its parent
    /// anew reads, and reading it proves no handle. The fixtures have what the record must get right: a proven handle that is not the
    /// pass's own, a child two joins of one parent wait for, a parent two of whose children have joins, and a handle region whose
    /// executions are in two trees.</summary>
    [Fact]
    public void Every_start_of_the_fixtures_reads_from_the_record_what_proving_its_parent_anew_reads()
    {
        var seen = new Probe();
        foreach (var run in Runs.Value)
            seen.Add(CompareStarts(Graph(run.Execution.Analysis)));

        Assert.True(seen.NotOwn > 0, "a proven handle that is not the pass's own");
        Assert.True(seen.ChildWaitedTwice > 0, "a child two joins of one parent wait for");
        Assert.True(seen.ParentOfTwo > 0, "a parent two of whose children have joins");
        Assert.True(seen.Recorded > 0, "a start read from the record");
        Assert.Contains(Runs.Value, run => SpansTrees(Graph(run.Execution.Analysis)));
    }

    /// <summary>On every demo scope, after its accesses are collected and paired as the analysis pairs them, every start reads from the
    /// record what a start proving every join of its parent anew reads, and reading it proves no handle.</summary>
    [Fact]
    public async Task Every_start_of_the_demo_reads_from_the_record_what_proving_its_parent_anew_reads()
    {
        var seen = new Probe();
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            if (GraphOrNone(input.Executions) is not { } order)
                return;
            var collection = InterproceduralAccesses.Collect(input, CancellationToken.None);
            InterproceduralPairing.Pair(collection.Accesses, input.Executions, input.Heap);
            seen.Add(CompareStarts(order));
        });
        Assert.True(seen.Recorded > 0, "a start read from the record");
    }

    /// <summary>The record is each graph's own: a replay that withholds assumption sets reads the fault of a blocking wait otherwise (R5),
    /// so its record holds other readings than the graph's. On the fixtures, every start of the replay the graph builds for each of the
    /// seven combinations of withheld sets reads from that replay's record what proving its parent anew on the replay reads, and what a
    /// graph of the same inputs built alone with those sets reads; the indexes the replay shares with the graph give what the pass over
    /// their lists gives.</summary>
    [Fact]
    public void Every_start_of_every_replay_reads_from_its_own_record_what_a_graph_built_alone_with_its_withheld_sets_reads()
    {
        var withholding = typeof(HappensBefore).GetMethod("Withholding", NON_PUBLIC)!;
        var differ = 0;
        foreach (var run in Runs.Value)
        {
            var order = Graph(run.Execution.Analysis);
            var own = CompareStarts(order).Answers;
            foreach (var withheld in Subsets())
            {
                var replay = (HappensBefore)withholding.Invoke(order, [withheld])!;
                var answers = CompareStarts(replay).Answers;
                Assert.Equal(CompareStarts(Alone(order, withheld)).Answers, answers);
                CompareIndexes(replay);
                differ += own.SequenceEqual(answers) ? 0 : 1;
            }
        }
        Assert.True(differ > 0, "a replay whose record holds other readings than the graph's");
    }

    /// <summary>Every index answers what the pass over its list answers, in the list's order, on the fixtures and on every demo scope: the
    /// distinct callees of each call, the edges into each instance, the starts each execution makes, and the executions each handle region
    /// names in each tree.</summary>
    [Fact]
    public async Task Every_index_gives_what_the_pass_over_its_list_gives_in_its_order()
    {
        foreach (var run in Runs.Value)
            CompareIndexes(Graph(run.Execution.Analysis));
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            if (GraphOrNone(input.Executions) is { } order)
                CompareIndexes(order);
        });
    }

    /// <summary>On edges and starts made for it — a call two edges lead to one callee by, a call whose callees do not come in their
    /// order, a callee two calls lead to, an execution whose starts stand among another's — every index keeps the order of its list, and
    /// a call's callee two edges lead to is one callee.</summary>
    [Fact]
    public void The_indexes_keep_the_list_order_and_take_a_callee_two_edges_lead_to_once()
    {
        CallEdge[] edges =
        [
            new("a", 1, "y", "virtual"), new("b", 1, "x", "direct"), new("a", 1, "x", "virtual"), new("a", 1, "y", "delegate"),
            new("a", 2, "x", "direct")
        ];
        SpawnAnchor[] anchors =
        [
            new("e2", "a", 3, "c1", SpawnAnchorKind.AsyncReturn), new("e1", "a", 1, "c2", SpawnAnchorKind.Spawn),
            new("e2", "b", 2, "c3", SpawnAnchorKind.Timer)
        ];
        var calls = new CallIndex(edges, anchors);

        Assert.Equal(new[] { "y", "x" }, calls.Callees("a", 1));
        Assert.Equal(new[] { "x" }, calls.Callees("a", 2));
        Assert.Equal(new[] { edges[1], edges[2], edges[4] }, calls.Into("x"));
        Assert.Equal(new[] { anchors[0], anchors[2] }, calls.StartsBy("e2"));
        Assert.Equal(new[] { anchors[1] }, calls.StartsBy("e1"));
    }

    /// <summary>What the comparisons of starts met, summed over graphs.</summary>
    private sealed class Probe
    {
        /// <summary>The joins rejected because their proven handle is not the pass's own.</summary>
        internal int NotOwn;

        /// <summary>The starts with more than one join.</summary>
        internal int ChildWaitedTwice;

        /// <summary>The parents with joins for more than one of their children.</summary>
        internal int ParentOfTwo;

        /// <summary>The starts other than a timer's that read a join from the record.</summary>
        internal int Recorded;

        /// <summary>What each start read, in the order the starts were asked.</summary>
        internal List<string> Answers { get; } = [];

        /// <summary>Adds what another comparison met.</summary>
        /// <param name="other">The other comparison's counts.</param>
        internal void Add(Probe other)
        {
            NotOwn += other.NotOwn;
            ChildWaitedTwice += other.ChildWaitedTwice;
            ParentOfTwo += other.ParentOfTwo;
            Recorded += other.Recorded;
        }
    }

    /// <summary>Asks the joins of every start of every execution, with and without work whose units overlap, three times: once as the
    /// analysis asks (making the record where no start of the parent made it yet), once more, which must prove no handle unless the start
    /// is a timer's, and once as a diagnostic request asks, proving every join of the parent anew; the three must agree.</summary>
    /// <param name="order">The graph.</param>
    private static Probe CompareStarts(HappensBefore order)
    {
        var proof = (Field(order, "_passCompletion") ??
                     Activator.CreateInstance(ProofType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [order], null))!;
        var probe = new Probe();
        var children = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var execution in Executions(order).Keys.Order(StringComparer.Ordinal))
        {
            foreach (var anchor in order.StartsOf(execution))
            {
                foreach (var overlappingWork in new[] { false, true })
                {
                    var asked = Joins(proof, anchor, overlappingWork, null);
                    var proofs = order.Counters.HandleProofs;
                    var again = Joins(proof, anchor, overlappingWork, null);
                    if (anchor.Kind != SpawnAnchorKind.Timer)
                        Assert.Equal(proofs, order.Counters.HandleProofs);
                    var rejected = new List<PassJoinEvidence>();
                    var anew = Joins(proof, anchor, overlappingWork, rejected);
                    Assert.Equal(anew.Text, asked.Text);
                    Assert.Equal(anew.Text, again.Text);
                    probe.Answers.Add($"{anchor} overlapping={overlappingWork}: {anew.Text}");
                    probe.NotOwn += rejected.Count(join => join.Reason == "not-this-pass");
                    probe.ChildWaitedTwice += anew.Joins > 1 ? 1 : 0;
                    probe.Recorded += again.Joins > 0 && anchor.Kind != SpawnAnchorKind.Timer ? 1 : 0;
                    if (anew.Joins > 0)
                    {
                        if (!children.TryGetValue(anchor.ExecutionId, out var started))
                            children.Add(anchor.ExecutionId, started = new HashSet<string>(StringComparer.Ordinal));
                        started.Add(anchor.ChildId);
                    }
                }
            }
        }
        probe.ParentOfTwo = children.Values.Count(started => started.Count > 1);
        return probe;
    }

    /// <summary>The joins a pass of one start reads, as text, with how many there are, suspending ones included.</summary>
    /// <param name="Text">The answer, the start's kind and every join.</param>
    /// <param name="Joins">How many joins it read.</param>
    private sealed record Answer(string Text, int Joins);

    /// <summary>Asks the completion proof for the joins of a pass of one start.</summary>
    /// <param name="proof">The completion proof of the graph.</param>
    /// <param name="anchor">The start.</param>
    /// <param name="overlappingWork">Whether work whose units overlap within one pass has a frame.</param>
    /// <param name="rejected">Receives the rejected joins, which makes it a diagnostic request; null for the analysis's.</param>
    private static Answer Joins(object proof, SpawnAnchor anchor, bool overlappingWork, List<PassJoinEvidence>? rejected)
    {
        object?[] arguments = [anchor, overlappingWork, rejected, null, null, null];
        var framed = (bool)ProofType.GetMethod("Joins", NON_PUBLIC)!.Invoke(proof, arguments)!;
        var joins = ((System.Collections.IEnumerable)arguments[4]!).Cast<ITuple>().ToArray();
        var suspended = ((System.Collections.IEnumerable)arguments[5]!).Cast<ITuple>().ToArray();
        return new Answer($"{framed} {arguments[3]} joins=[{string.Join(";", joins.Select(Render))}] " +
                          $"suspended=[{string.Join(";", suspended.Select(Render))}]", joins.Length + suspended.Length);
    }

    /// <summary>A join as text, the instance of a suspending one by its id.</summary>
    /// <param name="join">The join.</param>
    private static string Render(ITuple join) =>
        "(" + string.Join(",", Enumerable.Range(0, join.Length).Select(item => join[item] is MethodInstance instance ? instance.Id : $"{join[item]}")) + ")";

    /// <summary>Compares each index of a graph with the pass over its list, for every key the lists hold and one they do not.</summary>
    /// <param name="order">The graph.</param>
    private static void CompareIndexes(HappensBefore order)
    {
        var heap = (HeapSolution)Field(order, "_heap")!;
        var anchors = (IReadOnlyList<SpawnAnchor>)Field(order, "_anchorList")!;
        var calls = (CallIndex)typeof(HappensBefore).GetProperty("Calls", NON_PUBLIC)!.GetValue(order)!;
        foreach (var (caller, operation) in heap.Edges.Select(edge => (edge.CallerInstance, edge.OperationId)).Distinct())
        {
            Assert.Equal(heap.Edges.Where(edge => edge.CallerInstance == caller && edge.OperationId == operation)
                             .Select(edge => edge.CalleeInstance).Distinct(StringComparer.Ordinal), calls.Callees(caller, operation));
        }
        foreach (var callee in heap.Edges.Select(edge => edge.CalleeInstance).Distinct(StringComparer.Ordinal))
            Assert.Equal(heap.Edges.Where(edge => edge.CalleeInstance == callee), calls.Into(callee));
        var executions = Executions(order);
        foreach (var execution in executions.Keys.Concat(anchors.Select(anchor => anchor.ExecutionId)).Distinct(StringComparer.Ordinal))
            Assert.Equal(anchors.Where(anchor => anchor.ExecutionId == execution), calls.StartsBy(execution));
        Assert.Empty(calls.Callees("no such instance", -1));
        Assert.Empty(calls.Into("no such instance"));
        Assert.Empty(calls.StartsBy("no such execution"));

        var handles = (Dictionary<string, List<string>>)Field(order, "_handles")!;
        var inTree = typeof(HappensBefore).GetMethod("HandlesInTree", NON_PUBLIC)!;
        var roots = executions.Values.Select(execution => execution.TreeRootId).Append("no such tree").Distinct(StringComparer.Ordinal).ToArray();
        foreach (var (region, candidates) in handles)
        {
            foreach (var root in roots)
            {
                Assert.Equal(candidates.Where(candidate => executions[candidate].TreeRootId == root),
                             (string[])inTree.Invoke(order, [region, root])!);
            }
        }
    }

    /// <summary>Whether some handle region of a graph names executions of two trees.</summary>
    /// <param name="order">The graph.</param>
    private static bool SpansTrees(HappensBefore order)
    {
        var executions = Executions(order);
        return ((Dictionary<string, List<string>>)Field(order, "_handles")!).Values
               .Any(candidates => candidates.Select(candidate => executions[candidate].TreeRootId).Distinct(StringComparer.Ordinal).Count() > 1);
    }

    /// <summary>Every non-empty combination of the assumption sets a reading can rest on, as the graph lists them.</summary>
    private static IEnumerable<OrderingAssumption[]> Subsets()
    {
        OrderingAssumption[] sets =
        [
            OrderingAssumption.ThreadContext | OrderingAssumption.Observers,
            OrderingAssumption.ThreadPoolEntry | OrderingAssumption.Observers,
            OrderingAssumption.ThreadPoolEntry | OrderingAssumption.ThreadContext | OrderingAssumption.Observers
        ];
        return Enumerable.Range(1, (1 << sets.Length) - 1).Select(mask => sets.Where((_, index) => (mask & (1 << index)) != 0).ToArray());
    }

    /// <summary>A graph of the same inputs as another, built alone with its own shared facts, withholding the sets given.</summary>
    /// <param name="order">The graph whose inputs are read.</param>
    /// <param name="withheld">The assumption sets withheld.</param>
    private static HappensBefore Alone(HappensBefore order, IReadOnlyList<OrderingAssumption> withheld) =>
        new((ScopeProgram)Field(order, "_scope")!, (HeapSolution)Field(order, "_heap")!, (AsyncSegments)Field(order, "_segments")!,
            Executions(order), (IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>>)Field(order, "_entries")!,
            (IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>>)Field(order, "_visits")!,
            (IReadOnlyDictionary<string, IReadOnlyList<ExecutionStep>>)Field(order, "_steps")!, (IReadOnlyList<SpawnAnchor>)Field(order, "_anchorList")!,
            (IReadOnlyDictionary<string, string>)Field(order, "_tails")!, (IReadOnlySet<string>)Field(order, "_duringStartup")!,
            (IReadOnlyList<ExecutionModel.AdmittedCall>)Field(order, "_admittedCalls")!, CancellationToken.None, withheld.ToHashSet());

    /// <summary>The analysis's happens-before graph.</summary>
    /// <param name="analysis">The analysis.</param>
    private static HappensBefore Graph(ExecutionAnalysis analysis) => Assert.IsType<HappensBefore>(GraphOrNone(analysis));

    /// <summary>The analysis's happens-before graph, or null for a scope without one.</summary>
    /// <param name="analysis">The analysis.</param>
    private static HappensBefore? GraphOrNone(ExecutionAnalysis analysis) =>
        (HappensBefore?)typeof(ExecutionAnalysis).GetField("_order", NON_PUBLIC)!.GetValue(analysis);

    /// <summary>The executions of a graph's scope, by id.</summary>
    /// <param name="order">The graph.</param>
    private static IReadOnlyDictionary<string, ExecutionInstance> Executions(HappensBefore order) =>
        (IReadOnlyDictionary<string, ExecutionInstance>)Field(order, "_executions")!;

    /// <summary>A private instance field's value.</summary>
    /// <param name="target">The object holding the field.</param>
    /// <param name="name">The field.</param>
    private static object? Field(object target, string name) => target.GetType().GetField(name, NON_PUBLIC)!.GetValue(target);

    /// <summary>A hosted service whose loop is the body given, with a static field its <c>Write</c> writes.</summary>
    /// <param name="body">The body of <c>ExecuteAsync</c>.</param>
    /// <param name="members">More members of the service.</param>
    private static string Worker(string body, string members = "") => Startup("services.AddHostedService<Worker>();") + $$"""

        public sealed class Worker : BackgroundService
        {
            public static int Value;
            private void Write() { Value = 1; }
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
            }
            {{members}}
        }
        """;
}
