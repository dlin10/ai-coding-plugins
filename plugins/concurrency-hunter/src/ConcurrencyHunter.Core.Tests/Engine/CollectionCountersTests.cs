using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Heap;
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
            SkippedVisits = 11, SkippedWhole = 12, WalkMs = 13, LocksMs = 14, DependenciesMs = 15, EmitMs = 16, OtherInstanceHits = 17,
            ConstructionCopies = 18, Firsts = 19, FirstsFromConstructions = 20, NewInstance = 21, NewRegion = 22, NewSite = 23,
            NewOperation = 24, NewGuards = 25, NewHeld = 26, NewLocal = 27, NewOther = 28, NewRootScoped = 29, ModelAccesses = 30,
            TailFrameWork = 31
        };

        Assert.Equal("entries=1 visits=2 states=3 rows=4 keys=5 hits=6 emitted=7 replaced=8 skipped=9 skippedKeys=10 " +
                     "skippedVisits=11 skippedWhole=12 walkMs=13 locksMs=14 dependenciesMs=15 emitMs=16 otherInstanceHits=17 " +
                     "constructionCopies=18 firsts=19 firstsFromConstructions=20 newInstance=21 newRegion=22 newSite=23 newOperation=24 " +
                     "newGuards=25 newHeld=26 newLocal=27 newOther=28 newRootScoped=29 modelAccesses=30 tailFrameWork=31", counters.Line());
    }

    /// <summary>The census of ADR 0042: every folded access is one execution's first, under one reason; a copy made on a visit from a
    /// construction entry is counted as such, and so is a first among them; the model's accesses are the execution's.</summary>
    [Fact]
    public void The_fold_counts_every_folded_access_once_as_the_first_of_one_execution_with_one_reason()
    {
        // The root's walk makes more copies than the constructions' walks, so the two are not counted alike by chance.
        var (collection, counted) = Collect("_state.Touch(); _ = _state.Value;");

        Assert.Equal(collection.Folded.Count, counted.Sum(item => item.Counters.Firsts));
        Assert.All(counted, item =>
        {
            var counters = item.Counters;
            Assert.Equal(counters.Firsts, counters.NewInstance + counters.NewRegion + counters.NewSite + counters.NewOperation +
                                          counters.NewGuards + counters.NewHeld + counters.NewLocal + counters.NewOther);
            Assert.InRange(counters.FirstsFromConstructions, 0, Math.Min(counters.Firsts, counters.ConstructionCopies));
            Assert.InRange(counters.NewRootScoped, 0, counters.Firsts);
        });
        // A copy whose code flow starts at its execution's construction entry, and no other, is a construction copy.
        var fromConstructions = collection.Accesses.Count(access => access.CodeFlow is [_, { Kind: "construction" } step, ..] &&
                                                                     !step.Text.Contains(" in ", StringComparison.Ordinal));
        Assert.True(fromConstructions > 0);
        Assert.NotEqual(fromConstructions, collection.Accesses.Count - fromConstructions);
        Assert.Equal(fromConstructions, counted.Sum(item => item.Counters.ConstructionCopies));
        Assert.Contains(counted, item => item.Counters.FirstsFromConstructions > 0);
        var worker = Assert.Single(counted, item => item.Execution.Contains("FirstWorker", StringComparison.Ordinal) && item.Counters.Emitted != 0).Counters;
        Assert.True(worker.ModelAccesses > 0);
    }

    /// <summary>The emission's key names the body, not the instance: the second instance of a generic body writing the field the first
    /// wrote finds the key taken, and that lookup is counted.</summary>
    [Fact]
    public void A_key_another_instance_of_the_body_holds_is_an_other_instance_hit()
    {
        var (collection, counted) = CollectSource("""
            public static class Counter { public static int Value; }
            public static class Bumps { public static void Bump<T>() => Counter.Value = 1; }
            public sealed class FirstWorker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Bumps.Bump<int>();
                    Bumps.Bump<string>();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<FirstWorker>();"));

        var worker = Assert.Single(counted, item => item.Execution.Contains("FirstWorker", StringComparison.Ordinal) && item.Counters.Emitted != 0);
        Assert.Equal(1, worker.Counters.OtherInstanceHits);
        Assert.Single(collection.Accesses, access => access.ExecutionId == worker.Execution && access.Symbol.Contains("Bump", StringComparison.Ordinal));
    }

    /// <summary>A scoped service resolved for a request is one object per invocation of its root: an access to it is new on a region of
    /// that invocation.</summary>
    [Fact]
    public void An_access_to_a_scoped_service_of_a_request_is_new_on_a_region_of_the_invocation()
    {
        var (collection, counted) = CollectSource("""
            public sealed class Counter { public int Value; }
            public class GateController(Counter counter) : ControllerBase
            {
                public void Get() { counter.Value = 2; }
            }
            """ + Startup("services.AddScoped<Counter>();"));

        var scoped = collection.Folded.Count(access => (access.Resource.CollectionId ?? access.Resource.RegionId ?? access.Resource.Region)
                                                           .Contains(WholeProgram.INVOCATION_SCOPE, StringComparison.Ordinal));
        Assert.True(scoped > 0);
        Assert.Equal(scoped, counted.Sum(item => item.Counters.NewRootScoped));
    }

    /// <summary>Every counter of every execution but the times is the same whether one worker collects the demo's scopes or four: the
    /// census counts in the fold, in collection order, and a collector counts its own execution alone.</summary>
    [Fact]
    public async Task Every_counter_but_the_times_is_the_same_at_one_worker_and_at_four()
    {
        var executions = 0;
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var one = Untimed(input with { Workers = 1 });
            Assert.Equal(one, Untimed(input with { Workers = 4 }));
            executions += one.Count;
        });

        Assert.True(executions > 1);
    }

    /// <summary>Each walked execution with its counters but the milliseconds, in the fold's order.</summary>
    /// <param name="input">The collection's input.</param>
    private static List<string> Untimed(InterproceduralInput input)
    {
        var lines = new List<string>();
        InterproceduralAccesses.Collect(input with { Counted = (id, counters) => lines.Add($"{id} {TIMES.Replace(counters.Line(), string.Empty)}") },
                                        CancellationToken.None);
        return lines;
    }

    /// <summary>The fields of the <c>execution:</c> line that are times.</summary>
    private static readonly System.Text.RegularExpressions.Regex TIMES = new(@"\w+Ms=\d+ ?");

    /// <summary>The census reads what the collection makes and writes only counters: the demo's scopes collected with it and without it
    /// give the same accesses, field for field and pooled object for pooled object.</summary>
    [Fact]
    public async Task Demo_accesses_are_the_same_with_the_census_and_without_it()
    {
        long firsts = 0;
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var expected = RepeatedGuardsTests.Describe(InterproceduralAccesses.Collect(input with { Counted = null }, CancellationToken.None));
            Assert.NotEmpty(expected);
            var counted = InterproceduralAccesses.Collect(input with { Counted = (_, counters) => firsts += counters.Firsts }, CancellationToken.None);
            Assert.Equal(expected, RepeatedGuardsTests.Describe(counted));
        });

        Assert.True(firsts > 0);
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
        // Both paths reach one instance of the helper: no hit is another instance's.
        Assert.Equal(0, repeating.OtherInstanceHits);
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

    /// <summary>A tail bound through four async callers, each calling the next from 24 sites, the first started and not awaited, builds
    /// each caller's frames once for the collection of its execution: the frames built grow with the sites, not with the product of
    /// the sites of every level, and the tail's write through the parameter is still collected.</summary>
    [Fact]
    public void A_tail_bound_through_a_chain_of_async_callers_builds_frames_in_proportion_to_their_call_sites()
    {
        const int LEVELS = 4;
        const int SITES = 24;
        var levels = string.Concat(Enumerable.Range(0, LEVELS).Select(level =>
            $"public async Task L{level}(int[] p) {{ switch (Pick) {{ " +
            string.Concat(Enumerable.Range(0, SITES).Select(site => $"case {site}: await {(level == LEVELS - 1 ? "Fill" : $"L{level + 1}")}(p); break; ")) +
            "} } "));
        var (collection, counted) = CollectSource($$"""
            public sealed class Chain
            {
                public int Pick;
                public readonly int[] Cells = new int[1];
                {{levels}}
                private static async Task Fill(int[] p) { await Task.Yield(); p[0] = 1; }
            }

            public sealed class FirstWorker : BackgroundService
            {
                private readonly Chain _chain;
                public FirstWorker(Chain chain) => _chain = chain;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _ = _chain.L0(_chain.Cells);
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Chain>(); services.AddHostedService<FirstWorker>();"));

        var frames = counted.Sum(item => item.Counters.TailFrameWork);
        Assert.InRange(frames, LEVELS, LEVELS * SITES * 2);
        // Each place a reference resolves to is one row, not one for each chain of sites that hands it over.
        Assert.InRange(counted.Sum(item => item.Counters.Rows), 1, LEVELS * SITES * 2);
        Assert.Contains(collection.Accesses, access => access.Symbol.Contains("Fill", StringComparison.Ordinal) &&
                                                       access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
    }

    /// <summary>A tail bound through ten async callers, each handing the next one of two arrays on two branches, builds frames in
    /// proportion to the callers: a frame stands for every call that started its body, so the bindings of the levels above do not
    /// multiply, and the tail's write is still collected on both arrays.</summary>
    [Fact]
    public void A_tail_bound_through_callers_that_alternate_two_arrays_builds_frames_in_proportion_to_the_callers()
    {
        const int LEVELS = 10;
        var (collection, counted) = CollectSource($$"""
            public sealed class Chain
            {
                public bool Other;
                private readonly int[] _other = new int[1];
                private readonly int[] _cells = new int[1];
                {{AsyncTailArrayParameterProbeTests.Alternating(LEVELS)}}
                public Task Start() => L0(_other);
                private static async Task FillAsync(int[] p) { await Task.Yield(); p[0] = 1; }
            }

            public sealed class FirstWorker : BackgroundService
            {
                private readonly Chain _chain;
                public FirstWorker(Chain chain) => _chain = chain;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _ = _chain.Start();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Chain>(); services.AddHostedService<FirstWorker>();"));

        Assert.InRange(counted.Sum(item => item.Counters.TailFrameWork), LEVELS, LEVELS * 4);
        var writes = collection.Accesses.Where(access => access.Symbol.Contains("FillAsync", StringComparison.Ordinal) &&
                                                         access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write)
                                        .Select(access => access.Resource.AccessPath[0])
                                        .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["_cells", "_other"], writes.Order(StringComparer.Ordinal));
    }

    /// <summary>A tail bound through six async callers, each calling the next from eight sites, the last calling the tail's body or the
    /// first caller again with the window cut one cell further, builds frames in proportion to the sites: within the chain that comes
    /// back to the first caller each reference is worked out once, not once for each way into it, and the tail's write is still
    /// collected.</summary>
    [Fact]
    public void A_tail_bound_through_callers_that_call_the_first_again_with_a_cut_window_builds_frames_in_proportion_to_their_call_sites()
    {
        const int LEVELS = 6;
        const int SITES = 8;
        var levels = string.Concat(Enumerable.Range(0, LEVELS).Select(level =>
            $"public async Task L{level}(Memory<int> p) {{ switch (Pick) {{ " +
            string.Concat(Enumerable.Range(0, SITES).Select(site =>
                $"case {site}: {(level < LEVELS - 1 ? $"await L{level + 1}(p);" : site % 2 == 0 ? "await Fill(p);" : "await L0(p.Slice(1));")} break; ")) +
            "} } "));
        var (collection, counted) = CollectSource($$"""
            public sealed class Chain
            {
                public int Pick;
                public readonly int[] Cells = new int[64];
                {{levels}}
                private static async Task Fill(Memory<int> p) { await Task.Yield(); p.Span[0] = 1; }
            }

            public sealed class FirstWorker : BackgroundService
            {
                private readonly Chain _chain;
                public FirstWorker(Chain chain) => _chain = chain;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _ = _chain.L0(_chain.Cells);
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Chain>(); services.AddHostedService<FirstWorker>();"));

        Assert.InRange(counted.Sum(item => item.Counters.TailFrameWork), LEVELS, LEVELS * SITES * 8);
        Assert.Contains(collection.Accesses, access => access.Symbol.Contains("Fill", StringComparison.Ordinal) &&
                                                       access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
    }

    /// <summary>A tail bound through twelve async callers, each handing the next its window cut as <c>Slice(0)</c> on one branch and
    /// <c>Slice(0, 1)</c> on the other, whose tail calls a recursive helper that writes the window's first cell, names places in
    /// proportion to the callers: two chains of slices that cut the same cells name one place, so the bindings the helper's recursion
    /// compares do not double at every level, and the helper's write is still collected.</summary>
    [Fact]
    public void A_tail_bound_through_callers_that_cut_one_window_two_ways_names_places_in_proportion_to_the_callers()
    {
        const int LEVELS = 12;
        var levels = string.Concat(Enumerable.Range(0, LEVELS).Select(level =>
        {
            var next = level == LEVELS - 1 ? "Fill" : $"L{level + 1}";
            return $"public async Task L{level}(Memory<int> p) {{ if (Pick) await {next}(p.Slice(0)); else await {next}(p.Slice(0, 1)); }} ";
        }));
        var (collection, counted) = CollectSource($$"""
            public sealed class Chain
            {
                public bool Pick;
                public readonly int[] Cells = new int[1];
                {{levels}}
                private async Task Fill(Memory<int> p) { await Task.Yield(); Loop(p, 1); }
                private void Loop(Memory<int> p, int n) { if (n > 0) Loop(p, n - 1); else p.Span[0] = 1; }
            }

            public sealed class FirstWorker : BackgroundService
            {
                private readonly Chain _chain;
                public FirstWorker(Chain chain) => _chain = chain;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _ = _chain.L0(_chain.Cells.AsMemory());
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Chain>(); services.AddHostedService<FirstWorker>();"));

        Assert.InRange(counted.Sum(item => item.Counters.TailFrameWork), LEVELS, LEVELS * 16);
        Assert.Contains(collection.Accesses, access => access.Symbol.Contains("Loop", StringComparison.Ordinal) &&
                                                       access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
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

    /// <summary>Collects a case's scope, with the counters of each walked execution in the fold's order.</summary>
    /// <param name="source">The case's source.</param>
    private static (InterproceduralCollection Collection, List<(string Execution, CollectionCounters Counters)> Counted) CollectSource(string source)
    {
        var counted = new List<(string Execution, CollectionCounters Counters)>();
        var input = Input(Execute(source)) with { Counted = (id, counters) => counted.Add((id, counters)) };
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
