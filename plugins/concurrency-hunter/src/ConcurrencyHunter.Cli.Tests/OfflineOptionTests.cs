using System.Text.Json;
using Common.Mcp;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Mcp;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

[CollectionDefinition("Offline environment", DisableParallelization = true)]
public sealed class OfflineEnvironmentCollection;

[Collection("Offline environment")]
public sealed class OfflineOptionTests
{
    [Fact]
    public void Metrics_offline_accepts_every_generation_mode_recording_and_census()
    {
        MetricsCommand.Options Parse(params string[] extra) =>
            MetricsCommand.Options.Parse(["--target", "Fixture.slnx", "--out", "metrics.json", .. extra]);

        Assert.False(Parse().Offline);
        Assert.True(Parse("--offline").Offline);
        foreach (var mode in new[] { "on", "read", "off" })
        {
            Assert.True(Parse("--offline", "--generation", mode).Offline);
            Assert.True(Parse("--generation", mode, "--offline").Offline);
        }
        Assert.Equal(GenerationBudget.Recording, Parse("--offline", "--recording").Budget);
        Assert.True(Parse("--recording", "--offline").Offline);
        Assert.Equal(GenerationBudget.Census, Parse("--offline", "--census", "census").Budget);
        Assert.True(Parse("--census", "census", "--settle", "--offline").Offline);
        Assert.Throws<ArgumentException>(() => Parse("--offline", "true"));
        Assert.Throws<ArgumentException>(() => Parse("--offline", "--offline"));
    }

    [Fact]
    public void Generate_offline_is_an_optional_flag_without_a_value()
    {
        var args = Arguments("answer.json");
        Assert.False(GenerateCommand.Options.Parse(args).Offline);
        Assert.True(GenerateCommand.Options.Parse(["--offline", .. args]).Offline);
        Assert.True(GenerateCommand.Options.Parse([.. args, "--offline"]).Offline);
        Assert.Throws<ArgumentException>(() => GenerateCommand.Options.Parse([.. args, "--offline", "true"]));
        Assert.Throws<ArgumentException>(() => GenerateCommand.Options.Parse([.. args, "--offline", "--offline"]));
    }

    [Fact]
    public async Task Run_start_passes_offline_to_the_analysis_step_and_defaults_to_online()
    {
        using var environment = new RunTestEnvironment();
        foreach (var offline in new[] { false, true })
        {
            bool? observed = null;
            var registry = environment.Registry((_, _, value) =>
            {
                observed = value;
                return Task.FromResult(new Runs.RunAnalysis(false, false, 1, 1, [],
                    new AnalysisResult([], [], [], [], [], [], PairCounters.None, []), [], 0, 0));
            });
            using var response = JsonDocument.Parse(offline
                ? RunTools.run_start(registry, environment.SolutionPath, offline: true)
                : RunTools.run_start(registry, environment.SolutionPath));
            await registry.WaitForAnalysisAsync(response.RootElement.GetProperty("run_id").GetString()!);
            Assert.Equal(offline, observed);
        }
    }

