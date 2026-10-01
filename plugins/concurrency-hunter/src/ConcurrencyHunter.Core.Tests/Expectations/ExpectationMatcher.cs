using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Core.Tests.Expectations;

internal static class ExpectationMatcher
{
    private const string CONFIDENCE_PHASE = "2";

    /// <summary>From phase 2 an active entry's confidence is compared too: a finding of the entry's identity with another label is a
    /// confidence mismatch.</summary>
    /// <param name="findings">The findings produced by the analysis.</param>
    /// <param name="file">The expectations to match.</param>
    /// <param name="phase">The current known phase.</param>
    internal static ExpectationReport Match(IReadOnlyList<Finding> findings, ExpectationFile file, string phase)
    {
        _ = PhaseOrder.Compare(phase, phase);
        var eligibleFindings = file.Findings.Where(entry => BeforeUntil(entry.Phase, entry.Until, phase)).ToArray();
        var eligibleNotDefects = file.NotDefects.Where(entry => BeforeUntil(entry.Phase, entry.Until, phase)).ToArray();
        var activeFindings = eligibleFindings.Where(entry => PhaseOrder.Compare(entry.Phase, phase) <= 0).ToArray();
        var activeNotDefects = eligibleNotDefects.Where(entry => PhaseOrder.Compare(entry.Phase, phase) <= 0).ToArray();
        var comparesConfidence = PhaseOrder.Compare(phase, CONFIDENCE_PHASE) >= 0;

        var missing = activeFindings
            .Where(entry => !findings.Any(finding => Matches(finding, entry)))
            .Select(entry => entry.Id)
            .ToArray();
        var confidenceMismatches = comparesConfidence
            ? activeFindings
                .Where(entry => !string.IsNullOrEmpty(entry.Confidence))
                .SelectMany(entry => findings.Where(finding => Matches(finding, entry) &&
                                                               !string.Equals(finding.Confidence.Label, entry.Confidence,
                                                                              StringComparison.OrdinalIgnoreCase))
                    .Select(finding => $"{entry.Id}: {finding.FindingId} is {finding.Confidence.Label}, expected {entry.Confidence}"))
                .ToArray()
            : [];
        var forbiddenHits = activeNotDefects
            .SelectMany(entry => findings.Where(finding => Matches(finding, entry))
                .Select(finding => $"{entry.Id}: {finding.FindingId}"))
            .ToArray();
        var falsePositives = findings
            .Where(finding => !eligibleFindings.Any(entry => Matches(finding, entry)) &&
                              !eligibleNotDefects.Any(entry => Matches(finding, entry)))
            .Select(finding => finding.FindingId)
            .ToArray();
        return new ExpectationReport(missing, forbiddenHits, falsePositives, confidenceMismatches);
    }

    private static bool BeforeUntil(string entryPhase, string? until, string phase)
    {
        _ = PhaseOrder.Compare(entryPhase, phase);
        return until is null || PhaseOrder.Compare(phase, until) < 0;
    }

    private static bool Matches(Finding finding, FindingExpectation entry) =>
        finding.RuleId == entry.Rule &&
        Matches(finding.Resource, entry.Resource) &&
        Matches(finding, entry.Accesses);

    private static bool Matches(Finding finding, NotDefectExpectation entry) =>
        Matches(finding.Resource, entry.Resource) &&
        (entry.Accesses is null || Matches(finding, entry.Accesses));

    private static bool Matches(AccessResource resource, ExpectationResource expected) =>
        resource.Region == expected.Region &&
        resource.AccessPath.SequenceEqual(expected.AccessPath, StringComparer.Ordinal);

    private static bool Matches(Finding finding, IReadOnlyList<ExpectationAccess> expected)
    {
        if (expected.Count != 2)
            return false;

        var actualKeys = new[]
        {
            Key(finding.AccessA.Symbol, finding.AccessA.Operation.ToWireName()),
            Key(finding.AccessB.Symbol, finding.AccessB.Operation.ToWireName())
        };
        var expectedKeys = expected.Select(access => Key(access.Symbol, access.Operation)).ToArray();
        Array.Sort(actualKeys, StringComparer.Ordinal);
        Array.Sort(expectedKeys, StringComparer.Ordinal);
        return actualKeys.SequenceEqual(expectedKeys, StringComparer.Ordinal);
    }

    private static string Key(string symbol, string operation) => $"{symbol}\0{operation}";
}

internal sealed record ExpectationReport(IReadOnlyList<string> Missing, IReadOnlyList<string> ForbiddenHits,
                                         IReadOnlyList<string> FalsePositives, IReadOnlyList<string> ConfidenceMismatches)
{
    internal bool IsExactMatch =>
        Missing.Count == 0 && ForbiddenHits.Count == 0 && FalsePositives.Count == 0 && ConfidenceMismatches.Count == 0;
}
