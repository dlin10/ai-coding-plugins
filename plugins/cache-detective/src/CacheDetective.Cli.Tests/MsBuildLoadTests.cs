using CacheDetective.Workspaces;
using Xunit;

namespace CacheDetective.Tests;

public sealed class MsBuildLoadTests
{
    [Fact]
    public async Task MsBuildLoadReturnsProjectsAndWorkspaceDiagnostics()
    {
        var solutionPath = FindRepositoryFile("plugins", "plan-forge-flow", "src",
            "PlanForgeFlow.sln");
        var loader = new MsBuildSolutionLoader();

        using (var loaded = await loader.LoadAsync(solutionPath))
        {
            Assert.NotEmpty(loaded.Solution.Projects);
            Assert.NotNull(await loaded.Solution.Projects.First().GetCompilationAsync());
            Assert.All(loaded.Diagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(
                diagnostic.Message)));
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cache-detective-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var projectPath = Path.Combine(temporaryDirectory, "Diagnostic.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Missing.csproj" />
                  </ItemGroup>
                </Project>
                """);

            using var diagnosticLoad = await loader.LoadAsync(projectPath);
            Assert.NotEmpty(diagnosticLoad.Solution.Projects);
            Assert.NotEmpty(diagnosticLoad.Diagnostics);
            Assert.All(diagnosticLoad.Diagnostics, diagnostic => Assert.False(
                string.IsNullOrWhiteSpace(diagnostic.Message)));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(relativePath)}.");
    }
}
