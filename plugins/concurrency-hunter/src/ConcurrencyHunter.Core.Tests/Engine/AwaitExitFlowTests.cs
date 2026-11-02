using System.Collections;
using System.Reflection;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The exceptional exit of an <c>await</c> in the flow of the task that awaits (R3): the exception of an earlier <c>await</c>
/// the task does not catch leaves the task around a later join of the work it started, unless it comes after everything that join waits
/// for completed, and only the paths that ask for exceptional exits take it. The question is put to the flow itself, from where the task
/// starts the child to its late join, because the engine's other proofs of the child's completion answer through the same program: the
/// pass proof reads every <c>await</c> that is no join of the pass as a way out already, and any other call between the child's start
/// and its join leaves the child running too.</summary>
public sealed class AwaitExitFlowTests
{
    private const BindingFlags NonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    private const string START = "var failing = Task.Run(() => Fail()); var inner = Task.Run(() => { First(); return 1; }); ";

    private const string GROUP = "var inner = Task.Run(() => { First(); return 1; }); var other = Task.Run(() => 2); var group = Task.WhenAll(inner, other); ";

    /// <summary>Whether every path of the outer task, from where it starts the child to its last join, passes that join, or leaves the
    /// task around it with an exception only if exceptional exits are not asked for.</summary>
    /// <param name="body">The outer task's body.</param>
    /// <param name="exceptionalExits">Whether an exception that leaves the task reaches its end.</param>
    /// <param name="afterTheGroup">Whether the paths start just after the <c>Task.WhenAll</c> call that lists the child, so that only
    /// what stands after that call is in the window, and not where the task starts the child.</param>
    private static bool PassesTheLateJoin(string body, bool exceptionalExits, bool afterTheGroup = false)
    {
        var run = Execute(BlockingWaitAssumptionMatrixTests.Worker(
            $"var outer = Task.Run(async () => {{ {body} }}); try {{ await outer; }} catch (Exception) {{ }} After();", BlockingWaitAssumptionMatrixTests.FAIL));
        var order = (HappensBefore)typeof(ExecutionAnalysis).GetField("_order", NonPublic)!.GetValue(run.Analysis)!;
        var executions = (IDictionary)typeof(HappensBefore).GetField("_executions", NonPublic)!.GetValue(order)!;
        var joinsOf = typeof(HappensBefore).GetMethod("Joins", NonPublic)!;
        var joinedFrom = typeof(HappensBefore).GetMethod("JoinedFrom", NonPublic)!;
        var flowOf = typeof(HappensBefore).GetMethod("FlowOf", NonPublic)!;

        // The outer task is the execution whose last join waits for the deepest execution: the child.
        var outer = executions.Keys.Cast<string>()
                              .Select(execution => (Execution: execution, Joins: ((IEnumerable)joinsOf.Invoke(order, [execution])!).Cast<object>().ToArray()))
                              .Where(candidate => candidate.Joins.Length > 0)
                              .Select(candidate => (candidate.Execution, Late: candidate.Joins.OrderBy(join => PointOf(join).Operation).Last()))
                              .OrderByDescending(candidate => JoinedOf(candidate.Late).Length)
                              .First();
        var joined = JoinedOf(outer.Late);
        var from = afterTheGroup ? AfterTheGroup(order, PointOf(outer.Late))
                                 : (HappensBefore.PointKey?)joinedFrom.Invoke(order, [outer.Execution, joined]);
        var flow = (HappensBefore.Flow)flowOf.Invoke(order, [outer.Execution])!;

        Assert.NotNull(from);
        return flow.AlwaysFollows(from, [PointOf(outer.Late)], exceptionalExits);

        static HappensBefore.PointKey PointOf(object join) => (HappensBefore.PointKey)join.GetType().GetProperty("Point")!.GetValue(join)!;

        static string JoinedOf(object join) => ((IReadOnlyList<string>)join.GetType().GetProperty("Targets")!.GetValue(join)!).Single();
    }

