using System.Security.Cryptography;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Analysis;

/// <summary>
/// Findings from candidate pairs (R10): <c>DCA1002</c> when one side is a read-modify-write and the other writes, else
/// <c>DCA1001</c>. Findings are grouped by rule and object: scope, assembly, region identity, path and member key.
/// </summary>
internal static class ConflictFindings
{
    internal const string CONFLICT_RULE = "DCA1001";
    internal const string LOST_UPDATE_RULE = "DCA1002";
    internal const string PATH_UNCERTAINTY = "Path feasibility is not analyzed in this version.";
    internal const string LIFECYCLE_UNCERTAINTY = "Ordering between lifecycle methods of one hosted service is not analyzed in this version.";
    internal const string WILDCARD_UNCERTAINTY = "The resource is a wildcard: an access path longer than the analysis limit was collapsed.";

    private const string HOSTING_PROVIDER = "hosting";
    private const int RESOURCE_IDENTITY = 25;
    private const int WILDCARD_RESOURCE_IDENTITY = 10;
    private const int HIGH_MINIMUM = 80;
    private const int MEDIUM_MINIMUM = 55;
    private const int LISTED_OCCURRENCES = 3;

    internal static string MergedContextUncertainty(string method) =>
        $"Contexts of {method} were merged; the objects involved may be more than one.";

    internal static (IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups) Create(IEnumerable<AccessPair> pairs,
                                                                                                 CancellationToken cancellationToken) =>
        Create(pairs, [], cancellationToken);

