using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class EngineCounterTests
{
    [Fact]
    public void Passes_and_processings_are_counted()
    {
        var run = Solve("""
            public class CounterController : ControllerBase { public void Post() { } }
            """ + Startup());

        var passes = run.Heap.Counters[HeapCounters.PROPAGATE_PASSES];
        Assert.True(passes > 0);
        Assert.InRange(run.Heap.Counters[HeapCounters.INSTANCE_PROCESSINGS], run.Heap.Instances.Count, passes * run.Heap.Instances.Count);
        Assert.Equal(0, run.Heap.Counters[HeapCounters.REFERENCE_LOOKUPS]);
    }

    [Fact]
    public void Heap_ref_parameter_chain_of_nine_is_found()
    {
        var run = Solve(ParameterChain(9, "var o = slot; o.Touch();"));

        Assert.True(run.Heap.Counters[HeapCounters.REFERENCE_LOOKUPS] > 0);
        var touch = Assert.Single(run.Instances("body:Fixture:M:Foo.Touch"));
        Assert.False(touch.IsReceiverless);
        Assert.NotEmpty(touch.Receivers);
    }

    [Fact]
    public void Heap_ref_parameter_chain_of_three_is_not_cut()
    {
        var run = Solve(ParameterChain(3, "var o = slot; o.Touch();"));

        Assert.True(run.Heap.Counters[HeapCounters.REFERENCE_LOOKUPS] > 0);
        var touch = Assert.Single(run.Instances("body:Fixture:M:Foo.Touch"));
        Assert.False(touch.IsReceiverless);
        Assert.NotEmpty(touch.Receivers);
    }

    [Fact]
    public void Heap_ref_return_chain_of_nine_is_found()
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(1, 9).Select(index => index == 9
            ? "public static ref Foo Step9(Holder holder) => ref holder.Slot;"
            : $"public static ref Foo Step{index}(Holder holder) => ref Step{index + 1}(holder);"));
        var run = Solve("""
            public sealed class Foo { public int Value; public void Touch() => Value = 1; }
            public sealed class Holder { public Foo Slot = new(); }
            """ + $"public static class Chain {{ {methods} }}" + """
            public class CounterController(Holder holder) : ControllerBase
            {
                public void Post() { var o = Chain.Step1(holder); o.Touch(); }
            }
            """ + Startup("services.AddSingleton<Holder>();"));

        Assert.True(run.Heap.Counters[HeapCounters.REFERENCE_LOOKUPS] > 0);
        var touch = Assert.Single(run.Instances("body:Fixture:M:Foo.Touch"));
        Assert.False(touch.IsReceiverless);
        Assert.NotEmpty(touch.Receivers);
    }

    [Fact]
    public void Access_ref_parameter_chain_of_nine_is_found()
    {
        var one = Collect(ParameterChain(9, "slot = new Foo();"));
        var two = Collect(ParameterChain(9, "slot = new Foo();", roots: 2));
        var shortChain = Collect(ParameterChain(3, "slot = new Foo();"));

        Assert.Contains(one.Accesses, access => access.Resource.Member.Name == "Slot" &&
                                                      access.Root.Symbol.Contains("Post", StringComparison.Ordinal));
        Assert.Contains(two.Accesses, access => access.Resource.Member.Name == "Slot" &&
                                                     access.Root.Symbol.Contains("Post", StringComparison.Ordinal));
        Assert.Contains(shortChain.Accesses, access => access.Resource.Member.Name == "Slot" &&
                                                       access.Root.Symbol.Contains("Post", StringComparison.Ordinal));
    }

    [Fact]
    public void Walk_visits_are_counted()
    {
        var run = Execute("""
            public static class Calls
            {
                public static void Left() => Join();
                public static void Right() => Join();
                public static void Join() { }
            }
            public class CounterController : ControllerBase
            {
                public void Post() { Calls.Left(); Calls.Right(); Calls.Left(); }
            }
            """ + Startup());

        Assert.True(run.Analysis.WalkVisits > 0);
        Assert.Equal(run.Analysis.InstanceExecutions.Values.Sum(executions => executions.Count), run.Analysis.WalkVisits);
    }

    [Fact]
    public void Engine_counters_reach_no_coverage_key()
    {
        var run = Analyze(ParameterChain(9, "var o = slot; o.Touch();"));
        string[] engineKeys = [HeapCounters.PROPAGATE_PASSES, HeapCounters.INSTANCE_PROCESSINGS, HeapCounters.REFERENCE_LOOKUPS, "walkVisits"];

        Assert.True(run.Execution.Heap.Heap.Counters[HeapCounters.PROPAGATE_PASSES] > 0);
        Assert.True(run.Execution.Heap.Heap.Counters[HeapCounters.INSTANCE_PROCESSINGS] > 0);
        Assert.True(run.Execution.Heap.Heap.Counters[HeapCounters.REFERENCE_LOOKUPS] > 0);
        Assert.True(run.Execution.Analysis.WalkVisits > 0);
        foreach (var key in engineKeys)
        {
            Assert.DoesNotContain(key, run.Collection.Coverage.Counters.Keys);
            Assert.DoesNotContain(key, run.Collection.Coverage.Ordering.Keys);
            Assert.DoesNotContain(key, run.Execution.Analysis.Counters.Keys);
        }
    }

    private static string ParameterChain(int length, string lastBody, int roots = 1)
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(1, length).Select(index => index == length
            ? $"public static void Step{index}(ref Foo slot) {{ {lastBody} }}"
            : $"public static void Step{index}(ref Foo slot) => Step{index + 1}(ref slot);"));
        var entries = string.Join(Environment.NewLine, Enumerable.Range(0, roots).Select(index =>
            $"public void Post{index}() => Chain.Step1(ref holder.Slot);"));
        return """
            public sealed class Foo { public int Value; public void Touch() => Value = 1; }
            public sealed class Holder { public Foo Slot = new(); }
            """ + $"public static class Chain {{ {methods} }}" +
            $"public class CounterController(Holder holder) : ControllerBase {{ {entries} }}" +
            Startup("services.AddSingleton<Holder>();");
    }
}
