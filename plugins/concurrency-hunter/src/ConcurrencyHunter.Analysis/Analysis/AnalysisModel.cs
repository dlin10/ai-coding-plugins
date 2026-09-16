using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Roots;
using ConcurrencyHunter.Scopes;

namespace ConcurrencyHunter.Analysis;

public enum AccessOperation
{
    Read,
    Write,
    ReadModifyWrite
}

public static class AccessOperationExtensions
{
    public static string ToWireName(this AccessOperation operation) => operation switch
    {
        AccessOperation.Read => "read",
        AccessOperation.Write => "write",
        AccessOperation.ReadModifyWrite => "read-modify-write",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}

public sealed record SourceSpan(string Path, int StartLine, int StartColumn, int EndLine, int EndColumn);

public sealed record ConfidenceComponents(int ResourceIdentity, int ExecutionOverlap, int Operation, int Protection,
                                          int PathFeasibility);

public sealed record FindingConfidence(string Label, int Score, ConfidenceComponents Components);

public sealed record EvidenceItem(string Id, string Kind, string Text);

public sealed record Finding(string FindingId, string StableId, string GroupId, string RuleId, AccessResource Resource,
                             Access AccessA, Access AccessB, string ProtectionResult,
                             FindingConfidence Confidence, IReadOnlyList<string> ConcurrencyEvidence,
                             IReadOnlyList<string> Scenario, IReadOnlyList<string> Uncertainty,
                             IReadOnlyList<EvidenceItem> Evidence);

/// <summary>Findings of one rule on one object: the same scope and resource identity. Two singleton registrations
/// of one implementation are two objects, so two groups. <see cref="Ownership"/> and its evidence are the resource region's.</summary>
public sealed record FindingGroup(string GroupId, string StableId, string RuleId, string ConfidenceLabel,
                                  AccessResource Resource, OwnershipKind Ownership, IReadOnlyList<string> OwnershipEvidence,
                                  IReadOnlyList<string> FindingIds);

/// <summary>What one process scope's analysis saw: roots by provider, diagnostics from scope discovery, providers, the
/// DI index, injection bindings and lowering, the registrations indexed, and the coverage counters (<see cref="CoverageCounters"/>).
/// <see cref="LoweredNotReached"/> and <see cref="OutsideLoweredSet"/> are inventory, not counted against coverage.</summary>
public sealed record ScopeCoverage(string ScopeId, IReadOnlyDictionary<string, int> RootsPerProvider,
                                   IReadOnlyList<string> Diagnostics, int Registrations,
                                   IReadOnlyDictionary<string, int> Skips)
{
    public IReadOnlyList<(string Callee, int Count)> TopOpaqueCallees { get; init; } = [];
    public IReadOnlyList<string> LoweredNotReached { get; init; } = [];
    public IReadOnlyList<string> OutsideLoweredSet { get; init; } = [];
}

public sealed record PairCounters(int Candidates, int Suppressed, IReadOnlyDictionary<string, int> Skips)
{
    public static PairCounters None { get; } = new(0, 0, new Dictionary<string, int>());
}

/// <summary>A root provider the analysis ran, with its supported assembly ranges as <c>name min..max</c> (max exclusive).</summary>
public sealed record ProviderSummary(string ProviderId, IReadOnlyList<string> SupportedVersions);

public sealed record AnalysisResult(IReadOnlyList<ProcessScope> Scopes, IReadOnlyList<ExecutionRootDescriptor> Roots,
                                    IReadOnlyList<Access> Accesses, IReadOnlyList<Finding> Findings,
                                    IReadOnlyList<FindingGroup> Groups, IReadOnlyList<ScopeCoverage> Coverage,
                                    PairCounters Pairs, IReadOnlyList<ProviderSummary> Providers);
