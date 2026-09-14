using System.Security.Cryptography;
using System.Text;

namespace ConcurrencyHunter.Analysis;

internal static class ConflictFindings
{
    private const string RULE_ID = "DCA1001";
    private const string PATH_UNCERTAINTY = "Path feasibility is not analyzed in this version.";

    internal static (IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups) Create(IReadOnlyList<StaticAccess> accesses, 
                                                                                                 CancellationToken cancellationToken)
    {
        var candidates = new List<FindingCandidate>();
        var resources = accesses.GroupBy(access => ResourceKey.From(access.Resource))
                                .OrderBy(group => group.Key.Region, StringComparer.Ordinal)
                                .ThenBy(group => group.Key.Path, StringComparer.Ordinal)
                                .ThenBy(group => group.Key.Assembly, StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resourceAccesses = resource.ToArray();
            var pairs = new List<FindingCandidate>();
            for (var first = 0; first < resourceAccesses.Length; first++)
            {
                for (var second = first; second < resourceAccesses.Length; second++)
                {
                    var left = resourceAccesses[first];
                    var right = resourceAccesses[second];
                    if (left.Operation == AccessOperation.Read && right.Operation == AccessOperation.Read)
                        continue;

                    var (accessA, accessB) = Order(left, right);
                    if (accessA.HeldProtectionIds.Intersect(accessB.HeldProtectionIds, StringComparer.Ordinal).Any())
                        continue;

                    pairs.Add(new FindingCandidate(resource.Key, accessA.Resource, accessA, accessB, ProtectionResult(accessA, accessB)));
                }
            }

            candidates.AddRange(pairs.OrderBy(pair => pair.AccessA.Source.Path, StringComparer.Ordinal)
                                     .ThenBy(pair => pair.AccessA.Source.StartLine)
                                     .ThenBy(pair => pair.AccessA.Source.StartColumn)
                                     .ThenBy(pair => pair.AccessB.Source.Path, StringComparer.Ordinal)
                                     .ThenBy(pair => pair.AccessB.Source.StartLine)
                                     .ThenBy(pair => pair.AccessB.Source.StartColumn)
                                     .GroupBy(Identity)
                                     .Select(group => group.First()));
        }

        return Build(candidates);
    }

    private static (IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups) Build(IEnumerable<FindingCandidate> candidates)
    {
        var findings = new List<Finding>();
        var groups = new List<FindingGroup>();
        var findingNumber = 0;
        var orderedGroups = candidates.GroupBy(candidate => candidate.ResourceKey)
                                      .OrderBy(group => group.Key.Region, StringComparer.Ordinal)
                                      .ThenBy(group => group.Key.Path, StringComparer.Ordinal)
                                      .ThenBy(group => group.Key.Assembly, StringComparer.Ordinal)
                                      .ToArray();

        for (var groupIndex = 0; groupIndex < orderedGroups.Length; groupIndex++)
        {
            var candidateGroup = orderedGroups[groupIndex];
            var groupId = $"G{groupIndex + 1}";
            var groupFindings = new List<Finding>();
            foreach (var candidate in candidateGroup.OrderBy(item => item.AccessA.Root.RootId, StringComparer.Ordinal)
                                                    .ThenBy(item => item.AccessA.Operation.ToWireName(), StringComparer.Ordinal)
                                                    .ThenBy(item => item.AccessB.Root.RootId, StringComparer.Ordinal)
                                                    .ThenBy(item => item.AccessB.Operation.ToWireName(), StringComparer.Ordinal)
                                                    .ThenBy(item => item.AccessA.Source.Path, StringComparer.Ordinal)
                                                    .ThenBy(item => item.AccessA.Source.StartLine)
                                                    .ThenBy(item => item.AccessA.Source.StartColumn))
            {
                findingNumber++;
                groupFindings.Add(BuildFinding(candidate, groupId, $"F{findingNumber}"));
            }

            findings.AddRange(groupFindings);
            var first = groupFindings[0];
            groups.Add(new FindingGroup(groupId, StableId(GroupIdentity(candidateGroup.Key)), RULE_ID, HighestConfidence(groupFindings), first.Resource,
                                        groupFindings.Select(finding => finding.FindingId).ToArray()));
        }

        return (findings, groups);
    }

