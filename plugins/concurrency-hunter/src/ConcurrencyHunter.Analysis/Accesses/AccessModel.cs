using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Di;
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

/// <summary>What an access touches. <see cref="Identity"/> joins the scope, assembly, region and member key and is what
/// pairing, grouping and stable ids use; <see cref="Region"/> and <see cref="AccessPath"/> keep the display format.</summary>
public sealed record AccessResource(string Assembly, string Scope, string Region, IReadOnlyList<string> AccessPath, MemberKey Member)
{
    public string Identity => $"{Scope}|{Assembly}|{Region}|{Member.Identity}";
}

public sealed record AccessRoot(string RootId, string Symbol, string Display, string ProviderId, string RootKind,
                                InvocationPolicy Policy, string Scope, string? ReceiverType = null, string? ReceiverTypeKey = null)
{
    /// <summary>Whether two invocations of this one root may run at the same time.</summary>
    public bool OverlapsItself =>
        Policy is { Multiplicity: Multiplicity.Repeated, SelfOverlap: SelfOverlap.MayOverlap } ||
        Policy.Multiplicity == Multiplicity.Unknown || Policy.SelfOverlap == SelfOverlap.Unknown;
}

public sealed record CodeFlowStep(string Kind, string Text, SourceSpan Source);

public sealed record Access(AccessResource Resource, AccessOperation Operation, AccessRoot Root, string Symbol, SourceSpan Source,
                            IReadOnlyList<string> HeldProtection, IReadOnlyList<string> HeldProtectionIds, string SharingKey,
                            IReadOnlyList<BindingEvidence> BindingEvidence, IReadOnlyList<CodeFlowStep> CodeFlow,
                            IReadOnlyList<string> Uncertainties);

/// <summary>Everything the engines read for one process scope: its roots, every lowered body they reach by body key,
/// its DI index and the injection bindings of its source types.</summary>
public sealed record ScopeAnalysisInput(string ScopeId, IReadOnlyList<ExecutionRootDescriptor> Roots,
                                        IReadOnlyDictionary<string, IrBody> Bodies, DiIndex DiIndex,
                                        IReadOnlyList<TypeInjectionBindings> InjectionBindings);

public sealed record AccessExtractionResult(IReadOnlyList<Access> Accesses, IReadOnlyDictionary<string, int> Skips);

public static class SharingKeys
{
    public const string PROCESS = "process";
    public const string INVOCATION = "invocation";

    /// <summary>Prefix of a transient's key on the hosted-service instance whose constructor received it: every instance
    /// gets its own, so one root never shares it with another invocation of itself.</summary>
    public const string HOSTED_INSTANCE = "hosted";
}

public static class PairProtection
{
    public const string UNPROTECTED = "unprotected";
    public const string PARTIAL = "partial";
    public const string DIFFERENT_IDENTITY = "different-identity";
}

public sealed record AccessPair(Access First, Access Second, string Protection);

public sealed record PairAnalysis(IReadOnlyList<AccessPair> Pairs, int CandidatePairs, int Suppressed,
                                  IReadOnlyDictionary<string, int> Skips);
