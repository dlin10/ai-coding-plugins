using System.Collections;
using System.Reflection;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What a happens-before graph shares with the replays it builds for the assumptions of an ordered pair (R5): the facts no
/// withheld assumption set changes, and the flows of an execution whose inputs are the same in both graphs. A replay answers as a graph
/// built alone with the same withheld sets.</summary>
public sealed class ReplaySharingTests
{
    private const BindingFlags NON_PUBLIC = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The fields of a flow that the queries fill, which two flows of the same inputs may hold differently.</summary>
    private static readonly HashSet<string> FlowCaches = new(StringComparer.Ordinal) { "_reach", "_thrown", "_completing", "_checkCancellation" };

    /// <summary>Every assumption set a reading can rest on, as the graph lists them.</summary>
    private static readonly OrderingAssumption[] AssumptionSets =
    [
        OrderingAssumption.ThreadContext | OrderingAssumption.Observers,
        OrderingAssumption.ThreadPoolEntry | OrderingAssumption.Observers,
        OrderingAssumption.ThreadPoolEntry | OrderingAssumption.ThreadContext | OrderingAssumption.Observers
    ];

    /// <summary>A hosted service that waits for its work with a blocking wait whose exceptional exit is read as the work's completion: the
    /// waiting execution's early waits change when that reading's assumptions are withheld, the work's own do not.</summary>
    private static readonly Lazy<EngineRun> Run = new(() => Analyze(Startup("services.AddHostedService<Worker>();") + """

        public sealed class Box
        {
            public int Count;
        }

        public sealed class Worker : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                var box = new Box();
                var t = Task.Run(() => { box.Count = 1; });
                try { t.Wait(); } catch (AggregateException) { }
                box.Count = 2;
                await Task.CompletedTask;
            }
        }
        """));

    /// <summary>Each replay the pairing built takes from the graph every flow whose inputs are the graph's own, and builds the flow of an
    /// execution whose early waits its withheld sets change; either way its flow has the nodes and edges of the flow a graph built alone
    /// with the same withheld sets makes.</summary>
    [Fact]
    public void A_replay_takes_the_flows_whose_inputs_are_the_graphs_and_builds_the_others()
    {
        var order = Graph();
        var replays = ((IDictionary)Field(order, "_replays")).Values.Cast<HappensBefore>().ToArray();
        Assert.NotEmpty(replays);
        Assert.True(order.Counters.SharedFlows > 0);
        var executions = Executions(order);
        var changed = 0;
        foreach (var replay in replays)
        {
            var alone = Alone(order, (IReadOnlySet<OrderingAssumption>)Field(replay, "_withheld"));
            foreach (var execution in executions)
            {
                var same = ((IEnumerable<(string, int)>)Invoke(order, "EarlyTaskWaits", execution))
                           .ToHashSet().SetEquals((IEnumerable<(string, int)>)Invoke(replay, "EarlyTaskWaits", execution));
                foreach (var flowOf in new[] { "PlainFlow", "FlowOf" })
                {
                    var taken = (HappensBefore.Flow)Invoke(replay, flowOf, execution);
                    Assert.Equal(Structure((HappensBefore.Flow)Invoke(alone, flowOf, execution)), Structure(taken));
                    if (flowOf == "PlainFlow")
                    {
                        // A plain flow has no throwing waits, so its early waits are all that can tell it from the graph's.
                        var own = (HappensBefore.Flow)Invoke(order, flowOf, execution);
                        if (same)
                            Assert.Same(own, taken);
                        else
                            Assert.NotSame(own, taken);
                    }
                }
                changed += same ? 0 : 1;
            }
        }
        // The waiting execution's blocking wait is early in every replay that withholds its reading.
        Assert.True(changed > 0);
    }

    /// <summary>The facts a graph shares with its replays read no withheld assumption set: a graph built alone, withholding any of the
    /// sets, answers each of them as the analysis's own graph does.</summary>
    [Fact]
    public void The_facts_a_graph_shares_with_its_replays_read_no_withheld_set()
    {
        var order = Graph();
        var executions = Executions(order);
        var heap = (HeapSolution)Field(order, "_heap");
        var visits = (IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>>)Field(order, "_visits");
        var regions = heap.Regions.Keys.ToArray();
        foreach (var withheld in Subsets())
        {
            var alone = Alone(order, withheld);
            foreach (var graph in new[] { order, alone })
                Invoke(graph, "EnsureJoins");
            var (expected, actual) = (Shared(order), Shared(alone));
            Assert.Equal(Render(expected.Anchors), Render(actual.Anchors));
            Assert.Equal(Render(expected.StartsOfChild), Render(actual.StartsOfChild));
            Assert.Equal(Render(expected.Handles), Render(actual.Handles));
            Assert.Equal(Render(expected.SpawnSites!), Render(actual.SpawnSites!));
            Assert.Equal(Render(expected.TailEntries), Render(actual.TailEntries));
            Assert.Equal(Render(Invoke(order, "SpawnsOfHandle")), Render(Invoke(alone, "SpawnsOfHandle")));
            Assert.Equal(expected.ContextFacts?.InstallsContext, actual.ContextFacts?.InstallsContext);
            Assert.Equal(Render(expected.Children!), Render(actual.Children!));
            foreach (var execution in executions)
            {
                foreach (var visit in visits.GetValueOrDefault(execution) ?? [])
                    Assert.Equal(Invoke(order, "IsEnumerated", execution, visit.InstanceId), Invoke(alone, "IsEnumerated", execution, visit.InstanceId));
            }
            foreach (var region in regions)
                Assert.Equal(Invoke(order, "IsQuietEvent", region), Invoke(alone, "IsQuietEvent", region));
        }
    }