    /// <summary>The point just after the <c>Task.WhenAll</c> call in the body of an instance.</summary>
    /// <param name="order">The happens-before graph.</param>
    /// <param name="inBody">A point of the instance whose body holds the call.</param>
    private static HappensBefore.PointKey AfterTheGroup(HappensBefore order, HappensBefore.PointKey inBody)
    {
        var scope = (ScopeProgram)typeof(HappensBefore).GetField("_scope", NonPublic)!.GetValue(order)!;
        var heap = (HeapSolution)typeof(HappensBefore).GetField("_heap", NonPublic)!.GetValue(order)!;
        var operations = scope.Reachable.Bodies[heap.Instances[inBody.Instance].BodyId].Blocks.SelectMany(block => block.Operations).ToArray();
        var group = operations.OfType<IrWhenAllOperation>().Single();

        return new HappensBefore.PointKey(inBody.Instance, operations.OfType<IrCallOperation>().Single(call => call.ResultValue == group.ResultValue).Id, true);
    }

    /// <summary>An earlier <c>await</c> of a task of its own, whose exception the task does not catch, is a path around the late join
    /// with exceptional exits and none without them: the late join stands on every normal path.</summary>
    /// <param name="row">A short name of the case; it comes first so that the display name of the row, which xUnit cuts after the first
    /// characters of a long argument, stays different from every other row's.</param>
    /// <param name="body">The outer task's body.</param>
    [Theory]
    [InlineData("failing await, then await inner", START + "await failing; await inner; return 1;")]
    [InlineData("failing await, then inner.Wait", START + "await failing; inner.Wait(); return 1;")]
    public void An_earlier_await_that_may_throw_is_a_path_around_the_late_join_on_exceptional_exits(string row, string body)
    {
        Assert.True(PassesTheLateJoin(body, exceptionalExits: false), row);
        Assert.False(PassesTheLateJoin(body, exceptionalExits: true), row);
    }

    /// <summary>The exception of an earlier <c>await</c> that the task catches before the late join, or that comes after the child
    /// completed because the <c>await</c> is a wait for the child itself, leaves nothing running around the late join.</summary>
    /// <param name="row">A short name of the case; it comes first so that the display name of the row, which xUnit cuts after the first
    /// characters of a long argument, stays different from every other row's.</param>
    /// <param name="body">The outer task's body.</param>
    [Theory]
    [InlineData("caught await, then await inner", START + "try { await failing; } catch (Exception) { } await inner; return 1;")]
    [InlineData("caught await, then inner.Wait", START + "try { await failing; } catch (Exception) { } inner.Wait(); return 1;")]
    [InlineData("await inner, then inner.Wait", START + "await inner; inner.Wait(); return 1;")]
    [InlineData("await inner twice", START + "await inner; await inner; return 1;")]
    [InlineData("await inner alone", "var inner = Task.Run(() => { First(); return 1; }); await inner; return 1;")]
    public void An_earlier_await_whose_exception_is_caught_or_comes_after_the_child_is_no_path_around_the_late_join(string row, string body)
    {
        Assert.True(PassesTheLateJoin(body, exceptionalExits: false), row);
        Assert.True(PassesTheLateJoin(body, exceptionalExits: true), row);
    }

    /// <summary>The <c>await</c> of a group listing the child is the only step between the <c>Task.WhenAll</c> call and the late join,
    /// and its exception comes after the child completed as well as after the group's other task: the gates of the exit are those of
    /// every wait for no more than the group waits for, and the late join, a wait for the child alone, holds them closed. An exit gated
    /// by the group's own <c>await</c> alone would take the exception around the late join.</summary>
    /// <param name="row">A short name of the case; it comes first so that the display name of the row, which xUnit cuts after the first
    /// characters of a long argument, stays different from every other row's.</param>
    /// <param name="body">The outer task's body.</param>
    [Theory]
    [InlineData("group await, then await inner", GROUP + "await group; await inner; return 1;")]
    [InlineData("group await, then inner.Wait", GROUP + "await group; inner.Wait(); return 1;")]
    public void The_await_of_a_group_listing_the_child_is_no_path_around_the_late_join_from_after_the_group(string row, string body)
    {
        Assert.True(PassesTheLateJoin(body, exceptionalExits: false, afterTheGroup: true), row);
        Assert.True(PassesTheLateJoin(body, exceptionalExits: true, afterTheGroup: true), row);
    }
}
