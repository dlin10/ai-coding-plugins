using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Reporting;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>The phase 5b cases whose expectation is about semantic gaps rather than findings (R4, R8): the shared demo analysis's
/// coverage says which gaps there are and in what order.</summary>
public sealed class DemoGapCoverageTests
{
    [Fact]
    public async Task Reflection_call_with_only_strings_and_numbers_makes_no_gap()
    {
        var web = await Web();

        Assert.DoesNotContain(web.Gaps, gap => gap.Sites.Any(site => InCase(site, "ReflectionPrimitiveArgsNoGap")));
    }

    [Fact]
    public async Task Calls_the_library_table_describes_make_no_gap()
    {
        var web = await Web();

        Assert.DoesNotContain(web.Gaps, gap => gap.Sites.Any(site => InCase(site, "LibraryTableNoGap")));
    }

    [Fact]
    public async Task Gap_reaching_three_roots_comes_before_gap_reaching_one()
    {
        var web = await Web();

        var gaps = web.Gaps.Where(gap => gap.Sites.Any(site => InCase(site, "GapMaterialityOrder"))).ToArray();
        Assert.Equal([("System.Console.WriteLine(object)", SemanticGapKinds.UNKNOWN_LIBRARY, 3, 2, 3),
                      ("System.Console.Write(object)", SemanticGapKinds.UNKNOWN_LIBRARY, 1, 1, 1)],
                     gaps.Select(gap => (gap.Callee, gap.Kind, gap.Roots, gap.Regions, gap.CallSites)));
    }

    [Fact]
    public async Task Channel_handoff_is_a_gap_in_coverage_and_no_finding()
    {
        var result = await DemoExpectationTests.SharedDemo.Value;
        var web = Assert.Single(result.Coverage, coverage => coverage.ScopeId == "Demo.Web");
        var rendered = ReportRenderer.Render(ReportingTestData.CreateReport(result));

        // No pair between the producer and the consumer: the order the consumer reads out is tied to nothing (open question 7).
        Assert.DoesNotContain(result.Findings, finding => InCase(finding.AccessA, "ChannelHandoff") || InCase(finding.AccessB, "ChannelHandoff"));
        // The producer's unknown effect reaches the order it wrote into the channel; the consumer's read of what it took out reaches nothing.
        Assert.Contains(result.Accesses, access => access.Symbol.StartsWith($"{CASES}ChannelHandoff.Producer.", StringComparison.Ordinal) &&
                                                   access.Operation.IsUnknownEffect() && access.Resource.Member?.Name == "Quantity");
        Assert.DoesNotContain(result.Accesses, access => access.Symbol.StartsWith($"{CASES}ChannelHandoff.Consumer.", StringComparison.Ordinal) &&
                                                         access.Resource.Member?.Name == "Quantity");
        // The write into the channel is the gap that stands for the pair, in report.md and in the gaps findings.json lists. The
        // channel's creation is a gap of its own: the singleton keeps what it returns.
        Assert.Equal(["System.Threading.Channels.Channel.CreateUnbounded<T>()",
                      "System.Threading.Channels.ChannelWriter<Demo.Web.Cases.ChannelHandoff.Order>.TryWrite(Order)"],
                     web.Gaps.Where(gap => gap.Sites.Any(site => InCase(site, "ChannelHandoff"))).Select(gap => gap.Callee).Order(StringComparer.Ordinal));
        var gap = Assert.Single(web.Gaps, gap => gap.Callee.EndsWith(".TryWrite(Order)", StringComparison.Ordinal));
        Assert.Equal(SemanticGapKinds.UNKNOWN_LIBRARY, gap.Kind);
        Assert.Contains($"    - {gap.Callee} ({gap.Kind}): roots 1, regions 1, call sites 1", rendered.ReportMarkdown.Split('\n'));
        using var json = JsonDocument.Parse(rendered.FindingsJson);
        var listedByScope = json.RootElement.GetProperty("findings").EnumerateArray()
                                .Select(finding => finding.GetProperty("analysis").GetProperty("semanticGaps").EnumerateArray()
                                                          .Select(listed => (Scope: listed.GetProperty("scope").GetString(),
                                                                             Callee: listed.GetProperty("callee").GetString()))
                                                          .ToArray())
                                .Where(listed => listed.Any(item => item.Scope == "Demo.Web"))
                                .ToArray();
        Assert.NotEmpty(listedByScope);
        Assert.All(listedByScope, listed => Assert.Contains(("Demo.Web", gap.Callee), listed.Select(item => (item.Scope, item.Callee))));
    }

    private const string CASES = "Demo.Web.Cases.";

    private static bool InCase(Access access, string name) => access.Symbol.StartsWith($"{CASES}{name}.", StringComparison.Ordinal);

    private static async Task<ScopeCoverage> Web() =>
        Assert.Single((await DemoExpectationTests.SharedDemo.Value).Coverage, coverage => coverage.ScopeId == "Demo.Web");

    private static bool InCase(OperationSite site, string name) => site.BodyId.Contains($".Cases.{name}.", StringComparison.Ordinal);
}