    /// <summary>The analysis's own graph, after the pairing of its accesses asked the assumptions of the ordered pair.</summary>
    private static HappensBefore Graph()
    {
        var run = Run.Value;
        Assert.NotEmpty(run.Pairs.Assumed);
        return (HappensBefore)typeof(ExecutionAnalysis).GetField("_order", NON_PUBLIC)!.GetValue(run.Execution.Analysis)!;
    }

    /// <summary>A graph of the same inputs, built alone with its own shared facts, withholding the sets given.</summary>
    /// <param name="order">The graph whose inputs are read.</param>
    /// <param name="withheld">The assumption sets withheld.</param>
    private static HappensBefore Alone(HappensBefore order, IReadOnlySet<OrderingAssumption> withheld) =>
        new((ScopeProgram)Field(order, "_scope"), (HeapSolution)Field(order, "_heap"), (AsyncSegments)Field(order, "_segments"),
            (IReadOnlyDictionary<string, ExecutionInstance>)Field(order, "_executions"),
            (IReadOnlyDictionary<string, IReadOnlyList<ExecutionEntry>>)Field(order, "_entries"),
            (IReadOnlyDictionary<string, IReadOnlyList<ExecutionVisit>>)Field(order, "_visits"),
            (IReadOnlyDictionary<string, IReadOnlyList<ExecutionStep>>)Field(order, "_steps"), (IReadOnlyList<SpawnAnchor>)Field(order, "_anchorList"),
            (IReadOnlyDictionary<string, string>)Field(order, "_tails"), (IReadOnlySet<string>)Field(order, "_duringStartup"),
            (IReadOnlyList<ExecutionModel.AdmittedCall>)Field(order, "_admittedCalls"), CancellationToken.None, withheld);

    /// <summary>Every non-empty combination of the assumption sets.</summary>
    private static IEnumerable<IReadOnlySet<OrderingAssumption>> Subsets() =>
        Enumerable.Range(1, (1 << AssumptionSets.Length) - 1)
                  .Select(mask => (IReadOnlySet<OrderingAssumption>)AssumptionSets.Where((_, index) => (mask & (1 << index)) != 0).ToHashSet());

    private static string[] Executions(HappensBefore order) =>
        ((IReadOnlyDictionary<string, ExecutionInstance>)Field(order, "_executions")).Keys.Order(StringComparer.Ordinal).ToArray();

    private static HappensBefore.Shared Shared(HappensBefore order) => (HappensBefore.Shared)Field(order, "_shared");

    private static object Field(object target, string name) => target.GetType().GetField(name, NON_PUBLIC)!.GetValue(target)!;

    private static object Invoke(HappensBefore order, string name, params object[] arguments) =>
        typeof(HappensBefore).GetMethods(NON_PUBLIC).Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
                             .Invoke(order, arguments)!;

    /// <summary>The nodes and edges of a flow, every field but the caches its queries fill.</summary>
    /// <param name="flow">The flow.</param>
    private static string Structure(HappensBefore.Flow flow) =>
        string.Join("\n", typeof(HappensBefore.Flow).GetFields(NON_PUBLIC).Where(field => !FlowCaches.Contains(field.Name))
                                                    .OrderBy(field => field.Name, StringComparer.Ordinal)
                                                    .Select(field => $"{field.Name}={Render(field.GetValue(flow))}"));

    /// <summary>A value as text: a dictionary or a set by its sorted entries, a list in its order, an object of the flow by its
    /// properties.</summary>
    /// <param name="value">The value.</param>
    private static string Render(object? value) => value switch
    {
        null => "null",
        string text => text,
        IDictionary map => "{" + string.Join(";", map.Keys.Cast<object>().Select(key => $"{Render(key)}={Render(map[key])}").Order(StringComparer.Ordinal)) + "}",
        IEnumerable items when value.GetType().Name.StartsWith("HashSet", StringComparison.Ordinal) =>
            "{" + string.Join(";", items.Cast<object>().Select(Render).Order(StringComparer.Ordinal)) + "}",
        IEnumerable items => "[" + string.Join(";", items.Cast<object>().Select(Render)) + "]",
        _ when value.GetType().DeclaringType == typeof(HappensBefore.Flow) =>
            "(" + string.Join(",", value.GetType().GetProperties(NON_PUBLIC).OrderBy(property => property.Name, StringComparer.Ordinal)
                                         .Select(property => $"{property.Name}={Render(property.GetValue(value))}")) + ")",
        _ => value.ToString()!
    };
}
