using FindFiles.Everything;
using Xunit;

namespace FindFiles.Tests;

public sealed class EsArgumentBuilderTests
{
    [Fact]
    public void ResultArgumentsRequestTheErrorLevelThatDistinguishesAnEmptyIndex()
    {
        var arguments = EsArgumentBuilder.BuildResultArguments(new SearchRequest { Query = "SKILL.md" });

        // Without -no-result-error, es.exe exits 0 on an empty result and "nothing matched" becomes
        // indistinguishable from "matched successfully".
        Assert.Contains("-no-result-error", arguments);
        Assert.Equal("SKILL.md", arguments[^1]);
    }

    [Fact]
    public void QueryIsPassedVerbatimSoEverythingOperatorsSurvive()
    {
        const string query = @"plugins\find-files\*.cs !obj ext:cs";

        var arguments = EsArgumentBuilder.BuildResultArguments(new SearchRequest { Query = query });

        // Escaping the query would break wildcards, boolean operators and ext: filters, which are the
        // documented way to express complex conditions.
        Assert.Equal(query, arguments[^1]);
    }

    [Fact]
    public void PathScopeUsesTheNativeFlagRatherThanAQueryFragment()
    {
        var arguments = EsArgumentBuilder.BuildResultArguments(new SearchRequest
        {
            Query = "SKILL.md",
            Path = @"C:\Dev\ai-coding-plugins",
        });

        var pathIndex = arguments.IndexOf("-path");
        Assert.True(pathIndex >= 0);
        Assert.Equal(@"C:\Dev\ai-coding-plugins", arguments[pathIndex + 1]);
    }

    // The kind is passed as text because xUnit needs public test methods and EntryKind is internal.
    [Theory]
    [InlineData("Files", "/a-d")]
    [InlineData("Folders", "/ad")]
    public void KindMapsOntoDirStyleAttributeFilters(string kind, string expected)
    {
        var arguments = EsArgumentBuilder.BuildResultArguments(new SearchRequest
        {
            Query = "find-files",
            Kind = Enum.Parse<EntryKind>(kind),
        });

        Assert.Contains(expected, arguments);
    }

    [Fact]
    public void BothKindAddsNoAttributeFilter()
    {
        var arguments = EsArgumentBuilder.BuildResultArguments(new SearchRequest { Query = "x" });

        Assert.DoesNotContain("/ad", arguments);
        Assert.DoesNotContain("/a-d", arguments);
    }

    [Fact]
    public void PathLengthRankingPullsAWideWindowWhileDateRankingPullsOnlyTheLimit()
    {
        var byPathLength = EsArgumentBuilder.BuildResultArguments(new SearchRequest
        {
            Query = "x",
            Limit = 20,
        });
        var byDate = EsArgumentBuilder.BuildResultArguments(new SearchRequest
        {
            Query = "x",
            Limit = 20,
            Recent = true,
        });

        // es cannot sort by path length, so that ranking has to sort locally over a wider window.
        // Date ranking is done by es itself, so asking for more than the limit would be waste.
        Assert.Equal(SearchRequest.FetchWindow.ToString(), byPathLength[byPathLength.IndexOf("-n") + 1]);
        Assert.Equal("20", byDate[byDate.IndexOf("-n") + 1]);
        Assert.Contains("date-modified-descending", byDate);
        Assert.DoesNotContain("-sort", byPathLength);
    }

    [Fact]
    public void CountArgumentsAskOnlyForTheTotalAndKeepTheSameFilters()
    {
        var arguments = EsArgumentBuilder.BuildCountArguments(new SearchRequest
        {
            Query = "SKILL.md",
            Path = @"C:\Dev",
            Kind = EntryKind.Files,
        });

        Assert.Equal("-get-result-count", arguments[0]);
        Assert.Contains("-path", arguments);
        Assert.Contains("/a-d", arguments);
        Assert.DoesNotContain("-n", arguments);
    }

    [Fact]
    public void RegexIsForwardedToEs()
    {
        var arguments = EsArgumentBuilder.BuildResultArguments(new SearchRequest
        {
            Query = "^SKILL.*md$",
            Regex = true,
        });

        Assert.Contains("-r", arguments);
    }
}
