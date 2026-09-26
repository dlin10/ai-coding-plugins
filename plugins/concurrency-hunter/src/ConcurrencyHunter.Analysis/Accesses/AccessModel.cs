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
    /// <summary>Which cell of the collection the access touches, null when it touches the collection itself (TD-043). It is part of
    /// the identity: two proven cells are two resources.</summary>
    public ElementSelector? Selector { get; init; }

    /// <summary>The collection object this resource belongs to, where the resource is a collection or one of its cells (ADR 0010).
    /// It is the whole of the identity when it is there: a collection is the object it is, and two fields that hold one
    /// dictionary hold one dictionary however differently they are named. The field stays in <see cref="AccessPath"/> and
    /// <see cref="Member"/>, which is what the report shows.</summary>
    public string? CollectionId { get; init; }

    /// <summary>The collection itself, without the cell: what the candidate index groups by, so that a cell nothing proves apart
    /// still meets every cell it may be (TD-075).</summary>
    public string StructuralIdentity =>
        CollectionId is { } collection ? $"{Scope}|{collection}" : $"{Scope}|{Assembly}|{RegionId ?? Region}|{Member.Identity}";

    public string Identity => Selector is null ? StructuralIdentity : $"{StructuralIdentity}|{Selector.Text}";

    /// <summary>The region's context-free identity (R6), which fingerprints hash; the region identity when none was given.</summary>
    public string RegionKey { get => field ?? RegionId ?? Region; init; }
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

/// <summary>A <c>spawn:</c> or <c>timer-callback:</c> segment of a call path and the source of the site it names.</summary>
public sealed record SpawnSiteLocation(string Segment, SourceSpan Source);

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
    internal bool IsReferenceAccess { get; init; }
    public IReadOnlyList<ReadSource> ReadSources { get; init; } = [];
    public string BodyId { get; init; } = "";
    public int OperationId { get; init; }
    public string InstanceId { get; init; } = "";

    /// <summary>The member symbols of the access's discovery path, from its root's member to the member holding the access.</summary>
    public IReadOnlyList<string> CallPath { get; init; } = [];

    /// <summary>The sites of the <c>spawn:</c> and <c>timer-callback:</c> segments of <see cref="CallPath"/>, in path order.</summary>
    public IReadOnlyList<SpawnSiteLocation> SpawnSites { get; init; } = [];

    /// <summary>The root the call path starts at: the access's own root, or for a construction that is an execution of its own, the root
    /// whose walk first triggers it.</summary>
    public AccessRoot PathRoot { get => field ?? Root; init; }

    /// <summary>What the access holds on each protection of <see cref="HeldProtectionIds"/>, by that protection's id. A lock whose
    /// object identity is unknown has no id and no entry: it can never exclude the other side. One object can be held by several
    /// mechanisms at once - a <c>lock</c> taken over a <c>Lock</c> already entered, say - so an id carries every holding of it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HeldProtection>> HeldProtections { get; init; } =
        new Dictionary<string, IReadOnlyList<HeldProtection>>();

    /// <summary>The predicates that hold wherever this access runs (TD-090): a pair whose two conditions cannot both hold is no
    /// race at all.</summary>
    public IReadOnlyList<PathPredicate> Conditions { get; init; } = [];

    /// <summary>Whether the cell this access names is the iteration number of the parallel loop that runs it, which two
    /// iterations of one run never share (TD-068).</summary>
    public bool IsIterationIndexed { get; init; }

    /// <summary>The expression naming this access's cell, in the width of its own type, for the solver to compare with another
    /// candidate's (TD-092). Null where the access touches no cell or the expression is not one the analysis reads.</summary>
    public ValueTerm? SelectorTerm { get; init; }

    /// <summary>The checks of any pair of this access that a semantic gap decides by what this side alone does (R6).</summary>
    public IReadOnlyList<GapCheck> GapChecks { get; init; } = [];
}

/// <summary>What one access holds on one lock object: the mechanism that took it, the mode it is held in, and whether that holding
/// excludes anyone at all (TD-083).</summary>
public sealed record HeldProtection(IrSynchronizationPrimitive Primitive, IrLockMode Mode, bool IsExclusive);

/// <summary>The verdict of the protection analysis (SPEC 7). Only <see cref="SUFFICIENT"/> takes the candidate away; the three
/// verdicts between it and <see cref="UNPROTECTED"/> say that someone already treated the resource as shared.</summary>
public static class PairProtection
{
    public const string UNPROTECTED = "unprotected";
    public const string PARTIAL = "partial";
    public const string DIFFERENT_IDENTITY = "different-identity";
    public const string INCOMPATIBLE_MODE = "incompatible-mode";
    public const string SUFFICIENT = "sufficient";

