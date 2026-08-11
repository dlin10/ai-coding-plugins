using FindFiles.Everything;
using Xunit;
using Xunit.Abstractions;

namespace FindFiles.Tests;

/// <summary>
/// Runs against the real <c>es.exe</c>. These are the tests that catch what a fake cannot: the actual
/// output shape of Everything, and whether <c>-path</c> genuinely scopes a search.
/// </summary>
public sealed class EverythingIntegrationTests(ITestOutputHelper output)
{
    private static bool TryCreateService(out SearchService service)
    {
        var executable = EsLocator.Locate();
        if (executable is null)
        {
            service = new SearchService(runner: null);
            return false;
        }

        service = new SearchService(new EsRunner(executable));
        return true;
    }

    private bool SkipUnlessEverythingIsRunning(out SearchService service)
    {
        if (!TryCreateService(out service))
        {
            output.WriteLine("Skipped: es.exe was not found on this machine.");
            return true;
        }

        var probe = service.Execute(new SearchRequest { Query = "esfind-probe-that-matches-nothing-9f2c" });
        if (probe.Status is SearchStatus.EngineDown or SearchStatus.EsMissing)
        {
            output.WriteLine($"Skipped: Everything is unavailable ({probe.Status}).");
            return true;
        }

        return false;
    }

    [Fact]
    public void AKnownFileIsFoundAndEveryResultIsAnAbsolutePath()
    {
        if (SkipUnlessEverythingIsRunning(out var service))
        {
            return;
        }

        var outcome = service.Execute(new SearchRequest { Query = "SKILL.md", Limit = 5 });

        Assert.Equal(SearchStatus.Ok, outcome.Status);
        Assert.NotEmpty(outcome.Paths);
        Assert.All(outcome.Paths, path => Assert.True(Path.IsPathFullyQualified(path), path));
    }

    [Fact]
    public void PathScopingActuallyRestrictsResultsToTheRequestedTree()
    {
        if (SkipUnlessEverythingIsRunning(out var service))
        {
            return;
        }

        // The previous MCP wrapper for Everything appeared to be unable to scope a search. It turned
        // out to be a wrapper bug rather than an engine limit, so this asserts the real behaviour.
        var scope = AppContext.BaseDirectory;
        var outcome = service.Execute(new SearchRequest { Query = "*", Path = scope, Limit = 10 });

        if (outcome.Status == SearchStatus.NoMatches)
        {
            output.WriteLine($"Skipped: {scope} is not covered by the Everything index.");
            return;
        }

        Assert.Equal(SearchStatus.Ok, outcome.Status);
        Assert.All(outcome.Paths,
                   path => Assert.StartsWith(scope, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AQueryThatMatchesNothingIsReportedWithTheCaveatRatherThanAsAnError()
    {
        if (SkipUnlessEverythingIsRunning(out var service))
        {
            return;
        }

        var outcome = service.Execute(new SearchRequest { Query = "esfind-no-such-name-4c81be07" });

        Assert.Equal(SearchStatus.NoMatches, outcome.Status);
    }
}
