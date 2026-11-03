using System.Diagnostics;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

[Collection(EShopCollection.NAME)]
public sealed class H2EShopAcceptanceTests
{
    private const string REVISION = "7e5ae7b54ec9f331a1c60b65bd45519f5aecc327";

    [RequiresEShopFact]
    public async Task Exact_survivors_with_full_inputs()
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!);
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-C", root, "rev-parse", "HEAD" })
            start.ArgumentList.Add(argument);
        using var git = Process.Start(start)!;
        var output = git.StandardOutput.ReadToEndAsync();
        var error = git.StandardError.ReadToEndAsync();
        await git.WaitForExitAsync();
        Assert.True(git.ExitCode == 0, await error);
        Assert.Equal(REVISION, (await output).Trim());

        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
        Assert.Equal(30, projects.Length);
        Assert.All(projects, project => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "obj/project.assets.json")),
                                                    $"Missing assets for {project}"));
        var target = Path.Combine(root, "src/eShopOnContainers-ServicesAndWebApps.sln");
        async Task<LoadedTarget> Load(string path, CancellationToken token)
        {
            var loaded = await MetricsCommand.LoadWithMsBuildAsync(path, token);
            try
            {
                Assert.Equal(30, loaded.ProjectsExpected);
                Assert.Equal(30, loaded.ProjectsLoaded);
                Assert.Empty(loaded.MissingProjects);
                Assert.Equal(projects.Order(StringComparer.OrdinalIgnoreCase),
                             loaded.Solution.Projects.Select(project => project.FilePath!).Order(StringComparer.OrdinalIgnoreCase),
                             StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
            catch
            {
                loaded.Owner.Dispose();
                throw;
            }
        }

        var (_, analysis) = await MetricsCommand.MeasureAsync(target, new AnalysisLimits(8, 16, 16), Load,
                                                               ModelGeneration.None, CancellationToken.None);
        Assert.Equal(["7c919c7aedc4b38e", "fa3fbc528c46593e"],
                     analysis.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal));
        Assert.All(analysis.Findings, finding =>
        {
            Assert.Contains("WebhookClient", finding.AccessA.Source.Path, StringComparison.Ordinal);
            Assert.Contains("WebhookClient", finding.AccessB.Source.Path, StringComparison.Ordinal);
        });
    }
}
