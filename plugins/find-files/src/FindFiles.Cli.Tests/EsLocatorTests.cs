using FindFiles.Everything;
using Xunit;

namespace FindFiles.Tests;

public sealed class EsLocatorTests
{
    private static Func<string, string?> Environment(Dictionary<string, string?> values) =>
        name => values.GetValueOrDefault(name);

    [Fact]
    public void TheEsPathOverrideWinsOverEverythingElse()
    {
        var located = EsLocator.Locate(Environment(new()
                                       {
                                           ["ES_PATH"] = @"D:\tools\es.exe",
                                           ["PATH"] = @"C:\Program Files\Everything",
                                       }),
                                       path => path is @"D:\tools\es.exe" or @"C:\Program Files\Everything\es.exe");

        Assert.Equal(@"D:\tools\es.exe", located);
    }

    [Fact]
    public void AnOverridePointingAtNothingIsIgnoredRatherThanFatal()
    {
        var located = EsLocator.Locate(Environment(new()
                                       {
                                           ["ES_PATH"] = @"D:\missing\es.exe",
                                           ["PATH"] = @"C:\Program Files\Everything",
                                       }),
                                       path => path == @"C:\Program Files\Everything\es.exe");

        Assert.Equal(@"C:\Program Files\Everything\es.exe", located);
    }

    [Fact]
    public void TheDefaultInstallDirectoryIsProbedWhenPathDoesNotCoverIt()
    {
        var located = EsLocator.Locate(Environment(new()
                                       {
                                           ["PATH"] = @"C:\Windows;C:\Windows\System32",
                                           ["ProgramFiles"] = @"C:\Program Files",
                                       }),
                                       path => path == @"C:\Program Files\Everything\es.exe");

        Assert.Equal(@"C:\Program Files\Everything\es.exe", located);
    }

    [Fact]
    public void AMalformedPathEntryDoesNotAbortTheProbe()
    {
        var located = EsLocator.Locate(Environment(new()
                                       {
                                           ["PATH"] = "\"C:\\bad|entry\";C:\\good",
                                           ["ProgramFiles"] = @"C:\Program Files",
                                       }),
                                       path => path == @"C:\good\es.exe");

        Assert.Equal(@"C:\good\es.exe", located);
    }

    [Fact]
    public void AnAbsentEverythingCliReturnsNullSoTheCallerCanExplainItself()
    {
        var located = EsLocator.Locate(Environment(new() { ["PATH"] = @"C:\Windows" }), _ => false);

        Assert.Null(located);
    }
}
