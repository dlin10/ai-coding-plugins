using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Configuration;
using CacheDetective.Graph;
using CacheDetective.Mcp;
using CacheDetective.Tests.Database;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>One declaration of a caching library, read the same way by all three of its readers: the
/// workspace's <c>caches</c> section, an <c>annotate</c> resolution of kind <c>cache_api</c>, and the
/// <c>--recognizers</c> file. The <c>key_object</c> block is carried and validated here and folded by
/// nothing yet, so these also pin that declaring one changes no template.</summary>
public sealed class CacheRecognizerConfigTests
{
    private const string CACHES = """
        [{ "type": "MysteryCache", "store": "memory",
           "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }] }]
        """;

    private const string CACHES_WITH_KEY_OBJECT = """
        [{ "type": "MysteryCache", "store": "memory",
           "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }],
           "key_object": { "type": "MysteryKey", "template_arg": 0,
                           "factories": [{ "type": "MysteryKeys", "methods": ["Create"], "key_arg": 0, "args_arg": 1 }] } }]
        """;

    [Fact]
    public void Caches_without_a_key_object_parses()
    {
        var recognizer = Assert.Single(Parse(CACHES)).ToRecognizer(Confidence.Confirmed, null);

        Assert.Equal("MysteryCache", recognizer.TypeName);
        Assert.Equal("memory", recognizer.Store);
        Assert.Null(recognizer.KeyObject);
        var method = Assert.Single(recognizer.Methods);
        Assert.Equal("Set", method.Name);
        Assert.Equal(CacheSemantic.Set, method.Semantic);
        Assert.Equal(0, method.KeyArgumentIndex);
        Assert.Null(method.TtlOrOptionsArgumentIndex);
    }

    [Fact]
    public void Caches_with_a_key_object_parses_every_part_of_it()
    {
        var recognizer = Assert.Single(Parse(CACHES_WITH_KEY_OBJECT)).ToRecognizer(Confidence.Confirmed, null);

        Assert.NotNull(recognizer.KeyObject);
        var keyObject = recognizer.KeyObject;
        Assert.Equal("MysteryKey", keyObject.TypeName);
        Assert.Equal(0, keyObject.TemplateArgumentIndex);
        var factory = Assert.Single(keyObject.Factories);
        Assert.Equal("MysteryKeys", factory.TypeName);
        Assert.Equal(["Create"], factory.Methods);
        Assert.Equal(0, factory.KeyObjectArgumentIndex);
        Assert.Equal(1, factory.ArgumentsArgumentIndex);
    }

    /// <summary>A field this schema does not know is a setting the author believes is doing something.
    /// </summary>
    [Fact]
    public void An_unknown_field_is_refused_at_every_level()
    {
        Assert.Throws<JsonException>(() => Parse("""[{ "type": "T", "store": "memory", "methods": [], "prefix": "x" }]"""));
        Assert.Throws<JsonException>(() => Parse("""[{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0, "hash": 1 }] }]"""));
        Assert.Throws<JsonException>(() => Parse("""
            [{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }],
               "key_object": { "type": "K", "template_arg": 0, "separator": "-",
                               "factories": [{ "type": "F", "methods": ["Create"], "key_arg": 0, "args_arg": 1 }] } }]
            """));
    }

