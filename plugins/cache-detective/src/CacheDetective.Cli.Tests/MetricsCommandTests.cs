using System.Diagnostics;
using System.Text.Json;
using CacheDetective;
using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Events;
using CacheDetective.Serialization;
using CacheDetective.Workspaces;
using Xunit;

namespace CacheDetective.Tests;

public sealed class MetricsCommandTests
{
    [Fact]
    public async Task Two_runs_produce_the_same_sample_set()
    {
        using var repository = await TestRepository.CreateAsync();
        var first = repository.File("first.json");
        var second = repository.File("second.json");
        Assert.Equal(0, await Sample(repository, first));
        Assert.Equal(0, await Sample(repository, second));
        Assert.Equal(await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second));
    }

    [Fact]
    public async Task Sample_order_is_not_alphabetical()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("sample.json");
        Assert.Equal(0, await Sample(repository, output));
        var ids = Rows(output).Select(row => row.GetProperty("id").GetString()!).ToArray();
        Assert.NotEqual(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public async Task Stable_hash_order_is_deterministic()
    {
        using var repository = await TestRepository.CreateAsync();
        var first = repository.File("first.json");
        var second = repository.File("second.json");
        await Sample(repository, first);
        await Sample(repository, second);
        Assert.Equal(Rows(first).Select(row => row.GetProperty("id").GetString()), Rows(second).Select(row => row.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Equal_templates_in_different_stores_do_not_collapse()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("sample.json");
        await Sample(repository, output);
        Assert.Contains(Rows(output), row => row.GetProperty("id").GetString()!.Contains("memory:same", StringComparison.Ordinal));
        Assert.Contains(Rows(output), row => row.GetProperty("id").GetString()!.Contains("distributed:same", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requested_sample_count_is_honoured()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("sample.json");
        Assert.Equal(0, await Sample(repository, output, 1));
        Assert.Single(Rows(output));
        await Sample(repository, output, 99);
        Assert.True(Rows(output).Count < 99);
    }

    [Fact]
    public async Task Efwrite_sample_uses_heuristic_write_sites()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("efwrite.json");
        Assert.Equal(0, await MetricsCommand.RunAsync(["--sample", "efwrite", "--count", "3", "--root", repository.Path, "--solution", "App.csproj", "--out", output]));
        Assert.Equal("efwrite", Read(output).GetProperty("sample").GetString());
    }

    [Fact]
    public async Task Repeated_efwrite_site_is_listed_once()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("efwrite.json");
        await MetricsCommand.RunAsync(["--sample", "efwrite", "--count", "3", "--root", repository.Path, "--solution", "App.csproj", "--out", output]);
        var ids = Rows(output).Select(row => row.GetProperty("id").GetString()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Cache_coverage_uses_the_specified_formula()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("metrics.json");
        await Measure(repository, output);
        var document = Read(output);
        var operations = document.GetProperty("cacheOperations").GetInt32();
        var unresolved = document.GetProperty("unresolved");
        var denominator = operations + Count(unresolved, "key") + Count(unresolved, "cache_api");
        var coverage = document.GetProperty("cacheSiteCoverage");
        Assert.Equal((double)operations / denominator, coverage.GetProperty("value").GetDouble());
        Assert.Equal("not_applicable", Read(output).GetProperty("sqlSiteCoverage").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Zero_coverage_denominator_is_not_applicable()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        await File.WriteAllTextAsync(repository.File("App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(repository.File("App.cs"), "public class ControllerBase { } public sealed class AppController : ControllerBase { public void Get() { } }");
        var output = repository.File("metrics.json");
        await Measure(repository, output);
        var coverage = Read(output).GetProperty("cacheSiteCoverage");
        Assert.Equal("not_applicable", coverage.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Load_coverage_and_missing_projects_are_recorded()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("metrics.json");
        await Measure(repository, output);
        var document = Read(output);
        Assert.True(document.GetProperty("loadComplete").GetBoolean());
        Assert.True(document.GetProperty("projectsExpected").GetInt32() >= 1);
        Assert.True(document.GetProperty("projectsLoaded").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Array, document.GetProperty("missingProjects").ValueKind);
        Assert.Equal(JsonValueKind.Array, document.GetProperty("emptyProjects").ValueKind);
    }

    [Fact]
    public async Task Solution_is_recorded_and_non_empty()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("metrics.json");
        await Measure(repository, output);
        Assert.Equal("App.csproj", Read(output).GetProperty("solution").GetString());
    }

    [Fact]
    public async Task Recognizer_file_hash_and_count_are_recorded()
    {
        using var repository = await TestRepository.CreateAsync();
        var recognizers = repository.File("recognizers.json");
        await File.WriteAllTextAsync(recognizers, RecognizerJson());
        var output = repository.File("metrics.json");
        Assert.Equal(0, await MetricsCommand.RunAsync(["--root", repository.Path, "--solution", "App.csproj", "--out", output, "--recognizers", recognizers]));
        var document = Read(output);
        Assert.Equal(Path.GetFullPath(recognizers), document.GetProperty("recognizersFile").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.GetProperty("recognizersHash").GetString()));
        Assert.Equal(1, document.GetProperty("recognizersApplied").GetInt32());
    }

    [Fact]
    public async Task Run_without_recognizers_records_zero_applied()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("metrics.json");
        await Measure(repository, output);
        Assert.Equal(0, Read(output).GetProperty("recognizersApplied").GetInt32());
    }

    [Fact]
    public async Task Recognizer_changes_the_measurement()
    {
        using var repository = await TestRepository.CreateAsync();
        var baseline = repository.File("baseline.json");
        var recognized = repository.File("recognized.json");
        var recognizers = repository.File("recognizers.json");
        await File.WriteAllTextAsync(recognizers, RecognizerJson());
        await Measure(repository, baseline);
        await MetricsCommand.RunAsync(["--root", repository.Path, "--solution", "App.csproj", "--out", recognized, "--recognizers", recognizers]);
        Assert.NotEqual(Read(baseline).GetProperty("cacheOperations").GetInt32(), Read(recognized).GetProperty("cacheOperations").GetInt32());
    }

    [Fact]
    public async Task Declared_recognizer_creates_likely_edges()
    {
        using var repository = await TestRepository.CreateAsync();
        var recognizers = repository.File("recognizers.json");
        await File.WriteAllTextAsync(recognizers, RecognizerJson());
        var output = repository.File("metrics.json");
        await MetricsCommand.RunAsync(["--root", repository.Path, "--solution", "App.csproj", "--out", output, "--recognizers", recognizers]);
        Assert.Equal(1, Read(output).GetProperty("recognizersApplied").GetInt32());

        // A declared recognizer is somebody's claim about an API this tool does not know, so what it
        // produces is inferred and must say so. Indexing the same fixture with the same recognizer shows
        // the confidence the edges actually carry, which the measurement's counts cannot.
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(repository.File("App.csproj"));
        var declared = new CacheRecognizer("MysteryCache", "redis",
                                           [new CacheMethodRecognizer("Set", CacheSemantic.Set, 0)], Confidence.Likely);
        var graph = await new CallGraphIndexer(new IndexerOptions([declared, .. CacheRecognizers.All], EventRecognizers.All))
            .IndexAsync(loaded.Solution, "App.csproj");

        var mystery = Assert.Single(graph.Edges.OfType<Caches>(),
                                    edge => edge.To is CacheKey { Template: "mystery" });
        Assert.Equal(Confidence.Likely, mystery.Confidence);
        Assert.All(graph.Edges.OfType<Caches>().Where(edge => edge.To is CacheKey { Template: "orders" }),
                   edge => Assert.Equal(Confidence.Confirmed, edge.Confidence));
    }

    /// <summary>
    /// The two intervals are the whole of the total, and they are the whole of it because the solution was
    /// loaded once. Asserting only that the three numbers add up says nothing: they would add up just as
    /// well if the measurement had loaded twice and reported one of the loads.
    /// </summary>
    [Fact]
    public async Task Index_interval_does_not_include_a_second_index()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("metrics.json");
        var loads = 0;

        Assert.Equal(0, await MetricsCommand.RunAsync(["--root", repository.Path, "--solution", "App.csproj", "--out", output],
                                                       (path, token) =>
                                                       {
                                                           Interlocked.Increment(ref loads);
                                                           return new MsBuildSolutionLoader().LoadAsync(path, token);
                                                       }));

        Assert.Equal(1, loads);
        var document = Read(output);
        Assert.Equal(document.GetProperty("loadSeconds").GetDouble() + document.GetProperty("indexSeconds").GetDouble(), document.GetProperty("totalSeconds").GetDouble());
    }

    [Fact]
    public async Task Sample_does_not_overwrite_labels_without_force()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = repository.File("sample.json");
        await File.WriteAllTextAsync(output, SampleJson("role", "cache", "cache"));
        Assert.Equal(2, await Sample(repository, output));
    }

    [Fact]
    public async Task Compare_detects_file_line_and_prediction_changes()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = await ComparableSample(repository);
        await File.WriteAllTextAsync(output, (await File.ReadAllTextAsync(output)).Replace("\"file\":", "\"file\": \"changed.cs\", \"originalFile\":", StringComparison.Ordinal));
        Assert.Equal(1, await Compare(repository, output));
    }

    [Fact]
    public async Task Compare_detects_target_count_change()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = await ComparableSample(repository);
        await File.WriteAllTextAsync(output, (await File.ReadAllTextAsync(output)).Replace("\"targetCount\": 2", "\"targetCount\": 3", StringComparison.Ordinal));
        Assert.Equal(1, await Compare(repository, output));
    }

    [Fact]
    public async Task Compare_refuses_a_different_revision()
    {
        using var repository = await TestRepository.CreateAsync();
        var output = await ComparableSample(repository);
        var head = await Head(repository);
        await File.WriteAllTextAsync(output, (await File.ReadAllTextAsync(output)).Replace(head, "other", StringComparison.Ordinal));
        Assert.Equal(2, await Compare(repository, output));
    }

    [Fact]
    public async Task Compare_refuses_a_dirty_tree()
    {
        using var repository = await TestRepository.CreateAsync();
        await File.WriteAllTextAsync(repository.File("dirty.txt"), "dirty");
        var output = repository.File("sample.json");
        await File.WriteAllTextAsync(output, $"{{\"sample\":\"role\",\"targetCount\":2,\"candidateCount\":0,\"revision\":\"{await Head(repository)}\",\"rows\":[]}}");
        await WriteManifest(repository, await Head(repository), 2);
        Assert.Equal(2, await Compare(repository, output));
    }

    /// <summary>A sample may only be drawn from a corpus whose revision is knowable and whose tree is
    /// clean: the revision it is stamped with is the whole basis for comparing it later.</summary>
    [Fact]
    public async Task Sample_refuses_a_dirty_tree()
    {
        using var repository = await TestRepository.CreateAsync();
        await File.WriteAllTextAsync(repository.File("dirty.cs"), "// uncommitted");
        Assert.Equal(2, await Sample(repository, repository.File("sample.json")));
    }

    [Fact]
    public async Task Sample_refuses_a_corpus_with_no_revision()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        await File.WriteAllTextAsync(repository.File("App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(repository.File("App.cs"), "public class ControllerBase { } public sealed class AppController : ControllerBase { public void Get() { } }");

        // No git repository at all, so 'git rev-parse HEAD' produces nothing to pin the sample to.
        Assert.Equal(2, await Sample(repository, repository.File("sample.json")));
    }

    /// <summary>The manifest says what each file holds, so renaming one sample over another does not let
    /// its content be checked under the other's rules.</summary>
    [Fact]
    public async Task Compare_refuses_a_sample_of_another_kind()
    {
        using var repository = await TestRepository.CreateAsync();
        var role = await ComparableSample(repository);
        var efwrite = repository.File("ef-write-sample.json");
        File.Copy(role, efwrite, overwrite: true);
        await WriteManifest(repository, await Head(repository), 2, "ef-write-sample.json", "efwrite");

        Assert.Equal(2, await Compare(repository, efwrite));
    }

    [Fact]
    public async Task Role_accuracy_is_calculated()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        var sample = repository.File("sample.json");
        await File.WriteAllTextAsync(sample, SampleJson("role", "cache", "cache"));
        Assert.Equal(0, await MetricsCommand.RunAsync(["--score", sample]));
        Assert.Equal(1, Read(sample).GetProperty("metrics").GetProperty("accuracy").GetDouble());
    }

    [Fact]
    public async Task Efwrite_accuracy_is_calculated()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        var sample = repository.File("sample.json");
        await File.WriteAllTextAsync(sample, SampleJson("efwrite", "write", "write"));
        await MetricsCommand.RunAsync(["--score", sample]);
        Assert.Equal(1, Read(sample).GetProperty("metrics").GetProperty("accuracy").GetDouble());
    }

    [Fact]
    public async Task Efwrite_false_positive_rate_is_calculated()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        var sample = repository.File("sample.json");
        await File.WriteAllTextAsync(sample, SampleJson("efwrite", "write", "false_write"));
        await MetricsCommand.RunAsync(["--score", sample]);
        Assert.Equal(1, Read(sample).GetProperty("metrics").GetProperty("falsePositiveRate").GetDouble());
    }

    [Fact]
    public async Task Score_first_fill_succeeds()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        var sample = repository.File("sample.json");
        await File.WriteAllTextAsync(sample, SampleJson("role", "cache", "cache"));
        Assert.Equal(0, await MetricsCommand.RunAsync(["--score", sample]));
        Assert.True(Read(sample).TryGetProperty("metrics", out _));
    }

    [Fact]
    public async Task Score_mismatch_fails()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        var sample = repository.File("sample.json");
        await File.WriteAllTextAsync(sample, SampleJson("role", "cache", "cache"));
        await MetricsCommand.RunAsync(["--score", sample]);
        await File.WriteAllTextAsync(sample, (await File.ReadAllTextAsync(sample)).Replace("\"accuracy\": 1", "\"accuracy\": 0", StringComparison.Ordinal));
        Assert.Equal(1, await MetricsCommand.RunAsync(["--score", sample]));
    }

    [Fact]
    public async Task Score_all_unclear_is_not_applicable()
    {
        using var repository = await TestRepository.CreateEmptyAsync();
        var sample = repository.File("sample.json");
        await File.WriteAllTextAsync(sample, SampleJson("role", "cache", "unclear"));
        await MetricsCommand.RunAsync(["--score", sample]);
        var metrics = Read(sample).GetProperty("metrics");
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("accuracy").ValueKind);
        Assert.Equal("not_applicable", metrics.GetProperty("status").GetString());
    }

    /// <summary>Every measurement and sample record round-trips through the source-generated context, so
    /// nothing the command writes or reads goes through the reflection serializer.</summary>
    [Fact]
    public void A_sample_round_trips_through_the_source_generated_context()
    {
        var sample = new MetricsSample("efwrite", 20, 10, "abc123",
                                       [new SampleRow("id", "src/App.cs", 42, "write", "true_write", "because")],
                                       new SampleScores(1, 0, 0, "ok"));

        var json = JsonSerializer.Serialize(sample, MetricsJsonContext.Default.MetricsSample);
        var read = JsonSerializer.Deserialize(json, MetricsJsonContext.Default.MetricsSample)!;

        Assert.Equal(sample.Sample, read.Sample);
        Assert.Equal(sample.Revision, read.Revision);
        Assert.Equal("true_write", Assert.Single(read.Rows).Verdict);
        Assert.Equal(1, read.Metrics!.Accuracy);
        Assert.Contains("\"verdict\": \"true_write\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measurement_round_trips_through_the_source_generated_context()
    {
        var coverage = new CoverageMetric(0.5, "ok");
        var measurement = new Measurement("App.sln", 1, 2, 3, 4, 5, 6, new Dictionary<string, int> { ["key"] = 1 }, 0,
                                          1, 1, [], [], true, "abc123", true, null, null, 0, coverage, coverage, true,
                                          new MeasurementCounts(4, 5, 6), new MeasurementCoverage(coverage, coverage));

        var json = JsonSerializer.Serialize(measurement, MetricsJsonContext.Default.Measurement);
        var read = JsonSerializer.Deserialize(json, MetricsJsonContext.Default.Measurement)!;

        Assert.Equal("App.sln", read.Solution);
        Assert.True(read.CleanWorktree);
        Assert.Equal(4, read.Counts.Vertices);
        Assert.Equal(0.5, read.Coverage.Cache.Value);
    }

    private static Task<int> Measure(TestRepository repository, string output) =>
        MetricsCommand.RunAsync(["--root", repository.Path, "--solution", "App.csproj", "--out", output]);

    private static Task<int> Sample(TestRepository repository, string output, int count = 3) =>
        MetricsCommand.RunAsync(["--sample", "role", "--count", count.ToString(), "--root", repository.Path, "--solution", "App.csproj", "--out", output]);

    private static Task<int> Compare(TestRepository repository, string output) =>
        MetricsCommand.RunAsync(["--compare", output, "--root", repository.Path, "--solution", "App.csproj"]);

    private static async Task<string> ComparableSample(TestRepository repository)
    {
        var output = repository.File("sample.json");
        Assert.Equal(0, await Sample(repository, output, 2));
        await WriteManifest(repository, await Head(repository), 2);
        return output;
    }

    /// <summary>The corpus revision as git reports it, which is what a drawn sample is stamped with.</summary>
    private static async Task<string> Head(TestRepository repository)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repository.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("rev-parse");
        info.ArgumentList.Add("HEAD");
        using var process = Process.Start(info)!;
        var head = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return head.Trim();
    }

    private static async Task WriteManifest(TestRepository repository, string revision, int targetCount,
                                            string file = "sample.json", string sample = "role")
    {
        var directory = repository.File("skills/scan/evals/metrics");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            $$"""
              {
                "revision": "{{revision}}",
                "files": [ { "file": "{{file}}", "sample": "{{sample}}", "targetCount": {{targetCount}} } ],
                "samples": { "{{sample}}": { "targetCount": {{targetCount}} } }
              }
              """);
    }

    private static JsonElement Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> Rows(string path) => Read(path).GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToArray();
    private static int Count(JsonElement objectValue, string name) => objectValue.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
    private static string RecognizerJson() => """[{ "type": "MysteryCache", "store": "redis", "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }] }]""";
    private static string SampleJson(string sample, string prediction, string verdict) => $$"""{ "sample": "{{sample}}", "targetCount": 1, "candidateCount": 1, "revision": "unknown", "rows": [{ "id": "id", "file": "file.cs", "line": 1, "prediction": "{{prediction}}", "verdict": "{{verdict}}" }] }""";

    private static async Task Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TestRepository : IDisposable
    {
        private TestRepository(string path) => Path = path;
        public string Path { get; }
        public string File(string relative) => System.IO.Path.Combine(Path, relative);

        /// <summary>
        /// A corpus that can actually be sampled: sampling is refused on a dirty tree and on a corpus with
        /// no revision, because a sample is pinned by the revision it was drawn at. The generated JSON is
        /// ignored so that writing a sample into the fixture does not itself make the tree dirty and
        /// refuse the next draw.
        /// </summary>
        public static async Task<TestRepository> CreateAsync()
        {
            var repository = await CreateEmptyAsync();
            // The samples this fixture writes, and the obj/bin MSBuild leaves behind when it loads the
            // project, are not part of the corpus; without ignoring them the first draw would dirty the
            // tree and the second would be refused.
            await System.IO.File.WriteAllTextAsync(repository.File(".gitignore"), "*.json\nobj/\nbin/\n");
            await System.IO.File.WriteAllTextAsync(repository.File("App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>");
            await System.IO.File.WriteAllTextAsync(repository.File("App.cs"), """
                using System;
                namespace Microsoft.Extensions.Caching.Memory { public interface IMemoryCache { void Set(string key, object value, TimeSpan ttl); } }
                namespace Microsoft.Extensions.Caching.Distributed { public interface IDistributedCache { void Set(string key, object value, TimeSpan ttl); } }
                public class ControllerBase { }
                public sealed class MysteryCache { public void Set(string key) { } }
                public sealed class AppController : ControllerBase
                {
                    private Microsoft.Extensions.Caching.Memory.IMemoryCache memory = null!;
                    private Microsoft.Extensions.Caching.Distributed.IDistributedCache distributed = null!;
                    public void Get() { memory.Set("same", 1, TimeSpan.FromSeconds(1)); memory.Set("orders", 1, TimeSpan.FromSeconds(1)); distributed.Set("same", 1, TimeSpan.FromSeconds(1)); new MysteryCache().Set("mystery"); }
                }
                """);
            await Git(repository.Path, "init");
            await Git(repository.Path, "add", ".");
            await Git(repository.Path, "-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-m", "corpus");
            return repository;
        }

        public static Task<TestRepository> CreateEmptyAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-metrics-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return Task.FromResult(new TestRepository(path));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