    /// <summary>Folds the pairs into findings, one per rule, resource and unordered pair of sites (R5), each with its occurrences, and
    /// groups them by rule and resource. <paramref name="accesses"/> are the scope's accesses the site ordinals are counted over.</summary>
    internal static (IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups) Create(IEnumerable<AccessPair> pairs,
                                                                                                 IReadOnlyList<Access> accesses,
                                                                                                 CancellationToken cancellationToken)
    {
        var pairList = pairs.ToArray();
        var ordinals = new SiteOrdinals(accesses.Select(access => (access.Resource, access))
                                                .Concat(pairList.SelectMany(pair => new[] { (pair.Resource, pair.First), (pair.Resource, pair.Second) })));
        var folded = pairList.Select(pair =>
                                 {
                                     var (accessA, accessB) = Orient(pair.First, pair.Second, pair.Resource, ordinals);
                                     return new Candidate(GroupKey.From(Rule(accessA, accessB), pair.Resource), pair.Resource, accessA, accessB,
                                                          pair.Protection, pair.Uncertainties);
                                 })
                             .GroupBy(FindingIdentity, StringComparer.Ordinal)
                             .Select(Fold)
                             .ToArray();

        var groups = folded.GroupBy(finding => finding.Evidence.Key)
                           .Select(group => group.OrderBy(item => item.Evidence.AccessA.PathRoot.RootId, StringComparer.Ordinal)
                                                 .ThenBy(item => item.Evidence.AccessA.Operation.ToWireName(), StringComparer.Ordinal)
                                                 .ThenBy(item => item.Evidence.AccessB.PathRoot.RootId, StringComparer.Ordinal)
                                                 .ThenBy(item => item.Evidence.AccessB.Operation.ToWireName(), StringComparer.Ordinal)
                                                 .ThenBy(item => item.Evidence.AccessA.Source.Path, StringComparer.Ordinal)
                                                 .ThenBy(item => item.Evidence.AccessA.Source.StartLine)
                                                 .ThenBy(item => item.Evidence.AccessA.Source.StartColumn)
                                                 .ThenBy(item => item.Evidence.AccessB.Source.Path, StringComparer.Ordinal)
                                                 .ThenBy(item => item.Evidence.AccessB.Source.StartLine)
                                                 .ThenBy(item => item.Evidence.AccessB.Source.StartColumn)
                                                 .ToArray())
                           .OrderByDescending(group => group.Max(item => item.Score))
                           .ThenBy(group => group[0].Evidence.Key.Scope, StringComparer.Ordinal)
                           .ThenBy(group => group[0].Evidence.Key.Assembly, StringComparer.Ordinal)
                           .ThenBy(group => group[0].Evidence.Key.Region, StringComparer.Ordinal)
                           .ThenBy(group => group[0].Evidence.Key.Path, StringComparer.Ordinal)
                           .ThenBy(group => group[0].Evidence.Key.MemberKey, StringComparer.Ordinal)
                           .ThenBy(group => group[0].Evidence.AccessA.Symbol, StringComparer.Ordinal)
                           .ThenBy(group => group[0].Evidence.Key.Rule, StringComparer.Ordinal)
                           .ToArray();

        var findings = new List<Finding>();
        var findingGroups = new List<FindingGroup>();
        foreach (var (group, index) in groups.Select((group, index) => (group, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupId = $"G{index + 1}";
            var first = group[0].Evidence;
            var groupFingerprint = Hash($"ch-gfp-1|{first.Key.Rule}|{ResourceText(first.Resource)}");
            var groupFindings = new List<Finding>();
            foreach (var item in group)
                groupFindings.Add(BuildFinding(item, groupId, $"F{findings.Count + groupFindings.Count + 1}", ordinals) with { GroupFingerprint = groupFingerprint });
            findings.AddRange(groupFindings);
            var owner = first.AccessB.Resource.Identity == first.Resource.Identity ? first.AccessB : first.AccessA;
            owner = first.AccessA.Resource.Identity == first.Resource.Identity ? first.AccessA : owner;
            findingGroups.Add(new FindingGroup(groupId, groupFingerprint, first.Key.Rule, HighestConfidence(groupFindings),
                                               groupFindings[0].Resource, owner.Ownership, owner.OwnershipEvidence,
                                               groupFindings.Select(finding => finding.FindingId).ToArray())
            {
                OccurrenceCount = groupFindings.Sum(finding => finding.OccurrenceCount)
            });
        }

        return (findings, findingGroups);
    }

    /// <summary>The pairs of one finding folded into occurrences: one per pair of roots and call paths, whose pair is the least
    /// protected, then the one with the smallest contexts; the evidence is the first occurrence with the finding's protection.</summary>
    private static FoldedFinding Fold(IEnumerable<Candidate> candidates)
    {
        var occurrences = candidates.GroupBy(candidate => (candidate.AccessA.PathRoot.RootId, candidate.AccessB.PathRoot.RootId,
                                                           PathText(candidate.AccessA), PathText(candidate.AccessB)))
                                    .Select(group => group.OrderBy(candidate => ProtectionRank(candidate.Protection))
                                                          .ThenBy(candidate => candidate.AccessA.InstanceId, StringComparer.Ordinal)
                                                          .ThenBy(candidate => candidate.AccessB.InstanceId, StringComparer.Ordinal)
                                                          .First())
                                    .OrderBy(candidate => candidate.AccessA.PathRoot.RootId, StringComparer.Ordinal)
                                    .ThenBy(candidate => candidate.AccessB.PathRoot.RootId, StringComparer.Ordinal)
                                    .ThenBy(PathText, StringComparer.Ordinal)
                                    .ThenBy(candidate => PathText(candidate.AccessB), StringComparer.Ordinal)
                                    .ToArray();
        var protection = occurrences.Min(candidate => ProtectionRank(candidate.Protection));
        return new FoldedFinding(occurrences.First(candidate => ProtectionRank(candidate.Protection) == protection), occurrences,
                                 occurrences.Max(Score));
    }

    private static string PathText(Candidate candidate) => PathText(candidate.AccessA);

    private static string PathText(Access access) => string.Join(" → ", access.CallPath);

    private static int ProtectionRank(string protection) => protection switch
    {
        PairProtection.UNPROTECTED => 0,
        PairProtection.PARTIAL => 1,
        PairProtection.DIFFERENT_IDENTITY => 2,
        _ => 3
    };
    private static string Rule(Access accessA, Access accessB) =>
        (accessA.Operation, accessB.Operation) switch
        {
            (AccessOperation.ReadModifyWrite, AccessOperation.Write or AccessOperation.ReadModifyWrite) => LOST_UPDATE_RULE,
            (AccessOperation.Write, AccessOperation.ReadModifyWrite) => LOST_UPDATE_RULE,
            _ => CONFLICT_RULE
        };

    private static int Score(Candidate candidate) => Score(Components(candidate));

    private static int Score(ConfidenceComponents components) =>
        components.ResourceIdentity + components.ExecutionOverlap + components.Operation + components.Protection + components.PathFeasibility;

    /// <summary>The TD-103 components: a wildcard resource lowers resource identity; path feasibility is not analyzed.</summary>
    private static ConfidenceComponents Components(Candidate candidate) =>
        new(candidate.Resource.IsWildcard ? WILDCARD_RESOURCE_IDENTITY : RESOURCE_IDENTITY, 20, 20, 20, 0);

    private static string Label(int score) => score >= HIGH_MINIMUM ? "High" : score >= MEDIUM_MINIMUM ? "Medium" : "Low";

    private static Finding BuildFinding(FoldedFinding folded, string groupId, string findingId, SiteOrdinals ordinals)
    {
        var candidate = folded.Evidence;
        var (accessA, accessB) = (candidate.AccessA, candidate.AccessB);
        var overlap = accessA.PathRoot.RootId == accessB.PathRoot.RootId
            ? $"The root {accessA.PathRoot.Display} may run concurrently with itself in scope {candidate.Resource.Scope}."
            : $"The roots {accessA.PathRoot.Display} and {accessB.PathRoot.Display} may run concurrently in scope {candidate.Resource.Scope}.";
        var scenario = Scenario(candidate.Resource, accessA, accessB);
        var uncertainty = new List<string> { PATH_UNCERTAINTY };
        if (IsLifecyclePair(accessA.PathRoot, accessB.PathRoot))
            uncertainty.Add(LIFECYCLE_UNCERTAINTY);
        if (candidate.Resource.IsWildcard)
            uncertainty.Add(WILDCARD_UNCERTAINTY);
        uncertainty.AddRange(folded.Occurrences.SelectMany(occurrence => occurrence.AccessA.Uncertainties.Concat(occurrence.AccessB.Uncertainties)
                                                                                    .Concat(occurrence.Uncertainties))
                                   .Except(uncertainty, StringComparer.Ordinal)
                                   .Distinct(StringComparer.Ordinal)
                                   .Order(StringComparer.Ordinal));
        var components = folded.Occurrences.Select(Components).MaxBy(Score)!;
        var confidence = new FindingConfidence(Label(Score(components)), Score(components), components);
        var listed = folded.Occurrences.Take(LISTED_OCCURRENCES)
                           .Select(occurrence => new FindingOccurrence(occurrence.AccessA.PathRoot, occurrence.AccessB.PathRoot,
                                                                       occurrence.AccessA.CallPath, occurrence.AccessB.CallPath, occurrence.Protection)
                           {
                               SpawnSitesA = occurrence.AccessA.SpawnSites,
                               SpawnSitesB = occurrence.AccessB.SpawnSites
                           })
                           .ToArray();
        var evidence = Evidence(findingId, candidate.Resource, accessA, accessB, candidate.Protection, overlap, scenario)
                       .Concat(SpawnEvidence(findingId, listed))
                       .ToArray();
        var fingerprint = Hash($"ch-fp-1|{candidate.Key.Rule}|{ResourceText(candidate.Resource)}|" +
                               $"{accessA.BodyId}#{ordinals.Of(candidate.Resource, accessA)}|{accessA.Operation.ToWireName()}|" +
                               $"{accessB.BodyId}#{ordinals.Of(candidate.Resource, accessB)}|{accessB.Operation.ToWireName()}|" +
                               candidate.Protection);
        return new Finding(findingId, fingerprint, groupId, candidate.Key.Rule, candidate.Resource, accessA,
                           accessB, candidate.Protection, confidence, [overlap], scenario, uncertainty, evidence)
        {
            OccurrenceCount = folded.Occurrences.Count,
            Occurrences = listed
        };
    }

    /// <summary>The resource as fingerprints hash it: scope, context-free region identity, path and member identity.</summary>
    private static string ResourceText(AccessResource resource) =>
        $"{resource.Scope}|{resource.RegionKey}|{string.Join(".", resource.AccessPath)}|{resource.Member.Identity}";

    /// <summary>Two different lifecycle roots of one hosted-service implementation, whose relative order is not modeled.</summary>
    private static bool IsLifecyclePair(AccessRoot first, AccessRoot second) =>
        first.ProviderId == HOSTING_PROVIDER && second.ProviderId == HOSTING_PROVIDER && first.RootId != second.RootId &&
        first.ReceiverTypeKey is not null && first.ReceiverTypeKey == second.ReceiverTypeKey;

    private static IReadOnlyList<EvidenceItem> Evidence(string findingId, AccessResource resource, Access accessA, Access accessB,
                                                        string protection, string overlap, IReadOnlyList<string> scenario)
    {
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
            new EvidenceItem($"{findingId}.A", "access-a", AccessText("A", resource, accessA)),
            new EvidenceItem($"{findingId}.B", "access-b", AccessText("B", resource, accessB)),
            new EvidenceItem($"{findingId}.R", "resource",
                             $"Resource: {kind} {resource.Member.DeclaringType}.{resource.Member.Name} in region {resource.Region} " +
                             $"of scope {resource.Scope}; ownership: {Ownership(ResourceAccess(resource, accessA, accessB))}; binding evidence: " +
                             $"{(bindings.Length == 0 ? "none" : string.Join("; ", bindings))}."),
            new EvidenceItem($"{findingId}.O", "overlap", overlap),
            new EvidenceItem($"{findingId}.P", "protection",
                             $"Protection: {protection}; A holds {Holdings(accessA)}; B holds {Holdings(accessB)}."),
            new EvidenceItem($"{findingId}.S", "scenario", $"Scenario: {string.Join("; ", scenario)}")
        ];
    }

    /// <summary>
    /// One <c>spawn</c> item per distinct site the listed occurrences pass through a <c>spawn:</c> or <c>timer-callback:</c> segment:
    /// side A's sites, then side B's, each side in occurrence order and path order, a repeated (API, member, location) kept at its
    /// first place. Ids continue the finding's scheme as <c>&lt;findingId&gt;.SP1</c>, <c>.SP2</c>, …
    /// </summary>
    internal static IReadOnlyList<EvidenceItem> SpawnEvidence(string findingId, IReadOnlyList<FindingOccurrence> occurrences)
    {
        var items = new List<EvidenceItem>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var site in occurrences.SelectMany(occurrence => occurrence.SpawnSitesA).Concat(occurrences.SelectMany(occurrence => occurrence.SpawnSitesB)))
        {
            var named = site.Segment[(site.Segment.IndexOf(':') + 1)..];
            var at = named.IndexOf('@');
            var (api, member) = at < 0 ? (named, "") : (named[..at], named[(at + 1)..]);
            var location = $"{site.Source.Path}:{site.Source.StartLine}";
            if (!seen.Add((api, member, location)))
                continue;
            items.Add(new EvidenceItem($"{findingId}.SP{items.Count + 1}", "spawn", $"Spawn site: {api} in {member} at {location}.")
            {
                Source = site.Source
            });
        }

        return items;
    }

