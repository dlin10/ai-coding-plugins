using FindFiles.Everything;
using Xunit;

namespace FindFiles.Tests;

public sealed class SearchServiceTests
{
    private static readonly SearchRequest Request = new() { Query = "SKILL.md" };

    [Fact]
    public void ExitCodeEightIsReportedAsTheEngineBeingDownRatherThanAsAnEmptyResult()
    {
        var runner = new FakeEsRunner().Enqueue(EsExitCode.EverythingNotRunning);

        var outcome = new SearchService(runner).Execute(Request);

        Assert.Equal(SearchStatus.EngineDown, outcome.Status);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public void ExitCodeNineIsReportedAsNoMatchesAndSkipsTheCountCall()
    {
        var runner = new FakeEsRunner().Enqueue(EsExitCode.NoResults);

        var outcome = new SearchService(runner).Execute(Request);

        Assert.Equal(SearchStatus.NoMatches, outcome.Status);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public void AZeroExitCodeWithNoRowsIsAlsoNoMatches()
    {
        var runner = new FakeEsRunner().Enqueue(EsExitCode.Success);

        var outcome = new SearchService(runner).Execute(Request);

        Assert.Equal(SearchStatus.NoMatches, outcome.Status);
    }

    [Fact]
    public void AnUnmodelledExitCodeSurfacesTheStandardErrorText()
    {
        var runner = new FakeEsRunner().Enqueue(23, standardError: "Error 23: something odd\nsecond line");

        var outcome = new SearchService(runner).Execute(Request);

        Assert.Equal(SearchStatus.Failed, outcome.Status);
        Assert.Equal("Error 23: something odd", outcome.Detail);
    }

    [Fact]
    public void ShortestPathsRankFirstAndTheResultSetIsCappedAtTheLimit()
    {
        var runner = new FakeEsRunner()
            .Enqueue(EsExitCode.Success,
                     [
                         @"C:\cache\deep\deeper\deepest\SKILL.md",
                         @"C:\a\SKILL.md",
                         @"C:\bb\SKILL.md",
                     ])
            .Enqueue(EsExitCode.Success, ["3"]);

        var outcome = new SearchService(runner).Execute(new SearchRequest { Query = "SKILL.md", Limit = 2 });

        Assert.Equal(SearchStatus.Ok, outcome.Status);
        Assert.Equal([@"C:\a\SKILL.md", @"C:\bb\SKILL.md"], outcome.Paths);
        Assert.Equal(3, outcome.TotalMatches);
    }

    [Fact]
    public void DateRankingPreservesTheOrderEsReturned()
    {
        var runner = new FakeEsRunner()
            .Enqueue(EsExitCode.Success, [@"C:\very\deep\newest.txt", @"C:\a.txt"])
            .Enqueue(EsExitCode.Success, ["2"]);

        var outcome = new SearchService(runner).Execute(new SearchRequest
        {
            Query = "*.txt",
            Recent = true,
        });

        Assert.Equal([@"C:\very\deep\newest.txt", @"C:\a.txt"], outcome.Paths);
    }

    [Fact]
    public void AFailedCountCallFallsBackToTheNumberOfRowsActuallyFetched()
    {
        var runner = new FakeEsRunner()
            .Enqueue(EsExitCode.Success, [@"C:\a\SKILL.md"])
            .Enqueue(EsExitCode.Success, ["not a number"]);

        var outcome = new SearchService(runner).Execute(Request);

        Assert.Equal(1, outcome.TotalMatches);
    }

    [Fact]
    public void ANonZeroCountCallLeavesTheTotalUnknown()
    {
        var runner = new FakeEsRunner()
            .Enqueue(EsExitCode.Success, [@"C:\a\SKILL.md"])
            .Enqueue(EsExitCode.EverythingNotRunning);

        var outcome = new SearchService(runner).Execute(Request);

        Assert.Equal(SearchOutcome.UnknownTotal, outcome.TotalMatches);
    }

    [Fact]
    public void AMissingEverythingCliIsItsOwnStatus()
    {
        var outcome = new SearchService(runner: null).Execute(Request);

        Assert.Equal(SearchStatus.EsMissing, outcome.Status);
    }
}
