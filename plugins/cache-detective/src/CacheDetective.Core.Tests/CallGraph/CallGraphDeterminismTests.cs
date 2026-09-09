using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.CallGraph;

/// <summary>
/// The walk must be a function of the solution, not of the order the solution happens to arrive in.
/// Neither the order <see cref="Microsoft.CodeAnalysis.Solution.Projects"/> yields its projects nor the
/// order <see cref="Microsoft.CodeAnalysis.FindSymbols.SymbolFinder"/> yields an interface's
/// implementations is specified, and both feed the traversal.
/// </summary>
public sealed class CallGraphDeterminismTests
{
    private static readonly string[] WALK_ORDER_FIXTURE =
    [
        "SourceFiles/WalkOrderShallow.cs",
        "SourceFiles/WalkOrderMid.cs",
        "SourceFiles/WalkOrderDeep.cs",
        "SourceFiles/WalkOrderShared.cs"
    ];

    [Fact]
    public async Task IndexingOneSolutionTwiceProducesTheSameGraph()
    {
        var solution = await FixtureSolution.CreateAsync(WALK_ORDER_FIXTURE);

        var first = await new CallGraphIndexer().IndexAsync(solution, "fixture");
        var second = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        Assert.Equal(Edges(first), Edges(second));
        Assert.Equal(Unresolved(first), Unresolved(second));
    }

    [Fact]
    public async Task ReachingOneMethodFromThreeDepthsDoesNotDependOnTheEntryPointOrder()
    {
        var forwards = await FixtureSolution.CreateAsync(WALK_ORDER_FIXTURE);
        var backwards = await FixtureSolution.CreateAsync([.. WALK_ORDER_FIXTURE.Reverse()]);

        var first = await new CallGraphIndexer().IndexAsync(forwards, "fixture");
        var second = await new CallGraphIndexer().IndexAsync(backwards, "fixture");

        Assert.Equal(Edges(first), Edges(second));
        Assert.Equal(Unresolved(first), Unresolved(second));
    }

    /// <summary>
    /// The property the two tests above compare, asserted on its own so that they keep guarding
    /// something even if perturbing the fixture's order stops perturbing the walk. The shared chain is
    /// reached from three entry points at depths 1, 4 and 12; the walk must record each of its edges
    /// once, and nothing in the fixture may reach the limit, because the shortest way to the chain is
    /// one hop.
    /// </summary>
    [Fact]
    public async Task AMethodReachedFromThreeCallersIsExpandedOnce()
    {
        var solution = await FixtureSolution.CreateAsync(WALK_ORDER_FIXTURE);

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        Assert.Single(Edges(graph), edge => edge.StartsWith("WalkOrderFixture.Shared.S1() -> WalkOrderFixture.Shared.S2()",
                                                            StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Unresolved,
            item => item.Reason.Contains("Maximum call depth", StringComparison.Ordinal));
    }

    /// <summary>Every call edge as text, ordered but not deduplicated: an edge recorded twice is a
    /// difference, because the edge count the metrics report counts sites and not pairs.</summary>
    private static IReadOnlyList<string> Edges(CacheGraph graph) =>
        graph.Edges.OfType<Calls>()
             .Select(edge => $"{((Handler)edge.From).Symbol} -> {((Handler)edge.To).Symbol} ({edge.Confidence})")
             .OrderBy(text => text, StringComparer.Ordinal)
             .ToArray();

    private static IReadOnlyList<string> Unresolved(CacheGraph graph) =>
        graph.Unresolved.Select(item => $"{item.Kind}|{item.File}:{item.Line}|{item.Snippet}|{item.Reason}")
             .OrderBy(text => text, StringComparer.Ordinal)
             .ToArray();
}
