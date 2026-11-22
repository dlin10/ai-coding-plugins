using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What the pairing checkpoints report of the timing: the counters of a happens-before graph and of the replays it builds for
/// the assumptions of an ordered pair (R5), which the analysis never reads.</summary>
public sealed class TimingCountersTests
{
    /// <summary>A pair ordered only through an exceptional wait read as completion asks its assumptions on replays of the graph: the graph
    /// and its replays share one set of counters, which counts the order questions, the path searches and their events, the replays and
    /// the flows they build, and a copy taken before a pairing reports only what that pairing did.</summary>
    [Fact]
    public void Graph_and_its_replays_count_the_timing_of_an_ordered_pair()
    {
        var run = Analyze(Startup("services.AddHostedService<Worker>();") + """

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
            """);

        Assert.True(run.Skipped(InterproceduralPairing.SKIP_ORDERED) > 0);
        Assert.NotEmpty(run.Pairs.Assumed);
        var counters = Assert.IsType<TimingCounters>(run.Execution.Analysis.TimingCounters);
        Assert.True(counters.OrderQueries > 0);
        Assert.True(counters.PathSearches > 0);
        Assert.True(counters.PathEvents >= counters.PathSearches);
        Assert.True(counters.AssumptionQueries > 0);
        Assert.True(counters.Replays > 0);
        Assert.True(counters.Flows > 0);
        Assert.True(counters.FlowNodes >= counters.Flows);
        Assert.True(counters.ReachSets > 0);
        // The hosted service's reach holds at least the work it starts.
        Assert.True(counters.ReachExecutions > 0);

        var before = counters.Copy();
        InterproceduralPairing.Pair(run.Collection.Accesses, run.Execution.Analysis, run.Execution.Heap.Heap);
        var again = counters.Since(before);
        // A second pairing on the same graph builds no replay and no flow: everything it asks for was built by the first.
        Assert.Contains(" replays=0 ", again, StringComparison.Ordinal);
        Assert.Contains(" flows=0 ", again, StringComparison.Ordinal);
        Assert.Contains($"replaysTotal={counters.Replays} ", again, StringComparison.Ordinal);
        Assert.DoesNotContain("orderQueries=0 ", again, StringComparison.Ordinal);
    }
}