    [Fact]
    public void A_missing_required_field_is_refused_by_name()
    {
        Assert.Contains("store", Refused("""[{ "type": "T", "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }] }]"""), StringComparison.Ordinal);
        Assert.Contains("method", Refused("""[{ "type": "T", "store": "memory", "methods": [] }]"""), StringComparison.Ordinal);
        Assert.Contains("key_arg", Refused("""[{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "set" }] }]"""), StringComparison.Ordinal);
        Assert.Contains("semantic", Refused("""[{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "cache", "key_arg": 0 }] }]"""), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_required_field_of_a_key_object_is_refused_by_name()
    {
        Assert.Contains("template_arg", Refused("""
            [{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }],
               "key_object": { "type": "K", "factories": [{ "type": "F", "methods": ["Create"], "key_arg": 0, "args_arg": 1 }] } }]
            """), StringComparison.Ordinal);
        Assert.Contains("factory", Refused("""
            [{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }],
               "key_object": { "type": "K", "template_arg": 0, "factories": [] } }]
            """), StringComparison.Ordinal);
        Assert.Contains("args_arg", Refused("""
            [{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }],
               "key_object": { "type": "K", "template_arg": 0, "factories": [{ "type": "F", "methods": ["Create"], "key_arg": 0 }] } }]
            """), StringComparison.Ordinal);
    }

    /// <summary>The snake_case spelling <c>annotate</c> documents and the bare enum name the committed
    /// <c>--recognizers</c> files already carry must both keep parsing: one shared parse cannot break
    /// either without breaking a file that parses today.</summary>
    [Fact]
    public void Both_spellings_of_a_semantic_are_accepted_and_a_number_is_not()
    {
        Assert.Equal(CacheSemantic.RemoveByPrefix, Semantic("remove_by_prefix"));
        Assert.Equal(CacheSemantic.RemoveByPrefix, Semantic("removebyprefix"));
        Assert.Equal(CacheSemantic.RemoveByTag, Semantic("RemoveByTag"));
        Assert.Contains("semantic", Refused("""[{ "type": "T", "store": "memory", "methods": [{ "name": "Set", "semantic": "1", "key_arg": 0 }] }]"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caches_section_survives_a_re_read_of_the_written_file()
    {
        using var repository = new TemporaryRepository();
        var configuration = new WorkspaceConfiguration
        {
            Root = repository.Path,
            Solutions = ["App.csproj"],
            Caches = Parse(CACHES_WITH_KEY_OBJECT)
        };

        Assert.True(await WorkspaceConfigurationStore.WriteAsync(repository.Path, configuration));
        var reread = await WorkspaceConfigurationStore.ReadAsync(repository.Path);

        var recognizer = Assert.Single(reread.Caches!).ToRecognizer(Confidence.Confirmed, null);
        Assert.Equal("MysteryCache", recognizer.TypeName);
        Assert.NotNull(recognizer.KeyObject);
        var factory = Assert.Single(recognizer.KeyObject.Factories);
        Assert.Equal("MysteryKeys", factory.TypeName);
        Assert.Equal(1, factory.ArgumentsArgumentIndex);
    }

    /// <summary>Refused eagerly, when the file is read, rather than at the first index that needed it.
    /// The config file goes through the source-generated context rather than the reflecting one the
    /// other two readers use, so both refusals are checked on that path too.</summary>
    [Fact]
    public async Task An_unreadable_caches_section_is_refused_when_the_file_is_read()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteConfigurationAsync("""
            { "version": 1, "root": ".", "solutions": ["App.csproj"], "budgets": {},
              "caches": [{ "type": "T", "store": "memory", "methods": [] }] }
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceConfigurationStore.ReadAsync(repository.Path));

        Assert.Contains("method", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_field_in_a_caches_section_is_refused_when_the_file_is_read()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteConfigurationAsync("""
            { "version": 1, "root": ".", "solutions": ["App.csproj"], "budgets": {},
              "caches": [{ "type": "T", "store": "memory", "prefix": "x",
                           "methods": [{ "name": "Set", "semantic": "set", "key_arg": 0 }] }] }
            """);

        var error = await Assert.ThrowsAsync<JsonException>(() => WorkspaceConfigurationStore.ReadAsync(repository.Path));

        Assert.Contains("prefix", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_recognizer_reaches_the_indexer_as_confirmed()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null, caches: Parse(CACHES));

        var indexed = await session.IndexSolutionAsync("App.csproj");

        Assert.True(indexed.Succeeded, indexed.Error);
        var caches = session.Graph.Edges.OfType<Caches>().ToArray();
        Assert.NotEmpty(caches);
        Assert.All(caches, edge => Assert.Equal(Confidence.Confirmed, edge.Confidence));
        Assert.All(caches, edge => Assert.Null(edge.AnnotationId));
    }

    [Fact]
    public async Task An_annotated_recognizer_carrying_a_key_object_reaches_the_indexer_as_likely()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);
        var unresolved = Assert.Single(session.Graph.Unresolved, item => item.Kind == UnresolvedKind.CacheApi);

        var result = await session.AnnotateAsync($"u:{unresolved.Id}", SingleObject(CACHES_WITH_KEY_OBJECT), null);

        Assert.NotNull(result.Reindexed);
        var edge = Assert.Single(session.Graph.Edges.OfType<Caches>());
        Assert.Equal(Confidence.Likely, edge.Confidence);
        Assert.Equal(result.AnnotationId, edge.AnnotationId);
    }

    [Fact]
    public async Task An_annotated_recognizer_with_an_unknown_field_is_refused_and_changes_nothing()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);
        var unresolved = Assert.Single(session.Graph.Unresolved, item => item.Kind == UnresolvedKind.CacheApi);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            session.AnnotateAsync($"u:{unresolved.Id}", Element("""{ "type": "MysteryCache", "store": "memory", "methods": [], "prefix": "x" }"""), null));

