using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class UnsupportedOperationCounterTests
{
    [Fact]
    public void Counts_unsupported_operations_of_a_reached_body()
    {
        var run = Analyze(Source("var message = $\"value {value}\";") + Startup());

        Assert.Equal(3, run.Counter("unsupported-operation"));
    }

    [Fact]
    public void Ignores_a_body_outside_the_reachable_set()
    {
        var run = Analyze(Source("") + "public static class Unused { public static void Read(int value) { var message = $\"value {value}\"; } }" + Startup());

        Assert.True(run.Collection.Coverage.Counters.ContainsKey("unsupported-operation"));
        Assert.Equal(0, run.Counter("unsupported-operation"));
    }

    [Fact]
    public async Task Counts_each_process_scope_apart()
    {
        var options = new FixtureOptions
        {
            ProjectOutputKinds = new Dictionary<string, OutputKind>
            {
                ["First"] = OutputKind.ConsoleApplication,
                ["Second"] = OutputKind.ConsoleApplication
            }
        };
        var solution = FixtureSolution.CreateProjects(options,
            ("First", "Program.cs", "public static class Program { public static void Main() { } }"),
            ("First", "Case.cs", Usings + Source("var message = $\"value {value}\";", className: "FirstController") + Startup("services.AddControllers();")),
            ("Second", "Program.cs", "public static class Program { public static void Main() { } }"),
            ("Second", "Case.cs", Usings + Source("", className: "SecondController") + Startup("services.AddControllers();")));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        var first = result.Coverage.Single(scope => scope.ScopeId == "First").Skips["unsupported-operation"];
        var second = result.Coverage.Single(scope => scope.ScopeId == "Second").Skips["unsupported-operation"];
        Assert.Equal(second + 3, first);
    }

    [Fact]
    public void Body_reached_in_two_contexts_counts_each_operation_once()
    {
        var run = Analyze(Source("Shared.Read(value);", "public void Get(int value) => Shared.Read(value);") +
                          "public static class Shared { public static void Read(int value) { var message = $\"value {value}\"; } }" + Startup());

        Assert.Equal(3, run.Counter("unsupported-operation"));
    }

    [Fact]
    public void Is_zero_for_a_scope_whose_operations_all_lower()
    {
        var run = Analyze(Source("var number = 1;") + Startup());

        Assert.True(run.Collection.Coverage.Counters.ContainsKey("unsupported-operation"));
        Assert.Equal(0, run.Counter("unsupported-operation"));
    }

    [Fact]
    public async Task Report_gives_the_counter_its_meaning()
    {
        var solution = FixtureSolution.Create(("Case.cs", Usings + Source("try { throw new Exception(); } catch (Exception e) { var message = $\"value {e}\"; GC.KeepAlive(e); }") + Startup()));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        var markdown = ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown;

        Assert.True(result.Coverage[0].Skips["unsupported-operation"] > 0);
        Assert.Contains("unsupported-operation ", markdown, StringComparison.Ordinal);
        Assert.Contains("operations inside reached bodies the lowering does not model: their operands are lowered, but what the operation itself binds, reads or calls is lost",
                        markdown, StringComparison.Ordinal);
    }

    private static string Source(string body, string extraMember = "", string className = "CaseController") => $$"""
        public class {{className}} : ControllerBase
        {
            public void Post(int value) { {{body}} }
            {{extraMember}}
        }
        """;
}