    private static Finding BuildFinding(FindingCandidate candidate, string groupId, string findingId)
    {
        var concurrencyEvidence = candidate.AccessA.Root.RootId == candidate.AccessB.Root.RootId
                                      ? new[] { "The same ControllerBase action may run concurrently with itself." }
                                      : new[] { "Two ControllerBase actions may run concurrently in one process." };
        var scenario = Scenario(candidate.AccessA, candidate.AccessB);
        var confidence = Confidence(85, new ConfidenceComponents(25, 20, 20, 20, 0));
        var stableId = StableId(FindingIdentity(candidate));
        var evidence = Evidence(findingId, candidate.Resource, candidate.AccessA, candidate.AccessB, candidate.ProtectionResult, concurrencyEvidence, scenario);
        return new Finding(findingId, stableId, groupId, RULE_ID, candidate.Resource, candidate.AccessA, candidate.AccessB, candidate.ProtectionResult,
                           confidence, concurrencyEvidence, scenario, [PATH_UNCERTAINTY], evidence);
    }

    private static IReadOnlyList<EvidenceItem> Evidence(string findingId, ResourceId resource, StaticAccess accessA, StaticAccess accessB,
                                                        string protectionResult, IReadOnlyList<string> concurrencyEvidence, IReadOnlyList<string> scenario)
    {
        var field = Field(resource);
        var type = resource.Region.StartsWith("static:", StringComparison.Ordinal) ? resource.Region["static:".Length..] : resource.Region;
        return
        [
            new EvidenceItem($"{findingId}.A", "access-a", AccessText("A", accessA, field)),
            new EvidenceItem($"{findingId}.B", "access-b", AccessText("B", accessB, field)),
            new EvidenceItem($"{findingId}.R", "resource", $"Resource: static field {type}.{field} in region {resource.Region}."),
            new EvidenceItem($"{findingId}.O", "overlap", concurrencyEvidence[0]),
            new EvidenceItem($"{findingId}.P", "protection", $"Protection: {protectionResult}."),
            new EvidenceItem($"{findingId}.S", "scenario", $"Scenario: {string.Join("; ", scenario)}")
        ];
    }

    private static string AccessText(string role, StaticAccess access, string field)
    {
        var protection = access.HeldProtection.Count == 0 ? "no protection" : string.Join(", ", access.HeldProtection);
        return $"Access {role}: {access.Symbol} performs {access.Operation.ToWireName()} on {field} at " +
               $"{access.Source.Path}:{access.Source.StartLine} under root {access.Root.Display}; holds {protection}.";
    }

    private static IReadOnlyList<string> Scenario(StaticAccess accessA, StaticAccess accessB)
    {
        var field = $"`{Field(accessA.Resource)}`";
        if (accessA.Operation == AccessOperation.ReadModifyWrite || accessB.Operation == AccessOperation.ReadModifyWrite)
        {
            var modification = accessA.Operation == AccessOperation.ReadModifyWrite ? "A" : "B";
            var other = modification == "A" ? "B" : "A";
            var otherAccess = modification == "A" ? accessB : accessA;
            return otherAccess.Operation == AccessOperation.Read
                       ?
                       [
                           $"{modification} reads {field}", $"{other} reads {field} before {modification} writes it",
                           $"{other} observes the value {modification} is about to replace"
                       ]
                       :
                       [
                           $"{modification} reads {field}", $"{other} writes {field}",
                           $"{modification} writes a value computed from its stale read, overwriting {other}'s update"
                       ];
        }

        if (accessA.Operation == AccessOperation.Write && accessB.Operation == AccessOperation.Write)
        {
            return
            [
                $"A writes {field}", $"B writes {field} before A's value is used",
                "One of the two writes is lost"
            ];
        }

        var writer = accessA.Operation == AccessOperation.Write ? "A" : "B";
        var reader = writer == "A" ? "B" : "A";
        return
        [
            $"{writer} writes {field}", $"{reader} reads {field} at the same time",
            $"{reader} observes either the old or the new value"
        ];
    }

