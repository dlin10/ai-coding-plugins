using System.Text.Json.Serialization;
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

public sealed record EvidenceItem(string Id, string Kind, string Text)
{
    /// <summary>The source the item names, for the narrative validator; not part of any wire format.</summary>
    [JsonIgnore]
    public SourceSpan? Source { get; init; }
}

/// <summary>One pair of roots reaching a finding's two sites, with the call path of each side, sides ordered as the finding's sites,
/// and the protection of the pair chosen for it (R5).</summary>
public sealed record FindingOccurrence(AccessRoot RootA, AccessRoot RootB, IReadOnlyList<string> CallPathA, IReadOnlyList<string> CallPathB,
                                       string Protection)
{
    /// <summary>The sites of the <c>spawn:</c> and <c>timer-callback:</c> segments of each side's call path.</summary>
    public IReadOnlyList<SpawnSiteLocation> SpawnSitesA { get; init; } = [];

    public IReadOnlyList<SpawnSiteLocation> SpawnSitesB { get; init; } = [];
}

/// <summary>A pair of access sites on one resource under one rule (ADR 0007). <see cref="AccessA"/> and <see cref="AccessB"/> are the
/// pair the evidence comes from; <see cref="Occurrences"/> lists the first three of <see cref="OccurrenceCount"/>.</summary>
public sealed record Finding(string FindingId, string Fingerprint, string GroupId, string RuleId, AccessResource Resource,
                             Access AccessA, Access AccessB, string ProtectionResult,
                             FindingConfidence Confidence, IReadOnlyList<string> ConcurrencyEvidence,
                             IReadOnlyList<string> Scenario, IReadOnlyList<string> Uncertainty,
                             IReadOnlyList<EvidenceItem> Evidence)
{
    public string GroupFingerprint { get; init; } = "";
    public int OccurrenceCount { get; init; } = 1;
    public IReadOnlyList<FindingOccurrence> Occurrences { get; init; } = [];
}

/// <summary>Findings of one rule on one object: the same scope and resource identity. Two singleton registrations
/// of one implementation are two objects, so two groups. <see cref="Ownership"/> and its evidence are the resource region's.</summary>
public sealed record FindingGroup(string GroupId, string Fingerprint, string RuleId, string ConfidenceLabel,
                                  AccessResource Resource, OwnershipKind Ownership, IReadOnlyList<string> OwnershipEvidence,
                                  IReadOnlyList<string> FindingIds)
{
    /// <summary>The sum of the findings' occurrence counts.</summary>
    public int OccurrenceCount { get; init; }
}

/// <summary>What one process scope's analysis saw: roots by provider, diagnostics from scope discovery, providers, the
/// DI index, injection bindings and lowering, the registrations indexed, and the coverage counters (<see cref="CoverageCounters"/>).
/// <see cref="LoweredNotReached"/> and <see cref="OutsideLoweredSet"/> are inventory, not counted against coverage.</summary>
public sealed record ScopeCoverage(string ScopeId, IReadOnlyDictionary<string, int> RootsPerProvider,
                                   IReadOnlyList<string> Diagnostics, int Registrations,
                                   IReadOnlyDictionary<string, int> Skips)
{
    /// <summary>The sites behind the scope's ordering counters (<see cref="OrderingCounters"/>), by source: the summary counts a site
    /// two scopes reach once, in the widest kind any of them gives it, so identity and not only numbers crosses the scope boundary.</summary>
    public IReadOnlyList<SpawnSiteCoverage> SpawnSites { get; init; } = [];

    public IReadOnlyList<TimerSiteCoverage> TimerSites { get; init; } = [];
    public IReadOnlyList<OperationSite> UnprovenJoins { get; init; } = [];
    public IReadOnlyDictionary<string, int> Ordering { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<(string Callee, int Count)> TopOpaqueCallees { get; init; } = [];
    public IReadOnlyList<string> LoweredNotReached { get; init; } = [];
    public IReadOnlyList<string> OutsideLoweredSet { get; init; } = [];
}

/// <summary>The pair counters summed over scopes; <see cref="LargestBucket"/> is the largest bucket of any scope.</summary>
public sealed record PairCounters(int Comparisons, int CartesianBound, int Buckets, int LargestBucket, int Candidates, int Suppressed,
                                  IReadOnlyDictionary<string, int> Skips)
{
    public static PairCounters None { get; } = new(0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
}

/// <summary>A root provider the analysis ran, with its supported assembly ranges as <c>name min..max</c> (max exclusive).</summary>
public sealed record ProviderSummary(string ProviderId, IReadOnlyList<string> SupportedVersions);

/// <summary>The wall time of one analysis step (R7), summed over scopes.</summary>
public sealed record StepTiming(string Step, double Seconds);

/// <summary>How large one scope's analysis was: reachable bodies, bodies with a summary, heap regions and instances, and the
/// non-construction-local accesses pairing ran over.</summary>
public sealed record ScopeSize(string ScopeId, int ReachableBodies, int Summaries, int Regions, int Instances, int Accesses);

public sealed record AnalysisResult(IReadOnlyList<ProcessScope> Scopes, IReadOnlyList<ExecutionRootDescriptor> Roots,
                                    IReadOnlyList<Access> Accesses, IReadOnlyList<Finding> Findings,
                                    IReadOnlyList<FindingGroup> Groups, IReadOnlyList<ScopeCoverage> Coverage,
                                    PairCounters Pairs, IReadOnlyList<ProviderSummary> Providers)
{
    /// <summary>The steps the analyzer runs, in order: scope discovery, program index, lowering, reachable set, summaries and
    /// fixpoint, executions, accesses, pairing, findings.</summary>
    public IReadOnlyList<StepTiming> Timings { get; init; } = [];

    public IReadOnlyList<ScopeSize> ScopeSizes { get; init; } = [];
}