    [Fact]
    public async Task Source_cache_follows_LOCALAPPDATA_beside_runs_and_is_created_only_on_write()
    {
        using var environment = new RunTestEnvironment();
        var previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", environment.Root);
            var root = Path.Combine(environment.Root, "concurrency-hunter");
            Assert.Equal(root, SourceProvider.ApplicationData);
            using var sources = SourceProvider.Create(offline: true);
            Assert.False(Directory.Exists(root));
            using var library = new SourceLibrary(new Uri("https://unused.invalid/"), adjacent: true, embeddedDocuments: true);
            Assert.Equal(LibraryCodeKind.OriginalSources, (await sources.GetAsync(library.Dll)).Kind);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "sources"), "symbols.pdb", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(root, "runs")));
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", previous); }
    }

    [Fact]
    public async Task Offline_generate_with_a_cold_cache_sends_no_loopback_requests()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        library.Serve(server);
        using var install = Install(library);
        var output = Path.Combine(install.Root, "answer.json");
        using var error = new StringWriter();
        var code = GenerateCommand.Run([.. Arguments(output), "--offline"], error, install.Resolver(), createSources: offline =>
        {
            Assert.True(offline);
            return Sources(library, server, offline);
        });
        Assert.True(code == ExitCode.Ok, error.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(output));
        var used = json.RootElement.GetProperty("generation").GetProperty("code");
        Assert.Equal("decompiled-this-run", used.GetProperty("kind").GetString());
        Assert.Equal("offline", used.GetProperty("reason").GetString());
        Assert.Empty(server.Requests);
        Assert.False(Directory.Exists(library.Cache));
    }

    [Fact]
    public async Task Offline_generate_uses_cached_sources_without_requests()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        library.Serve(server);
        using var install = Install(library);
        var output = Path.Combine(install.Root, "answer.json");
        using var error = new StringWriter();
        var onlineCode = GenerateCommand.Run(Arguments(output), error, install.Resolver(), createSources: offline =>
        {
            Assert.False(offline);
            return Sources(library, server, offline);
        });
        Assert.True(onlineCode == ExitCode.Ok, error.ToString());
        using (var onlineJson = JsonDocument.Parse(File.ReadAllBytes(output)))
            Assert.Equal("sources", onlineJson.RootElement.GetProperty("generation").GetProperty("code").GetProperty("kind").GetString());
        Assert.NotEmpty(server.Requests);
        server.Requests.Clear();
        var code = GenerateCommand.Run([.. Arguments(output), "--offline"], error, install.Resolver(),
                                       createSources: offline => Sources(library, server, offline));
        Assert.True(code == ExitCode.Ok, error.ToString());
        using var json = JsonDocument.Parse(File.ReadAllBytes(output));
        Assert.Equal("sources", json.RootElement.GetProperty("generation").GetProperty("code").GetProperty("kind").GetString());
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Metrics_json_carries_offline_with_zero_downloads_in_every_generation_mode()
    {
        using var environment = new RunTestEnvironment();
        foreach (var mode in new[] { "on", "read", "off" })
        {
            var output = Path.Combine(environment.Root, mode + ".json");
            using var error = new StringWriter();
            var code = await MetricsCommand.RunAsync(["--target", environment.SolutionPath, "--out", output, "--generation", mode, "--offline"],
                error, EmptyLoad, offline => new LibrarySources(offline, Path.Combine(environment.Root, "cache")));
            Assert.True(code == ExitCode.Ok, error.ToString());
            using var json = JsonDocument.Parse(File.ReadAllBytes(output));
            Assert.Equal("1.6", json.RootElement.GetProperty("schemaVersion").GetString());
            var sources = json.RootElement.GetProperty("sources");
            Assert.True(sources.GetProperty("offline").GetBoolean());
            Assert.All(sources.GetProperty("libraries").EnumerateObject(), item => Assert.Equal(0, item.Value.GetInt32()));
            Assert.All(sources.GetProperty("downloads").EnumerateObject(), item => Assert.Equal(0, item.Value.GetDouble()));
        }
        Assert.False(Directory.Exists(Path.Combine(environment.Root, "cache")));
    }

    [Fact]
    public async Task Metrics_carries_source_counts_and_actual_loopback_download_totals()
    {
        await using var server = new LoopbackSymbolServer();
        using var library = new SourceLibrary(server.Address);
        library.Serve(server);
        using var sources = Sources(library, server, offline: false);
        async Task<LoadedTarget> Load(string path, CancellationToken token)
        {
            await sources.GetAsync(library.Dll, token);
            await sources.GetAsync(library.Dll, token);
            return await EmptyLoad(path, token);
        }
        var (measurement, analysis) = await MetricsCommand.MeasureAsync(Path.Combine(library.Root, "Fixture.slnx"), AnalysisLimits.Default,
            Load, ModelGeneration.None, CancellationToken.None, sources: sources);
        Assert.Equal(sources.Statistics, analysis.Sources);
        Assert.Equal(analysis.Sources, measurement.Sources);
        Assert.False(measurement.Sources.Offline);
        Assert.Equal(new SourceLibraryCounts(1, 0, 0), measurement.Sources.Libraries);
        Assert.Equal(server.Requests.Count, measurement.Sources.Downloads.Requests);
        Assert.Equal(library.PdbBytes.Length + library.Documents.Values.Sum(bytes => bytes.Length), measurement.Sources.Downloads.Bytes);
        Assert.True(measurement.Sources.Downloads.Seconds > 0);
    }

    private static string[] Arguments(string output) =>
        ["--assembly", "library", "--version", "8.0", "--framework", "net8.0", "--member", "M:C0.M", "--out", output];

    private static GenerationInstall Install(SourceLibrary library)
    {
        var install = new GenerationInstall();
        install.SharedFramework("8.0.5", runtime: true, images: new Dictionary<string, byte[]> { ["library"] = File.ReadAllBytes(library.Dll) });
        return install;
    }

    private static LibrarySources Sources(SourceLibrary library, LoopbackSymbolServer server, bool offline) =>
        new(offline, library.Cache, new Uri(server.Address, "symbols/"), new Uri(server.Address, "nuget/"), httpsOnly: false);

    private static Task<LoadedTarget> EmptyLoad(string path, CancellationToken token)
    {
        var solution = FixtureSolution.Create(("Empty.cs", "public class Empty { }"));
        return Task.FromResult(new LoadedTarget(solution, 1, 1, [], solution.Workspace));
    }
}
