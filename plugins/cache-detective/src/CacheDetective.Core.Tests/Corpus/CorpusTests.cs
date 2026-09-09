using CacheDetective.Tests.Fixtures;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Corpus;

public sealed class CorpusTests
{
    [Fact]
    public async Task PlantedBugsProduceTheExactFindingSet()
    {
        var catalogue = await IndexAsync("SourceFiles/CorpusCatalogue.cs", "catalogue");
        var pricing = await IndexAsync("SourceFiles/CorpusPricing.cs", "pricing");
        var graph = new CacheGraph();
        graph.ReplaceSolution("catalogue", catalogue);
        graph.ReplaceSolution("pricing", pricing);

        var unguarded = new UnguardedWriteRule().Evaluate(graph,
            new Dictionary<string, double> { ["dbo.BudgetEntries"] = 60 });
        var invalidations = new OrphanInvalidationRule().Evaluate(graph);

        var visible = Describe(unguarded.Where(finding => !finding.Suppressed), invalidations);
        Assert.Equal(new[]
        {
            "ORPHAN_INVALIDATION|Confirmed|legacy:{id}",
            "ORPHAN_INVALIDATION|Confirmed|products:{id}",
            "PATTERN_MISMATCH|Confirmed|products:{id}|product:{id}|1",
            "UNGUARDED_WRITE|Confirmed|dbo.Discounts|product:{id}|Writes>Reads>Caches"
        }, visible);

        var all = Describe(unguarded, invalidations);
        Assert.Equal(new[]
        {
            "ORPHAN_INVALIDATION|Confirmed|legacy:{id}",
            "ORPHAN_INVALIDATION|Confirmed|products:{id}",
            "PATTERN_MISMATCH|Confirmed|products:{id}|product:{id}|1",
            "UNGUARDED_WRITE|Confirmed|dbo.BudgetEntries|budget:{id}|Writes>Reads>Caches",
            "UNGUARDED_WRITE|Confirmed|dbo.Discounts|product:{id}|Writes>Reads>Caches"
        }, all);

        var suppressed = Assert.Single(unguarded, finding => finding.Suppressed);
        Assert.Equal("budget:{id}", suppressed.Key.Template);
        Assert.Equal(30, suppressed.TtlSeconds);
        Assert.Equal(60, suppressed.BudgetSeconds);
        Assert.Single(graph.Tables, table => table.Name == "dbo.Discounts");
        Assert.Equal("store", Assert.Single(graph.CacheKeys,
            key => key.Template == "session:{id}" && key.Store == "memory").Role);
        Assert.Equal("store", Assert.Single(graph.CacheKeys,
            key => key.Template == "lock:{id}" && key.Store == "redis").Role);
        Assert.DoesNotContain(unguarded, finding => finding.Table.Name == "dbo.Products");
        Assert.DoesNotContain(unguarded, finding => finding.Key.Role != "cache");
    }

    private static async Task<CacheGraph> IndexAsync(string sourceFile, string solutionName)
    {
        var solution = await FixtureSolution.CreateAsync(sourceFile);
        return await new CallGraphIndexer().IndexAsync(solution, solutionName);
    }

    private static string[] Describe(IEnumerable<UnguardedWriteFinding> unguarded,
                                     InvalidationRuleResult invalidations)
    {
        var findings = new List<string>();
        findings.AddRange(unguarded.Select(finding =>
            $"{UnguardedWriteFinding.Rule}|{finding.Confidence}|{finding.Table.Name}|" +
            $"{finding.Key.Template}|{string.Join('>', finding.Chain.Select(edge => edge.GetType().Name))}"));
        findings.AddRange(invalidations.Orphans.Select(finding =>
            $"{OrphanInvalidationFinding.Rule}|{finding.Invalidation.Confidence}|" +
            $"{((CacheKey)finding.Invalidation.To).Template}"));
        findings.AddRange(invalidations.PatternMismatches.Select(finding =>
            $"{PatternMismatchFinding.Rule}|{finding.Invalidation.Confidence}|" +
            $"{((CacheKey)finding.Invalidation.To).Template}|{finding.CachedKey.Template}|{finding.Distance}"));
        return findings.Order(StringComparer.Ordinal).ToArray();
    }
}
