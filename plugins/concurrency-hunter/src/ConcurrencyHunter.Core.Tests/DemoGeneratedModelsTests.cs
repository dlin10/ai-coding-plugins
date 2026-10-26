using System.Security.Cryptography;
using System.Text.Json;
using Common.Roslyn;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>The demo's committed generated models (R9, R10): a run of the demo finds every answer it needs in
/// <c>demo/.concurrency-hunter/models/generated/</c>, so it generates nothing, and the cases that rest on those answers have them.</summary>
public sealed class DemoGeneratedModelsTests
{
    private const string CHECK_NOT_NULL = "M:Google.Protobuf.ProtoPreconditions.CheckNotNull``1(``0,System.String)";
    private const string OPTIONS_CREATE = "M:Microsoft.Extensions.Options.Options.Create``1(``0)";

    /// <summary>Every committed generated file names the generator built from the current sources.</summary>
    [Fact]
    public void Committed_files_name_the_current_generator()
    {
        var files = Directory.GetFiles(Path.Combine(DemoModels(), "generated"), "*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        Assert.All(files, path =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(GeneratorVersion.Fingerprint, document.RootElement.GetProperty("generator").GetString());
        });
    }

    /// <summary>A run that only reads the files has nothing left to generate; if it does, a model is missing from the committed files and
    /// <c>build/record-demo-models.ps1</c> writes it.</summary>
    [Fact]
    public async Task A_demo_run_generates_nothing()
    {
        var result = await DemoExpectationTests.SharedDemo.Value;

        Assert.True(result.GenerationWanted.Count == 0,
                    $"A Generate run of the demo would generate {result.GenerationWanted.Count} member(s):{Environment.NewLine}" +
                    string.Join(Environment.NewLine, result.GenerationWanted.Select(item => $"  {item.MemberId} ({item.FileKey})")));
    }

    /// <summary>A run that generates asks the generator for nothing when the committed files answer every target, and so creates no file
    /// and changes no byte or write time below the demo's <c>.concurrency-hunter/</c>. A generator that throws is recorded as a failed
    /// generation and does not fail the run, so the test counts the calls instead of failing inside the generator.</summary>
    [Fact]
    public async Task A_generate_run_of_the_demo_calls_no_generator_and_writes_nothing()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var solutionPath = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        var demoDirectory = Path.GetDirectoryName(solutionPath)!;
        var repositoryRoot = RepositoryRoot.Find(demoDirectory);
        var folder = Path.Combine(repositoryRoot, ".concurrency-hunter");
        var requested = new List<string>();
        var before = Snapshot(folder);

        using (var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath))
        {
            Assert.True(loaded.Coverage.LoadComplete, $"Missing demo projects: {string.Join(", ", loaded.Coverage.MissingProjects)}");
            _ = await PhaseOneAnalyzer.AnalyzeAsync(loaded.Solution, demoDirectory, repositoryRoot,
                                                    new GenerationSetup(ModelGeneration.Generate, Generator: (request, _, _) =>
                                                    {
                                                        requested.Add(request.MemberId);
                                                        throw new InvalidOperationException($"The generator was called for {request.MemberId}.");
                                                    }),
                                                    CancellationToken.None);
        }

        Assert.True(requested.Count == 0, $"A Generate run of the demo called the generator for: {string.Join(", ", requested)}");
        Assert.Equal(before, Snapshot(folder));
    }

    /// <summary>The generated-model case needs the model of <c>ProtoPreconditions.CheckNotNull</c> and the project-opaque case needs the one
    /// of <c>Options.Create</c> to stand over, so both are in the committed files as models, not as refusals; and the project's
    /// <c>opaque</c> entry that stands over the second is in the project model file.</summary>
    [Fact]
    public void The_cases_rest_on_committed_models()
    {
        var generated = Path.Combine(DemoModels(), "generated");
        var models = Directory.EnumerateFiles(generated, "*.json", SearchOption.AllDirectories)
                              .SelectMany(path => Members(path).Where(member => member.HasModel).Select(member => member.Id))
                              .ToArray();

        Assert.Contains(CHECK_NOT_NULL, models);
        Assert.Contains(OPTIONS_CREATE, models);
        using var project = JsonDocument.Parse(File.ReadAllText(Path.Combine(DemoModels(), "demo.json")));
        var entry = Assert.Single(project.RootElement.GetProperty("models").EnumerateArray(),
                                  item => item.GetProperty("member").GetString() == OPTIONS_CREATE);
        Assert.True(entry.GetProperty("opaque").GetBoolean());
    }

    /// <summary>Both Polly <c>Execute</c> overloads have a generated answer without a default-receiver refusal, in the file and coverage.</summary>
    [Fact]
    public async Task Polly_execute_answers_are_no_default_receiver_refusal()
    {
        const string detail = "only a default could fill the Polly.ResiliencePipeline of Recv";
        string[] members = ["M:Polly.ResiliencePipeline.Execute(System.Action)",
                            "M:Polly.ResiliencePipeline.Execute``1(System.Func{``0})"];
        var path = Assert.Single(Directory.GetFiles(Path.Combine(DemoModels(), "generated", "Polly.Core"), "*.json", SearchOption.AllDirectories));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = await DemoExpectationTests.SharedDemo.Value;
        var web = Assert.Single(result.Coverage, coverage => coverage.ScopeId == "Demo.Web");

        // bounded: the fixed list of target members above.
        foreach (var member in members)
        {
            var entry = Assert.Single(document.RootElement.GetProperty("members").EnumerateArray(),
                                      item => item.GetProperty("member").GetString() == member);
            Assert.DoesNotContain(detail, entry.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(detail, JsonSerializer.Serialize(web.GenerationRefusals.Where(item => item.MemberId == member)),
                                  StringComparison.Ordinal);
        }
    }

    /// <summary>The committed answer of Polly's <c>Execute&lt;T&gt;(Func&lt;T&gt;)</c> is not a <c>timeout</c>: its generation finishes within the
    /// member limit.</summary>
    [Fact]
    public void Polly_execute_of_T_is_no_timeout()
    {
        const string member = "M:Polly.ResiliencePipeline.Execute``1(System.Func{``0})";
        var path = Assert.Single(Directory.GetFiles(Path.Combine(DemoModels(), "generated", "Polly.Core"), "*.json", SearchOption.AllDirectories));
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var entry = Assert.Single(document.RootElement.GetProperty("members").EnumerateArray(),
                                  item => item.GetProperty("member").GetString() == member);
        Assert.False(entry.TryGetProperty("refusal", out var refusal) &&
                     refusal.GetProperty("reason").GetString() == GenerationOutcome.TIMEOUT,
                     entry.GetRawText());
    }

    /// <summary>The demo's target auto-property accessors have answers that do not refuse a call for never reaching its body.</summary>
    [Fact]
    public void Demo_auto_property_accessors_reach_their_bodies()
    {
        string[] members = ["M:System.Threading.Channels.Channel`2.get_Reader",
                            "M:System.Threading.Channels.Channel`2.get_Writer",
                            "M:Polly.CircuitBreaker.CircuitBreakerStrategyOptions`1.set_FailureRatio(System.Double)",
                            "M:Polly.CircuitBreaker.CircuitBreakerStrategyOptions`1.set_MinimumThroughput(System.Int32)",
                            "M:Polly.CircuitBreaker.CircuitBreakerStrategyOptions`1.set_OnOpened(System.Func{Polly.CircuitBreaker.OnCircuitOpenedArguments{`0},System.Threading.Tasks.ValueTask})"];
        var entries = Directory.EnumerateFiles(Path.Combine(DemoModels(), "generated"), "*.json", SearchOption.AllDirectories)
                               .SelectMany(path =>
                               {
                                   using var document = JsonDocument.Parse(File.ReadAllText(path));
                                   return document.RootElement.GetProperty("members").EnumerateArray().Select(item => item.Clone()).ToArray();
                               }).ToArray();

        // bounded: the fixed list of target members above.
        foreach (var member in members)
        {
            var entry = Assert.Single(entries, item => item.GetProperty("member").GetString() == member);
            Assert.False(entry.TryGetProperty("refusal", out var refusal) &&
                         refusal.GetProperty("reason").GetString() == GenerationReasons.ANALYSIS_FAILED &&
                         refusal.GetProperty("detail").GetString()!.Contains("the call never reached body", StringComparison.Ordinal),
                         entry.GetRawText());
        }
    }

    private static string DemoModels() =>
        RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", ".concurrency-hunter", "models");

    /// <summary>The members one generated file holds, each with whether it holds a model.</summary>
    /// <param name="path">The file.</param>
    private static IEnumerable<(string Id, bool HasModel)> Members(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("members").EnumerateArray()
                           .Select(member => (member.GetProperty("member").GetString()!, member.TryGetProperty("model", out _)))];
    }

    /// <summary>Every file and folder below a folder, with each file's bytes and write time, in ordinal order of path.</summary>
    /// <param name="folder">The folder.</param>
    private static string[] Snapshot(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal)
                     .Select(path => File.Exists(path)
                                         ? $"{Path.GetRelativePath(folder, path)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))} " +
                                           $"{File.GetLastWriteTimeUtc(path).Ticks}"
                                         : $"{Path.GetRelativePath(folder, path)}{Path.DirectorySeparatorChar}")];
}