    /// <summary>What the two sides hold decides the verdict, and the strongest common protection decides it: one lock object taken
    /// by one mechanism in modes that exclude each other is sufficient when both holdings exclude at all, and partial when one of
    /// them does not; another mechanism or a mode that does not exclude is <see cref="INCOMPATIBLE_MODE"/>. With nothing in common,
    /// two objects are <see cref="DIFFERENT_IDENTITY"/> and one side alone is <see cref="PARTIAL"/> (TD-081, TD-083). An object the
    /// two sides hold by several mechanisms is decided by the strongest pair of holdings: one mechanism they share excludes them
    /// whatever else either of them also took.</summary>
    public static string Of(Access first, Access second)
    {
        var verdict = UNPROTECTED;
        foreach (var id in first.HeldProtectionIds.Intersect(second.HeldProtectionIds, StringComparer.Ordinal))
            foreach (var held in Held(first, id))
                foreach (var other in Held(second, id))
                    verdict = Stronger(verdict, Of(held, other));
        if (verdict != UNPROTECTED)
            return verdict;

        return (first.HeldProtection.Count != 0, second.HeldProtection.Count != 0) switch
        {
            (false, false) => UNPROTECTED,
            (true, true) => DIFFERENT_IDENTITY,
            _ => PARTIAL
        };
    }

    private static string Of(HeldProtection first, HeldProtection second) =>
        first.Primitive != second.Primitive || !Excludes(first.Mode, second.Mode) ? INCOMPATIBLE_MODE
            : first.IsExclusive && second.IsExclusive ? SUFFICIENT
            : PARTIAL;

    private static IReadOnlyList<HeldProtection> Held(Access access, string id) =>
        access.HeldProtections.TryGetValue(id, out var held) && held.Count != 0
            ? held
            : [new HeldProtection(IrSynchronizationPrimitive.Monitor, IrLockMode.Exclusive, true)];

    /// <summary>Whether two acquisitions of one lock exclude each other. Readers do not exclude readers, and a reader does not
    /// exclude an upgradeable reader; every other pair of modes does (TD-083).</summary>
    private static bool Excludes(IrLockMode first, IrLockMode second) =>
        !(first == IrLockMode.Read && second is IrLockMode.Read or IrLockMode.UpgradeableRead ||
          second == IrLockMode.Read && first == IrLockMode.UpgradeableRead);

    private static string Stronger(string first, string second) => Rank(first) >= Rank(second) ? first : second;

    private static int Rank(string verdict) => verdict switch
    {
        SUFFICIENT => 4,
        PARTIAL => 3,
        INCOMPATIBLE_MODE => 2,
        DIFFERENT_IDENTITY => 1,
        _ => 0
    };
}

/// <summary>Two accesses that may race. <see cref="Resource"/> is the resource the pair is reported on, which differs from the first access's
/// for a wildcard or open-region pair; <see cref="Uncertainties"/> come from how the two resources met.</summary>
public sealed record AccessPair(Access First, Access Second, string Protection)
{
    public AccessResource Resource { get; init; } = First.Resource;
    public IReadOnlyList<string> Uncertainties { get; init; } = [];

    /// <summary>What the solver said about the two paths meeting, null where it was never asked (TD-093). Only
    /// <see cref="SolverAnswer.Sat"/> is a proof that the pair can happen; an undecided answer lowers its confidence.</summary>
    public SolverAnswer? Feasibility { get; init; }

    /// <summary>The checks of the pair a semantic gap decides (R6), each of which costs its component of the confidence.</summary>
    public IReadOnlyList<GapCheck> GapChecks { get; init; } = [];
}

/// <summary>A scope's pairs. <see cref="Comparisons"/> counts the compared pairs; <see cref="CartesianBound"/> is n(n+1)/2 over the
/// scope's non-construction-local accesses; <see cref="Candidates"/> are the compared pairs that survived the filters.</summary>
public sealed record PairAnalysis(IReadOnlyList<AccessPair> Pairs, int Comparisons, int Suppressed,
                                  IReadOnlyDictionary<string, int> Skips)
{
    public int CartesianBound { get; init; }
    public int Buckets { get; init; }
    public int LargestBucket { get; init; }
    public int Candidates => Pairs.Count;
}
