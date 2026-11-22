using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A visit emits an access only under guards the execution has not emitted it under (ADR 0041). The rule answers ahead a check
/// the emission makes anyway, for all the path-free accesses of a visit at once before it lists them, so a collection with it gives
/// what a collection without it gives: the same accesses in the same order, field for field and pooled object for pooled object. One
/// case stands for each way a visit's path reaches the emission besides the guards, where the rule must leave the access alone.</summary>
public sealed class RepeatedGuardsTests
{
    [Fact]
    public async Task Demo_accesses_are_the_same_with_the_rule_and_without_it()
    {
        long skipped = 0;
        long skippedVisits = 0;
        long skippedWithout = 0;
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var expected = Describe(InterproceduralAccesses.Collect(input with
            {
                EmitsUnderRepeatedGuards = true,
                Counted = (_, counters) => skippedWithout += counters.Skipped + counters.SkippedVisits
            }, CancellationToken.None));
            Assert.NotEmpty(expected);
            Assert.Equal(expected, Describe(InterproceduralAccesses.Collect(input with
            {
                Counted = (_, counters) =>
                {
                    skipped += counters.Skipped;
                    skippedVisits += counters.SkippedVisits;
                }
            }, CancellationToken.None)));
        });

        Assert.Equal(0, skippedWithout);
        Assert.True(skipped > 0);
        Assert.True(skippedVisits > 0);
    }

    /// <summary>A second visit of a state under the same guards skips its path-free accesses together, and where it has nothing
    /// else to emit it lists nothing at all.</summary>
    [Fact]
    public void A_second_visit_under_the_same_guards_is_skipped_whole()
    {
        var run = Compare(Guarded("_state.Touch(); _state.Touch();"));

        Assert.Single(run.Collection.Accesses, access => access.Symbol.Contains("Touch", StringComparison.Ordinal));
        Assert.Equal(1, run.Worker.SkippedVisits);
        Assert.Equal(1, run.Worker.SkippedWhole);
        Assert.Equal(1, run.Worker.Skipped);
        Assert.Equal(0, run.Worker.Hits);
    }

    /// <summary>Two accesses of one block run under one list of own guards. The one with a term is bound along the path, so the
    /// second visit emits it although it skips the other.</summary>
    [Fact]
    public void An_access_with_a_term_is_emitted_where_the_rest_of_its_block_is_skipped()
    {
        var run = Compare(Guarded("_state.Mark(1); _state.Mark(2);"));

        var marked = run.Collection.Accesses.Where(access => access.Symbol.Contains("Mark", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, marked.Where(access => access.Resource.Selector is not null)
                              .Select(access => access.Resource.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(marked, access => access.Resource.Member.Name == "Value");
        Assert.Equal(1, run.Worker.SkippedVisits);
        Assert.Equal(0, run.Worker.SkippedWhole);
    }

    /// <summary>An access's own guards are bound along the path: two paths that bind them to two values run under two sets of
    /// guards, neither including the other, and a third that binds them as the first did is skipped.</summary>
    [Fact]
    public void Own_guards_bound_to_two_values_on_two_paths_are_two_sets_of_guards()
    {
        var run = Compare(Guarded("_state.TouchIfLow(1); _state.TouchIfLow(9); _state.TouchIfLow(1);"));

        var touched = run.Collection.Accesses.Where(access => access.Symbol.Contains("TouchIfLow", StringComparison.Ordinal) &&
                                                              access.Resource.Member.Name == "Value").ToArray();
        Assert.Equal(2, touched.Length);
        Assert.All(touched, access => Assert.NotEmpty(access.Conditions));
        Assert.Equal(1, run.Worker.SkippedVisits);
    }

    /// <summary>A known call handed a collection ready reads the storage the path names for it: the cells of one field on one path
    /// and of another on the next, and nothing a field holds where the path hands it a new array. Its list is made on every visit.</summary>
    [Fact]
    public void A_collection_handed_over_ready_keeps_the_storage_each_path_names()
    {
        var run = Compare(Guarded("_state.Walk(_state.First); _state.Walk(new int[2]); _state.Walk(_state.Second);"));

        var walked = run.Collection.Accesses.Where(access => access.Symbol.Contains("Walk(", StringComparison.Ordinal))
                        .Select(access => access.Resource.AccessPath[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(["First", "Second"], walked);
        Assert.Equal(0, run.Worker.SkippedWhole);
    }

    /// <summary>A known call on a collection a field of its own body's object holds lists its effects without the path, so a second
    /// visit lists nothing.</summary>
    [Fact]
    public void A_known_call_on_a_collection_its_own_field_holds_is_listed_on_the_first_visit_alone()
    {
        var run = Compare(Guarded("_state.WalkFirst(); _state.WalkFirst();"));

        Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("WalkFirst", StringComparison.Ordinal) &&
                                                           access.Resource.AccessPath[0] == "First" && access.Resource.Selector is not null);
        Assert.Equal(1, run.Worker.SkippedWhole);
        Assert.Equal(0, run.Worker.Hits);
    }

    [Fact]
    public void A_path_under_more_guards_is_skipped()
    {
        var run = Compare(Guarded("_state.Touch(); if (_state.Shared < 4) _state.Touch();"));

        var touch = Assert.Single(run.Collection.Accesses, access => access.Symbol.Contains("Touch", StringComparison.Ordinal));
        Assert.Empty(touch.Conditions);
        Assert.Equal(1, run.Worker.Skipped);
        Assert.Equal(1, run.Worker.SkippedKeys);
        Assert.Equal(0, run.Worker.Replaced);
    }

    [Fact]
    public void A_path_under_fewer_guards_after_one_under_more_is_not_skipped_and_takes_its_place()
    {
        var run = Compare(Guarded("if (_state.Shared < 4) _state.Touch(); _state.Touch();"));

        var touch = Assert.Single(run.Collection.Accesses, access => access.Symbol.Contains("Touch", StringComparison.Ordinal));
        Assert.Empty(touch.Conditions);
        Assert.Equal(0, run.Worker.Skipped);
        Assert.Equal(1, run.Worker.Replaced);
    }

    [Fact]
    public void A_path_under_other_guards_is_not_skipped()
    {
        var run = Compare(Guarded("if (_state.Shared < 4) _state.Touch(); if (_state.Shared >= 4) _state.Touch();"));

        Assert.Equal(2, run.Collection.Accesses.Count(access => access.Symbol.Contains("Touch", StringComparison.Ordinal)));
        Assert.Equal(0, run.Worker.Skipped);
    }

    /// <summary>The term of an access is bound along the path, so two paths under the same guards name two cells. The read of the
    /// field holding the array has no term, and the second path skips it.</summary>
    [Fact]
    public void A_term_bound_to_two_values_on_two_paths_keeps_both_cells()
    {
        var run = Compare(Guarded("_state.Write(1); _state.Write(2);"));

        var cells = run.Collection.Accesses.Where(access => access.Symbol.Contains("Write", StringComparison.Ordinal) &&
                                                            access.Resource.Selector is not null).ToArray();
        Assert.Equal(2, cells.Select(access => access.Resource.Identity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, run.Worker.Skipped);
    }

    /// <summary>A reference resolves where its caller stands, so two paths under the same guards reach two places.</summary>
    [Fact]
    public void A_reference_resolved_to_two_places_on_two_paths_keeps_both_places()
    {
        var run = Compare(Guarded("_state.Bump(ref _state.Left); _state.Bump(ref _state.Right);"));

        var written = run.Collection.Accesses.Where(access => access.Symbol.Contains("Bump", StringComparison.Ordinal))
                         .Select(access => access.Resource.Member.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(["Left", "Right"], written);
        Assert.Equal(0, run.Worker.Skipped);
    }

    /// <summary>An explicit enumeration runs the iterator's body under what its caller holds at that call: the body's access is held
    /// on the path of <c>MoveNext</c> and not on the path of <c>Dispose</c>, under the same guards.</summary>
    [Fact]
    public void A_lock_of_an_enumeration_on_one_path_keeps_the_access_of_the_other()
    {
        var run = Compare("""
            using System.Collections.Generic;
            public sealed class State
            {
                public int Value;
                public readonly object Gate = new();
                public IEnumerable<int> Walk() { try { yield return 1; } finally { Value++; } }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var e = _state.Walk().GetEnumerator(); lock (_state.Gate) e.MoveNext(); e.Dispose();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));

        var walked = run.Collection.Accesses.Where(access => access.Symbol == "State.Walk()" && access.Resource.Member.Name == "Value").ToArray();
        Assert.Contains(walked, access => access.HeldProtection.Count != 0);
        Assert.Contains(walked, access => access.HeldProtection.Count == 0);
        Assert.Equal(0, run.Worker.Skipped);
    }

    /// <summary>The body of an iterator a <c>foreach</c> runs holds nothing; an explicit enumeration of the same iterator under a lock
    /// runs it held, on another path under the same guards. Whichever path comes first, the other emits its access: a visit with
    /// the locks of an enumeration is neither skipped nor a reason to skip.</summary>
    /// <param name="enumerations">The two enumerations, in the order the worker makes them.</param>
    [Theory]
    [InlineData("foreach (var value in sequence) { } var e = sequence.GetEnumerator(); lock (_state.Gate) e.MoveNext();")]
    [InlineData("var e = sequence.GetEnumerator(); lock (_state.Gate) e.MoveNext(); foreach (var value in sequence) { }")]
    public void A_lock_of_an_enumeration_on_one_path_and_none_on_another_keep_both_accesses(string enumerations)
    {
        var run = Compare($$"""
            using System.Collections.Generic;
            public sealed class State
            {
                public int Value;
                public readonly object Gate = new();
                public IEnumerable<int> Walk() { Value++; yield return 1; }
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var sequence = _state.Walk();
                    {{enumerations}}
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));

        var walked = run.Collection.Accesses.Where(access => access.Symbol == "State.Walk()" && access.Resource.Member.Name == "Value").ToArray();
        Assert.Contains(walked, access => access.HeldProtection.Count != 0);
        Assert.Contains(walked, access => access.HeldProtection.Count == 0);
    }

    /// <summary>The visit hosting work without a body makes the work's unknown effect and holds nothing over it, whatever its starter
    /// holds where it starts the work.</summary>
    [Fact]
    public void A_host_of_work_without_a_body_emits_its_unknown_effect_holding_nothing()
    {
        var run = Compare("""
            using System.Runtime.CompilerServices;
            public class Base
            {
                public int BaseValue;
                [MethodImpl(MethodImplOptions.InternalCall)]
                public extern void Touch();
            }
            public sealed class State : Base
            {
                public readonly object Gate = new();
            }
            public sealed class Worker : BackgroundService
            {
                private readonly State _state;
                public Worker(State state) => _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    lock (_state.Gate) { Task.Run(_state.Touch); }
                    lock (_state.Gate) { _ = _state.BaseValue; }
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"));

        Assert.Contains(run.Collection.Accesses, access => access.Resource.Member.Name == "BaseValue" &&
                                                           access.Operation == AccessOperation.UnknownEffect && access.HeldProtection.Count == 0);
        Assert.Contains(run.Collection.Accesses, access => access.Resource.Member.Name == "BaseValue" &&
                                                           access.Operation == AccessOperation.Read && access.HeldProtection.Count != 0);
    }

    /// <summary>A spawn that enters an instance whole and also hosts the work without a body that instance starts visits one state
    /// twice: the visit that runs the body is under the rule like any other, and the host is not. Each makes its own accesses.</summary>
    [Fact]
    public void An_instance_entered_whole_and_hosted_by_one_execution_makes_the_accesses_of_both_visits()
    {
        var run = Compare("""
            using System.Runtime.CompilerServices;
            public sealed class Pump
            {
                public int Hits;
                public bool Stop;
                public void Loop()
                {
                    Hits++;
                    Action next = Stop ? Touch : Loop;
                    Task.Run(next);
                }

                [MethodImpl(MethodImplOptions.InternalCall)]
                public extern void Touch();
            }
            public sealed class Worker : BackgroundService
            {
                private readonly Pump _pump;
                public Worker(Pump pump) => _pump = pump;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _pump.Loop();
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<Pump>(); services.AddHostedService<Worker>();"));

        var spawned = run.Collection.Accesses.Where(access => access.ExecutionId.Contains("spawn:", StringComparison.Ordinal) &&
                                                              access.Resource.Member.Name == "Hits").ToArray();
        Assert.Contains(spawned, access => access.Operation == AccessOperation.ReadModifyWrite);
        Assert.Contains(spawned, access => access.Operation.IsUnknownEffect());
    }

    /// <summary>What a case's collection with the rule made and counted.</summary>
    /// <param name="Collection">The collection.</param>
    /// <param name="Counted">Each walked execution with its counters, in the fold's order.</param>
    private sealed record Compared(InterproceduralCollection Collection, IReadOnlyList<(string Execution, CollectionCounters Counters)> Counted)
    {
        /// <summary>The counters of the execution of the worker's root.</summary>
        internal CollectionCounters Worker =>
            Assert.Single(Counted, item => item.Execution.StartsWith("root:", StringComparison.Ordinal) &&
                                           item.Execution.Contains("Worker", StringComparison.Ordinal)).Counters;
    }

    /// <summary>Collects a case's scope without the rule and with it, and requires the two to describe the same accesses.</summary>
    /// <param name="source">The source file of the case.</param>
    private static Compared Compare(string source)
    {
        var input = Input(Execute(source));
        // A first collection names every region a collection names, so the two compared read the same heap.
        InterproceduralAccesses.Collect(input, CancellationToken.None);
        var expected = InterproceduralAccesses.Collect(input with { EmitsUnderRepeatedGuards = true }, CancellationToken.None);
        var counted = new List<(string Execution, CollectionCounters Counters)>();
        var collection = InterproceduralAccesses.Collect(input with { Counted = (id, counters) => counted.Add((id, counters)) }, CancellationToken.None);
        Assert.NotEmpty(collection.Accesses);
        Assert.Equal(Describe(expected), Describe(collection));
        return new Compared(collection, counted);
    }

    /// <summary>Every folded access in order with every field the collection gives it, then each of its executions with its position
    /// and variant. A list the collectors pool, or share between the accesses of one visit, is named by its content and by which object
    /// it is, numbered in order of first use: the fold tells variants and code flows apart by reference.</summary>
    /// <param name="collection">The collection.</param>
    private static string[] Describe(InterproceduralCollection collection)
    {
        var objects = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        string Shared<T>(IReadOnlyCollection<T> list, Func<T, string> item)
        {
            if (!objects.TryGetValue(list, out var number))
                objects.Add(list, number = objects.Count);
            return $"#{number}[{string.Join("/", list.Select(item))}]";
        }

        string Flow(IReadOnlyList<CodeFlowStep> flow) => Shared(flow, step => step.ToString());
        string Variant(AccessVariant variant)
        {
            if (!objects.TryGetValue(variant, out var number))
                objects.Add(variant, number = objects.Count);
            return $"v{number}({variant.Root}>{variant.PathRoot} path={Shared(variant.CallPath, symbol => symbol)} " +
                   $"spawns={Shared(variant.SpawnSites, site => site.ToString())} flow={Flow(variant.CodeFlow)} " +
                   $"reads=[{string.Join("/", variant.ReadSources.Select(read => $"{read.Symbol}@{read.Source} {Flow(read.CodeFlow)}"))}])";
        }

        var accesses = collection.Folded.Select(access =>
            $"{access.BodyId}#{access.OperationId} {access.InstanceId} {access.Symbol}@{access.Source} {access.Operation} {access.Resource.Identity} " +
            $"unseen={access.UnseenWriteOperation} whole={access.WritesWholeStruct} local={access.IsConstructionLocal} " +
            $"reference={access.IsReferenceAccess} fresh={access.IsFresh} libraryInternal={access.IsLibraryInternal} " +
            $"indexed={access.IsIterationIndexed} term={access.SelectorTerm} ownership={access.Ownership}" +
            $"[{string.Join("/", access.OwnershipEvidence)}] held={Shared(access.HeldProtection, display => display)} " +
            $"heldIds={Shared(access.HeldProtectionIds, id => id)} " +
            $"holdings=[{string.Join("/", access.HeldProtections.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{string.Join(",", pair.Value)}"))}] " +
            $"binding={Shared(access.BindingEvidence, evidence => evidence.ToString())} " +
            $"uncertainties={Shared(access.Uncertainties, label => label)} conditions={Shared(access.Conditions, condition => condition.ToString())} " +
            $"gapChecks={Shared(access.GapChecks, check => check.ToString())} deciding={Shared(access.DecidingCalls, call => call.ToString())} " +
            string.Join(" ", access.Executions.Select(execution => $"{execution.ExecutionId}@{execution.Position}:{Variant(execution.Variant)}")));
        var coverage = collection.Coverage;
        return
        [
            .. accesses,
            $"unseen-filled=[{string.Join(",", collection.UnseenFilledStorage)}]",
            $"counters=[{string.Join(",", coverage.Counters.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}]",
            $"gaps=[{string.Join("/", coverage.Gaps.Select(gap => $"{gap.Kind} {gap.Callee} regions={gap.Regions} callSites={gap.CallSites}"))}]"
        ];
    }

    /// <summary>A worker over a state with a guard subject, a helper writing a field, one writing a cell its parameter names and one
    /// writing through a reference.</summary>
    /// <param name="first">The statements of the worker.</param>
    private static string Guarded(string first) => $$"""
        public sealed class GuardState
        {
            public readonly int[] Slots = new int[16];
            public readonly int[] First = new int[4];
            public readonly int[] Second = new int[4];
            public readonly int Shared = 5;
            public int Value;
            public int Left;
            public int Right;
            public void Touch() => Value = 1;
            public void Write(int index) => Slots[index] = 1;
            public void Bump(ref int cell) => cell = 1;
            public void Mark(int index) { if (Left < 4) { Value = 1; Slots[index] = 1; } }
            public void TouchIfLow(int index) { if (index < 4) Value = 1; }
            public void Walk(int[] items) { foreach (var item in items) { } }
            public void WalkFirst() { foreach (var item in First) { } }
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
