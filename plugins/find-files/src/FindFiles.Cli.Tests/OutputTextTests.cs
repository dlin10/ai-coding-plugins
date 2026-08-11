using FindFiles.Everything;
using FindFiles.Formatting;
using Xunit;

namespace FindFiles.Tests;

public sealed class OutputTextTests
{
    private static readonly SearchRequest Request = new() { Query = "SKILL.md" };

    [Fact]
    public void ATruncatedResultSetSaysHowManyMatchesExistInTotal()
    {
        var text = OutputText.Render(new SearchOutcome
        {
            Status = SearchStatus.Ok,
            Paths = [@"C:\a\SKILL.md", @"C:\b\SKILL.md"],
            TotalMatches = 1542,
        }, Request);

        // Without the total, twenty paths look like the whole answer and the caller stops looking.
        Assert.Contains("shown 2 of 1542 matches", text);
        Assert.Contains(@"C:\a\SKILL.md", text);
    }

    [Fact]
    public void ACompleteResultSetReportsOnlyTheCount()
    {
        var text = OutputText.Render(new SearchOutcome
        {
            Status = SearchStatus.Ok,
            Paths = [@"C:\a\SKILL.md"],
            TotalMatches = 1,
        }, Request);

        Assert.Contains("-- 1 match", text);
        Assert.DoesNotContain("shown", text);
    }

    [Fact]
    public void AnUnknownTotalIsStatedRatherThanGuessed()
    {
        var text = OutputText.Render(new SearchOutcome
        {
            Status = SearchStatus.Ok,
            Paths = [@"C:\a\SKILL.md"],
            TotalMatches = SearchOutcome.UnknownTotal,
        }, Request);

        Assert.Contains("total match count unavailable", text);
    }

    [Fact]
    public void AnEmptyIndexResultCarriesTheCaveatThatItProvesNothing()
    {
        var text = OutputText.Render(new SearchOutcome { Status = SearchStatus.NoMatches }, Request);

        // The tool output is read every time; the skill file is only read sometimes. The caveat has
        // to live here or an agent will report "the file does not exist" on an unindexed folder.
        Assert.Contains("does not prove the file is absent", text);
        Assert.Contains("filesystem walk", text);
    }

    [Fact]
    public void AnEmptyScopedResultNamesTheScope()
    {
        var text = OutputText.Render(new SearchOutcome { Status = SearchStatus.NoMatches },
                                     new SearchRequest { Query = "x", Path = @"C:\Dev" });

        Assert.Contains(@"under ""C:\Dev""", text);
    }

    [Fact]
    public void AStoppedEngineTellsTheCallerWhatToUseInstead()
    {
        var text = OutputText.Render(new SearchOutcome { Status = SearchStatus.EngineDown }, Request);

        Assert.Contains("Everything is not running", text);
        Assert.Contains("Glob or ripgrep", text);
    }

    [Fact]
    public void AMissingCliExplainsHowToFixItWithoutStartingAnything()
    {
        var text = OutputText.Render(new SearchOutcome { Status = SearchStatus.EsMissing }, Request);

        Assert.Contains("ES_PATH", text);
        Assert.Contains("Glob or ripgrep", text);
    }
}
