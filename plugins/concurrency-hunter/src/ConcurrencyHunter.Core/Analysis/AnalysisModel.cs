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

public sealed record ExecutionRoot(string RootId, string Symbol, string Display);

public sealed record ResourceId(string Assembly, string Region, IReadOnlyList<string> AccessPath);

public sealed record StaticAccess(ResourceId Resource, AccessOperation Operation, ExecutionRoot Root, string Symbol,
                                  SourceSpan Source, IReadOnlyList<string> HeldProtection,
                                  IReadOnlyList<string> HeldProtectionIds);

public sealed record AccessInventory(IReadOnlyList<ExecutionRoot> Roots, IReadOnlyList<StaticAccess> Accesses);

public sealed record ConfidenceComponents(int ResourceIdentity, int ExecutionOverlap, int Operation, int Protection,
                                          int PathFeasibility);

public sealed record FindingConfidence(string Label, int Score, ConfidenceComponents Components);

public sealed record EvidenceItem(string Id, string Kind, string Text);

public sealed record Finding(string FindingId, string StableId, string GroupId, string RuleId, ResourceId Resource,
                             StaticAccess AccessA, StaticAccess AccessB, string ProtectionResult,
                             FindingConfidence Confidence, IReadOnlyList<string> ConcurrencyEvidence,
                             IReadOnlyList<string> Scenario, IReadOnlyList<string> Uncertainty,
                             IReadOnlyList<EvidenceItem> Evidence);

public sealed record FindingGroup(string GroupId, string StableId, string RuleId, string ConfidenceLabel,
                                  ResourceId Resource, IReadOnlyList<string> FindingIds);

public sealed record AnalysisResult(IReadOnlyList<ExecutionRoot> Roots, IReadOnlyList<StaticAccess> Accesses,
                                    IReadOnlyList<Finding> Findings, IReadOnlyList<FindingGroup> Groups);
