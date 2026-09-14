using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Narrative;

public sealed record NarrativeScope(bool IsSummary, IReadOnlyList<Finding> Findings, IReadOnlyList<string> FindingsToCite)
{
    // findingsListed is how many of the group's findings get_groups showed the composer; the narrative
    // must cite evidence of each of them, and cannot be asked to cite findings it was never shown.
    public static NarrativeScope ForGroup(AnalysisResult result, string groupId, int findingsListed)
    {
        var group = result.Groups.Single(group => group.GroupId == groupId);
        return new NarrativeScope(false, result.Findings.Where(finding => finding.GroupId == groupId).ToArray(),
                                  group.FindingIds.Take(findingsListed).ToArray());
    }

    public static NarrativeScope ForSummary(AnalysisResult result) => new(true, result.Findings, []);
}

public sealed record NarrativeVerdict(bool Accepted, IReadOnlyList<string> Reasons);
