using CacheDetective.Configuration;
using CacheDetective.Database;
using CacheDetective.Mcp;
using CacheDetective.Graph;
using Xunit;

namespace CacheDetective.Tests;

public sealed class WorkspaceToolsTests
{
    [Fact]
    public async Task WorkspaceTools_init_writes_new_configuration()
    {
        using var repository = new TemporaryRepository();
        var session = new WorkspaceSession();

        var result = await session.InitializeAsync(repository.Path, ["src/App.csproj"],
            new Dictionary<string, double> { ["dbo.Products"] = 30 });

        Assert.True(result.Written);
        Assert.True(File.Exists(WorkspaceConfigurationStore.GetPath(repository.Path)));
        Assert.Equal(["src/App.csproj"], result.Configuration.Solutions);
        Assert.Equal(30, result.Configuration.Budgets["dbo.Products"]);
    }

    [Fact]
    public async Task WorkspaceTools_init_loads_without_rewriting_and_budgets_reach_rules()
    {
        using var repository = new TemporaryRepository();
        var path = WorkspaceConfigurationStore.GetPath(repository.Path);
        await WorkspaceConfigurationStore.WriteAsync(repository.Path, new WorkspaceConfiguration
        {
            Root = repository.Path,
            Solutions = ["App.csproj"],
            Budgets = new Dictionary<string, double> { ["dbo.Products"] = 45 }
        });
        var originalWriteTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        var session = new WorkspaceSession();

        var result = await session.InitializeAsync(repository.Path, null, null);
        AddUnguardedWrite(session.Graph);
        var finding = Assert.Single(session.GetUnguardedWriteFindings());

        Assert.False(result.Written);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(45, finding.BudgetSeconds);
        Assert.True(finding.Suppressed);
    }

    [Fact]
    public async Task WorkspaceTools_status_updates_after_index_and_failure_keeps_graph()
    {
        using var repository = new TemporaryRepository();
        await repository.WriteProjectAsync();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);

        var before = await session.GetStatusAsync();
        var indexed = await session.IndexSolutionAsync("App.csproj");
        var after = await session.GetStatusAsync();
        var countsAfterSuccess = after.Counts;
        var failed = await session.IndexSolutionAsync("missing.csproj");
        var afterFailure = await session.GetStatusAsync();

