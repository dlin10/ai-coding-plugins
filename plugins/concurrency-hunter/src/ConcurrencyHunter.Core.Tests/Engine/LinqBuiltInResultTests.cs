using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Reporting;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What the built-in LINQ models return, through the engine (R6): <c>ToList</c> and <c>ToArray</c> a new collection holding the
/// source's own elements, an element operator one of those elements, and a <c>Where</c> copied by <c>ToList</c> runs its predicate in
/// the caller. The worker writes through the result; the reader clears the source list and writes its first element.</summary>
public sealed class LinqBuiltInResultTests
{
    private const string ITEM = "alloc:State..ctor()#Item";
    private const string SPARE = "alloc:State..ctor()#Spare";

    [Fact]
    public void ToList_returns_a_new_list_holding_the_sources_elements()
    {
        var run = Run("var copy = System.Linq.Enumerable.ToList(_state.Items);\ncopy[0].Hits = 1;\ncopy.Add(new Item());");

        AssertHoldsTheSourcesElements(run);
        AssertTheSourceListIsNotWritten(run);
    }

    [Fact]
    public void ToArray_returns_a_new_array_holding_the_sources_elements()
    {
        var run = Run("var copy = System.Linq.Enumerable.ToArray(_state.Items);\ncopy[0].Hits = 1;\ncopy[0] = new Item();");

        AssertHoldsTheSourcesElements(run);
        AssertTheSourceListIsNotWritten(run);
    }

    [Fact]
    public void Where_then_ToList_runs_the_predicate_in_the_caller()
    {
        var run = Run("System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(_state.Items, item => { _state.Count = 1; return true; }));");

        var writes = run.Of("Count").Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(writes);
        Assert.All(writes, access => Assert.Equal(ExecutionKind.Root, run.Execution.Analysis.Execution(access.ExecutionId).Kind));
        Assert.DoesNotContain(run.Execution.Analysis.Executions, execution => execution.Kind is ExecutionKind.UnknownEnumeration or
                                                                                              ExecutionKind.UnknownDelegateCall);
        Assert.Equal(0, run.Counter(CoverageCounters.DELEGATE_TO_OPAQUE));
    }

    [Theory]
    [InlineData("First")]
    [InlineData("FirstOrDefault")]
    [InlineData("Single")]
    [InlineData("SingleOrDefault")]
    [InlineData("Last")]
    [InlineData("LastOrDefault")]
    public void Element_operator_returns_one_of_the_sources_elements(string name)
    {
        var run = Run($"System.Linq.Enumerable.{name}(_state.Items)!.Hits = 1;");

        // Only the list's element: an opaque call's result would be every compatible object, the spare item among them.
        Assert.Equal([ITEM], Written(run, "Hits"));
        Assert.Contains(run.PairsOn("Hits"), pair => pair.Resource.Region == ITEM);
    }

    // ---- helpers ----

    /// <summary>The objects the worker writes a member of.</summary>
    private static string[] Written(EngineRun run, string member) =>
        run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Operation == AccessOperation.Write)
                      .Select(access => access.Resource.Region)
                      .Distinct()
                      .Order(StringComparer.Ordinal)
                      .ToArray();

    /// <summary>The worker's write through the copy's element reaches the list's item and not the spare one; the worker's own item,
    /// which it adds to the copy, is written too.</summary>
    private static void AssertHoldsTheSourcesElements(EngineRun run)
    {
        var written = Written(run, "Hits");
        Assert.Contains(ITEM, written);
        Assert.DoesNotContain(SPARE, written);
    }

    /// <summary>The worker writes objects, but none the reader's <c>Clear</c> writes: the copy is a new object.</summary>
    private static void AssertTheSourceListIsNotWritten(EngineRun run)
    {
        var writes = run.Collection.Accesses.Where(access => access.Operation == AccessOperation.Write).ToArray();
        var source = writes.Where(access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal) && access.Resource.Region != ITEM)
                           .Select(access => access.Resource.Region)
                           .ToHashSet(StringComparer.Ordinal);
        var worker = writes.Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Resource.Region != ITEM)
                           .Select(access => access.Resource.Region)
                           .ToArray();

        Assert.NotEmpty(source);
        Assert.NotEmpty(worker);
        Assert.DoesNotContain(worker, source.Contains);
    }

    private static EngineRun Run(string work) =>
        AnalyzeScope(FixtureSolution.Create(new FixtureOptions(), ("Case.cs", Usings + Source(work))), "scope:Fixture");

    /// <summary>A singleton <c>State</c> holding a list of one item and a spare item outside it, a worker doing <paramref name="work"/>
    /// on it, and a reader clearing the list and writing its first item.</summary>
    private static string Source(string work) => $$"""
        using System.Collections.Generic;

        public class Item { public int Hits; }
        public sealed class Spare : Item { }

        public sealed class State
        {
            public readonly List<Item> Items = new();
            public readonly Spare Spare = new Spare();
            public int Count;

            public State() => Items.Add(new Item());
        }

        public sealed class Worker : BackgroundService
        {
            private readonly State _state;
            public Worker(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            public Reader(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _state.Items.Clear();
                _state.Items[0].Hits = 2;
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");
}
