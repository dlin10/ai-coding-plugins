using System.Diagnostics;
using CacheDetective.Graph;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests.Graph;

/// <summary>
/// The dependency closure at the size an enterprise solution has. The shape is the one measured on a
/// 64-project solution — a few thousand handlers, an out-degree around five, a call chain far longer
/// than the depth limit, and cache keys at the top — and it is the shape a walk that counts paths
/// rather than vertices cannot finish: out-degree five at depth twelve is a quarter of a billion paths
/// per starting handler, and the rules ask for the closure once per key.
/// <para>The budget is the same twenty seconds the rest of the scale suite uses. It is not a benchmark:
/// it is the line between a polynomial search and an exponential enumeration, and the enumeration
/// misses it by hours rather than by milliseconds.</para>
/// </summary>
public sealed class DependsScaleTests
{
    private const double BUDGET_SECONDS = 20;
    private const int HANDLERS = 2000;
    private const int TABLES = 64;

    [Fact]
    public void The_closure_of_a_key_over_a_wide_call_graph_stays_within_the_budget()
    {
        var graph = BuildWide();
        var key = Assert.Single(graph.CacheKeys);

        IReadOnlyList<KeyDependency> dependencies = [];
        var elapsed = Measure(() => dependencies = graph.DependsOn(key));

        Assert.True(elapsed < BUDGET_SECONDS * 1000, $"The closure took {elapsed:F0} ms.");
        Assert.Equal(TABLES, dependencies.Select(dependency => dependency.Target).OfType<Table>().Distinct().Count());
    }

    /// <summary>One row per target, which is the row every caller of the closure already selected for
    /// itself with <c>OrderBy(Confidence).ThenBy(Path.Count).First()</c>. A walk that returns a row per
    /// path returns them in the thousands here and every caller throws all but one away.</summary>
    [Fact]
    public void The_closure_reports_each_target_once()
    {
        var graph = BuildWide();
        var key = Assert.Single(graph.CacheKeys);

        var dependencies = graph.DependsOn(key);

        Assert.Equal(dependencies.Count, dependencies.Select(dependency => dependency.Target).Distinct().Count());
    }

    [Fact]
    public void Evaluating_the_rules_over_a_wide_call_graph_stays_within_the_budget()
    {
        var graph = BuildWide();

        var elapsed = Measure(() =>
        {
            new UnguardedWriteRule().Evaluate(graph);
            new StaleParentKeyRule().Evaluate(graph);
            new ExternalNoTtlRule().Evaluate(graph);
        });

        Assert.True(elapsed < BUDGET_SECONDS * 1000, $"Evaluating the rules took {elapsed:F0} ms.");
    }

    /// <summary>Handlers that call five others each — one of them the next in a chain longer than the
    /// depth limit, so the walk meets the limit as well as the fan-out — and that each read one of a
    /// small set of tables, so a table is reachable by very many distinct paths.</summary>
    private static CacheGraph BuildWide()
    {
        var graph = new CacheGraph();
        var handlers = new Handler[HANDLERS];
        for (var index = 0; index < HANDLERS; index++)
            handlers[index] = new Handler("big", $"Big.Handler{index}", "http", "C.cs", index + 1) { Project = "Big" };

        for (var index = 0; index < HANDLERS; index++)
        {
            graph.AddEdge(new Calls(handlers[index], handlers[(index + 1) % HANDLERS], Confidence.Confirmed));
            for (var branch = 1; branch <= 4; branch++)
                graph.AddEdge(new Calls(handlers[index], handlers[(index * 7 + branch * 13) % HANDLERS], Confidence.Likely));

            graph.AddEdge(new Reads(handlers[index], new Table($"dbo.T{index % TABLES}", "shop"), Confidence.Confirmed));
        }

        graph.AddEdge(new Caches(handlers[0], new CacheKey("k:root", "memory", null, [], "cache"), Confidence.Confirmed));
        graph.AddEdge(new Writes(handlers[1], new Table("dbo.T0", "shop"), Confidence.Confirmed));
        return graph;
    }

    private static double Measure(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
