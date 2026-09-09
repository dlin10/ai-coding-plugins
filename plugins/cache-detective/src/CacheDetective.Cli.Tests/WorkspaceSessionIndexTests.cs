using CacheDetective.Mcp;
using CacheDetective.Workspaces;
using Xunit;

namespace CacheDetective.Tests;

/// <summary>
/// What <c>index_solution</c> remembers and what it counts as expected. Both are things a reader acts on:
/// a remembered index answers a second page without loading again, and the expected composition is what
/// <c>loadComplete</c> is measured against.
/// </summary>
public sealed class WorkspaceSessionIndexTests
{
    /// <summary>
    /// A solution that lists a <c>.dcproj</c> beside a <c>.csproj</c>. MSBuildWorkspace opens only the
    /// languages it knows, so counting the Docker project as expected made loadComplete false forever and
    /// told the agent the scan was partial when nothing was missing.
    /// </summary>
    [Fact]
    public async Task A_project_kind_msbuild_cannot_open_is_not_missing()
    {
        var root = Temporary();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Present.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(root, "App.slnx"), """
                <Solution>
                  <Project Path="Present.csproj" />
                  <Project Path="docker-compose.dcproj" />
                </Solution>
                """);

            using var loaded = await new MsBuildSolutionLoader().LoadAsync(Path.Combine(root, "App.slnx"));

            Assert.True(loaded.Coverage.LoadComplete, $"missing: {string.Join(", ", loaded.Coverage.MissingProjects)}");
            Assert.Equal(1, loaded.Coverage.ProjectsExpected);
            Assert.Empty(loaded.Coverage.MissingProjects);
            var skipped = Assert.Single(loaded.Coverage.SkippedProjects);
            Assert.Contains("docker-compose.dcproj", skipped, StringComparison.Ordinal);
            Assert.Contains(".dcproj", skipped, StringComparison.Ordinal);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The remembered index belongs to the workspace it was taken in. It is keyed by the solution's
    /// relative name alone, so a solution of the same name in another workspace was answered for page two
    /// out of the first workspace's diagnostics.
    /// </summary>
    [Fact]
    public async Task A_second_page_after_a_workspace_change_does_not_return_the_old_index()
    {
        var first = TemporaryProject();
        var second = TemporaryProject(withProject: false);
        try
        {
            var session = new WorkspaceSession();
            await session.InitializeAsync(first, ["App.csproj"], null);
            var indexed = await session.IndexSolutionAsync("App.csproj", new PageArguments { Page = 1, PageSize = 1 });
            Assert.True(indexed.Succeeded, "the first workspace did not index, so there is nothing to leak");

            await session.InitializeAsync(second, ["App.csproj"], null);
            var after = await session.IndexSolutionAsync("App.csproj", new PageArguments { Page = 2, PageSize = 1 });

            // The second workspace has no project of that name at all. Were the remembered index still
            // there, page two would have been answered out of the first workspace's successful run.
            Assert.False(after.Succeeded, "page two was answered from the previous workspace's remembered index");
        }
        finally
        {
            Delete(first);
            Delete(second);
        }
    }

    private static string TemporaryProject(bool withProject = true)
    {
        var path = Temporary();
        if (withProject)
        {
            File.WriteAllText(Path.Combine(path, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(path, "App.cs"), "public class App { }");
        }

        return path;
    }

    private static string Temporary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cache-detective-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
