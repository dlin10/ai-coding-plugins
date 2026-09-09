using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Indexing;
using CacheDetective.Indexing.EntryPoints;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.CallGraph;

/// <summary>The two properties of the entry-point vocabulary that no snapshot can show: that a uniform form
/// is added by a table row rather than by editing a branch, and that the finders are asked in the recorded
/// order.</summary>
public sealed class EntryPointFinderTests
{
    [Fact]
    public async Task A_new_recognizer_row_yields_a_new_entry_point_kind()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/UnknownEntryPointShape.cs");
        var tables = EntryPointTables.Default with
        {
            Jobs = [.. EntryPointTables.Default.Jobs, new EntryPointRecognizer("IWorkUnit", 0, "Run", "test_worker")]
        };

        var graph = await new CallGraphIndexer(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All), tables)
            .IndexAsync(solution, "fixture");

        // The row is the whole of the change: no branch, no finder and no fixture-aware code path mentions
        // IWorkUnit anywhere in the indexer.
        Assert.Contains("test_worker", graph.Handlers.Select(handler => handler.Kind));
    }

    [Fact]
    public void The_finder_order_is_the_recorded_one()
    {
        var finders = CallGraphIndexer.Finders(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All),
                                               EntryPointTables.Default);

        // Type names alone would not do: two of the six are RecognizerEntryPointFinder, and a test that read
        // only the names would pass with those two swapped and leave the swap to surface in a snapshot.
        Assert.Equal(
            [
                "ControllerEntryPointFinder",
                "GrpcEntryPointFinder",
                "RecognizerEntryPointFinder(IRequestHandler/2, IRequestHandler/1)",
                "EventConsumerEntryPointFinder",
                "HostedServiceEntryPointFinder",
                "RecognizerEntryPointFinder(IJob/0)"
            ],
            finders.Select(Describe));
    }

    private static string Describe(IEntryPointFinder finder)
    {
        var name = finder.GetType().Name;
        return finder is RecognizerEntryPointFinder recognizerFinder
                   ? $"{name}({string.Join(", ", recognizerFinder.Recognizers.Select(row => $"{row.ShapeName}/{row.Arity}"))})"
                   : name;
    }
}
