using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Accesses;

/// <summary>The declaring type, name and kind of the field, backing field or constructor parameter an access touches.
/// A field hidden by a same-named field of a derived type has a different declaring type, so a different key.
/// <see cref="DeclaringTypeIdentity"/> tells closed generics over same-named type arguments apart.</summary>
public sealed record MemberKey(string DeclaringType, string Name, IrFieldKind Kind, string? DeclaringTypeIdentity = null)
{
    public string Identity => $"{DeclaringTypeIdentity ?? DeclaringType}.{Name}:{Kind}";
}

/// <summary>What an access touches. <see cref="Identity"/> joins the scope, assembly, region identity (<see cref="RegionId"/>, which carries
/// the region's context) and member key and is what pairing, grouping and stable ids use; <see cref="Region"/> and
/// <see cref="AccessPath"/> keep the display format. A wildcard resource has the path <c>*</c> and the member key <c>*</c>.</summary>
public sealed record AccessResource(string Assembly, string Scope, string Region, IReadOnlyList<string> AccessPath, MemberKey Member,
                                    string? RegionId = null, bool IsWildcard = false)
{
    public string Identity => $"{Scope}|{Assembly}|{RegionId ?? Region}|{Member.Identity}";
}

/// <summary>The root an access runs under. A construction or type-initializer execution, which has no root, is described as one:
/// its execution id, display and at-most-once policy.</summary>
public sealed record AccessRoot(string RootId, string Symbol, string Display, string ProviderId, string RootKind,
                                InvocationPolicy Policy, string Scope, string? ReceiverType = null, string? ReceiverTypeKey = null)
{
    /// <summary>Whether two invocations of this one root may run at the same time.</summary>
    public bool OverlapsItself =>
        Policy is { Multiplicity: Multiplicity.Repeated, SelfOverlap: SelfOverlap.MayOverlap } ||
        Policy.Multiplicity == Multiplicity.Unknown || Policy.SelfOverlap == SelfOverlap.Unknown;
}

public sealed record CodeFlowStep(string Kind, string Text, SourceSpan Source);

/// <summary>A load that feeds a read-modify-write store: its symbol, source and code flow.</summary>
public sealed record ReadSource(string Symbol, SourceSpan Source, IReadOnlyList<CodeFlowStep> CodeFlow);

/// <summary>An access as one execution runs it on one resource. It carries its region's ownership and evidence chain; a construction-local
/// access touches the object its construction builds and never pairs; a read-modify-write lists the loads it depends on.</summary>
public sealed record Access(AccessResource Resource, AccessOperation Operation, AccessRoot Root, string Symbol, SourceSpan Source,
                            IReadOnlyList<string> HeldProtection, IReadOnlyList<string> HeldProtectionIds,
                            IReadOnlyList<BindingEvidence> BindingEvidence, IReadOnlyList<CodeFlowStep> CodeFlow,
                            IReadOnlyList<string> Uncertainties)
{
    public string ExecutionId { get; init; } = "";
    public OwnershipKind Ownership { get; init; } = OwnershipKind.Unknown;
    public IReadOnlyList<string> OwnershipEvidence { get; init; } = [];
    public bool IsConstructionLocal { get; init; }
    public IReadOnlyList<ReadSource> ReadSources { get; init; } = [];
    public string BodyId { get; init; } = "";
    public int OperationId { get; init; }
    public string InstanceId { get; init; } = "";
}

public static class PairProtection
{
    public const string UNPROTECTED = "unprotected";
    public const string PARTIAL = "partial";
    public const string DIFFERENT_IDENTITY = "different-identity";
}

/// <summary>Two accesses that may race. <see cref="Resource"/> is the resource the pair is reported on, which differs from the first access's
/// for a wildcard or open-region pair; <see cref="Uncertainties"/> come from how the two resources met.</summary>
public sealed record AccessPair(Access First, Access Second, string Protection)
{
    public AccessResource Resource { get; init; } = First.Resource;
    public IReadOnlyList<string> Uncertainties { get; init; } = [];
}

public sealed record PairAnalysis(IReadOnlyList<AccessPair> Pairs, int CandidatePairs, int Suppressed,
                                  IReadOnlyDictionary<string, int> Skips);
