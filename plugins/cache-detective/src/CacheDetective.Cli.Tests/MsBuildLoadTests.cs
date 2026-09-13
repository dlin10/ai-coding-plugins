using CacheDetective.Mcp;
using Common.Mcp;
using Xunit;

namespace CacheDetective.Tests;

public sealed class MsBuildLoadTests
{
    /// <summary>The same failure through the tool: index_solution answers with the failure rather than
    /// letting it out, and the diagnostics it had are paged like any others.</summary>
    [Fact]
    public async Task A_load_that_throws_reaches_index_solution_as_a_failed_result()
    {
        using var temporary = new TemporaryTree();
        await temporary.WriteAsync("global.json", """{ "sdk": { "version": "99.99.999" } }""");
        await temporary.WriteAsync("App/App.csproj", Project());
        await temporary.WriteAsync("App/App.cs", "public class App { }");
        var session = new WorkspaceSession();
        await session.InitializeAsync(temporary.Path, ["App/App.csproj"], null);

        var result = await session.IndexSolutionAsync("App/App.csproj", null);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.NotNull(result.Diagnostics);
    }

    /// <summary>The project is deleted between the two calls: a second page that re-indexed would fail on
    /// the missing file, and the timestamp of the first index would not survive it.</summary>
    [Fact]
    public async Task The_second_page_arrives_without_indexing_again()
    {
        using var temporary = new TemporaryTree();
        var projectPath = await temporary.WriteAsync("App.csproj", Project());
        await temporary.WriteAsync("Controller.cs", "public sealed class Controller { public void Get() { } }");
        var session = new WorkspaceSession();
        await session.InitializeAsync(temporary.Path, ["App.csproj"], null);

        var first = await session.IndexSolutionAsync("App.csproj", new PageArguments { Page = 1, PageSize = 1 });
        Assert.True(first.Succeeded);
        File.Delete(projectPath);

        var second = await session.IndexSolutionAsync("App.csproj", new PageArguments { Page = 2, PageSize = 1 });

        Assert.True(second.Succeeded);
        Assert.Equal(first.IndexedAt, second.IndexedAt);
        Assert.Equal(2, second.Diagnostics.Page);
    }

    private static string Project(string items = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            {items}
          </ItemGroup>
        </Project>
        """;

    private sealed class TemporaryTree : IDisposable
    {
        public TemporaryTree()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public async Task<string> WriteAsync(string relativePath, string content)
        {
            var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
            return full;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
