using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Serialization;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

/// <summary>The semantic gaps in a <c>metrics</c> measurement (R5): each scope's <c>semantic-gap</c> counter and the sorted callees of
/// its gaps, so that a snapshot proves which gaps there are as the fingerprints prove which findings.</summary>
public sealed class SemanticGapTests
{
    private const string SOURCE = """
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.Extensions.DependencyInjection;
        public sealed class Tally { public int Count; }
        public sealed class TallyController(Tally tally) : ControllerBase
        {
            public void Post() { System.Console.WriteLine(tally); System.Console.Write(tally); System.Console.Write("text"); }
        }
        public static class Startup
        {
            public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
            {
                services.AddControllers();
                services.AddSingleton<Tally>();
                app.MapControllers();
            }
        }
        """;

    [Fact]
    public async Task Measurement_carries_the_gap_counter_and_the_sorted_callees_of_each_scope()
    {
        var (measurement, analysis) = await MetricsCommand.MeasureAsync("Fixture.slnx", AnalysisLimits.Default, Load, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(measurement, MetricsJsonContext.Default.Measurement));

        var scope = Assert.Single(json.RootElement.GetProperty("coverage").EnumerateArray());
        Assert.Equal(2, scope.GetProperty("counters").GetProperty(CoverageCounters.SEMANTIC_GAP).GetInt32());
        Assert.Equal(["System.Console.Write(object)", "System.Console.WriteLine(object)"],
                     scope.GetProperty("semanticGaps").EnumerateArray().Select(callee => callee.GetString()));
        Assert.Equal(Assert.Single(analysis.Coverage).Gaps.Select(gap => gap.Callee).Order(StringComparer.Ordinal),
                     Assert.Single(measurement.Coverage).SemanticGaps);
    }

    private static Task<LoadedTarget> Load(string target, CancellationToken cancellationToken) =>
        Task.FromResult(new LoadedTarget(FixtureSolution.Create(("Case.cs", SOURCE)), 1, 1, [], new Owner()));

    private sealed class Owner : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