        Assert.Contains("cache_api", error.Message, StringComparison.Ordinal);
        Assert.Contains("key_object", error.Message, StringComparison.Ordinal);
        Assert.Contains(unresolved, session.Graph.Unresolved);
        Assert.Empty(session.Graph.Edges.OfType<Caches>());
    }

    /// <summary>What makes this task analysis-neutral: the same declaration with and without a
    /// <c>key_object</c> block indexes to the same keys. The block is carried and folded by nothing yet.
    /// </summary>
    [Fact]
    public async Task Declaring_a_key_object_changes_no_folded_template()
    {
        var without = await TemplatesAsync(CACHES);
        var with = await TemplatesAsync(CACHES_WITH_KEY_OBJECT);

        Assert.NotEmpty(without);
        Assert.Equal(without, with);
    }

    [Fact]
    public async Task Recognizers_file_accepts_a_key_object()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var recognizers = Path.Combine(repository.Path, "recognizers.json");
        var output = Path.Combine(repository.Path, "metrics.json");
        await File.WriteAllTextAsync(recognizers, CACHES_WITH_KEY_OBJECT);

        var exit = await MetricsCommand.RunAsync(["--root", repository.Path, "--solution", "App.csproj", "--out", output,
                                                  "--recognizers", recognizers]);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.Equal(1, document.RootElement.GetProperty("recognizersApplied").GetInt32());
    }

    /// <summary>The recognizer files the corpus runs already read. A shared parse that stopped reading
    /// one of them would leave a "before" run and an "after" run reading different bytes, which is the
    /// whole reason the schema was unified before the measurement rather than after it.</summary>
    [Theory]
    [InlineData("recognizers-nopcommerce.json")]
    [InlineData("recognizers-orchard.json")]
    public async Task The_committed_corpus_recognizer_files_still_parse(string name)
    {
        var path = SqlServerHarness.FindRepositoryFile("plugins", "cache-detective", "skills", "scan", "evals", "metrics", name);

        var declared = Parse(await File.ReadAllTextAsync(path));

        Assert.NotEmpty(declared);
        Assert.All(declared, configuration =>
        {
            var recognizer = configuration.ToRecognizer(Confidence.Likely, null);
            Assert.NotEmpty(recognizer.Methods);
            // Whether a file carries a key_object is the corpus's business, not this test's: nopCommerce's
            // does and Orchard's does not. What is pinned here is that both still parse.
            Assert.All(recognizer.KeyObject?.Factories ?? [], factory => Assert.NotEmpty(factory.Methods));
        });
    }

    [Fact]
    public async Task Recognizers_file_refuses_an_unknown_field()
    {
        using var repository = new TemporaryRepository();
        var recognizers = Path.Combine(repository.Path, "recognizers.json");
        await File.WriteAllTextAsync(recognizers, """[{ "type": "T", "store": "memory", "methods": [], "prefix": "x" }]""");

        var exit = await MetricsCommand.RunAsync(["--root", repository.Path, "--solution", "App.csproj",
                                                  "--out", Path.Combine(repository.Path, "metrics.json"),
                                                  "--recognizers", recognizers]);

        Assert.Equal(2, exit);
    }

    private static async Task<string[]> TemplatesAsync(string caches)
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null, caches: Parse(caches));
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);
        return session.Graph.CacheKeys.Select(key => $"{key.Store}/{key.Template}").Order(StringComparer.Ordinal).ToArray();
    }

    private static CacheRecognizerConfiguration[] Parse(string json) =>
        JsonSerializer.Deserialize<CacheRecognizerConfiguration[]>(json)!;

    private static string Refused(string json) =>
        Assert.Throws<InvalidDataException>(() => Parse(json).Single().ToRecognizer(Confidence.Confirmed, null)).Message;

    private static CacheSemantic Semantic(string name) =>
        Parse($$"""[{ "type": "T", "store": "memory", "methods": [{ "name": "M", "semantic": "{{name}}", "key_arg": 0 }] }]""")
            .Single().ToRecognizer(Confidence.Confirmed, null).Methods.Single().Semantic;

    /// <summary>The array form the config and <c>--recognizers</c> take, reduced to the single object an
    /// <c>annotate</c> resolution takes — the same fields either way.</summary>
    private static JsonElement SingleObject(string array)
    {
        using var document = JsonDocument.Parse(array);
        return document.RootElement.EnumerateArray().Single().Clone();
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-caches-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public async Task WriteConfigurationAsync(string json)
        {
            var path = WorkspaceConfigurationStore.GetPath(Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
        }

        /// <summary>A project with one call to an API no built-in recognizer knows, so that a declared
        /// recognizer is the only thing that can turn it into a cache operation.</summary>
        public async Task WriteProjectAsync()
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "Controller.cs"), """
                public class ControllerBase { }
                public sealed class MysteryCache { public void Set(string key) { } }
                public sealed class DemoController : ControllerBase
                {
                    public void Get() => new MysteryCache().Set("item:1");
                }
                """);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
