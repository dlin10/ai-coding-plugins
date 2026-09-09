using CacheDetective.Graph;
using CacheDetective.Mcp;
using CacheDetective.Rules;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// `STALE_PARENT_KEY` is the second rule that reads coverage, and it owes a reader the same thing
/// `UNGUARDED_WRITE` does: a removal of the parent that may fire withholds the suppression, so the finding
/// has to show that removal and say why it was not enough. Asserted through
/// <see cref="WorkspaceSession"/> and the evidence renderer, because what matters is what a reader is
/// shown rather than what the rule put in a list. See <c>docs/adr/0015</c>.
/// </summary>
public sealed class StaleParentEvidenceTests
{
    [Fact]
    public async Task The_rendered_finding_shows_the_possible_parent_removal_and_why_it_did_not_count()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);

        var fragments = await RenderAsync(session, repository.Path);

        var removals = fragments.Where(fragment => fragment.Edge == "invalidates").ToArray();
        Assert.Contains(removals, fragment => fragment.To.Contains("parent:x", StringComparison.Ordinal) &&
                                              fragment.Reason is not null &&
                                              fragment.Reason.Contains("does not count as coverage", StringComparison.Ordinal));
    }

    /// <summary>The finding exists at all, which is the half the previous round fixed: a parent that only
    /// may be removed is a parent that may be left stale.</summary>
    [Fact]
    public async Task The_stale_parent_finding_is_reported_despite_the_possible_removal()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);
        Assert.True((await session.IndexSolutionAsync("App.csproj")).Succeeded);

        var finding = Assert.Single(new StaleParentKeyRule().Evaluate(session.Graph));

        Assert.Equal("parent:x", finding.Parent.Template);
        Assert.Equal("child:x", finding.Child.Template);
    }

    private static async Task<FindingEvidenceFragment[]> RenderAsync(WorkspaceSession session, string root)
    {
        var catalog = new FindingCatalog();
        var findingId = await session.ReadGraphAsync((graph, configuration) =>
            catalog.GetAll(graph, configuration?.Budgets)
                   .First(snapshot => snapshot.Item.Rule == StaleParentKeyFinding.Rule).Item.Id);
        var evidence = await session.ReadGraphAsync((graph, configuration) =>
            FindingQueries.GetEvidence(graph, configuration?.Budgets, root, catalog, findingId, new PageArguments()));
        return [.. evidence.Fragments.Items];
    }

    /// <summary>A parent key that outlives the child it is built from, one handler that removes the child,
    /// and — in that same handler, so the coverage search reaches it — a removal of the parent that folds to
    /// two values and may therefore remove either. Shims stand in for the libraries so the project needs no
    /// package references.</summary>
    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public async Task WriteProjectAsync()
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(Path, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>
                """);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(Path, "App.cs"), """
                using System;
                using System.Collections.Generic;
                using System.Data;
                using Dapper;
                namespace Microsoft.Extensions.Caching.Memory
                {
                    public interface IMemoryCache
                    {
                        void Set(string key, object value, TimeSpan ttl);
                        object Get(string key);
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
                public sealed class ChildController : ControllerBase
                {
                    private Microsoft.Extensions.Caching.Memory.IMemoryCache cache = null!;
                    private IDbConnection connection = null!;
                    public void Get() => cache.Set("child:x", connection.Query<object>("select Id from dbo.Products"), TimeSpan.FromSeconds(30));
                }
                public sealed class ParentController : ControllerBase
                {
                    private Microsoft.Extensions.Caching.Memory.IMemoryCache cache = null!;
                    private IDbConnection connection = null!;
                    public void Get()
                    {
                        var child = cache.Get("child:x");
                        cache.Set("parent:x", connection.Query<object>("select Id from dbo.Products"), TimeSpan.FromSeconds(3600));
                    }
                }
                public sealed class InvalidatorController : ControllerBase
                {
                    private Microsoft.Extensions.Caching.Memory.IMemoryCache cache = null!;
                    public void Post(bool flag)
                    {
                        cache.Remove("child:x");
                        cache.Remove(flag ? "parent:x" : "parent:y");
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
