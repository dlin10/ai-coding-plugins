using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A visit emits an access only under guards the execution has not emitted it under (ADR 0041). The rule answers ahead a check
/// the emission makes anyway, so a collection with it gives what a collection without it gives: the same accesses in the same order,
/// field for field and pooled object for pooled object. One case stands for each way a visit's path reaches the emission besides the
/// guards, where the rule must leave the access alone.</summary>
public sealed class RepeatedGuardsTests
{
    [Fact]
    public async Task Demo_accesses_are_the_same_with_the_rule_and_without_it()
    {
        long skipped = 0;
        long skippedWithout = 0;
        await ParallelCollectionTests.ForEachDemoScope(input =>
        {
            var expected = Describe(InterproceduralAccesses.Collect(input with
            {
                EmitsUnderRepeatedGuards = true,
                Counted = (_, counters) => skippedWithout += counters.Skipped
            }, CancellationToken.None));
            Assert.NotEmpty(expected);
            Assert.Equal(expected, Describe(InterproceduralAccesses.Collect(input with { Counted = (_, counters) => skipped += counters.Skipped },
                                                                           CancellationToken.None)));
        });

        Assert.Equal(0, skippedWithout);
        Assert.True(skipped > 0);
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
            public readonly int Shared = 5;
            public int Value;
            public int Left;
            public int Right;
            public void Touch() => Value = 1;
            public void Write(int index) => Slots[index] = 1;
            public void Bump(ref int cell) => cell = 1;
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