    private static (StaticAccess AccessA, StaticAccess AccessB) Order(StaticAccess left, StaticAccess right) =>
        Compare(left, right) <= 0 ? (left, right) : (right, left);

    private static int Compare(StaticAccess left, StaticAccess right)
    {
        var result = string.Compare(left.Root.RootId, right.Root.RootId, StringComparison.Ordinal);
        if (result != 0)
            return result;
        result = string.Compare(left.Operation.ToWireName(), right.Operation.ToWireName(), StringComparison.Ordinal);
        if (result != 0)
            return result;
        result = string.Compare(left.Source.Path, right.Source.Path, StringComparison.Ordinal);
        if (result != 0)
            return result;
        result = left.Source.StartLine.CompareTo(right.Source.StartLine);
        return result != 0 ? result : left.Source.StartColumn.CompareTo(right.Source.StartColumn);
    }

    private static string ProtectionResult(StaticAccess accessA, StaticAccess accessB)
    {
        if (accessA.HeldProtectionIds.Count == 0 && accessB.HeldProtectionIds.Count == 0)
            return "unprotected";
        if (accessA.HeldProtectionIds.Count == 0 || accessB.HeldProtectionIds.Count == 0)
            return "partial";
        return "different-identity";
    }

    private static FindingIdentityKey Identity(FindingCandidate candidate) => new(candidate.ResourceKey, candidate.AccessA.Root.RootId,
                                                                                  candidate.AccessA.Operation.ToWireName(), candidate.AccessB.Root.RootId,
                                                                                  candidate.AccessB.Operation.ToWireName());

    private static string FindingIdentity(FindingCandidate candidate) =>
        $"{RULE_ID}|{candidate.ResourceKey.Assembly}|{candidate.ResourceKey.Region}|{candidate.ResourceKey.Path}|" +
        $"{candidate.AccessA.Root.RootId}|{candidate.AccessA.Operation.ToWireName()}|" +
        $"{candidate.AccessB.Root.RootId}|{candidate.AccessB.Operation.ToWireName()}";

    private static string GroupIdentity(ResourceKey key) =>
        $"{RULE_ID}|{key.Assembly}|{key.Region}|{key.Path}";

    private static string StableId(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();

    private static FindingConfidence Confidence(int score, ConfidenceComponents components) =>
        new(score >= 80 ? "High" : score >= 55 ? "Medium" : "Low", score, components);

    private static string HighestConfidence(IEnumerable<Finding> findings) =>
        findings.Select(finding => finding.Confidence.Label).OrderByDescending(ConfidenceRank).First();

    private static int ConfidenceRank(string label) => label switch
                                                       {
                                                           "High" => 3,
                                                           "Medium" => 2,
                                                           _ => 1
                                                       };

    private static string Field(ResourceId resource) => resource.AccessPath[resource.AccessPath.Count - 1];

    private sealed record ResourceKey(string Assembly, string Region, string Path)
    {
        internal static ResourceKey From(ResourceId resource) =>
            new(resource.Assembly, resource.Region, string.Join(".", resource.AccessPath));
    }

    private sealed record FindingIdentityKey(ResourceKey Resource, string RootIdA, string OperationA, string RootIdB, string OperationB);

    private sealed record FindingCandidate(ResourceKey ResourceKey, ResourceId Resource, StaticAccess AccessA, StaticAccess AccessB, string ProtectionResult);
}
