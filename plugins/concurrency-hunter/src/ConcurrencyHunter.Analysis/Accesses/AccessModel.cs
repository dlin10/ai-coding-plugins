using System.Collections.Frozen;
using System.Collections.ObjectModel;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Accesses;

/// <summary>The declaring type, name and kind of the field, backing field or constructor parameter an access touches.
/// A field hidden by a same-named field of a derived type has a different declaring type, so a different key.
/// <see cref="DeclaringTypeIdentity"/> tells closed generics over same-named type arguments apart.</summary>
/// <param name="DeclaringType">The type that declares the member, as the report names it.</param>
/// <param name="Name">The member's name.</param>
/// <param name="Kind">Whether the member is a field, a backing field or a constructor parameter.</param>
/// <param name="DeclaringTypeIdentity">The declaring type's identity where it differs from <paramref name="DeclaringType"/>, null
/// otherwise.</param>
public sealed record MemberKey(string DeclaringType, string Name, IrFieldKind Kind, string? DeclaringTypeIdentity = null)
{
    public string Identity => $"{DeclaringTypeIdentity ?? DeclaringType}.{Name}:{Kind}";

    /// <summary>The key of a field as an access names it.</summary>
    /// <param name="field">The field.</param>
    internal static MemberKey Of(IrFieldRef field) =>
        new(field.ContainingType, field.Name, field.Kind, field.ContainingTypeIdentity == field.ContainingType ? null : field.ContainingTypeIdentity);
}

/// <summary>The fields an access goes through from the region it is on (R1): the region's own field first, then each field of a struct
/// the storage goes on through; where the storage is in a cell of a collection, the cell stands after the first <see cref="Cell"/> of
/// them, and the last of those holds the collection.</summary>
/// <param name="Fields">The fields, outermost first; one field, the struct's whole storage, for a box's.</param>
/// <param name="Cell">How many of <paramref name="Fields"/> lead to the collection the storage is in a cell of; 0 where it is in none.</param>
/// <param name="Region">The region the heap keeps the access's own field in: the resource's region, or the collection whose cell holds the
/// struct.</param>
/// <param name="Slot">That field's slot of <paramref name="Region"/>: the path through every struct field before it; empty for a box's whole
/// storage.</param>
internal sealed record StoragePath(IReadOnlyList<IrFieldRef> Fields, int Cell, string Region, string Slot)
{
    /// <summary>The field the resource is named by (<see cref="AccessResource.Member"/>): the one holding the collection for a cell, the
    /// region's own field otherwise.</summary>
    internal IrFieldRef Member => Cell == 0 ? Fields[0] : Fields[Cell - 1];

    /// <summary>The fields below <see cref="Member"/>, or below the cell, that the resource goes on through
    /// (<see cref="AccessResource.StructPath"/>).</summary>
    internal IEnumerable<IrFieldRef> Below => Fields.Skip(Cell == 0 ? 1 : Cell);
}

