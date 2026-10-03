using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class HeapFreezeTests
{
    // The solve evaluates nothing the lock is taken on, so it makes no region of the fresh object; the accesses stage resolves it
    // later as the object of the lock (open question 110).
    private static readonly string FreshLock = """
        public sealed class State { public int Value; }
        public class LockController(State state) : ControllerBase
        {
            public void Post() { lock (new object()) { state.Value = 1; } }
        }
        """ + Startup("services.AddSingleton<State>();");

    [Fact]
    public void Resolve_after_the_solve_names_a_region_without_adding_it()
    {
        var run = Solve(FreshLock);
        var post = Assert.Single(run.Instances("body:Fixture:M:LockController.Post"));
        var gate = Assert.Single(post.Summary.Locks.SelectMany(transfer => transfer.Values).OfType<AllocationValue>().Distinct());
        var solved = run.Heap.Regions.Keys.ToArray();
        var counters = run.Heap.Counters.ToArray();

        var named = Assert.Single(run.Heap.Resolve(post.Id, gate));

        Assert.Equal(solved, run.Heap.Regions.Keys);
        Assert.True(run.Heap.Regions.ContainsKey(named));
        Assert.Equal(HeapRegionKind.Allocation, run.Heap.Regions[named].Kind);
        Assert.Empty(run.Heap.FieldsOf(named));
        Assert.Equal([named], run.Heap.Resolve(post.Id, gate));
        Assert.Equal(counters, run.Heap.Counters);
    }

    [Fact]
    public void Stages_after_the_solve_leave_the_heap_as_the_solve_made_it()
    {
        var run = Solve(FreshLock);
        var solved = run.Heap.Regions.Keys.ToArray();

        var first = Execute(run);
        Collect(first);
        var second = Execute(run);

        Assert.Equal(solved, run.Heap.Regions.Keys);
        Assert.Equal(first.Analysis.Ownership.Keys.Order(StringComparer.Ordinal), second.Analysis.Ownership.Keys.Order(StringComparer.Ordinal));
    }
}
