using System.Diagnostics;
using System.Text;
using ConcurrencyHunter.Core.Tests.Expectations;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class ExpectationFileRuleTests
{
    [Fact]
    public void Real_expectation_file_breaks_no_rule()
    {
        Assert.Empty(ExpectationRules.Violations(ExpectationFile.Load(ExpectationPath())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Until_must_be_a_known_phase(bool notDefect)
    {
        var file = Combine(Entry("case/old", "5b", "unknown", notDefect), Entry("case/new", "unknown"));

        var violation = Assert.Single(ExpectationRules.Violations(file));
        Assert.Contains("case/old", violation);
        Assert.Contains("known phase", violation);
    }

    [Theory]
    [InlineData(false, "5b")]
    [InlineData(false, "5a")]
    [InlineData(true, "5b")]
    [InlineData(true, "5a")]
    public void Until_must_come_after_the_entry_phase(bool notDefect, string until)
    {
        var file = Combine(Entry("case/old", "5b", until, notDefect), Entry("case/new", until));

        var violation = Assert.Single(ExpectationRules.Violations(file));
        Assert.Contains("case/old", violation);
        Assert.Contains("after", violation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Entry_with_until_needs_a_replacement_of_its_case_at_that_phase(bool notDefect)
    {
        var file = Combine(Entry("case/old", "5b", "5d", notDefect),
                           Entry("case/new", "5e"), Entry("case-other/new", "5d"));

        var violation = Assert.Single(ExpectationRules.Violations(file));
        Assert.Contains("case/old", violation);
        Assert.Contains("replacement", violation);
        Assert.Contains("5d", violation);
        Assert.Empty(ExpectationRules.Violations(Combine(file, Entry("case/new/answer", "5d"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotDefect_entry_may_be_the_replacement(bool notDefect)
    {
        var file = Combine(Entry("case/old", "5b", "5d", notDefect), Entry("case/new", "5d", notDefect: true));

        Assert.Empty(ExpectationRules.Violations(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changed_until_is_refused(bool notDefect)
    {
        var head = Entry("case/old", "5b", "5d", notDefect);
        var current = Entry("case/old", "5b", "5e", notDefect);

        Assert.Contains("case/old", Assert.Single(ExpectationRules.Changes(head, current)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removed_until_is_refused(bool notDefect)
    {
        var head = Entry("case/old", "5b", "5d", notDefect);
        var current = Entry("case/old", "5b", notDefect: notDefect);

        Assert.Contains("case/old", Assert.Single(ExpectationRules.Changes(head, current)));
        Assert.Contains("case/old", Assert.Single(ExpectationRules.Changes(head, Combine())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Added_until_is_accepted(bool notDefect)
    {
        var head = Entry("case/old", "5b", notDefect: notDefect);
        var current = Entry("case/old", "5b", "5d", notDefect);

        Assert.Empty(ExpectationRules.Changes(head, current));
    }

    [Fact]
    public async Task Real_expectation_file_keeps_every_until_of_HEAD()
    {
        var path = ExpectationPath();
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add("HEAD:plugins/concurrency-hunter/demo/expected-findings.json");
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        var head = ExpectationFile.Parse((await output).TrimStart('\uFEFF'));

        Assert.Empty(ExpectationRules.Changes(head, ExpectationFile.Load(path)));
    }

    [Fact]
    public void Unchanged_until_is_accepted_when_the_entry_moves_between_lists()
    {
        var head = Entry("case/old", "5b", "5d");
        var current = Entry("case/old", "5b", "5d", notDefect: true);

        Assert.Empty(ExpectationRules.Changes(head, current));
    }

    [Fact]
    public void Every_broken_rule_has_its_own_message()
    {
        var file = Combine(Entry("first/old", "5b", "unknown"), Entry("second/old", "5b", "5a"));

        Assert.Equal(4, ExpectationRules.Violations(file).Count);
        var current = Combine(Entry("first/old", "5b"), Entry("second/old", "5b", "5d"));
        Assert.Equal(2, ExpectationRules.Changes(file, current).Count);
    }

    [Fact]
    public void Until_is_read_for_both_lists_and_defaults_to_null()
    {
        var file = ExpectationFile.Parse("""
            {"schemaVersion":"1","findings":[
              {"id":"case/old","phase":"5b","rule":"DCA1001","confidence":"high",
               "resource":{"region":"static:Demo.State","accessPath":["Value"]},"accesses":[],"until":"5d"}],
             "notDefects":[
              {"id":"case/new","phase":"5d","resource":{"region":"static:Demo.State","accessPath":["Value"]},"until":"5e"},
              {"id":"case/final","phase":"5e","resource":{"region":"static:Demo.State","accessPath":["Value"]}}]}
            """);

        Assert.Equal("5d", Assert.Single(file.Findings).Until);
        Assert.Equal("5e", file.NotDefects[0].Until);
        Assert.Null(file.NotDefects[1].Until);
        Assert.Empty(ExpectationRules.Violations(file));
    }

    private static string ExpectationPath() =>
        RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "expected-findings.json");

    private static ExpectationFile Entry(string id, string phase, string? until = null, bool notDefect = false)
    {
        var resource = new ExpectationResource("static:Demo.State", ["Value"]);
        return notDefect
            ? new ExpectationFile("1", [], [new NotDefectExpectation(id, phase, resource, Until: until)])
            : new ExpectationFile("1", [new FindingExpectation(id, phase, "DCA1001", "high", resource, [], until)], []);
    }

    private static ExpectationFile Combine(params ExpectationFile[] files) =>
        new("1", files.SelectMany(file => file.Findings).ToArray(), files.SelectMany(file => file.NotDefects).ToArray());
}
