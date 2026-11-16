using Common.Mcp;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Runs;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

/// <summary>What the product's entry points ask of the analyzer for the generated model layer: MCP and metrics read by default;
/// generation requires an explicit recording or census budget.</summary>
public sealed class ModelGenerationModeTests
{
    private const string TARGET = "Fixture.slnx";
    private const string OUT = "metrics.json";

    [Fact]
    public async Task Mcp_run_passes_ReadOnly()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var demo = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        var modes = new List<ModelGeneration>();
        var offlineModes = new List<bool>();

        // The run's own load, over the demo; the analysis is the test's, which only sees what the run asks for.
        var analysis = await SolutionAnalysis.RunAsync(demo, CancellationToken.None, (path, token) => new MsBuildSolutionLoader().LoadAsync(path, token),
                                                       (_, _, _, generation, sources) =>
                                                       {
                                                           modes.Add(generation);
                                                           offlineModes.Add(sources.Statistics.Offline);
                                                           return Task.FromResult(new AnalysisResult([], [], [], [], [], [], PairCounters.None, []));
                                                       }, offline: true);

        Assert.False(analysis.LoadFailed);
        Assert.False(analysis.AnalysisFailed);
        Assert.Equal([ModelGeneration.ReadOnly], modes);
        Assert.Equal([true], offlineModes);
    }

    [Fact]
    public async Task Generation_option_parses_on_read_off_and_refuses_another()
    {
        // The command reads unless generation and its budget are explicitly selected.
        Assert.Equal(ModelGeneration.ReadOnly, Parse().Generation);
        Assert.Equal(ModelGeneration.Generate, Parse("--generation", "on", "--recording").Generation);
        Assert.Contains("--generation on needs --recording or --census.",
                        Assert.Throws<ArgumentException>(() => Parse("--generation", "on")).Message, StringComparison.Ordinal);
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

    [Fact]
    public void Mcp_run_cannot_ask_for_the_recording_budget()
    {
        // The MCP run's seam to the analyzer carries no generation budget.
        Assert.DoesNotContain(typeof(SolutionAnalyzer).GetMethod(nameof(SolutionAnalyzer.Invoke))!.GetParameters(),
                              parameter => parameter.ParameterType == typeof(GenerationBudget) || parameter.ParameterType == typeof(GenerationBudget?));
    }

    [Fact]
    public async Task Recording_option_selects_the_recording_budget_only_for_a_run_that_generates()
    {
        // Reading has no budget; generation explicitly selects the recording's, beside the other options in any order.
        Assert.Null(Parse().Budget);
        Assert.Null(Parse("--generation", "read").Budget);
        Assert.Equal(GenerationBudget.Recording, Parse("--recording", "--generation", "on").Budget);
        Assert.Equal((ModelGeneration.Generate, GenerationBudget.Recording), (Parse("--generation", "on", "--recording").Generation,
                                                                             Parse("--recording", "--generation", "on").Budget));

        // A recording requires an explicit generating mode.
        foreach (var arguments in new[] { new[] { "--recording", "--generation", "read" }, ["--generation", "off", "--recording"], ["--recording"] })
        {
            using var error = new StringWriter();
            Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--target", TARGET, "--out", OUT, .. arguments], error));
            Assert.Contains("--recording needs --generation on.", error.ToString(), StringComparison.Ordinal);
        }

        // The option takes no value and comes once.
        foreach (var arguments in new[] { new[] { "--recording", "--recording" }, ["--recording", "on"] })
        {
            using var error = new StringWriter();
            Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--target", TARGET, "--out", OUT, .. arguments], error));
            Assert.Contains($"Unexpected argument '{arguments[1]}'.", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Census_option_selects_the_census_budget_and_its_folder_only_for_a_run_that_generates()
    {
        // The census folder stands for the run's repository root; --settle reads the census as written instead of leaving it out.
        var census = Parse("--census", "scratch/census", "--generation", "on");
        Assert.Equal((GenerationBudget.Census, "scratch/census", false), (census.Budget, census.Census, census.Settle));
        Assert.True(Parse("--settle", "--census", "scratch/census", "--generation", "on").Settle);
        Assert.Null(Parse().Budget);
        Assert.Null(Parse().Census);

        foreach (var arguments in new[] { new[] { "--census", "c", "--generation", "read" }, ["--census", "c", "--recording"], ["--recording", "--census", "c"], ["--census", "c"], ["--census", "c", "--generation", "off"] })
        {
            using var error = new StringWriter();
            Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--target", TARGET, "--out", OUT, .. arguments], error));
            Assert.Contains("--census needs a folder and --generation on, and does not go with --recording.", error.ToString(), StringComparison.Ordinal);
        }

        using var settleAlone = new StringWriter();
        Assert.Equal(ExitCode.UsageError, await MetricsCommand.RunAsync(["--target", TARGET, "--out", OUT, "--settle"], settleAlone));
        Assert.Contains("--settle needs --census.", settleAlone.ToString(), StringComparison.Ordinal);
    }

    private static MetricsCommand.Options Parse(params string[] extra) => MetricsCommand.Options.Parse(["--target", TARGET, "--out", OUT, .. extra]);

    [Fact]
    public async Task MeasureAsync_refuses_Generate_without_a_budget()
    {
        var repository = Directory.CreateTempSubdirectory("ch-metrics-budget-").FullName;
        try
        {
            var calls = 0;
            var loads = 0;
            var setup = new GenerationSetup(ModelGeneration.ReadOnly, Generator: (_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("must not generate");
            }) with { Mode = ModelGeneration.Generate };
            MetricsCommand.TargetLoad load = (_, _) =>
            {
                loads++;
                using var pass = GenerationPass.Create(setup, repository, CancellationToken.None);
                throw new InvalidOperationException("must refuse before loading");
            };
            var error = await Assert.ThrowsAsync<ArgumentException>(() => MetricsCommand.MeasureAsync(Path.Combine(repository, TARGET),
                AnalysisLimits.Default, load, setup.Mode, CancellationToken.None));
            Assert.Contains("Generate requires a Recording or Census budget.", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, calls);
            Assert.Equal(0, loads);
            Assert.Empty(Directory.EnumerateFileSystemEntries(repository));
        }
        finally { Directory.Delete(repository, recursive: true); }
    }
}
