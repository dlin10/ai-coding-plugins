using CacheDetective.Configuration;
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