/// <summary>What an access touches. <see cref="Identity"/> joins the scope, assembly, region identity (<see cref="RegionId"/>, which carries
/// the region's context), member key and the struct fields below it, and is what pairing and grouping tell resources apart by: they
/// compare the numbers of those parts (<see cref="KeyIn"/>, <see cref="SameIdentity"/>) and order by the text without joining it
/// (<see cref="IDENTITY_ORDER"/>), so that no resource keeps the text (issue 474). <see cref="Region"/> and <see cref="AccessPath"/> keep
/// the display format. A wildcard resource has the path <c>*</c> and the member key <c>*</c>.</summary>
/// <param name="Assembly">The assembly that declares the member.</param>
/// <param name="Scope">The scope the access was collected in.</param>
/// <param name="Region">The region's display, such as <c>alloc:</c>, <c>static:</c>, <c>di:</c> or <c>closure:</c>.</param>
/// <param name="AccessPath">The path's names from the region: every field, a cell's selector right after the collection's field.</param>
/// <param name="Member">The field the resource is named by: the region's own field, or the one holding the collection for a cell.</param>
/// <param name="RegionId">The region's identity, which carries its context; null where the resource names no region.</param>
/// <param name="IsWildcard">Whether the path collapsed into the region's wildcard resource (TD-042).</param>
public sealed record AccessResource(string Assembly, string Scope, string Region, IReadOnlyList<string> AccessPath, MemberKey Member,
                                    string? RegionId = null, bool IsWildcard = false)
{
    /// <summary>The readonly root fields excluded from an ordinary unknown effect's wildcard write. Reads and reflection exclude none,
    /// and share the one empty set (issue 454).</summary>
    internal IReadOnlySet<string> WildcardExclusions { get; init; } = FrozenSet<string>.Empty;

    /// <summary>The fields of the structs the access goes on through below <see cref="Member"/>, or below the cell of the collection it
    /// holds, outermost first (R1): a struct kept in the region's storage is part of it, and a field of it is the region's resource with
    /// the whole path. Empty for a field, a collection or a cell itself.</summary>
    public IReadOnlyList<MemberKey> StructPath { get; init; } = [];

    /// <summary>The fields the access goes through from <see cref="RegionId"/>, as the analysis names them: <see cref="Member"/>'s field,
    /// then those of <see cref="StructPath"/>, with <see cref="StoragePath.Cell"/> saying where a cell stands between them.</summary>
    internal StoragePath? Storage { get; init; }

    /// <summary>Which cell of the collection the access touches, null when it touches the collection itself (TD-043). It is part of
    /// the identity: two proven cells are two resources.</summary>
    public ElementSelector? Selector { get; init; }

    /// <summary>The collection object this resource belongs to, where the resource is a collection or one of its cells (ADR 0010).
    /// It is the whole of the identity when it is there: a collection is the object it is, and two fields that hold one
    /// dictionary hold one dictionary however differently they are named. The field stays in <see cref="AccessPath"/> and
    /// <see cref="Member"/>, which is what the report shows.</summary>
    public string? CollectionId { get; init; }

    /// <summary>The collection itself, without the cell: what the candidate index groups by, so that a cell nothing proves apart
    /// still meets every cell it may be (TD-075). It is joined anew on every read, for the report and tests; the analysis compares
    /// <see cref="ResourceKey.Structural"/> instead.</summary>
    public string StructuralIdentity =>
        (CollectionId is { } collection ? $"{Scope}|{collection}" : $"{Scope}|{Assembly}|{RegionId ?? Region}|{Member.Identity}") + PathText();

    /// <summary>The resource's identity: <see cref="StructuralIdentity"/>, then the cell where there is one. It is joined anew on every
    /// read, for the report and tests; the analysis compares <see cref="KeyIn"/>'s key or <see cref="SameIdentity"/> and orders by
    /// <see cref="IDENTITY_ORDER"/>.</summary>
    public string Identity => Selector is null ? StructuralIdentity : $"{StructuralIdentity}|{Selector.Text}";

    /// <summary>How many parts <see cref="Part"/> lists.</summary>
    private const int PARTS = 7;

    /// <summary>The struct fields below the member as <see cref="Identity"/> names them; empty where there are none.</summary>
    private string PathText() => StructPath.Count == 0 ? string.Empty : string.Concat(StructPath.Select(member => $"/{member.Identity}"));

    /// <summary>A part <see cref="Identity"/> is joined from, null where the resource has none: the scope; the collection; for a resource
    /// of no collection the assembly, the region's identity (its display where it has none) and the member's identity; the struct fields
    /// below, as the text names them; the cell's text. <see cref="KeyIn"/> numbers these parts and <see cref="SameIdentity"/> compares
    /// them, so the two read one list.</summary>
    /// <param name="index">The part's place, from 0 to <see cref="PARTS"/> − 1.</param>
    private string? Part(int index) => index switch
    {
        0 => Scope,
        1 => CollectionId,
        2 => CollectionId is null ? Assembly : null,
        3 => CollectionId is null ? RegionId ?? Region : null,
        4 => CollectionId is null ? Member.Identity : null,
        5 => StructPath.Count == 0 ? null : PathText(),
        _ => Selector?.Text
    };

    /// <summary>The key of <see cref="Identity"/> in a table of numbers: the number of each of its parts, so that a key is made without
    /// joining the text and compares and hashes without reading a string (<see cref="ResourceKey"/>).</summary>
    /// <param name="numbers">The table the parts are numbered in; two keys tell resources apart only when made in one table.</param>
    internal ResourceKey KeyIn(TextNumbers numbers) =>
        new(NumberOf(numbers, 0), NumberOf(numbers, 1), NumberOf(numbers, 2), NumberOf(numbers, 3), NumberOf(numbers, 4), NumberOf(numbers, 5),
            NumberOf(numbers, 6));

    /// <summary>The number of one part of the identity, <see cref="ResourceKey.NONE"/> where the resource has none.</summary>
    /// <param name="numbers">The table the part is numbered in.</param>
    /// <param name="index">The part's place (<see cref="Part"/>).</param>
    private int NumberOf(TextNumbers numbers, int index) => Part(index) is { } part ? numbers.Of(part) : ResourceKey.NONE;

    /// <summary>Whether two resources have one identity: their parts compared one by one, as <see cref="KeyIn"/> numbers them, where no
    /// table of numbers is at hand (the candidate index's rule, the findings and the report). A later part is worked out only where
    /// every earlier one agrees.</summary>
    /// <param name="one">One resource.</param>
    /// <param name="other">The other resource.</param>
    internal static bool SameIdentity(AccessResource one, AccessResource other)
    {
        if (ReferenceEquals(one, other))
            return true;
        // bounded: the seven parts of an identity, each worked out once.
        for (var index = 0; index < PARTS; index++)
        {
            if (!string.Equals(one.Part(index), other.Part(index), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>Resources in the ordinal order of their <see cref="Identity"/>, read from the strings the text is joined from without
    /// joining it: the order the candidate index enumerates its buckets in and the solver spends its budget in (issue 474).</summary>
    internal static readonly IComparer<AccessResource> IDENTITY_ORDER = Comparer<AccessResource>.Create(CompareIdentities);

    /// <summary>How two identities compare, as <see cref="string.CompareOrdinal(string, string)"/> compares their texts: the strings
    /// each text is joined from (<see cref="Segment"/>) are read in turn, and each step compares the rest of the current string of one
    /// side with as much of the other side's.</summary>
    /// <param name="one">One resource.</param>
    /// <param name="other">The other resource.</param>
    private static int CompareIdentities(AccessResource? one, AccessResource? other)
    {
        if (ReferenceEquals(one, other))
            return 0;
        if (one is null || other is null)
            return one is null ? -1 : 1;
        var (oneCount, otherCount) = (one.SegmentCount, other.SegmentCount);
        var (oneAt, otherAt) = (0, 0);
        ReadOnlySpan<char> oneRest = default;
        ReadOnlySpan<char> otherRest = default;
        // bounded: each step reads at least one character of each text or returns, so it ends within the shorter text.
        while (true)
        {
            // bounded: each turn takes the next of the side's strings, of which there are SegmentCount.
            while (oneRest.IsEmpty && oneAt < oneCount)
                oneRest = one.Segment(oneAt++);
            // bounded: each turn takes the next of the side's strings, of which there are SegmentCount.
            while (otherRest.IsEmpty && otherAt < otherCount)
                otherRest = other.Segment(otherAt++);
            if (oneRest.IsEmpty || otherRest.IsEmpty)
                return oneRest.IsEmpty ? otherRest.IsEmpty ? 0 : -1 : 1;
            var length = Math.Min(oneRest.Length, otherRest.Length);
            var order = oneRest[..length].SequenceCompareTo(otherRest[..length]);
            if (order != 0)
                return order;
            oneRest = oneRest[length..];
            otherRest = otherRest[length..];
        }
    }

    /// <summary>How many strings <see cref="Segment"/> lists: the head, six for each struct field below and two for a cell.</summary>
    private int SegmentCount => (CollectionId is null ? 11 : 3) + 6 * StructPath.Count + (Selector is null ? 0 : 2);

    /// <summary>The strings <see cref="Identity"/> is joined from, in order: the scope, a bar and the collection, or the scope, the
    /// assembly, the region and the member's five strings (<see cref="MemberSegment"/>) with a bar between each; for each struct field
    /// below a slash and its five strings; for a cell a bar and its text.</summary>
    /// <param name="index">The string's place, from 0 to <see cref="SegmentCount"/> − 1.</param>
    private string Segment(int index)
    {
        var head = CollectionId is null ? 11 : 3;
        if (index < head)
        {
            return CollectionId is { } collection
                ? index switch { 0 => Scope, 1 => "|", _ => collection }
                : index switch { 0 => Scope, 1 or 3 or 5 => "|", 2 => Assembly, 4 => RegionId ?? Region, _ => MemberSegment(Member, index - 6) };
        }

        index -= head;
        if (index < 6 * StructPath.Count)
            return index % 6 == 0 ? "/" : MemberSegment(StructPath[index / 6], index % 6 - 1);
        return index == 6 * StructPath.Count ? "|" : Selector!.Text;
    }

    /// <summary>The strings <see cref="MemberKey.Identity"/> is joined from: the declaring type's identity, a dot, the name, a colon and
    /// the kind.</summary>
    /// <param name="member">The member.</param>
    /// <param name="index">The string's place, from 0 to 4.</param>
    private static string MemberSegment(MemberKey member, int index) => index switch
    {
        0 => member.DeclaringTypeIdentity ?? member.DeclaringType,
        1 => ".",
        2 => member.Name,
        3 => ":",
        _ => member.Kind switch
        {
            IrFieldKind.Field => nameof(IrFieldKind.Field),
            IrFieldKind.PropertyBackingField => nameof(IrFieldKind.PropertyBackingField),
            IrFieldKind.PrimaryConstructorParameter => nameof(IrFieldKind.PrimaryConstructorParameter),
            _ => member.Kind.ToString()
        }
    };

    /// <summary>The region's context-free identity (R6), which fingerprints hash; the region identity when none was given.</summary>
    public string RegionKey { get => field ?? RegionId ?? Region; init; }
}

/// <summary>A resource's identity as the numbers of the parts its text is joined from (<see cref="AccessResource.KeyIn"/>, issue 474): the
/// scope; the collection, or the assembly, the region and the member; the struct fields below; the cell. Equal keys are equal texts. The
/// converse holds as long as no part, joined to its neighbours, reads as another split of the same text, which
/// <c>ResourceKeyTests</c> checks on the demo and on a fixture. A number is a <see cref="TextNumbers"/> number, so a key is compared and
/// hashed and never ordered: <see cref="AccessResource.IDENTITY_ORDER"/> orders resources by their text.</summary>
/// <param name="Scope">The number of the scope.</param>
/// <param name="Collection">The number of the collection; <see cref="NONE"/> for a resource of no collection.</param>
/// <param name="Assembly">The number of the assembly; <see cref="NONE"/> for a collection's resource.</param>
/// <param name="Region">The number of the region's identity, or of its display where it has none; <see cref="NONE"/> for a collection's
/// resource.</param>
/// <param name="Member">The number of the member's identity; <see cref="NONE"/> for a collection's resource.</param>
/// <param name="StructPath">The number of the struct fields below, as the text names them; <see cref="NONE"/> for none.</param>
/// <param name="Selector">The number of the cell's text; <see cref="NONE"/> for none.</param>
internal readonly record struct ResourceKey(int Scope, int Collection, int Assembly, int Region, int Member, int StructPath, int Selector)
{
    /// <summary>The number of a part the resource does not have.</summary>
    internal const int NONE = -1;

    /// <summary>The key of <see cref="AccessResource.StructuralIdentity"/>: the collection itself, without the cell.</summary>
    internal ResourceKey Structural => this with { Selector = NONE };
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

/// <summary>What an execution gives an access besides the execution itself, and what pairing never reads: the root it runs under, the
/// path the walk reached it by and the loads it depends on, as the report shows them (ADR 0038).</summary>
/// <param name="Root">The root whose execution runs the access.</param>
/// <param name="PathRoot">The root the call path starts at.</param>
/// <param name="CallPath">The member symbols of the access's discovery path.</param>
/// <param name="SpawnSites">The sites of the call path's spawn and timer-callback segments.</param>
/// <param name="CodeFlow">The steps reaching the access.</param>
/// <param name="ReadSources">The loads a read-modify-write depends on.</param>
public sealed record AccessVariant(AccessRoot Root, AccessRoot PathRoot, IReadOnlyList<string> CallPath, IReadOnlyList<SpawnSiteLocation> SpawnSites,
                                   IReadOnlyList<CodeFlowStep> CodeFlow, IReadOnlyList<ReadSource> ReadSources);

/// <summary>One execution of an access recorded once (ADR 0038): the execution, what it gives the access, and where the access stood
/// among every access the scope's executions made, in the order they made them.</summary>
/// <param name="ExecutionId">The execution that runs the access.</param>
/// <param name="Variant">The root, path and loads that execution gives it.</param>
/// <param name="Position">The access's place in the scope's collection order, which pairing keeps.</param>
public readonly record struct AccessExecution(string ExecutionId, AccessVariant Variant, int Position);

/// <summary>An access on one resource, recorded once with the executions that run it so (ADR 0038). It carries its region's ownership and
/// evidence chain; a construction-local access touches the object its construction builds and never pairs; a read-modify-write lists the
/// loads it depends on. Its own execution, root and path are its first execution's; <see cref="Executions"/> lists every execution, and an
/// access built for one execution alone lists none.</summary>
/// <param name="Resource">The storage this access touches.</param>
/// <param name="Operation">What the access does to that storage.</param>
/// <param name="Root">The root whose execution runs the access.</param>
/// <param name="Symbol">The member containing the access.</param>
/// <param name="Source">The access's source span.</param>
/// <param name="HeldProtection">The displayed protections held over the access.</param>
/// <param name="HeldProtectionIds">The identities of the objects those protections hold.</param>
/// <param name="BindingEvidence">The evidence for the resource's binding.</param>
/// <param name="CodeFlow">The steps reaching the access.</param>
/// <param name="Uncertainties">The uncertainty labels the access carries.</param>
public sealed record Access(AccessResource Resource, AccessOperation Operation, AccessRoot Root, string Symbol, SourceSpan Source,
                            IReadOnlyList<string> HeldProtection, IReadOnlyList<string> HeldProtectionIds,
                            IReadOnlyList<BindingEvidence> BindingEvidence, IReadOnlyList<CodeFlowStep> CodeFlow,
                            IReadOnlyList<string> Uncertainties)
{
    /// <summary>The emitted unseen write before ownership makes an unknown effect a read; null for accesses that are no unseen write.</summary>
    internal AccessOperation? UnseenWriteOperation { get; init; }

    /// <summary>Whether that unseen write replaces the struct storage whole, including its presence.</summary>
    internal bool WritesWholeStruct { get; init; }

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
        ReadOnlyDictionary<string, IReadOnlyList<HeldProtection>>.Empty;

    /// <summary>The predicates that hold wherever this access runs (TD-090): a pair whose two conditions cannot both hold is no
    /// race at all.</summary>
    public IReadOnlyList<PathPredicate> Conditions { get; init; } = [];

    /// <summary>Whether the cell this access names is the iteration number of the parallel loop that runs it, which two
    /// iterations of one run never share (TD-068).</summary>
    public bool IsIterationIndexed { get; init; }

    /// <summary>Whether the access touches a field of a fresh object, reached directly through a value holding only objects the
    /// same invocation created, in its body or handed down through direct synchronous calls, immediately awaited async calls or
    /// proven Task forwarders whose returned work it waits for. Only the object's own fields qualify; every load folded into a
    /// read-modify-write or compound access must qualify too. Two fresh accesses never pair, however far the object has escaped.</summary>
    public bool IsFresh { get; init; }

    /// <summary>Whether a library body analysed at the call site touches an object only the library holds directly.</summary>
    public bool IsLibraryInternal { get; init; }

    /// <summary>The expression naming this access's cell, in the width of its own type, for the solver to compare with another
    /// candidate's (TD-092). Null where the access touches no cell or the expression is not one the analysis reads.</summary>
    public ValueTerm? SelectorTerm { get; init; }

    /// <summary>The checks of any pair of this access that a semantic gap decides by what this side alone does (R6).</summary>
    public IReadOnlyList<GapCheck> GapChecks { get; init; } = [];

    /// <summary>The calls deciding this access's checks, retained separately from check equality.</summary>
    public IReadOnlyList<DecidingCall> DecidingCalls { get; init; } = [];

    /// <summary>The executions of this access in collection order (ADR 0038); empty for an access built for one execution alone.</summary>
    public IReadOnlyList<AccessExecution> Executions { get; init; } = [];

    /// <summary>How many executions the access has: at least one.</summary>
    public int ExecutionCount => Executions.Count == 0 ? 1 : Executions.Count;

    /// <summary>The ids of the executions that run the access, in order; one execution that makes it twice, by two paths, is named twice.</summary>
    public IEnumerable<string> ExecutionIds => Executions.Count == 0 ? [ExecutionId] : Executions.Select(execution => execution.ExecutionId);

    /// <summary>The access as one of its executions has it: that execution, its root, path and loads, and no executions of its own.</summary>
    /// <param name="execution">One of <see cref="Executions"/>.</param>
    public Access View(AccessExecution execution) =>
        this with
        {
            ExecutionId = execution.ExecutionId,
            Root = execution.Variant.Root,
            PathRoot = execution.Variant.PathRoot,
            CallPath = execution.Variant.CallPath,
            SpawnSites = execution.Variant.SpawnSites,
            CodeFlow = execution.Variant.CodeFlow,
            ReadSources = execution.Variant.ReadSources,
            Executions = []
        };
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
    /// <param name="first">The first access.</param>
    /// <param name="second">The second access.</param>
    public static string Of(Access first, Access second)
    {
        WorkCancellation.Check();
        var verdict = UNPROTECTED;
        foreach (var id in first.HeldProtectionIds.Intersect(second.HeldProtectionIds, StringComparer.Ordinal))
        {
            WorkCancellation.Check();
            foreach (var held in Held(first, id))
            {
                WorkCancellation.Check();
                foreach (var other in Held(second, id))
                {
                    WorkCancellation.Check();
                    verdict = Stronger(verdict, Of(held, other));
                }
            }
        }
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

    /// <summary>The calls of every occurrence deciding a check of this pair.</summary>
    public IReadOnlyList<DecidingCall> DecidingCalls { get; init; } = [];

    /// <summary>The candidate this pair is a witness of, by its number among its scope's candidates (ADR 0042): the witnesses of one pair
    /// of accesses, one pair of path roots and one orientation are one candidate, which the solver asks once. Null for a pair that is a
    /// candidate of its own.</summary>
    internal int? Candidate { get; init; }
}

/// <summary>A scope's pairs. <see cref="Comparisons"/> counts the compared pairs; <see cref="CartesianBound"/> is n(n+1)/2 over the
/// scope's non-construction-local accesses; <see cref="Candidates"/> counts the candidates the compared pairs that survived the filters
/// gave, each with one or more witnesses in <see cref="Pairs"/> (ADR 0042).</summary>
/// <param name="Pairs">The witnesses of the candidates.</param>
/// <param name="Comparisons">How many pairs were compared.</param>
/// <param name="Suppressed">The compared pairs a sufficient protection suppressed.</param>
/// <param name="Skips">The compared pairs a filter skipped, by the filter.</param>
public sealed record PairAnalysis(IReadOnlyList<AccessPair> Pairs, int Comparisons, int Suppressed,
                                  IReadOnlyDictionary<string, int> Skips)
{
    public int CartesianBound { get; init; }
    public int Buckets { get; init; }
    public int LargestBucket { get; init; }
    /// <summary>How many candidates the pairs are witnesses of: a pair with no candidate's number is one of its own.</summary>
    public int Candidates =>
        Pairs.Count(pair => pair.Candidate is null) + Pairs.Where(pair => pair.Candidate is not null).Select(pair => pair.Candidate).Distinct().Count();

    /// <summary>The ordered pairs, by each assumption (<see cref="OrderingAssumptions"/>) their ordering rests on alone (R5): a pair is
    /// counted once under every assumption it rests on, and is among the <c>ordered</c> skips as well. Empty unless given.</summary>
    public IReadOnlyDictionary<string, int> Assumed { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}