    /// <summary>The access, and for a read-modify-write every load it depends on.</summary>
    private static string AccessText(string role, AccessResource resource, Access access)
    {
        var reads = access.Operation == AccessOperation.ReadModifyWrite
            ? string.Concat(access.ReadSources.Select(read => $"; reads it at {read.Source.Path}:{read.Source.StartLine} in {read.Symbol}"))
            : "";
        return $"Access {role}: {access.Symbol} performs {access.Operation.ToWireName()} on {Field(resource)} at " +
               $"{access.Source.Path}:{access.Source.StartLine} under root {access.PathRoot.Display}{reads}; holds {Holdings(access)}.";
    }

    /// <summary>The access whose resource is the one the pair is reported on, whose region's ownership the evidence names; an
    /// open-region pair is reported on the closed side's resource.</summary>
    private static Access ResourceAccess(AccessResource resource, Access accessA, Access accessB) =>
        accessA.Resource.Identity != resource.Identity && accessB.Resource.Identity == resource.Identity ? accessB : accessA;

    private static string Ownership(Access access) =>
        access.OwnershipEvidence.Count == 0
            ? access.Ownership.ToString()
            : $"{access.Ownership} ({string.Join("; ", access.OwnershipEvidence)})";

    private static string Holdings(Access access) =>
        access.HeldProtection.Count == 0 ? "no protection" : string.Join(", ", access.HeldProtection);