        Assert.False(Assert.Single(before.Solutions.Items).Indexed);
        Assert.True(indexed.Succeeded, indexed.Error);
        Assert.True(Assert.Single(after.Solutions.Items).Indexed);
        Assert.NotNull(Assert.Single(after.Solutions.Items).IndexedAt);
        Assert.True(after.Counts.Vertices > 0);
        Assert.False(failed.Succeeded);
        Assert.NotNull(failed.Error);
        Assert.Equal(countsAfterSuccess, afterFailure.Counts);
        Assert.True(Assert.Single(afterFailure.Solutions.Items).Indexed);
    }

    [Fact]
    public async Task WorkspaceTools_init_requires_file_or_solutions()
    {
        using var repository = new TemporaryRepository();
        var session = new WorkspaceSession();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.InitializeAsync(repository.Path, null, null));

        Assert.Contains("no solutions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspaceTools_index_database_without_a_configured_database_says_so()
    {
        using var repository = new TemporaryRepository();
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, ["App.csproj"], null);

        var result = await session.IndexDatabaseAsync("shop");

        Assert.False(result.Succeeded);
        Assert.Contains("No database is configured", result.Error!, StringComparison.Ordinal);
        Assert.Contains("workspace.json", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceTools_index_database_reports_catalogue_counts_in_status()
    {
        using var repository = new TemporaryRepository();
        var session = await ConfiguredSessionAsync(repository);

        var indexed = await session.IndexDatabaseAsync("shop", Catalogue);
        var status = await session.GetStatusAsync();

        Assert.True(indexed.Succeeded, indexed.Error);
        Assert.Equal("shop", indexed.Database);
        Assert.Equal(1, indexed.Added.Procedures);
        Assert.Equal(1, indexed.Added.Triggers);
        Assert.Equal(1, indexed.Added.Views);
        Assert.Equal(4, indexed.Added.Edges);
        Assert.Equal(1, indexed.Added.Unresolved);
        Assert.Equal(["dbo.NamesAGhost"], indexed.UnresolvableObjects);
        Assert.Equal(1, status.Counts.Procedures);
        Assert.Equal(1, status.Counts.Triggers);
        Assert.Equal(1, status.Counts.Views);
        Assert.Equal(1, status.Counts.Unresolved);
    }

    [Fact]
    public async Task WorkspaceTools_index_database_twice_replaces_instead_of_doubling()
    {
        using var repository = new TemporaryRepository();
        var session = await ConfiguredSessionAsync(repository);
        AddUnguardedWrite(session.Graph);

        await session.IndexDatabaseAsync("shop", Catalogue);
        var once = await session.GetStatusAsync();
        var findingsOnce = session.GetUnguardedWriteFindings().Count;
        await session.IndexDatabaseAsync("shop", Catalogue);
        var twice = await session.GetStatusAsync();

        Assert.Equal(once.Counts, twice.Counts);
        Assert.Equal(findingsOnce, session.GetUnguardedWriteFindings().Count);
        // The code half is untouched by a database re-index.
        Assert.Contains(session.Graph.Edges.OfType<Writes>(), edge => edge.From is Handler);
    }

    [Fact]
    public void WorkspaceTools_read_only_intent_is_added_but_never_overridden()
    {
        Assert.Contains("ApplicationIntent=ReadOnly",
            ReadOnlyIntent.Apply("Server=.;Database=Shop;Trusted_Connection=True"),
            StringComparison.Ordinal);
        Assert.Contains("ApplicationIntent=ReadWrite",
            ReadOnlyIntent.Apply("Server=.;Database=Shop;ApplicationIntent=ReadWrite"),
            StringComparison.Ordinal);
    }

    private static async Task<WorkspaceSession> ConfiguredSessionAsync(TemporaryRepository repository)
    {
        await WorkspaceConfigurationStore.WriteAsync(repository.Path, new WorkspaceConfiguration
        {
            Root = repository.Path,
            Solutions = ["App.csproj"],
            Databases = [new DatabaseConfiguration { Name = "shop", Connection = "env:CD_SHOP_CONN" }]
        });
        var session = new WorkspaceSession();
        await session.InitializeAsync(repository.Path, null, null);
        return session;
    }

    /// <summary>Stands in for the connect-and-read-the-catalogue step, which needs a server; the live
    /// path is covered by the integration tests.</summary>
    private static Task<DatabaseIndexResult> Catalogue(DatabaseConfiguration database, string name,
                                                       CancellationToken cancellationToken)
    {
        var graph = new CacheGraph();
        var products = new Table("dbo", "Products", name);
        var procedure = new StoredProcedure("dbo", "ApplyDiscount", name);
        var view = new View("dbo", "vw_ProductCard", name);
        var trigger = new Trigger("dbo", "trg_Products_Audit", products.Name, [WriteEvent.Insert], name);

        graph.AddEdge(new Reads(procedure, products, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.ApplyDiscount", name)]));
        graph.AddEdge(new Writes(procedure, products, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.ApplyDiscount", name)], [WriteEvent.Update]));
        graph.AddEdge(new Reads(view, products, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.vw_ProductCard", name)]));
        graph.AddEdge(new Fires(products, trigger, Confidence.Confirmed,
            [Evidence.InDatabase("dbo.trg_Products_Audit", name)]));
        graph.AddUnresolved(UnresolvedKind.Sql, solution: null,
            Evidence.InDatabase("dbo.NamesAGhost", name), "dbo.NamesAGhost",
            "The catalogue could not resolve what this object references.");

        return Task.FromResult(new DatabaseIndexResult(graph, ["dbo.NamesAGhost"]));
    }

    private static void AddUnguardedWrite(CacheGraph graph)
    {
        var cachedBy = new Handler("App.csproj", "Products.Get", "controller", "Products.cs", 10);
        var writtenBy = new Handler("App.csproj", "Products.Put", "controller", "Products.cs", 20);
        var table = new Table("dbo.Products", "default");
        var key = new CacheKey("product:{id}", "memory", TimeSpan.FromSeconds(30), [], "cache");
        graph.AddEdge(new Caches(cachedBy, key, Confidence.Confirmed));
        graph.AddEdge(new Reads(cachedBy, table, Confidence.Confirmed));
        graph.AddEdge(new Writes(writtenBy, table, Confidence.Confirmed));
    }

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
            await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(System.IO.Path.Combine(Path, "Controller.cs"), """
                public class ControllerBase { }
                public sealed class ProductsController : ControllerBase
                {
                    public void Get() => Helper();
                    private static void Helper() { }
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
