using System.Text.RegularExpressions;
using Common.Roslyn;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class UnsupportedCensusTests
{
    [Fact]
    public async Task Census_lists_every_kind_the_demo_leaves()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        await AssertCensus(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx"), "demo");
    }

    [RequiresEShopFact]
    public async Task Census_lists_every_kind_eshop_leaves()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable(RequiresEShopFactAttribute.VARIABLE)!,
                                "src", "eShopOnContainers-ServicesAndWebApps.sln");
        await AssertCensus(path, "eshop");
    }

    private static async Task AssertCensus(string solutionPath, string target)
    {
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(solutionPath);
        Assert.True(loaded.Coverage.LoadComplete, string.Join(", ", loaded.Coverage.MissingProjects));
        var rootDirectory = Path.GetDirectoryName(solutionPath)!;
        var repositoryRoot = RepositoryRoot.Find(rootDirectory);
        var projectModels = ProjectModelFiles.Read(repositoryRoot);
        var modelLock = ModelLock.Read(repositoryRoot, projectModels);
        var bodies = new Dictionary<string, IrBody>(StringComparer.Ordinal);

        foreach (var scoped in ProcessScopes.Discover(loaded.Solution, rootDirectory).Scopes)
        {
            var keep = scoped.Projects.Select(project => project.Id).ToHashSet();
            var solution = loaded.Solution;
            foreach (var project in loaded.Solution.Projects.Where(project => !keep.Contains(project.Id)))
                solution = solution.RemoveProject(project.Id);
            var compilations = solution.Projects.Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()!).ToArray();
            var (models, _) = ProjectModelResolver.Resolve(projectModels, compilations, modelLock);
            var run = ReachScope(solution, scoped.Scope.Id, models, rootDirectory);
            var heap = Solve(run).Heap;
            foreach (var bodyId in heap.ReachableBodies)
                if (run.Result.Bodies.TryGetValue(bodyId, out var body))
                    bodies.TryAdd(bodyId, body);
        }

        var actual = bodies.Values.SelectMany(body => body.Blocks.SelectMany(block => block.Operations))
                           .OfType<IrUnknownOperation>().Where(operation => operation.Reason == "unsupported")
                           .GroupBy(operation => operation.OperationKind, StringComparer.Ordinal)
                           .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var census = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "skills", "hunt", "evals", "metrics", "unsupported-census.md");
        var lines = File.ReadAllLines(census);
        var matches = lines.Select(line => Regex.Match(line,
            @"^kind: (?<kind>.+) \| demo: (?<demo>\d+) \| eshop: (?<eshop>\d+) \| question: (?<question>\d+)$")).ToArray();
        Assert.All(matches, match => Assert.True(match.Success, "Every census line names a kind, both counts and its question."));
        var expected = matches.Where(match => int.Parse(match.Groups[target].Value) != 0)
            .ToDictionary(match => match.Groups["kind"].Value,
                          match => int.Parse(match.Groups[target].Value), StringComparer.Ordinal);
        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    }
}

public sealed class RequiresEShopFactAttribute : FactAttribute
{
    public const string VARIABLE = "CH_ESHOP_ROOT";

    public RequiresEShopFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VARIABLE)))
            Skip = $"{VARIABLE} is not set. Set it to an eShopOnContainers checkout to run this read-only eval.";
    }
}
