using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.CallGraph;

public sealed class CallGraphIndexerTests
{
    [Fact]
    public async Task FindsEveryEntryPointShape()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/EntryPoints.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        var kinds = graph.Handlers.Select(handler => handler.Kind).ToHashSet();
        Assert.Contains("controller", kinds);
        Assert.Contains("minimal_api", kinds);
        Assert.Contains("request_handler", kinds);
        Assert.Contains("notification_handler", kinds);
        Assert.Contains("consumer", kinds);
        Assert.Contains("message_handler", kinds);
        Assert.Contains("hosted_service", kinds);
        Assert.Contains("background_service", kinds);
        Assert.Contains("job", kinds);
        Assert.DoesNotContain(graph.Handlers, handler => handler.Symbol.Contains("Hidden", StringComparison.Ordinal));
        Assert.All(graph.Handlers, handler =>
        {
            Assert.EndsWith("EntryPoints.cs", handler.File, StringComparison.OrdinalIgnoreCase);
            Assert.True(handler.Line > 0);
        });
        Assert.All(graph.Edges.OfType<Calls>(), edge => Assert.All(edge.Evidence, evidence =>
        {
            Assert.EndsWith("EntryPoints.cs", evidence.File, StringComparison.OrdinalIgnoreCase);
            Assert.True(evidence.Line > 0);
        }));
    }

    [Fact]
    public async Task StopsAtDepthTwelveAndCutsCycles()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/Traversal.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        var cutoff = Assert.Single(graph.Unresolved,
            unresolved => unresolved.Reason.Contains("Maximum call depth of 12", StringComparison.Ordinal));
        Assert.Contains("M13", cutoff.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Handlers,
            handler => handler.Symbol.Contains("Chain.M13", StringComparison.Ordinal));
        Assert.Contains(graph.Edges.OfType<Calls>(), edge =>
            ((Handler)edge.From).Symbol.Contains("Cycle.B", StringComparison.Ordinal) &&
            ((Handler)edge.To).Symbol.Contains("Cycle.A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvesZeroOneAndManyInterfaceImplementations()
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/InterfaceCalls.cs");

        var graph = await new CallGraphIndexer().IndexAsync(solution, "fixture");

        var calls = graph.Edges.OfType<Calls>()
                         .Where(edge => ((Handler)edge.From).Symbol.Contains("InterfaceController.Invoke",
                             StringComparison.Ordinal))
                         .ToArray();
        Assert.Single(calls, edge => edge.Confidence == Confidence.Confirmed);
        Assert.Equal(2, calls.Count(edge => edge.Confidence == Confidence.Likely));
        var unresolved = Assert.Single(graph.Unresolved,
            item => item.Reason.Contains("No implementation found", StringComparison.Ordinal));
        Assert.Contains("_missing.Run", unresolved.Snippet, StringComparison.Ordinal);
        Assert.True(unresolved.Line > 0);
        Assert.EndsWith("InterfaceCalls.cs", unresolved.File, StringComparison.OrdinalIgnoreCase);
    }
}
