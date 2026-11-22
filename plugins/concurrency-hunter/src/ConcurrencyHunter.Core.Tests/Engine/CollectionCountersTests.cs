using ConcurrencyHunter.Accesses;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What the collection of an execution counts for the <c>execution:</c> checkpoint of a diagnostic run (ADR 0041).</summary>
public sealed class CollectionCountersTests
{
    [Fact]
    public void The_execution_line_names_every_counter_in_one_order()
    {
        var counters = new CollectionCounters
        {
            Entries = 1, Visits = 2, States = 3, Rows = 4, Keys = 5, Hits = 6, Emitted = 7, Replaced = 8, Skipped = 9, SkippedKeys = 10,
            SkippedVisits = 11, SkippedWhole = 12, WalkMs = 13, LocksMs = 14, DependenciesMs = 15, EmitMs = 16
        };

        Assert.Equal("entries=1 visits=2 states=3 rows=4 keys=5 hits=6 emitted=7 replaced=8 skipped=9 skippedKeys=10 " +
                     "skippedVisits=11 skippedWhole=12 walkMs=13 locksMs=14 dependenciesMs=15 emitMs=16", counters.Line());
    }

    [Fact]
    public void A_collection_counts_its_visits_lookups_and_accesses()
    {
        var (collection, counted) = Collect("_state.Touch();");

        Assert.NotEmpty(counted);
        Assert.All(counted, item =>
        {
            var (execution, counters) = item;
            Assert.Equal(counters.Keys, counters.Hits + counters.Emitted);
            Assert.Equal(collection.Accesses.Count(access => access.ExecutionId == execution), counters.Emitted - counters.Replaced);
            Assert.InRange(counters.States, 0, counters.Visits);
            Assert.InRange(counters.Keys, 0, long.MaxValue);
        });
        var worker = Assert.Single(counted, item => item.Execution.Contains("FirstWorker", StringComparison.Ordinal) && item.Counters.Emitted != 0).Counters;
        Assert.True(worker.Entries >= 1);
        // The worker's body and the helper it calls: two states, each on one path.
        Assert.True(worker.Visits >= 2);
        Assert.Equal(worker.Visits, worker.States);
        Assert.True(worker.Rows >= 1);
        Assert.Equal(0, worker.Hits);
        Assert.Equal(0, worker.Skipped);
    }

    /// <summary>A second path to the helper under the first one's guards and one more finds every key of its access taken. The rule of
    /// ADR 0041 skips the access there, and counts the lookups it left out: the hits of a collection without the rule.</summary>
    [Fact]
    public void A_path_under_more_guards_counts_as_skipped_the_lookups_that_were_hits_without_the_rule()
    {
        const string twice = "_state.Touch(); if (_state.Shared < 4) _state.Touch();";
        var worker = Assert.Single(Collect(twice).Counted, item => item.Execution.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                                   item.Counters.Emitted != 0).Counters;
        var repeating = Assert.Single(Collect(twice, repeats: true).Counted,
                                      item => item.Execution.Contains("FirstWorker", StringComparison.Ordinal) && item.Counters.Emitted != 0).Counters;

        Assert.True(worker.Visits > worker.States);
        Assert.Equal(1, worker.Skipped);
        Assert.Equal(1, worker.SkippedVisits);
        Assert.Equal(1, worker.SkippedWhole);
        Assert.Equal(0, worker.Hits);
        Assert.Equal(0, repeating.Skipped);
        Assert.True(repeating.Hits >= 1);
        Assert.Equal(repeating.Hits, worker.SkippedKeys);
        Assert.Equal(repeating.Keys, worker.Keys + worker.SkippedKeys);
        Assert.Equal(repeating.Emitted, worker.Emitted);
        Assert.Equal(0, worker.Replaced);
    }

    /// <summary>A later path under fewer guards makes the access again and replaces the first, and the rule does not skip it.</summary>
    [Fact]
    public void A_path_under_fewer_guards_counts_a_replaced_access()
    {
        var (_, counted) = Collect("if (_state.Shared < 4) _state.Touch(); _state.Touch();");

        var worker = Assert.Single(counted, item => item.Execution.Contains("FirstWorker", StringComparison.Ordinal) && item.Counters.Emitted != 0).Counters;
        Assert.Equal(1, worker.Replaced);
        Assert.Equal(0, worker.Skipped);
    }

    /// <summary>Collects the case's scope, with the counters of each walked execution in the fold's order.</summary>
    /// <param name="first">The statements of the first worker.</param>
    /// <param name="repeats">Whether the collection goes without the rule of ADR 0041.</param>
    private static (InterproceduralCollection Collection, List<(string Execution, CollectionCounters Counters)> Counted) Collect(string first,
                                                                                                                              bool repeats = false)
    {
        var counted = new List<(string Execution, CollectionCounters Counters)>();
        var input = Input(Execute(Source(first))) with { Counted = (id, counters) => counted.Add((id, counters)), EmitsUnderRepeatedGuards = repeats };
        return (InterproceduralAccesses.Collect(input, CancellationToken.None), counted);
    }

    private static string Source(string first) => $$"""
        public sealed class GuardState
        {
            public readonly int Shared = 5;
            public int Value;
            public void Touch() => Value = 1;
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly GuardState _state;
            public FirstWorker(GuardState state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<GuardState>(); services.AddHostedService<FirstWorker>();");
}
