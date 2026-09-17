using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>Hand-built accesses, groups and results for tests of the consumers of findings: the renderer, the run
/// registry, the narrative validator and the expectation matcher.</summary>
public static class FindingTestData
{
    public const string SCOPE = "scope:Fixture";

    public static AccessResource Resource(string region, string field, string assembly = "Fixture") =>
        new(assembly, SCOPE, region, [field],
            new MemberKey(region.StartsWith("static:", StringComparison.Ordinal) ? region["static:".Length..] : region, field, IrFieldKind.Field));

    public static Access Access(AccessResource resource, AccessOperation operation, string rootId, string symbol, SourceSpan source,
                                IReadOnlyList<string>? heldProtection = null, IReadOnlyList<string>? heldProtectionIds = null,
                                string? display = null) =>
        new(resource, operation,
            new AccessRoot(rootId, symbol, display ?? $"ControllerBase action {symbol}", "aspnetcore", "controller-action",
                           new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, resource.Scope), resource.Scope),
            symbol, source, heldProtection ?? [], heldProtectionIds ?? [], [], [], []);

    public static FindingGroup Group(string groupId, string confidenceLabel, AccessResource resource, IReadOnlyList<string> findingIds,
                                     string ruleId = "DCA1001") =>
        new(groupId, $"fingerprint-{groupId}", ruleId, confidenceLabel, resource, OwnershipKind.Shared, [$"{resource.Region} is static storage."], findingIds)
        {
            OccurrenceCount = findingIds.Count
        };

    public static AnalysisResult Result(IReadOnlyList<Finding> findings, IReadOnlyList<FindingGroup> groups)
    {
        var accesses = findings.SelectMany(finding => new[] { finding.AccessA, finding.AccessB }).ToArray();
        var roots = accesses.GroupBy(access => access.Root.RootId, StringComparer.Ordinal)
                            .Select(group => group.First())
                            .Select(access => new ExecutionRootDescriptor(
                                access.Root.RootId, access.Root.RootKind, access.Root.ProviderId,
                                new RootEntry($"body:{access.Root.RootId}", access.Root.Symbol, access.Root.Display, access.Source),
                                new InstanceBindings(ReceiverKind.PerInvocation, []), access.Root.Policy, [], [], [], []))
                            .ToArray();
        return new AnalysisResult([], roots, accesses, findings, groups, [], PairCounters.None, []);
    }
}
