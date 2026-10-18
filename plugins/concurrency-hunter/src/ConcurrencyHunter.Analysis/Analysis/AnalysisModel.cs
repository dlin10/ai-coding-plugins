using System.Text.Json.Serialization;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Roots;
using ConcurrencyHunter.Scopes;

namespace ConcurrencyHunter.Analysis;

/// <summary>What an access does to its cell (TD-071). The atomic kinds are the ones a single hardware operation performs:
/// <c>Interlocked</c>, <c>Volatile</c> and a <c>volatile</c> field (TD-082). A read-modify-write built from a volatile read and a
/// volatile write is not one of them. <see cref="UnknownEffect"/> is what an unresolved call may do to what it reaches, a read and a
/// write at once that conflicts like a write (TD-071, TD-072); on the structure or a cell of a thread-safe collection it is
/// <see cref="AtomicUnknownEffect"/>, as atomic as the collection's own members (ADR 0010).</summary>
public enum AccessOperation
{
    Read,
    Write,
    ReadModifyWrite,
    AtomicRead,
    AtomicWrite,
    AtomicReadModifyWrite,
    CompoundOperation,
    UnknownEffect,
    AtomicUnknownEffect
}

public static class AccessOperationExtensions
{
    public static string ToWireName(this AccessOperation operation) => operation switch
    {
        AccessOperation.Read => "read",
        AccessOperation.Write => "write",
        AccessOperation.ReadModifyWrite => "read-modify-write",
        AccessOperation.AtomicRead => "atomic-read",
        AccessOperation.AtomicWrite => "atomic-write",
        AccessOperation.AtomicReadModifyWrite => "atomic-read-modify-write",
        AccessOperation.CompoundOperation => "compound-operation",
        AccessOperation.UnknownEffect => "unknown-effect",
        AccessOperation.AtomicUnknownEffect => "atomic-unknown-effect",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    /// <summary>Whether an access can make a pair a conflict: only a non-atomic write, a non-atomic read-modify-write, a compound
    /// operation or a non-atomic unknown effect can (TD-072). Two accesses of which neither can are never reported.</summary>
    public static bool Conflicts(this AccessOperation operation) =>
        operation is AccessOperation.Write or AccessOperation.ReadModifyWrite or AccessOperation.CompoundOperation or AccessOperation.UnknownEffect;

    /// <summary>Whether an access is an unknown effect, atomic or not: a side that may read and write the resource in a call.</summary>
    public static bool IsUnknownEffect(this AccessOperation operation) =>
        operation is AccessOperation.UnknownEffect or AccessOperation.AtomicUnknownEffect;
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

    /// <summary>What the solver said about the two paths meeting, null where it was never asked (TD-093).</summary>
    public SolverAnswer? PathFeasibility { get; init; }

    /// <summary>The callees of the semantic gaps that decide a check of one of its occurrences (TD-039): what its uncertainty names,
    /// and what a narrative may name.</summary>
    public IReadOnlyList<string> GapCallees { get; init; } = [];
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
/// <param name="ScopeId">The scope the coverage is of.</param>
/// <param name="RootsPerProvider">The number of roots each provider found in the scope.</param>
/// <param name="Diagnostics">The scope's diagnostics, each prefixed with its source.</param>
/// <param name="Registrations">The number of registrations the scope indexed.</param>
/// <param name="Skips">The <see cref="CoverageCounters"/> values, by counter name, with what the solver did to the scope's candidates.</param>
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

    /// <summary>The scope's semantic gaps, in materiality order (TD-039a).</summary>
    public IReadOnlyList<SemanticGap> Gaps { get; init; } = [];
    public IReadOnlyList<string> LoweredNotReached { get; init; } = [];
    public IReadOnlyList<string> OutsideLoweredSet { get; init; } = [];

    /// <summary>The scope's generation targets that no generated model describes, once each, in ordinal order of declaration id and then
    /// of file key, each with the cause that says why.</summary>
    public IReadOnlyList<GenerationRefusal> GenerationRefusals { get; init; } = [];
}

/// <summary>One generation target of a scope that no generated model describes: a library member reached through a direct opaque call,
/// with the generated file that answers for it. One member reached through two file keys is two items.</summary>
/// <param name="Member">The member as the report shows it.</param>
/// <param name="MemberId">The member's declaration id, the id the generated files name it by.</param>
/// <param name="FileKey">The key of the generated file that answers for the member: its path below the generated folder, or, for a package
/// whose implementation is not on disk, the package's id and version alone.</param>
/// <param name="Cause">Why no generated model describes the member: a word of the generator's, <c>timeout</c>, <c>budget</c>,
/// <c>failed</c>, <c>no-implementation</c>, <c>not-applicable</c>, <c>not-generated</c> or <c>reached-through-model</c>.</param>
public sealed record GenerationRefusal(string Member, string MemberId, string FileKey, string Cause);

/// <summary>A generation target that a run which generates would have generated but a read-only run did not: no layer describes the member
/// and no generated file answers for it, and its implementation is on disk.</summary>
/// <param name="MemberId">The member's declaration id, the id the generated files name it by.</param>
/// <param name="FileKey">The key of the generated file that would hold the answer: its path below the generated folder.</param>
public sealed record WantedGeneration(string MemberId, string FileKey);

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

    /// <summary>For a read-only run, the generation targets a run that generates would have generated, once each across the scopes, in ordinal
    /// order of declaration id and then of file key; empty for a run that generates and for one with the generated layer off.</summary>
    public IReadOnlyList<WantedGeneration> GenerationWanted { get; init; } = [];
}