    private static IReadOnlyList<string> Scenario(AccessResource resource, Access accessA, Access accessB)
    {
        var field = $"`{Field(resource)}`";
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

    /// <summary>The two sides in the finding's site order: body id, operation, then ordinal; when both are one site, by root id and
    /// call path.</summary>
    private static (Access AccessA, Access AccessB) Orient(Access left, Access right, AccessResource resource, SiteOrdinals ordinals)
    {
        var result = string.CompareOrdinal(left.BodyId, right.BodyId);
        if (result == 0)
            result = string.CompareOrdinal(left.Operation.ToWireName(), right.Operation.ToWireName());
        if (result == 0)
            result = ordinals.Of(resource, left).CompareTo(ordinals.Of(resource, right));
        if (result == 0)
            result = string.CompareOrdinal(Site(left), Site(right));
        if (result == 0)
        {
            result = string.CompareOrdinal(left.PathRoot.RootId, right.PathRoot.RootId);
            if (result == 0)
                result = string.CompareOrdinal(PathText(left), PathText(right));
        }

        return result <= 0 ? (left, right) : (right, left);
    }

    /// <summary>An access's site (R5): its body's assembly-qualified id, operation, path and source span.</summary>
    private static string Site(Access access) =>
        $"{access.BodyId}|{access.Operation.ToWireName()}|{string.Join(".", access.Resource.AccessPath)}|{access.Source.StartLine}|{access.Source.StartColumn}|" +
        $"{access.Source.EndLine}|{access.Source.EndColumn}";

    private static string FindingIdentity(Candidate candidate)
    {
        var sites = new[] { Site(candidate.AccessA), Site(candidate.AccessB) }.Order(StringComparer.Ordinal).ToArray();
        return $"{candidate.Key.Identity}|{sites[0]}|{sites[1]}";
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static string HighestConfidence(IEnumerable<Finding> findings) =>
        findings.Select(finding => finding.Confidence.Label).OrderByDescending(ConfidenceRank).First();

    private static int ConfidenceRank(string label) => label switch
    {
        "High" => 3,
        "Medium" => 2,
        _ => 1
    };

    private static string Field(AccessResource resource) => resource.AccessPath[^1];

    private sealed record GroupKey(string Rule, string Scope, string Assembly, string Region, string Path, string MemberKey)
    {
        internal string Identity => $"{Rule}|{Scope}|{Assembly}|{Region}|{Path}|{MemberKey}";

        internal static GroupKey From(string rule, AccessResource resource) =>
            new(rule, resource.Scope, resource.Assembly, resource.RegionId ?? resource.Region, string.Join(".", resource.AccessPath),
                resource.Member.Identity);
    }

    private sealed record Candidate(GroupKey Key, AccessResource Resource, Access AccessA, Access AccessB, string Protection,
                                    IReadOnlyList<string> Uncertainties);

    private sealed record FoldedFinding(Candidate Evidence, IReadOnlyList<Candidate> Occurrences, int Score);

    /// <summary>Each site's 1-based position among its body's accesses on the same resource with the same operation, in source order:
    /// start, end, then operation id, so two accesses starting at one position keep two ordinals. An access paired on another
    /// resource (a wildcard or an open region) counts among that resource's accesses too.</summary>
    private sealed class SiteOrdinals(IEnumerable<(AccessResource Resource, Access Access)> accesses)
    {
        private readonly Dictionary<string, List<(int, int, int, int, int)>> _positions =
            accesses.GroupBy(item => Key(item.Resource, item.Access), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key,
                                  group => group.Select(item => Position(item.Access)).Distinct().Order().ToList(),
                                  StringComparer.Ordinal);

        internal int Of(AccessResource resource, Access access) =>
            _positions.TryGetValue(Key(resource, access), out var positions) ? positions.IndexOf(Position(access)) + 1 : 0;

        private static (int, int, int, int, int) Position(Access access) =>
            (access.Source.StartLine, access.Source.StartColumn, access.Source.EndLine, access.Source.EndColumn, access.OperationId);

        private static string Key(AccessResource resource, Access access) =>
            $"{access.BodyId}|{resource.Scope}|{resource.RegionKey}|{string.Join(".", resource.AccessPath)}|" +
            $"{resource.Member.Identity}|{access.Operation.ToWireName()}";
    }
}
