using Common.Mcp;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Runs;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

/// <summary>What the product's entry points ask of the analyzer for the generated model layer (R9): the MCP run generates, and the
/// <c>metrics</c> command takes the mode from <c>--generation on|read|off</c>, generating by default.</summary>
public sealed class ModelGenerationModeTests
{
    private const string TARGET = "Fixture.slnx";
    private const string OUT = "metrics.json";

    [Fact]
    public async Task Mcp_run_passes_Generate()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var demo = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        var modes = new List<ModelGeneration>();

        // The run's own load, over the demo; the analysis is the test's, which only sees what the run asks for.
        var analysis = await SolutionAnalysis.RunAsync(demo, CancellationToken.None, (path, token) => new MsBuildSolutionLoader().LoadAsync(path, token),
                                                       (_, _, _, generation) =>
                                                       {
                                                           modes.Add(generation);
                                                           return Task.FromResult(new AnalysisResult([], [], [], [], [], [], PairCounters.None, []));
                                                       });

        Assert.False(analysis.LoadFailed);
        Assert.False(analysis.AnalysisFailed);
        Assert.Equal([ModelGeneration.Generate], modes);
    }

    [Fact]
    public async Task Generation_option_parses_on_read_off_and_refuses_another()
    {
        // The command generates unless it is told not to.
        Assert.Equal(ModelGeneration.Generate, Parse().Generation);
        Assert.Equal(ModelGeneration.Generate, Parse("--generation", "on").Generation);
        Assert.Equal(ModelGeneration.ReadOnly, Parse("--generation", "read").Generation);
        Assert.Equal(ModelGeneration.None, Parse("--generation", "off").Generation);
        // It sits beside the limits without disturbing them, in either order.
        var combined = Parse("--generation", "off", "--limits", "4,8,16");
        Assert.Equal((ModelGeneration.None, 4, 8, 16),
                     (combined.Generation, combined.Limits.MaxAccessPathDepth, combined.Limits.MaxContextsPerMethod, combined.Limits.MaxSccIterations));
        Assert.Equal(ModelGeneration.ReadOnly, Parse("--limits", "4,8,16", "--generation", "read").Generation);

        // The option takes no other spelling, nor a second value, nor none.
        foreach (var other in new[] { "ON", "Read", "generate", "read-only", "none", "true", "" })
        {
            using var error = new StringWriter();
            Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--target", TARGET, "--out", OUT, "--generation", other], error));
            Assert.Contains("--generation", error.ToString(), StringComparison.Ordinal);
        }

        foreach (var arguments in new[] { new[] { "--generation" }, ["--generation", "on", "--generation", "off"] })
        {
            using var error = new StringWriter();
            Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--target", TARGET, "--out", OUT, .. arguments], error));
        }
    }

    private static MetricsCommand.Options Parse(params string[] extra) => MetricsCommand.Options.Parse(["--target", TARGET, "--out", OUT, .. extra]);
}
