using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Rules;

public sealed class UnguardedWriteTests
{
    [Fact]
    public void ReportsUnguardedWritesAndIgnoresGuardsAndStoreKeys()
    {
        var graph = new CacheGraph();
        var plain = AddScenario(graph, "Plain", null);
        var guarded = AddScenario(graph, "Guarded", null);
        var store = AddScenario(graph, "Store", null, role: "store");
        var firstGuard = Handler("Guarded.FirstGuard");
        var secondGuard = Handler("Guarded.SecondGuard");
        graph.AddEdge(new Calls(guarded.Writer, firstGuard, Confidence.Confirmed));
        graph.AddEdge(new Calls(firstGuard, secondGuard, Confidence.Confirmed));
        graph.AddEdge(new Invalidates(secondGuard,
            new CacheKey(guarded.Key.Template, guarded.Key.Store, null, [], null),
            Confidence.Confirmed));

        var findings = new UnguardedWriteRule().Evaluate(graph);

        var finding = Assert.Single(findings, candidate => candidate.Table.Name == plain.Table.Name);
        Assert.Equal(UnguardedWriteFinding.Rule, "UNGUARDED_WRITE");
        Assert.Equal(Confidence.Confirmed, finding.Confidence);
        Assert.Equal(60, finding.BudgetSeconds);
        Assert.Null(finding.TtlSeconds);
        Assert.False(finding.Suppressed);
        Assert.Collection(finding.Chain,
            edge => Assert.IsType<Writes>(edge),
            edge => Assert.IsType<Reads>(edge),
            edge => Assert.IsType<Caches>(edge));
        Assert.DoesNotContain(findings, candidate => candidate.Table.Name == guarded.Table.Name);
        Assert.DoesNotContain(findings, candidate => candidate.Table.Name == store.Table.Name);
    }

    [Fact]
    public void AppliesExactSchemaAndDefaultBudgetsWithoutSuppressingInfinity()
    {
        var graph = new CacheGraph();
        var shortTtl = AddScenario(graph, "sales.Suppressed", TimeSpan.FromSeconds(30));
        var longTtl = AddScenario(graph, "sales.Long", TimeSpan.FromSeconds(120));
        var noTtl = AddScenario(graph, "sales.None", null);
        var merged = AddMergedScenario(graph, "sales.MixedTtl",
            new CacheKey("key:mixed-ttl", "memory", TimeSpan.FromSeconds(10), [], "cache"),
            new CacheKey("key:mixed-ttl", "memory", null, [], "cache"));
        var budgets = new Dictionary<string, double>
        {
            ["sales.*"] = 10,
            [shortTtl.Table.Name] = 45,
            [longTtl.Table.Name] = 60,
            [noTtl.Table.Name] = 500,
            [merged.Table.Name] = 500
        };

        var findings = new UnguardedWriteRule().Evaluate(graph, budgets);

        var suppressed = Find(findings, shortTtl.Table);
        Assert.True(suppressed.Suppressed);
        Assert.Equal(30, suppressed.TtlSeconds);
        Assert.Equal(45, suppressed.BudgetSeconds);

        var longLived = Find(findings, longTtl.Table);
        Assert.False(longLived.Suppressed);
        Assert.Equal(120, longLived.TtlSeconds);
        Assert.Equal(60, longLived.BudgetSeconds);

        var infinite = Find(findings, noTtl.Table);
        Assert.False(infinite.Suppressed);
        Assert.Null(infinite.TtlSeconds);
        Assert.Equal(500, infinite.BudgetSeconds);

        var mergedInfinite = Find(findings, merged.Table);
        Assert.False(mergedInfinite.Suppressed);
        Assert.Null(mergedInfinite.TtlSeconds);
    }

