using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Expectations;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class ExpectationMatcherTests
{
    [Fact]
    public void Finding_matching_an_active_entry_satisfies_it()
    {
        var finding = Finding("F1", ("Controller.Set()", "write"), ("Controller.Set()", "write"));
        var file = File(findings: [Expected("expected", "1a", finding)]);

        var report = ExpectationMatcher.Match([finding], file, "1a");

        Assert.True(report.IsExactMatch);
    }

    [Fact]
    public void Missing_finding_for_an_active_entry_is_reported()
    {
        var finding = Finding("F1", ("Controller.Set()", "write"), ("Controller.Set()", "write"));
        var file = File(findings: [Expected("expected", "1a", finding)]);

        var report = ExpectationMatcher.Match([], file, "1a");

        Assert.Equal(["expected"], report.Missing);
    }

    [Fact]
    public void Access_pair_matches_in_either_order()
    {
        var finding = Finding("F1", ("Controller.Set()", "write"), ("Controller.Get()", "read"));
        var entry = Expected("expected", "1a", finding) with
        {
            Accesses = [
                new ExpectationAccess("Controller.Get()", "read"),
                new ExpectationAccess("Controller.Set()", "write")]
        };

        var report = ExpectationMatcher.Match([finding], File(findings: [entry]), "1a");

        Assert.True(report.IsExactMatch);
    }

    [Fact]
    public void Finding_matching_only_a_later_phase_entry_is_ignored()
    {
        var finding = Finding("F1", ("Controller.Set()", "write"), ("Controller.Set()", "write"));
        var file = File(findings: [Expected("later", "1b", finding)]);

        var report = ExpectationMatcher.Match([finding], file, "1a");

        Assert.True(report.IsExactMatch);
    }

    [Fact]
    public void Finding_matching_no_entry_is_a_false_positive()
    {
        var finding = Finding("F1", ("Controller.Set()", "write"), ("Controller.Set()", "write"));

        var report = ExpectationMatcher.Match([finding], File(), "1a");

        Assert.Equal(["F1"], report.FalsePositives);
    }

    [Fact]
    public void NotDefect_without_accesses_forbids_any_finding_on_the_resource()
    {
        var finding = Finding("F1", ("Controller.Set()", "write"), ("Controller.Set()", "write"));
        var notDefect = new NotDefectExpectation(
            "guarded",
            "1a",
            new ExpectationResource(finding.Resource.Region, finding.Resource.AccessPath));

        var report = ExpectationMatcher.Match([finding], File(notDefects: [notDefect]), "1a");

        Assert.Equal(["guarded: F1"], report.ForbiddenHits);
        Assert.Empty(report.FalsePositives);
    }

    [Fact]
    public void NotDefect_with_accesses_forbids_only_that_pair()
    {
        var forbidden = Finding("F1", ("Controller.Set()", "write"), ("Controller.Get()", "read"));
        var allowed = Finding("F2", ("Controller.Other()", "write"), ("Controller.Other()", "write"));
        var notDefect = new NotDefectExpectation(
            "guarded-pair",
            "1a",
            new ExpectationResource(forbidden.Resource.Region, forbidden.Resource.AccessPath),
            [new ExpectationAccess("Controller.Set()", "write"),
                new ExpectationAccess("Controller.Get()", "read")]);
        var file = File(
            findings: [Expected("allowed", "1a", allowed)],
            notDefects: [notDefect]);

        var report = ExpectationMatcher.Match([forbidden, allowed], file, "1a");

        Assert.Equal(["guarded-pair: F1"], report.ForbiddenHits);
        Assert.DoesNotContain("guarded-pair: F2", report.ForbiddenHits);
    }

    [Fact]
    public void Later_phase_notDefect_is_ignored()
    {
        var finding = Finding("F1", ("Controller.Set()", "write"), ("Controller.Set()", "write"));
        var notDefect = new NotDefectExpectation(
            "later",
            "2",
            new ExpectationResource(finding.Resource.Region, finding.Resource.AccessPath));

        var report = ExpectationMatcher.Match([finding], File(notDefects: [notDefect]), "1a");

        Assert.True(report.IsExactMatch);
    }

    [Fact]
    public void Phases_order_from_0_through_1a_1b_to_8()
    {
        string[] phases = ["0", "1a", "1b", "2", "3", "4", "5", "6", "7", "8"];

        for (var first = 0; first < phases.Length; first++)
        {
            for (var second = 0; second < phases.Length; second++)
                Assert.Equal(first.CompareTo(second), PhaseOrder.Compare(phases[first], phases[second]));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseOrder.Compare("unknown", "8"));
    }

    private static ExpectationFile File(IReadOnlyList<FindingExpectation>? findings = null,
                                        IReadOnlyList<NotDefectExpectation>? notDefects = null) =>
        new("1", findings ?? [], notDefects ?? []);

    private static FindingExpectation Expected(string id, string phase, Finding finding) =>
        new(
            id,
            phase,
            finding.RuleId,
            finding.Confidence.Label.ToLowerInvariant(),
            new ExpectationResource(finding.Resource.Region, finding.Resource.AccessPath),
            [new ExpectationAccess(finding.AccessA.Symbol, finding.AccessA.Operation.ToWireName()),
                new ExpectationAccess(finding.AccessB.Symbol, finding.AccessB.Operation.ToWireName())]);

    private static Finding Finding(string id, (string Symbol, string Operation) first,
                                   (string Symbol, string Operation) second)
    {
        var resource = new ResourceId("Fixture", "static:Demo.State", ["Value"]);
        var accessA = Access(resource, first, 1);
        var accessB = Access(resource, second, 2);
        return new Finding(
            id,
            "stable",
            "G1",
            "DCA1001",
            resource,
            accessA,
            accessB,
            "unprotected",
            new FindingConfidence("High", 85, new ConfidenceComponents(25, 20, 20, 20, 0)),
            [],
            [],
            [],
            []);
    }

    private static StaticAccess Access(ResourceId resource, (string Symbol, string Operation) expected, int line)
    {
        var operation = expected.Operation switch
        {
            "read" => AccessOperation.Read,
            "write" => AccessOperation.Write,
            "read-modify-write" => AccessOperation.ReadModifyWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(expected))
        };
        var root = new ExecutionRoot($"root:{expected.Symbol}", expected.Symbol,
            $"ControllerBase action {expected.Symbol}");
        return new StaticAccess(resource, operation, root, expected.Symbol,
            new SourceSpan("Controller.cs", line, 1, line, 2), [], []);
    }
}
