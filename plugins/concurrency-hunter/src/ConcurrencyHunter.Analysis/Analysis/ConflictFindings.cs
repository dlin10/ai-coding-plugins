using System.Security.Cryptography;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Analysis;

/// <summary>
/// Findings from candidate pairs (R10): <c>DCA1002</c> when one side is a read-modify-write and the other writes, else
/// <c>DCA1001</c>. Findings are grouped by rule and object: scope, assembly, region, path, member key and sharing key.
/// </summary>
internal static class ConflictFindings
{
    internal const string CONFLICT_RULE = "DCA1001";
    internal const string LOST_UPDATE_RULE = "DCA1002";
    internal const string PATH_UNCERTAINTY = "Path feasibility is not analyzed in this version.";
    internal const string LIFECYCLE_UNCERTAINTY = "Ordering between lifecycle methods of one hosted service is not analyzed in this version.";

    private const string HOSTING_PROVIDER = "hosting";

    internal static (IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups) Create(IEnumerable<AccessPair> pairs,
                                                                                                 CancellationToken cancellationToken)
    {
        var candidates = pairs.Select(pair =>
                              {
                                  var (accessA, accessB) = Order(pair.First, pair.Second);
                                  return new Candidate(GroupKey.From(Rule(accessA, accessB), accessA), accessA, accessB, pair.Protection);
                              })
                              .OrderBy(candidate => candidate.AccessA.Source.Path, StringComparer.Ordinal)
                              .ThenBy(candidate => candidate.AccessA.Source.StartLine)
                              .ThenBy(candidate => candidate.AccessA.Source.StartColumn)
                              .ThenBy(candidate => candidate.AccessB.Source.Path, StringComparer.Ordinal)
                              .ThenBy(candidate => candidate.AccessB.Source.StartLine)
                              .ThenBy(candidate => candidate.AccessB.Source.StartColumn)
                              .GroupBy(FindingIdentity, StringComparer.Ordinal)
                              .Select(duplicates => duplicates.First());

        var groups = candidates.GroupBy(candidate => candidate.Key)
                               .Select(group => group.OrderBy(item => item.AccessA.Root.RootId, StringComparer.Ordinal)
                                                     .ThenBy(item => item.AccessA.Operation.ToWireName(), StringComparer.Ordinal)
                                                     .ThenBy(item => item.AccessB.Root.RootId, StringComparer.Ordinal)
                                                     .ThenBy(item => item.AccessB.Operation.ToWireName(), StringComparer.Ordinal)
                                                     .ThenBy(item => item.AccessA.Source.Path, StringComparer.Ordinal)
                                                     .ThenBy(item => item.AccessA.Source.StartLine)
                                                     .ThenBy(item => item.AccessA.Source.StartColumn)
                                                     .ToArray())
                               .OrderByDescending(group => group.Max(Score))
                               .ThenBy(group => group[0].Key.Scope, StringComparer.Ordinal)
                               .ThenBy(group => group[0].Key.Assembly, StringComparer.Ordinal)
                               .ThenBy(group => group[0].Key.Region, StringComparer.Ordinal)
                               .ThenBy(group => group[0].Key.Path, StringComparer.Ordinal)
                               .ThenBy(group => group[0].Key.MemberKey, StringComparer.Ordinal)
                               .ThenBy(group => group[0].Key.SharingKey, StringComparer.Ordinal)
                               .ThenBy(group => group[0].AccessA.Symbol, StringComparer.Ordinal)
                               .ThenBy(group => group[0].Key.Rule, StringComparer.Ordinal)
                               .ToArray();

        var findings = new List<Finding>();
        var findingGroups = new List<FindingGroup>();
        foreach (var (group, index) in groups.Select((group, index) => (group, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupId = $"G{index + 1}";
            var groupFindings = new List<Finding>();
            foreach (var candidate in group)
                groupFindings.Add(BuildFinding(candidate, groupId, $"F{findings.Count + groupFindings.Count + 1}"));
            findings.AddRange(groupFindings);
            var key = group[0].Key;
            findingGroups.Add(new FindingGroup(groupId, StableId(key.Identity), key.Rule, HighestConfidence(groupFindings),
                                               groupFindings[0].Resource, key.SharingKey,
                                               groupFindings.Select(finding => finding.FindingId).ToArray()));
        }

        return (findings, findingGroups);
    }

    private static string Rule(Access accessA, Access accessB) =>
        (accessA.Operation, accessB.Operation) switch
        {
            (AccessOperation.ReadModifyWrite, AccessOperation.Write or AccessOperation.ReadModifyWrite) => LOST_UPDATE_RULE,
            (AccessOperation.Write, AccessOperation.ReadModifyWrite) => LOST_UPDATE_RULE,
            _ => CONFLICT_RULE
        };

    private static int Score(Candidate candidate) => 85;

    private static Finding BuildFinding(Candidate candidate, string groupId, string findingId)
    {
        var (accessA, accessB) = (candidate.AccessA, candidate.AccessB);
        var overlap = accessA.Root.RootId == accessB.Root.RootId
            ? $"The root {accessA.Root.Display} may run concurrently with itself in scope {accessA.Resource.Scope}."
            : $"The roots {accessA.Root.Display} and {accessB.Root.Display} may run concurrently in scope {accessA.Resource.Scope}.";
        var scenario = Scenario(accessA, accessB);
        var uncertainty = new List<string> { PATH_UNCERTAINTY };
        if (IsLifecyclePair(accessA.Root, accessB.Root))
            uncertainty.Add(LIFECYCLE_UNCERTAINTY);
        uncertainty.AddRange(accessA.Uncertainties.Concat(accessB.Uncertainties).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var confidence = new FindingConfidence("High", Score(candidate), new ConfidenceComponents(25, 20, 20, 20, 0));
        var evidence = Evidence(findingId, accessA, accessB, candidate.Protection, overlap, scenario);
        return new Finding(findingId, StableId(FindingIdentity(candidate)), groupId, candidate.Key.Rule, accessA.Resource, accessA,
                           accessB, candidate.Protection, confidence, [overlap], scenario, uncertainty, evidence);
    }

    /// <summary>Two different lifecycle roots of one hosted-service implementation, whose relative order is not modeled.</summary>
    private static bool IsLifecyclePair(AccessRoot first, AccessRoot second) =>
        first.ProviderId == HOSTING_PROVIDER && second.ProviderId == HOSTING_PROVIDER && first.RootId != second.RootId &&
        first.ReceiverTypeKey is not null && first.ReceiverTypeKey == second.ReceiverTypeKey;

    private static IReadOnlyList<EvidenceItem> Evidence(string findingId, Access accessA, Access accessB, string protection,
                                                        string overlap, IReadOnlyList<string> scenario)
    {
        var resource = accessA.Resource;
        var kind = resource.Region.StartsWith("static:", StringComparison.Ordinal)
            ? "static field"
            : resource.Member.Kind switch
            {
                IrFieldKind.PropertyBackingField => "property",
                IrFieldKind.PrimaryConstructorParameter => "primary constructor parameter",
                _ => "field"
            };
        var bindings = accessA.BindingEvidence.Concat(accessB.BindingEvidence)
                              .Select(item => $"{item.Text} at {item.Source.Path}:{item.Source.StartLine}")
                              .Distinct(StringComparer.Ordinal)
                              .ToArray();
        return
        [
            new EvidenceItem($"{findingId}.A", "access-a", AccessText("A", accessA)),
            new EvidenceItem($"{findingId}.B", "access-b", AccessText("B", accessB)),
            new EvidenceItem($"{findingId}.R", "resource",
                             $"Resource: {kind} {resource.Member.DeclaringType}.{resource.Member.Name} in region {resource.Region} " +
                             $"of scope {resource.Scope}, shared as {accessA.SharingKey}; binding evidence: " +
                             $"{(bindings.Length == 0 ? "none" : string.Join("; ", bindings))}."),
            new EvidenceItem($"{findingId}.O", "overlap", overlap),
            new EvidenceItem($"{findingId}.P", "protection",
                             $"Protection: {protection}; A holds {Holdings(accessA)}; B holds {Holdings(accessB)}."),
            new EvidenceItem($"{findingId}.S", "scenario", $"Scenario: {string.Join("; ", scenario)}")
        ];
    }

    private static string AccessText(string role, Access access) =>
        $"Access {role}: {access.Symbol} performs {access.Operation.ToWireName()} on {Field(access.Resource)} at " +
        $"{access.Source.Path}:{access.Source.StartLine} under root {access.Root.Display}; holds {Holdings(access)}.";

    private static string Holdings(Access access) =>
        access.HeldProtection.Count == 0 ? "no protection" : string.Join(", ", access.HeldProtection);

    private static IReadOnlyList<string> Scenario(Access accessA, Access accessB)
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
            return [$"A writes {field}", $"B writes {field} before A's value is used", "One of the two writes is lost"];

        var writer = accessA.Operation == AccessOperation.Write ? "A" : "B";
        var reader = writer == "A" ? "B" : "A";
        return [$"{writer} writes {field}", $"{reader} reads {field} at the same time", $"{reader} observes either the old or the new value"];
    }

    private static (Access AccessA, Access AccessB) Order(Access left, Access right) =>
        Compare(left, right) <= 0 ? (left, right) : (right, left);

    private static int Compare(Access left, Access right)
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

    private static string FindingIdentity(Candidate candidate) =>
        $"{candidate.Key.Identity}|{candidate.AccessA.Root.RootId}|{candidate.AccessA.Operation.ToWireName()}|" +
        $"{candidate.AccessB.Root.RootId}|{candidate.AccessB.Operation.ToWireName()}";

    private static string StableId(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();

    private static string HighestConfidence(IEnumerable<Finding> findings) =>
        findings.Select(finding => finding.Confidence.Label).OrderByDescending(ConfidenceRank).First();

    private static int ConfidenceRank(string label) => label switch
    {
        "High" => 3,
        "Medium" => 2,
        _ => 1
    };

    private static string Field(AccessResource resource) => resource.AccessPath[^1];

    private sealed record GroupKey(string Rule, string Scope, string Assembly, string Region, string Path, string MemberKey,
                                   string SharingKey)
    {
        internal string Identity => $"{Rule}|{Scope}|{Assembly}|{Region}|{Path}|{MemberKey}|{SharingKey}";

        internal static GroupKey From(string rule, Access access) =>
            new(rule, access.Resource.Scope, access.Resource.Assembly, access.Resource.Region, string.Join(".", access.Resource.AccessPath),
                access.Resource.Member.Identity, access.SharingKey);
    }

    private sealed record Candidate(GroupKey Key, Access AccessA, Access AccessB, string Protection);
}
