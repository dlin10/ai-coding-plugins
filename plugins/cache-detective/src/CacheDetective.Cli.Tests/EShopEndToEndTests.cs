using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Configuration;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Tests.Database;
using CacheDetective.Workspaces;
using Xunit;
using Xunit.Abstractions;

namespace CacheDetective.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresEShopFactAttribute : FactAttribute
{
    public RequiresEShopFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CD_ESHOP_ROOT")))
            Skip = "CD_ESHOP_ROOT is not set. Set it to an eShopOnContainers checkout to run this read-only eval.";
    }
}

public sealed class EShopEndToEndTests(ITestOutputHelper output)
{
    [RequiresEShopFact]
    public async Task Recognizes_cross_service_events_and_the_planted_web_cache()
    {
        var root = Environment.GetEnvironmentVariable("CD_ESHOP_ROOT")!;
        var eval = Path.GetDirectoryName(SqlServerHarness.FindRepositoryFile("plugins", "cache-detective", "skills", "scan", "evals", "eshop", "workspace.json"))!;
        var configuration = await WorkspaceConfigurationStore.ReadFileAsync(Path.Combine(eval, "workspace.json"));
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(eval, "expected.json")));
        var solutionPath = Path.Combine(root, configuration.Solutions.Single());

        CacheGraph graph;
        using (var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath))
        {
            var configuredEvents = (configuration.Events ?? []).Select(item => item.ToRecognizer(Confidence.Confirmed, null));
            var indexer = new CallGraphIndexer(new IndexerOptions(CacheRecognizers.All, EventRecognizers.All.Concat(configuredEvents).ToArray()));
            graph = await indexer.IndexAsync(loaded.Solution, Path.GetFileName(solutionPath));
        }

        var allowlist = expected.RootElement.GetProperty("eventsWithoutCrossProjectConsumer").EnumerateArray()
                                .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var missing = graph.StoredEdges.OfType<Publishes>()
                           .Where(publish => publish.To is Event @event && !allowlist.Contains(@event.Name))
                           .Where(publish => !graph.EventHops().Any(hop => hop.Publish == publish &&
                                                                           ((Handler)hop.Consume.To).Project != ((Handler)publish.From).Project))
                           .Select(publish => ((Event)publish.To).Name).Distinct().Order().ToArray();
        Assert.True(missing.Length == 0, "Events without a cross-project consumer: " + string.Join(", ", missing));

        foreach (var eventExpectation in expected.RootElement.GetProperty("crossServiceEvents").EnumerateArray())
        {
            var name = eventExpectation.GetProperty("event").GetString();
            var publisher = eventExpectation.GetProperty("publisher").GetString();
            var consumers = eventExpectation.GetProperty("consumers").EnumerateArray().Select(item => item.GetString()).ToHashSet();
            var matches = graph.EventHops().Where(hop => ((Event)hop.Publish.To).Name == name &&
                                                          ((Handler)hop.Publish.From).Project == publisher)
                                  .Select(hop => ((Handler)hop.Consume.To).Project).ToHashSet();
            Assert.True(consumers.IsSubsetOf(matches), $"Missing consumers for {name}: {string.Join(", ", consumers.Except(matches))}");
        }

        var serves = expected.RootElement.GetProperty("serves");
        Assert.Contains(graph.Edges.OfType<Serves>(), edge => TraceId(edge.From) == serves.GetProperty("source").GetString() &&
                                                              ((Handler)edge.To).Project == serves.GetProperty("targetProject").GetString() &&
                                                              ((Handler)edge.To).Symbol.Contains(serves.GetProperty("targetSymbolContains").GetString()!, StringComparison.Ordinal));
        foreach (var key in expected.RootElement.GetProperty("unresolvedKeys").EnumerateArray().Select(item => item.GetString()!))
            Assert.Contains(graph.Unresolved, unresolved => unresolved.Kind == UnresolvedKind.Key && unresolved.Snippet.Contains(key, StringComparison.Ordinal));

        var marker = Path.Combine(root, "src", "Web", "WebMVC", ".cache-detective-planted");
        if (!File.Exists(marker))
        {
            output.WriteLine("Optional planted cache patch is not applied.");
            return;
        }

        foreach (var annotation in expected.RootElement.GetProperty("annotations").EnumerateArray())
        {
            var source = graph.ExternalSources.Single(candidate => candidate.Owner == annotation.GetProperty("owner").GetString() &&
                candidate.ClientName == annotation.GetProperty("clientName").GetString() && candidate.Method == annotation.GetProperty("method").GetString() &&
                candidate.Template.EndsWith(annotation.GetProperty("templateEndsWith").GetString()!, StringComparison.Ordinal));
            var target = graph.Handlers.Single(candidate => candidate.Project == annotation.GetProperty("targetProject").GetString() &&
                candidate.Symbol.Contains(annotation.GetProperty("targetSymbolContains").GetString()!, StringComparison.Ordinal));
            graph.AddServesAnnotation(source, target, 1);
        }

        var planted = expected.RootElement.GetProperty("plantedFinding");
        Assert.Single(new UnguardedWriteRule().Evaluate(graph), finding => finding.RuleName == planted.GetProperty("rule").GetString() &&
                                                                  finding.Table.Name == planted.GetProperty("table").GetString() &&
                                                                  finding.Key.Template == planted.GetProperty("key").GetString());
    }

    private static string TraceId(GraphVertex vertex) => vertex is ExternalSource source
        ? $"external:{source.Kind}:{source.Owner}:{source.ClientName ?? "-"}:{source.Method} {source.Template}"
        : string.Empty;
}