    [Fact]
    public void APartialTagRemovalDoesNotGuardTheMergedKey()
    {
        var graph = new CacheGraph();
        var scenario = AddMergedScenario(graph, "dbo.MixedTags",
            new CacheKey("key:mixed-tags", "hybrid", TimeSpan.FromSeconds(120), ["catalog"], "cache"),
            new CacheKey("key:mixed-tags", "hybrid", TimeSpan.FromSeconds(120), [], "cache"));
        graph.AddEdge(new Invalidates(scenario.Writer,
            new CacheKey("catalog", "hybrid", null, [], null), Confidence.Confirmed,
            semantic: CacheSemantic.RemoveByTag));

        var findings = new UnguardedWriteRule().Evaluate(graph);

        var finding = Find(findings, scenario.Table);
        Assert.Empty(finding.Key.TagsAll);
        Assert.Contains("catalog", finding.Key.TagsAny);
    }

    [Fact]
    public void CarriesConfirmedLikelyAndUnknownConfidence()
    {
        var graph = new CacheGraph();
        var confirmed = AddScenario(graph, "ConfidenceConfirmed", null);
        var likely = AddScenarioThroughCall(graph, "ConfidenceLikely", Confidence.Likely);
        var unknown = AddScenario(graph, "ConfidenceUnknown", null);
        graph.AddUnresolved(UnresolvedKind.Sql, unknown.CacheHandler, "fixture.cs", 40,
            "Query()", "SQL parsing is out of scope for this phase.");

        var findings = new UnguardedWriteRule().Evaluate(graph);

        Assert.Equal(Confidence.Confirmed, Find(findings, confirmed.Table).Confidence);
        Assert.Equal(Confidence.Likely, Find(findings, likely.Table).Confidence);
        Assert.Equal(Confidence.Unknown, Find(findings, unknown.Table).Confidence);
    }

    private static Scenario AddScenario(CacheGraph graph, string name, TimeSpan? ttl,
                                        string role = "cache")
    {
        var table = Table(name);
        var key = new CacheKey($"key:{name}", "memory", ttl, [], role);
        var writer = Handler($"{name}.Writer");
        var cacheHandler = Handler($"{name}.Cache");
        graph.AddEdge(new Writes(writer, table, Confidence.Confirmed));
        graph.AddEdge(new Caches(cacheHandler, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(cacheHandler, table, Confidence.Confirmed));
        return new Scenario(table, key, writer, cacheHandler);
    }

    private static Scenario AddScenarioThroughCall(CacheGraph graph, string name,
                                                   Confidence callConfidence)
    {
        var table = Table(name);
        var key = new CacheKey($"key:{name}", "memory", null, [], "cache");
        var writer = Handler($"{name}.Writer");
        var cacheHandler = Handler($"{name}.Cache");
        var reader = Handler($"{name}.Reader");
        graph.AddEdge(new Writes(writer, table, Confidence.Confirmed));
        graph.AddEdge(new Caches(cacheHandler, key, Confidence.Confirmed));
        graph.AddEdge(new Calls(cacheHandler, reader, callConfidence));
        graph.AddEdge(new Reads(reader, table, Confidence.Confirmed));
        return new Scenario(table, key, writer, cacheHandler);
    }

    private static Scenario AddMergedScenario(CacheGraph graph, string name,
                                              CacheKey firstKey, CacheKey secondKey)
    {
        var table = Table(name);
        var writer = Handler($"{name}.Writer");
        var firstCache = Handler($"{name}.FirstCache");
        var secondCache = Handler($"{name}.SecondCache");
        graph.AddEdge(new Writes(writer, table, Confidence.Confirmed));
        graph.AddEdge(new Caches(firstCache, firstKey, Confidence.Confirmed));
        graph.AddEdge(new Reads(firstCache, table, Confidence.Confirmed));
        graph.AddEdge(new Caches(secondCache, secondKey, Confidence.Confirmed));
        graph.AddEdge(new Reads(secondCache, table, Confidence.Confirmed));
        return new Scenario(table, firstKey, writer, firstCache);
    }

    private static UnguardedWriteFinding Find(IEnumerable<UnguardedWriteFinding> findings,
                                              Table table) =>
        Assert.Single(findings, finding => finding.Table.Name == table.Name);

    private static Table Table(string name) =>
        new(name.Contains('.', StringComparison.Ordinal) ? name : $"dbo.{name}", "default");

    private static Handler Handler(string symbol) =>
        new("fixture", symbol, "method", "fixture.cs", 1);

    private sealed record Scenario(Table Table, CacheKey Key, Handler Writer, Handler CacheHandler);
}
