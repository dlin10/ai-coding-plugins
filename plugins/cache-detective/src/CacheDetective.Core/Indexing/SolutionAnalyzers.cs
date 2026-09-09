using Microsoft.CodeAnalysis;
using CacheDetective.Caching;
using CacheDetective.Data;
using CacheDetective.Events;
using CacheDetective.External;

namespace CacheDetective.Indexing;

/// <summary>The six analyzers one indexing run reads a solution with, as one value. They are not
/// interchangeable and are not behind interfaces: the walk calls each by name, and <see cref="EfWrites"/> is
/// built from <see cref="EfReads"/> rather than from the solution, so the order they are constructed in is
/// part of what they are.</summary>
internal sealed record SolutionAnalyzers(CacheCallAnalyzer CacheCalls, EventCallAnalyzer EventCalls, HttpCallAnalyzer HttpCalls,
                                         EfReadAnalyzer EfReads, EfWriteAnalyzer EfWrites, SqlAnalyzer Sql)
{
    internal static SolutionAnalyzers Create(Solution solution, IndexerOptions options)
    {
        var cacheCallAnalyzer = new CacheCallAnalyzer(solution, options.CacheRecognizers);
        var eventCallAnalyzer = new EventCallAnalyzer(solution, options.EventRecognizers);
        var httpCallAnalyzer = new HttpCallAnalyzer(solution);
        var efReadAnalyzer = new EfReadAnalyzer(solution);
        var efWriteAnalyzer = new EfWriteAnalyzer(efReadAnalyzer);
        var sqlAnalyzer = new SqlAnalyzer(solution);

        return new SolutionAnalyzers(cacheCallAnalyzer, eventCallAnalyzer, httpCallAnalyzer, efReadAnalyzer, efWriteAnalyzer,
                                     sqlAnalyzer);
    }
}
