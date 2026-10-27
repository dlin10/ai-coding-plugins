using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Serialization;
using Microsoft.CodeAnalysis;
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
        var (measurement, analysis) = await MetricsCommand.MeasureAsync("Fixture.slnx", AnalysisLimits.Default, Load, ModelGeneration.None, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(measurement, MetricsJsonContext.Default.Measurement));

        var scope = Assert.Single(json.RootElement.GetProperty("coverage").EnumerateArray());
        Assert.Equal(2, scope.GetProperty("counters").GetProperty(CoverageCounters.SEMANTIC_GAP).GetInt32());
        Assert.Equal(["System.Console.Write(object)", "System.Console.WriteLine(object)"],
                     scope.GetProperty("semanticGaps").EnumerateArray().Select(callee => callee.GetString()));
        Assert.Equal(Assert.Single(analysis.Coverage).Gaps.Select(gap => gap.Callee).Order(StringComparer.Ordinal),
                     Assert.Single(measurement.Coverage).SemanticGaps);
    }

    [Fact]
    public async Task Measurement_carries_the_deciding_gaps_of_each_scope()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.Extensions.DependencyInjection;
            public sealed class Tally { public int Count; }
            public sealed class TallyController(Tally tally) : ControllerBase
            {
                public void Post() { System.Console.WriteLine(tally); }
            }
            public static class Startup
            {
                public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
                {
                    services.AddControllers(); services.AddSingleton<Tally>(); app.MapControllers();
                }
            }
            """;
        Task<LoadedTarget> LoadScopes(string _, CancellationToken token)
        {
            var options = new FixtureOptions
            {
                ProjectOutputKinds = new Dictionary<string, OutputKind>
                {
                    ["A"] = OutputKind.ConsoleApplication, ["B"] = OutputKind.ConsoleApplication, ["C"] = OutputKind.ConsoleApplication
                }
            };
            const string main = "System.Console.WriteLine();";
            var solution = FixtureSolution.CreateProjects(options,
                ("A", "Program.cs", main), ("A", "Case.cs", source),
                ("B", "Program.cs", main), ("B", "Case.cs", source.Replace("WriteLine(tally)", "Write(tally)", StringComparison.Ordinal)),
                ("C", "Program.cs", main), ("C", "Case.cs", source.Replace("System.Console.WriteLine(tally);", "", StringComparison.Ordinal)));
            return Task.FromResult(new LoadedTarget(solution, 3, 3, [], new Owner()));
        }

        var (measurement, analysis) = await MetricsCommand.MeasureAsync(@"C:\fixture\Fixture.slnx", AnalysisLimits.Default, LoadScopes, ModelGeneration.None, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(measurement, MetricsJsonContext.Default.Measurement));
        var scopes = json.RootElement.GetProperty("coverage").EnumerateArray().ToArray();

        Assert.Equal("1.4", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(["A", "B", "C"], scopes.Select(scope => scope.GetProperty("scopeId").GetString()));
        foreach (var (scopeId, callee) in new[] { ("A", "System.Console.WriteLine(object)"), ("B", "System.Console.Write(object)") })
        {
            var scope = Assert.Single(scopes, scope => scope.GetProperty("scopeId").GetString() == scopeId);
            var gap = Assert.Single(scope.GetProperty("decidingGaps").EnumerateArray());
            var finding = Assert.Single(analysis.Findings, finding => finding.Resource.Scope == scopeId);
            var decided = Assert.Single(gap.GetProperty("findings").EnumerateArray());

            Assert.Equal(["callee", "findings", "callSites"], gap.EnumerateObject().Select(property => property.Name));
            Assert.Equal(callee, gap.GetProperty("callee").GetString());
            Assert.Equal(["fingerprint", "checks"], decided.EnumerateObject().Select(property => property.Name));
            Assert.Equal(finding.Fingerprint, decided.GetProperty("fingerprint").GetString());
            Assert.Equal(["operation"], decided.GetProperty("checks").EnumerateArray().Select(check => check.GetString()));
            Assert.Equal(new[] { scopeId + "/Case.cs:8" }, gap.GetProperty("callSites").EnumerateArray().Select(site => site.GetString()));
            Assert.Same(Assert.Single(analysis.Coverage, coverage => coverage.ScopeId == scopeId).DecidingGaps,
                        Assert.Single(measurement.Coverage, coverage => coverage.ScopeId == scopeId).DecidingGaps);
        }
        Assert.Empty(scopes[2].GetProperty("decidingGaps").EnumerateArray());
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
