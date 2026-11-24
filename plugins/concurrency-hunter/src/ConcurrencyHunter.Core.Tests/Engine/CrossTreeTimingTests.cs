using System.Collections;
using System.Reflection;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>How the timing meets two executions of different instance trees (#492). The edges of the happens-before graph stay in one
/// tree but for two: the end of startup, which starts every execution outside <c>_duringStartup</c>, and the join of a task an admitted
/// sequential-awaited call returns, which names that call's continuations whatever tree the waiting execution is in
/// (<c>HappensBefore.ProveCallTask</c>), where a spawn handle's join takes only an execution of the waiter's tree
/// (<c>HandleExecution</c>). So two executions of different trees, neither during startup, are ordered where one waits for the task
/// of an admitted call the other continues, with the assumptions the wait's reading rests on; the completion proof of the queried
/// passes never orders two executions of different trees. These tests hold today's answers.</summary>
public sealed class CrossTreeTimingTests
{
    private const BindingFlags NON_PUBLIC = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The admitted call of the hosted worker, which runs once: its delegate's continuation writes 1.</summary>
    private const string CALL = "var t = Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; });\n";

    /// <summary>The fixtures, by name: the worker's call task waited for by a callback handed to a call the analysis cannot see into,
    /// which runs in a tree of its own, in three forms; a <c>Task.Run</c> task instead of the call task; the call task awaited from a
    /// field by another hosted service; and startup's call task waited for by a callback startup hands over.</summary>
    private static readonly Dictionary<string, Lazy<EngineRun>> Runs = new(StringComparer.Ordinal)
    {
        ["wait"] = new(() => AwaitedRunsFixture.Run(CALL + "stoppingToken.Register(() => { t.Wait(); _state.C = 2; });\nawait t;")),
        ["await"] = new(() => AwaitedRunsFixture.Run(CALL + "stoppingToken.Register(async () => { await t; _state.C = 2; });\nawait t;")),
        ["catch"] = new(() => AwaitedRunsFixture.Run(CALL + "stoppingToken.Register(() => { try { t.Wait(); } catch (AggregateException) { } _state.C = 2; });\nawait t;")),
        ["task-run"] = new(() => AwaitedRunsFixture.Run("var t = Task.Run(async () => { await Task.Yield(); _state.C = 1; });\n" +
                                                        "stoppingToken.Register(() => { t.Wait(); _state.C = 2; });\nawait t;")),
        ["field"] = new(() => AwaitedRunsFixture.Run("_state.Task = Runs.Lib.Run(async () => { await Task.Yield(); _state.C = 1; });\nawait _state.Task;",
                                                     other: "await _state.Task; _state.C = 2;")),
        ["startup"] = new(() => AwaitedRunsFixture.Run("await Task.CompletedTask;", members: "public static int Shared;",
                                                       startup: " var t = Runs.Lib.Run(async () => { await Task.Yield(); Worker.Shared = 1; }); " +
                                                                "CancellationToken.None.Register(() => { t.Wait(); Worker.Shared = 2; });"))
    };

    /// <summary>A callback in a tree of its own that waits for the worker's call task and then writes is ordered after the write of the
    /// call's continuation, in the worker's tree, by a path through the call task's join, on no assumption: neither runs during startup.</summary>
    [Fact]
    public void A_callback_waiting_for_an_admitted_call_task_is_ordered_after_its_continuation_in_another_tree()
    {
        var decided = Decide("wait", "C");

        Assert.NotEqual(decided.FirstTree, decided.SecondTree);
        Assert.Equal((false, false), (decided.FirstDuringStartup, decided.SecondDuringStartup));
        Assert.Equal(ExecutionKind.UnknownDelegateCall, decided.SecondKind);
        Assert.Equal((PairTiming.Ordered, TimingBranch.HappensBeforePath), (decided.Timing, decided.Branch));
        Assert.Empty(decided.Assumed);
    }

    /// <summary>The callback awaiting the call task is ordered as the one waiting for it.</summary>
    [Fact]
    public void A_callback_awaiting_an_admitted_call_task_is_ordered_after_its_continuation_in_another_tree()
    {
        var decided = Decide("await", "C");

        Assert.NotEqual(decided.FirstTree, decided.SecondTree);
        Assert.Equal((false, false), (decided.FirstDuringStartup, decided.SecondDuringStartup));
        Assert.Equal((PairTiming.Ordered, TimingBranch.HappensBeforePath), (decided.Timing, decided.Branch));
        Assert.Empty(decided.Assumed);
    }

    /// <summary>A callback that writes after catching the wait's exception is ordered only as the exceptional exit is read as the call
    /// task's completion: across trees too, the order rests on the thread context and the observers.</summary>
    [Fact]
    public void A_callback_catching_the_wait_for_an_admitted_call_task_is_ordered_across_trees_on_assumptions()
    {
        var decided = Decide("catch", "C");

        Assert.NotEqual(decided.FirstTree, decided.SecondTree);
        Assert.Equal((false, false), (decided.FirstDuringStartup, decided.SecondDuringStartup));
        Assert.Equal((PairTiming.Ordered, TimingBranch.HappensBeforePath), (decided.Timing, decided.Branch));
        Assert.Equal([OrderingAssumptions.OBSERVERS, OrderingAssumptions.THREAD_CONTEXT], decided.Assumed.Order(StringComparer.Ordinal));
    }

    /// <summary>The same callback waiting for a <c>Task.Run</c> task of the worker is not ordered: a spawn handle's join takes only an
    /// execution of the waiter's own tree.</summary>
    [Fact]
    public void A_callback_waiting_for_a_spawned_task_of_another_tree_is_concurrent()
    {
        var decided = Decide("task-run", "C");

        Assert.NotEqual(decided.FirstTree, decided.SecondTree);
        Assert.Equal((PairTiming.Concurrent, (TimingBranch?)null), (decided.Timing, decided.Branch));
        Assert.Empty(decided.Assumed);
    }

    /// <summary>Another hosted service awaiting the call task from a field is not ordered with the continuation: its handle is not
    /// proven to be the call task.</summary>
    [Fact]
    public void A_root_awaiting_an_admitted_call_task_from_a_field_is_concurrent_with_its_continuation()
    {
        var decided = Decide("field", "C");

        Assert.NotEqual(decided.FirstTree, decided.SecondTree);
        Assert.Equal(ExecutionKind.Root, decided.SecondKind);
        Assert.Equal((PairTiming.Concurrent, (TimingBranch?)null), (decided.Timing, decided.Branch));
    }

    /// <summary>Startup's call task orders its continuation before a callback startup hands over: two trees, both during startup, ordered
    /// through the call task's join, not the end of startup.</summary>
    [Fact]
    public void Startup_s_admitted_call_task_orders_its_continuation_before_a_callback_it_hands_over()
    {
        var decided = Decide("startup", "Shared");

        Assert.Equal(ExecutionModel.STARTUP, decided.FirstTree);
        Assert.NotEqual(decided.FirstTree, decided.SecondTree);
        Assert.Equal((true, true), (decided.FirstDuringStartup, decided.SecondDuringStartup));
        Assert.Equal((PairTiming.Ordered, TimingBranch.HappensBeforePath), (decided.Timing, decided.Branch));
        Assert.Empty(decided.Assumed);
    }

    /// <summary>On every fixture, every stored edge of the graph of the whole scope — a start anchor, a tail, a continuation and a join —
    /// stays in one tree, but a join of a continuation of an admitted call, which the call task's join names; and at least one fixture has
    /// such a join across trees.</summary>
    [Fact]
    public void Fixture_edges_between_trees_are_only_joins_of_admitted_call_continuations()
    {
        var across = Runs.Values.Sum(run => AssertEdgesStayInTrees(run.Value.Execution.Analysis));

        Assert.True(across > 0, "a join of an admitted call's continuation across trees");
    }

    /// <summary>On every demo scope, every stored edge stays in one tree but a join of an admitted call's continuation.</summary>
    [Fact]
    public async Task Demo_edges_between_trees_are_only_joins_of_admitted_call_continuations() =>
        await ParallelCollectionTests.ForEachDemoScope(input => AssertEdgesStayInTrees(input.Executions));

    /// <summary>On every eShop scope, every stored edge stays in one tree but a join of an admitted call's continuation.</summary>
    [RequiresEShopFact]
    public async Task EShop_edges_between_trees_are_only_joins_of_admitted_call_continuations() =>
        await ParallelCollectionTests.ForEachScope(EShop(), input => AssertEdgesStayInTrees(input.Executions));

    /// <summary>On every fixture, the completion proof of the queried passes, asked anew for every pair of two writes and of a write and a
    /// read of one resource by executions of different trees, either way round, orders none.</summary>
    [Fact]
    public void Fixture_proof_orders_no_pair_of_different_trees() =>
        Assert.True(Runs.Values.Sum(run => AssertProofOrdersNoPairAcrossTrees(run.Value.Execution.Analysis, run.Value.Collection.Accesses)) > 0,
                    "a pair of different trees asked");

    /// <summary>On every demo scope, the completion proof orders no pair of executions of different trees.</summary>
    [Fact]
    public async Task Demo_proof_orders_no_pair_of_different_trees() =>
        await ParallelCollectionTests.ForEachDemoScope(input =>
            AssertProofOrdersNoPairAcrossTrees(input.Executions, InterproceduralAccesses.Collect(input, CancellationToken.None).Accesses));

    /// <summary>On every eShop scope, the completion proof orders no pair of executions of different trees.</summary>
    [RequiresEShopFact]
    public async Task EShop_proof_orders_no_pair_of_different_trees() =>
        await ParallelCollectionTests.ForEachScope(EShop(), input =>
            AssertProofOrdersNoPairAcrossTrees(input.Executions, InterproceduralAccesses.Collect(input, CancellationToken.None).Accesses));

    /// <summary>The pair of the write before the wait, which writes 1, and the write after it, which writes 2, of a fixture, as the
    /// timing of the analysis decides it.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="field">The field both write.</param>
    private static Decided Decide(string name, string field)
    {
        var run = Runs[name].Value;
        var analysis = run.Execution.Analysis;
        var order = Graph(analysis)!;
        var during = (IReadOnlySet<string>)Field(order, "_duringStartup")!;
        var first = Assert.Single(Writes(run, field, "1"));
        var second = Assert.Single(Writes(run, field, "2"));
        var timing = analysis.Timing(first, second, out var assumed);
        var (one, other) = (analysis.Execution(first.ExecutionId), analysis.Execution(second.ExecutionId));
        return new Decided(timing, assumed, order.OrderedBy(first, second), one.TreeRootId, other.TreeRootId, during.Contains(one.Id),
                           during.Contains(other.Id), other.Kind);
    }

    /// <summary>Asserts that every stored edge of the scope's graph stays in one tree but a join of an admitted call's continuation.</summary>
    /// <param name="analysis">The scope's analysis.</param>
    /// <returns>How many joins of an admitted call's continuation cross trees.</returns>
    private static int AssertEdgesStayInTrees(ExecutionAnalysis analysis)
    {
        if (Graph(analysis) is not { } order)
            return 0;
        // Reading the unproven joins builds every join of the whole scope.
        _ = order.UnprovenJoins;
        string Tree(string execution) => analysis.Execution(execution).TreeRootId;
        foreach (var anchor in (IReadOnlyList<SpawnAnchor>)Field(order, "_anchorList")!)
            Assert.True(Tree(anchor.ExecutionId) == Tree(anchor.ChildId), $"start {anchor.ExecutionId} -> {anchor.ChildId}");
        foreach (var (execution, tail) in (IReadOnlyDictionary<string, string>)Field(order, "_tails")!)
            Assert.True(Tree(execution) == Tree(tail), $"tail {execution} -> {tail}");
        var context = Field(order, "_global")!;
        foreach (var (antecedent, continuations) in (Dictionary<string, List<string>>)Property(context, "Continuations")!)
            Assert.All(continuations, continuation => Assert.True(Tree(antecedent) == Tree(continuation), $"continuation {antecedent} -> {continuation}"));
        var called = ((IEnumerable)Field(order, "_joinedCalls")!).Cast<ExecutionModel.AdmittedCall>().SelectMany(call => call.Continuations)
                                                                .ToHashSet(StringComparer.Ordinal);
        var across = 0;
        foreach (var (joined, joins) in (Dictionary<string, List<(string Execution, int Join)>>)Property(context, "JoinedBy")!)
        {
            foreach (var (joining, _) in joins.Where(join => Tree(join.Execution) != Tree(joined)))
            {
                Assert.True(called.Contains(joined), $"join {joined} -> {joining}");
                across++;
            }
        }

        return across;
    }

    /// <summary>Asserts that the completion proof, asked anew, orders no pair of executions of different trees on one resource.</summary>
    /// <param name="analysis">The scope's analysis.</param>
    /// <param name="views">Every execution of the scope's accesses.</param>
    /// <returns>How many pairs of different trees it asked about.</returns>
    private static int AssertProofOrdersNoPairAcrossTrees(ExecutionAnalysis analysis, IEnumerable<Access> views)
    {
        if (Graph(analysis) is not { } order)
            return 0;
        var asked = 0;
        foreach (var group in views.GroupBy(view => view.Resource.Identity, StringComparer.Ordinal))
        {
            var members = group.ToArray();
            for (var one = 0; one < members.Length; one++)
            {
                for (var other = one + 1; other < members.Length; other++)
                {
                    var (first, second) = (members[one], members[other]);
                    if (first.Operation == AccessOperation.Read && second.Operation == AccessOperation.Read ||
                        analysis.Execution(first.ExecutionId).TreeRootId == analysis.Execution(second.ExecutionId).TreeRootId)
                    {
                        continue;
                    }

                    Assert.False(order.TracePassCompletion(first, second, null).Proven, $"{first.ExecutionId} | {second.ExecutionId}");
                    Assert.False(order.TracePassCompletion(second, first, null).Proven, $"{second.ExecutionId} | {first.ExecutionId}");
                    asked++;
                }
            }
        }

        return asked;
    }

    /// <summary>The writes of a field that store a constant, each as its execution has it.</summary>
    /// <param name="run">The run.</param>
    /// <param name="field">The field.</param>
    /// <param name="constant">The constant's text.</param>
    private static Access[] Writes(EngineRun run, string field, string constant)
    {
        var sites = run.Execution.Heap.Program.Result.Bodies.Values
                       .SelectMany(body => body.Blocks.SelectMany(block => block.Operations).OfType<IrStoreFieldOperation>()
                                               .Where(store => store.Field.Name == field &&
                                                               body.Values.Any(value => value.Id == store.Value && value.Kind == IrValueKind.Constant &&
                                                                                        value.Name == constant))
                                               .Select(store => (body.BodyId, store.Id)))
                       .ToHashSet();
        return run.Collection.Accesses.Where(access => access.Resource.Member.Name == field && access.Operation == AccessOperation.Write &&
                                                       sites.Contains((access.BodyId, access.OperationId)))
                  .ToArray();
    }

    /// <summary>The eShop solution.</summary>
    private static string EShop() =>
        Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!, "src", "eShopOnContainers-ServicesAndWebApps.sln");

    /// <summary>The analysis's happens-before graph, or null for a scope without one.</summary>
    /// <param name="analysis">The analysis.</param>
    private static HappensBefore? Graph(ExecutionAnalysis analysis) => (HappensBefore?)Field(analysis, "_order");

    /// <summary>A private instance field's value.</summary>
    /// <param name="target">The object holding the field.</param>
    /// <param name="name">The field.</param>
    private static object? Field(object target, string name) => target.GetType().GetField(name, NON_PUBLIC)!.GetValue(target);

    /// <summary>An instance property's value, public or not.</summary>
    /// <param name="target">The object holding the property.</param>
    /// <param name="name">The property.</param>
    private static object? Property(object target, string name) =>
        target.GetType().GetProperty(name, NON_PUBLIC | BindingFlags.Public)!.GetValue(target);

    /// <summary>A fixture's pair as the timing decided it.</summary>
    /// <param name="Timing">The timing.</param>
    /// <param name="Assumed">The assumptions its order rests on.</param>
    /// <param name="Branch">The branch that ordered it, or null.</param>
    /// <param name="FirstTree">The tree of the write before the wait.</param>
    /// <param name="SecondTree">The tree of the write after it.</param>
    /// <param name="FirstDuringStartup">Whether the first write's execution may run during startup.</param>
    /// <param name="SecondDuringStartup">Whether the second write's execution may run during startup.</param>
    /// <param name="SecondKind">The kind of the second write's execution.</param>
    private sealed record Decided(PairTiming Timing, IReadOnlySet<string> Assumed, TimingBranch? Branch, string FirstTree, string SecondTree,
                                  bool FirstDuringStartup, bool SecondDuringStartup, ExecutionKind SecondKind);
}
