using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Mcp;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// The two ends of the may-modality that only the session can show: what an <c>annotate</c> makes of a
/// pending removal the fold left uncertain, and what a reader is actually shown for a finding a may-edge
/// failed to suppress. Both go through <see cref="WorkspaceSession"/> rather than around it — a version of
/// these that built the expected edge by hand would pass even if the session stopped building it that way.
/// See <c>docs/adr/0015</c>.
/// </summary>
public sealed class MaySetAnnotationTests
{
    /// <summary>A removal a reader will find in the code has to appear in the finding that says the write is
    /// unguarded, with the reason it did not count. Without it the reader's first move — look for the
    /// invalidation, find it — ends in concluding the tool is wrong.</summary>
    [Fact]
    public async Task The_rendered_finding_shows_the_possible_removal_and_why_it_did_not_count()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);

        var fragments = await RenderAsync(session, repository.Path, "product:one");

        var removal = Assert.Single(fragments, fragment => fragment.Edge == "invalidates");
        Assert.NotNull(removal.Reason);
        Assert.Contains("may fire", removal.Reason!, StringComparison.Ordinal);
        Assert.Contains("does not count as coverage", removal.Reason!, StringComparison.Ordinal);
    }

    /// <summary>The reason names which of the two shapes the fold met, because they are read differently: a
    /// branch to follow is not a fold that gave up.</summary>
    [Fact]
    public async Task The_reason_names_how_many_values_the_site_may_produce()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);

        var fragments = await RenderAsync(session, repository.Path, "product:one");

        var removal = Assert.Single(fragments, fragment => fragment.Edge == "invalidates");
        Assert.Contains("folds to 2 values", removal.Reason!, StringComparison.Ordinal);
    }

    /// <summary>The deferred half of the policy, driven through the real annotation path. An annotation says
    /// what the key <em>is</em>; it does not say the site had no choice, so naming the unnameable member of a
    /// mixed removal must not create the certainty the fold refused.</summary>
    [Fact]
    public async Task Annotating_the_unnameable_member_of_a_mixed_removal_yields_a_may_edge()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);
        var unresolved = Assert.Single(session.Graph.Unresolved,
                                       item => item.Kind == UnresolvedKind.Key &&
                                               item.Snippet.Contains("runtime", StringComparison.Ordinal));

        await session.AnnotateAsync($"u:{unresolved.Id}", Element("""{ "template": "product:three" }"""), null);

        var annotated = Assert.Single(session.Graph.Edges.OfType<Invalidates>(),
                                      edge => ((CacheKey)edge.To).Template == "product:three");
        Assert.Equal(InvalidationModality.May, annotated.Modality);
        Assert.NotNull(annotated.Reason);
        Assert.Contains("does not count as coverage", annotated.Reason!, StringComparison.Ordinal);
    }

    /// <summary>And the outcome that matters: the annotated removal suppresses nothing. A must-edge here
    /// would delete the finding for every other value the site may produce.</summary>
    [Fact]
    public async Task The_annotated_removal_grants_no_suppression()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);
        var unresolved = Assert.Single(session.Graph.Unresolved,
                                       item => item.Kind == UnresolvedKind.Key &&
                                               item.Snippet.Contains("runtime", StringComparison.Ordinal));

        await session.AnnotateAsync($"u:{unresolved.Id}", Element("""{ "template": "product:one" }"""), null);

        // The annotation named the very key the write endangers, and it still does not cover it.
        var annotated = Assert.Single(session.Graph.Edges.OfType<Invalidates>(),
                                      edge => edge.AnnotationId is not null);
        Assert.Equal(InvalidationModality.May, annotated.Modality);
        Assert.Contains(Findings(session), finding => finding.Key.Template == "product:one");
    }

    private static UnguardedWriteFinding[] Findings(WorkspaceSession session) =>
        [.. new UnguardedWriteRule().Evaluate(session.Graph)];

    private static async Task<FindingEvidenceFragment[]> RenderAsync(WorkspaceSession session, string root, string template)
    {
        var catalog = new FindingCatalog();
        var findingId = await session.ReadGraphAsync((graph, configuration) =>
            catalog.GetAll(graph, configuration?.Budgets)
                   .First(snapshot => snapshot.Item.Rule == UnguardedWriteFinding.Rule &&
                                      snapshot.Item.KeyTemplate == template).Item.Id);
        var evidence = await session.ReadGraphAsync((graph, configuration) =>
            FindingQueries.GetEvidence(graph, configuration?.Budgets, root, catalog, findingId, new PageArguments()));
        return [.. evidence.Fragments.Items];
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A cached key over a table, a write to that table, a removal that folds to two values, and a
    /// removal one of whose values cannot be named. Shims stand in for the libraries so the project needs no
    /// package references, exactly as the Core fixtures do.</summary>
    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public async Task WriteProjectAsync()
        {
            await System.IO.File.WriteAllTextAsync(File("App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>
                """);
            await System.IO.File.WriteAllTextAsync(File("App.cs"), """
                using System;
                using System.Collections.Generic;
                using System.Data;
                using Dapper;
                namespace Microsoft.Extensions.Caching.Memory
                {
                    public interface IMemoryCache
                    {
                        void Set(string key, object value, TimeSpan ttl);
                        void Remove(string key);
                    }
                }
                namespace Dapper
                {
                    public static class SqlMapper
                    {
                        public static IEnumerable<T> Query<T>(this IDbConnection connection, string sql) => Array.Empty<T>();
                        public static int Execute(this IDbConnection connection, string sql) => 0;
                    }
                }
                public class ControllerBase { }
                public sealed class ReaderController : ControllerBase
                {
                    private Microsoft.Extensions.Caching.Memory.IMemoryCache cache = null!;
                    private IDbConnection connection = null!;
                    public void Get()
                    {
                        cache.Set("product:one", connection.Query<object>("select Id from dbo.Products"), TimeSpan.FromSeconds(3600));
                        cache.Set("product:two", connection.Query<object>("select Id from dbo.Products"), TimeSpan.FromSeconds(3600));
                    }
                }
                public sealed class BranchyWriterController : ControllerBase
                {
                    private Microsoft.Extensions.Caching.Memory.IMemoryCache cache = null!;
                    private IDbConnection connection = null!;
                    public void Post(bool flag)
                    {
                        connection.Execute("update dbo.Products set Price = 1");
                        cache.Remove(flag ? "product:one" : "product:two");
                    }
                }
                public sealed class MixedWriterController : ControllerBase
                {
                    private Microsoft.Extensions.Caching.Memory.IMemoryCache cache = null!;
                    private IDbConnection connection = null!;
                    public void Post(bool flag, string runtime)
                    {
                        connection.Execute("update dbo.Products set Price = 2");
                        cache.Remove(flag ? "product:one" : runtime);
                    }
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
