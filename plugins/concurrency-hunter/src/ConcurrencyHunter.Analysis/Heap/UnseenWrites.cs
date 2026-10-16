using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Heap;

/// <summary>What writes struct storage that the analysis does not see (R9).</summary>
public enum UnseenWriteKind
{
    /// <summary>The unknown effect of an unresolved call that is no reflection, or of a <c>dynamic</c> operation: it cannot assign a
    /// readonly field of the object it reaches.</summary>
    UnknownEffect,

    /// <summary>The unknown effect of reflection, which assigns a readonly field as any other.</summary>
    Reflection,

    /// <summary>A library model's <c>writes-arg</c>: every field of the objects the argument may be, one level deep.</summary>
    ModelArgument,

    /// <summary>A library model's <c>writes-cells</c>: every cell of the arrays the argument may be.</summary>
    ModelCells,

    /// <summary>A call's <c>out</c> or <c>ref</c> output that no body the run has makes: an opaque member's, or a library model's.</summary>
    Output
}

/// <summary>What <see cref="UnseenWrites.ModelArgumentTargets{TRegion}"/> asks of a heap, over the region ids of the heap being solved or of
/// the solved one.</summary>
/// <typeparam name="TRegion">The heap's region id.</typeparam>
/// <param name="LibrarySequenceYields">What a library sequence or grouping yields, null for any other region.</param>
/// <param name="IsCollection">Whether a region is an array or a collection ADR 0010 models.</param>
/// <param name="TypeKey">A region's type, null where it has none.</param>
/// <param name="HeldBy">The objects a collection or an array holds: its cells and, for a dictionary, its keys.</param>
/// <param name="Slots">A region's slots.</param>
/// <param name="PointsTo">The regions a slot of a region points to.</param>
/// <param name="IsDelegate">Whether a region is a delegate.</param>
public sealed record RegionView<TRegion>(Func<TRegion, IEnumerable<TRegion>?> LibrarySequenceYields, Func<TRegion, bool> IsCollection,
                                         Func<TRegion, string?> TypeKey, Func<TRegion, IEnumerable<TRegion>> HeldBy,
                                         Func<TRegion, IEnumerable<string>> Slots, Func<TRegion, string, IEnumerable<TRegion>> PointsTo,
                                         Func<TRegion, bool> IsDelegate)
    where TRegion : notnull;

/// <summary>
/// The one rule of whether code the analysis does not see may write a slot (R9, TD-042, <c>CONTEXT.md</c> <b>Unknown effect</b>): an
/// unknown effect sees a receiver only through the state of the type declaring the member (<see cref="Seen"/>); it cannot assign storage
/// readonly in the object it touches, which only reflection writes (<see cref="IsOwnReadonly"/>, <see cref="MayWrite"/>); a model writes
/// what it says it writes, and an output the place it is handed; past the depth bound a wildcard over the region covers every slot the
/// rule lets it write (<see cref="IsPastBound"/>), excluding readonly root fields from that write even when writable neighbors remain.
/// <c>DeepReadAccesses</c> records these exclusions, which both <c>CandidateIndex.Meets</c> and its indexed enumeration honor. Struct storage such code writes may hold a value, as after a copy: a <c>Nullable</c>
/// read from it boxes (<see cref="PresenceSlots"/>).
/// </summary>
/// <remarks>
/// <para>The places that decide by it. The heap's <c>WholeProgram</c> records unknown-effect starts in <c>RecordUnseenStarts</c> during
/// propagation, deduplicated across instances and passes by kind, receiver restriction and region. After propagation converges,
/// <c>MarkUnseenWrites</c> runs one multi-source <c>MarkUnseenWalk</c> per kind and restriction, with a shared visited set of region/path
/// values and slot enumeration that copies no field sets. Each slot is marked through <c>MarkUnseen</c>, for every unknown effect
/// <c>UnknownCalls.Of</c> makes and every model write
/// the accesses make: an opaque call (<c>MarkUnknownEffect</c>) and a dispatch's library object no model knows (the <c>LibraryCalls</c>
/// projection of an unresolved dispatch in <c>Process</c>); an unresolved dispatch where the heap records it (<c>MarkUnresolvedDispatch</c>
/// from <c>Unresolved</c> — a source virtual or an interface member on a receiver it runs no body on, a factory, a fated delegate, a
/// library sequence's iterator delegate at its enumeration — and from <c>Call</c>'s dispatches with no receiver object), its receivers
/// through <c>MarkUnseenReceivers</c> as <c>UnknownCalls.Reach.Starts</c> starts from them, a receiver a library implementation decides
/// left out by <c>UnknownCalls.DecidedByLibrary</c>; work that resolves to no body (<c>RecordUnresolvedWork</c>); a <c>dynamic</c>
/// operation; a model's <c>writes-arg</c> and <c>writes-cells</c> (<c>MarkModelWrite</c>), at the call and, deferred to the enumeration of
/// the sequence the call returns, where that sequence is consumed (<c>Consume</c>), a <c>writes-arg</c>'s objects by
/// <see cref="ModelArgumentTargets{TRegion}"/>; and the target of an output — <c>WriteOpaqueOuts</c>' store, a model's <c>out</c> or
/// <c>ref</c> output — (<c>CopyStruct</c> on a copy <see cref="StructCopy.IsUnseenOutput"/> marks). <c>HoldsStruct</c> reads the marks. The
/// accesses' <c>InterproceduralAccesses.DeepReadAccesses</c> makes an unknown effect's access a write or a read by it, and
/// <c>Emit</c> records output copies through <see cref="OutputSlots"/>; <c>ArgumentWriteAccesses</c> (by <see cref="ModelArgumentTargets{TRegion}"/>) and <c>CellWriteAccesses</c> a model's write, each
/// recording the storage it writes (<see cref="Accesses.InterproceduralCollection.UnseenFilledStorage"/>, which equals
/// <see cref="HeapSolution.UnseenFilledStorage"/>); and <c>UnknownCalls.Reach.Seen</c> takes its receiver restriction from it. Both sides
/// take the slots a write may fill from <see cref="PresenceSlots"/> and <see cref="PresenceSlotsBelow"/>, and an array's cells of any rank
/// from <see cref="ElementTypeKey"/>. <see cref="FieldsOf"/> supplies the fields both walks see, including metadata-base fields named by
/// <see cref="LibraryFields"/> in reached instances, closed by each instance's substitution. The heap records those fields during
/// <c>Process</c>; <c>UnknownCalls.Reach</c> records them from the solved instances and uses the same field source.</para>
/// <para>The heap marks before ownership exists: an unknown effect on a region one execution owns is still a write here, and only the
/// accesses' <c>Emit</c> makes it a read afterwards (<c>CONTEXT.md</c> <b>Unknown effect</b>). Once the heap solves, its
/// <c>MarkEscapedDeferredWrites</c> applies deferred writes of sequences that escaped, including their sources, through the same
/// <c>MarkDeferredWrites</c> as a visible <c>Consume</c>. One outer loop in <c>Run</c> applies the grouped walks and escaped deferred
/// writes and resumes propagation while either adds storage. The filled-storage set tracks its membership readers (<c>HoldsStruct</c>
/// and <c>Holds</c>), so new marks invalidate exactly their dependent processings. Model writes and unseen outputs still mark only their
/// own targets during propagation. The sets only grow and are bounded by the scope's struct storage, before executions and ownership
/// are built.</para>
/// </remarks>
public static class UnseenWrites
{
    /// <summary>The fields an unseen write can reach: the source type's fields and the metadata fields the program names on this
    /// region. The heap's <c>MarkUnseenWalk</c>, <c>MarkModelWrite</c> and <c>OwnReadonlyRoot</c>, accesses' <c>DeepReadAccesses</c>,
    /// <c>ArgumentWriteAccesses</c> and <c>OwnReadonlyRoot</c>, and <c>UnknownCalls.Reach.FieldsOf</c> share this source and its receiver restriction.</summary>
    /// <param name="program">The scope's types.</param>
    /// <param name="typeKey">The region's type.</param>
    /// <param name="libraryFields">Metadata fields named on this region by reached instances.</param>
    /// <param name="declaringTypeKey">The receiver restriction, or null for the whole region.</param>
    public static IReadOnlyList<IrFieldRef>? FieldsOf(ProgramIndex program, string? typeKey, IEnumerable<IrFieldRef> libraryFields,
                                                      string? declaringTypeKey = null)
    {
        var own = (declaringTypeKey ?? typeKey) is { } key ? program.InstanceFieldsOf(key) : null;
        var fields = (own ?? []).Concat(libraryFields).DistinctBy(FieldSlot.Key).ToArray();
        return fields.Length == 0 && own is null && declaringTypeKey is null ? null : Seen(program, fields, declaringTypeKey);
    }

    /// <summary>Metadata fields a reached summary names directly or through a reference, with the instance's type arguments bound.
    /// Both <c>WholeProgram.Process</c> and <c>UnknownCalls.Reach.LibraryFields</c> resolve these bases to record the region's known fields.</summary>
    /// <param name="program">The scope's types.</param>
    /// <param name="summary">The reached body's summary.</param>
    /// <param name="substitution">The instance's type bindings, including generic base arguments.</param>
    public static IEnumerable<(IrFieldRef Field, IReadOnlySet<AbstractValue> Bases)> LibraryFields(ProgramIndex program, MethodSummary summary,
                                                                                                   IReadOnlyDictionary<string, string> substitution)
    {
        var named = summary.Accesses.Select(access => (access.Field, access.Bases))
                           .Concat(summary.ReferenceAccesses.SelectMany(access => access.Targets).OfType<ReferenceCell>().Select(cell => cell.Leaf()));
        foreach (var (field, bases) in named)
        {
            if (field.IsStatic || field.Kind != IrFieldKind.Field || program.Type(FieldSlot.TypeKey(field.Assembly, field.ContainingTypeId))?.IsSource == true)
                continue;
            yield return (field with
            {
                ContainingTypeIdentity = field.ContainingTypeIdentity is { } identity ? ProgramIndex.Substitute(identity, substitution) : null,
                FieldTypeKey = field.FieldTypeKey is { } type ? ProgramIndex.Substitute(type, substitution) : null
            }, bases);
        }
    }

    /// <summary>Whether an unseen write may write a slot: anything but an unknown effect that is no reflection writes it, and that one
    /// only where the slot is not readonly in the object it touches.</summary>
    /// <param name="kind">The unseen write.</param>
    /// <param name="isOwnReadonly">Whether the slot is readonly storage of the object it is in (<see cref="IsOwnReadonly"/>).</param>
    public static bool MayWrite(UnseenWriteKind kind, bool isOwnReadonly) => !isOwnReadonly || kind != UnseenWriteKind.UnknownEffect;

    /// <summary>Whether a field is readonly storage of an object, which nothing outside the object's construction can assign but
    /// reflection: a readonly field the object's type or a base declares, the object being no value type. Every struct field below it is
    /// readonly with it (R1); a readonly field below a struct field the object can replace whole is not, nor is a cell.</summary>
    /// <param name="program">The program whose types declare the fields.</param>
    /// <param name="objectTypeKey">The type of the object the field is the first field of the slot's path in.</param>
    /// <param name="field">That first field.</param>
    public static bool IsOwnReadonly(ProgramIndex program, string? objectTypeKey, IrFieldRef field) =>
        field.IsReadOnly && objectTypeKey is { } typeKey && program.Type(typeKey) is { IsValueType: false } &&
        program.Supertypes(typeKey).Any(type => FieldSlot.WithoutTypeArguments(type) == FieldSlot.TypeKey(field.Assembly, field.ContainingTypeId));

    /// <summary>The fields of an object an unknown effect starting at it sees: all of them, or, through the receiver of a member that
    /// <paramref name="declaringTypeKey"/> declares, only those that type and its bases declare (R1).</summary>
    /// <param name="program">The program whose types give the bases.</param>
    /// <param name="fields">The object's fields.</param>
    /// <param name="declaringTypeKey">The type declaring the member the object is the receiver of, or null for an object seen whole.</param>
    public static IReadOnlyList<IrFieldRef> Seen(ProgramIndex program, IReadOnlyList<IrFieldRef> fields, string? declaringTypeKey)
    {
        if (declaringTypeKey is null)
            return fields;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var key = declaringTypeKey; key is not null && declared.Add(FieldSlot.WithoutTypeArguments(key)); key = program.Type(key)?.BaseTypeKey)
        {
        }

        return fields.Where(field => declared.Contains(FieldSlot.TypeKey(field.Assembly, field.ContainingTypeId))).ToArray();
    }

    /// <summary>Whether a region an unknown effect reaches at <paramref name="depth"/> is past the depth bound of TD-042, where one wildcard
    /// over it stands for every slot it holds.</summary>
    /// <param name="depth">The number of fields walked to the region.</param>
    /// <param name="limits">The analysis limits.</param>
    public static bool IsPastBound(int depth, AnalysisLimits limits) => depth >= limits.MaxAccessPathDepth;

    /// <summary>The slots at or below <paramref name="slot"/> whose presence a write of that slot may change: the slot itself where it
    /// holds a <c>Nullable</c> of a struct of the run's own, and, where it holds such a struct or such a <c>Nullable</c> — which the engine
    /// holds as the struct it holds, at the same slot —, each slot below it through its struct fields that holds one. Presence is a
    /// <c>Nullable</c>'s alone: a struct that is none always holds a value. A struct is never walked into below itself: a primitive CoreLib
    /// declares in source holds a field of its own type.</summary>
    /// <param name="program">The program whose types declare the structs.</param>
    /// <param name="typeKey">The type the slot holds.</param>
    /// <param name="slot">The slot, a path below its holder.</param>
    public static IEnumerable<string> PresenceSlots(ProgramIndex program, string? typeKey, string slot)
    {
        var slots = new List<string>();
        AddPresenceSlots(program, typeKey, slot, new HashSet<string>(StringComparer.Ordinal), slots);
        return slots;
    }

    /// <summary>The slots filled by an unseen output into a struct: the target whole and every Nullable below it. The heap's
    /// <c>WholeProgram.CopyStruct</c> and the accesses' <c>InterproceduralAccesses.Emit</c> record exactly these slots, independently
    /// of unknown effects and model argument or cell effects.</summary>
    /// <param name="program">The program whose structs declare the slots.</param>
    /// <param name="typeKey">The target struct's type.</param>
    public static IEnumerable<string> OutputSlots(ProgramIndex program, string? typeKey) => ["", .. PresenceSlotsBelow(program, typeKey)];

    /// <summary>The slots below the storage of a struct of the run's own whose presence a write of its whole storage, or a copy into it,
    /// may change (<see cref="PresenceSlots"/> of each of its fields); none for any other type. Each program answers it once per type.</summary>
    /// <param name="program">The program whose types declare the structs.</param>
    /// <param name="typeKey">The struct's type.</param>
    public static string[] PresenceSlotsBelow(ProgramIndex program, string? typeKey) =>
        typeKey is null
            ? []
            : Below.GetValue(program, _ => new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal))
                   .GetOrAdd(typeKey, key => program.Type(key) is { IsValueType: true, IsSource: true }
                                                 ? (program.InstanceFieldsOf(key) ?? []).SelectMany(field => PresenceSlots(program, field.FieldTypeKey, FieldSlot.Key(field)))
                                                                                          .ToArray()
                                                 : []);

    private static readonly ConditionalWeakTable<ProgramIndex, ConcurrentDictionary<string, string[]>> Below = new();

    /// <summary>Adds the slots of <see cref="PresenceSlots"/> below a slot, past no struct already on the way to it.</summary>
    /// <param name="program">The program whose types declare the structs.</param>
    /// <param name="typeKey">The type the slot holds.</param>
    /// <param name="slot">The slot, a path below its holder.</param>
    /// <param name="enclosing">The structs on the way to the slot.</param>
    /// <param name="slots">The slots found.</param>
    private static void AddPresenceSlots(ProgramIndex program, string? typeKey, string slot, HashSet<string> enclosing, List<string> slots)
    {
        if (typeKey is null)
            return;
        // A Nullable is the struct it holds, at the same slot: a write of it may fill it, and each Nullable below the struct as well.
        if (TypeSafety.NullableValueType(typeKey) is { } held)
        {
            if (program.Type(held) is not { IsValueType: true, IsSource: true })
                return;
            slots.Add(slot);
            typeKey = held;
        }

        if (program.Type(typeKey) is not { IsValueType: true, IsSource: true } || !enclosing.Add(typeKey))
            return;
        foreach (var field in program.InstanceFieldsOf(typeKey) ?? [])
            AddPresenceSlots(program, field.FieldTypeKey, FieldSlot.Join(slot, FieldSlot.Key(field)), enclosing, slots);
        enclosing.Remove(typeKey);
    }

    /// <summary>The type of an array's elements, of any rank (<c>S?[]</c>, <c>S?[,]</c>), or null for a type that is no array. A jagged
    /// array's elements are the arrays of its last rank specifier's element type.</summary>
    /// <param name="typeKey">The type.</param>
    public static string? ElementTypeKey(string? typeKey) =>
        typeKey is { Length: > 2 } && typeKey.EndsWith(']') && typeKey.LastIndexOf('[') is var open and > 0 &&
        typeKey.AsSpan(open + 1, typeKey.Length - open - 2).Trim(',').IsEmpty
            ? typeKey[..open]
            : null;

    /// <summary>The objects a model's <c>writes-arg</c> writes the fields of, from the regions its argument may be (R3, R9): a library
    /// sequence or grouping writes what it yields, each yielded object itself, however deep the sequences go; any other region is written
    /// itself, or, handed to a parameter that takes a sequence, the objects it may yield (<see cref="SequenceElements{TRegion}"/>). The heap
    /// marks by it (<c>WholeProgram.MarkModelWrite</c>) and the accesses write by it (<c>InterproceduralAccesses.ArgumentWriteAccesses</c>),
    /// each over its own regions.</summary>
    /// <typeparam name="TRegion">The heap's region id.</typeparam>
    /// <param name="program">The program whose types tell what a sequence of the run's own enumerates.</param>
    /// <param name="regions">The regions the argument may be.</param>
    /// <param name="isSequence">Whether the parameter takes a sequence of the objects it writes.</param>
    /// <param name="view">The heap the regions are of.</param>
    public static IReadOnlyCollection<TRegion> ModelArgumentTargets<TRegion>(ProgramIndex program, IEnumerable<TRegion> regions, bool isSequence,
                                                                             RegionView<TRegion> view)
        where TRegion : notnull
    {
        var targets = new HashSet<TRegion>();
        var sequences = new HashSet<TRegion>();
        Add(regions, isSequence);
        return targets;

        void Add(IEnumerable<TRegion> values, bool sequence)
        {
            foreach (var region in values.ToArray())
            {
                if (view.LibrarySequenceYields(region) is { } yields)
                {
                    if (sequences.Add(region))
                        Add(yields, sequence: false);
                    continue;
                }

                if (sequence)
                    targets.UnionWith(SequenceElements(program, region, view));
                else
                    targets.Add(region);
            }
        }
    }

    /// <summary>The objects a sequence may yield: a collection's or an array's own, and for a sequence type of the run's own that is no
    /// collection, every object it reaches, however deep and through whatever holds it, of a type it enumerates. Its enumerator may hand
    /// out any of them, and the call never runs it to learn which (R3). A sequence that names no element type, or names <c>object</c>,
    /// may yield anything it reaches.</summary>
    /// <typeparam name="TRegion">The heap's region id.</typeparam>
    /// <param name="program">The program whose types tell what a sequence of the run's own enumerates.</param>
    /// <param name="region">The sequence region.</param>
    /// <param name="view">The heap the region is of.</param>
    private static IEnumerable<TRegion> SequenceElements<TRegion>(ProgramIndex program, TRegion region, RegionView<TRegion> view)
        where TRegion : notnull
    {
        if (view.IsCollection(region))
            return view.HeldBy(region);
        if (view.TypeKey(region) is not { } key || program.Type(key) is not { IsSource: true })
            return [];

        var enumerated = program.Supertypes(key).Where(type => TypeName(type) == "System.Collections.Generic.IEnumerable")
                                .Select(type => type[(type.IndexOf('<') + 1)..type.LastIndexOf('>')].Trim())
                                .ToArray();
        var anything = enumerated.Length == 0 || enumerated.Any(element => TypeName(element) == "object" || program.IsOpen(element));
        var yielded = new List<TRegion>();
        var visited = new HashSet<TRegion> { region };
        var pending = new Queue<TRegion>(Reached(region));
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current))
                continue;
            if (view.TypeKey(current) is { } type &&
                (anything || program.Supertypes(type).Any(supertype => enumerated.Contains(supertype, StringComparer.Ordinal))))
                yielded.Add(current);
            foreach (var next in Reached(current))
                pending.Enqueue(next);
        }

        return yielded;

        // What an object holds: the objects in a collection's cells, and what every field of it points to.
        IEnumerable<TRegion> Reached(TRegion holder) =>
            (view.IsCollection(holder) ? view.HeldBy(holder) : [])
                .Concat(view.Slots(holder).Where(slot => !PathValue.IsStorage(slot) || slot == PathValue.KEPT).SelectMany(slot => view.PointsTo(holder, slot)))
                .Where(target => !view.IsDelegate(target));

        static string TypeName(string typeKey) => FieldSlot.WithoutTypeArguments(typeKey[(typeKey.IndexOf(':') + 1)..]);
    }

    /// <summary>The unseen write an unresolved call's unknown effect is, by the call's semantic gap kind.</summary>
    /// <param name="gapKind">The kind (<see cref="Accesses.SemanticGapKinds"/>).</param>
    public static UnseenWriteKind OfGap(string gapKind) =>
        gapKind == Accesses.SemanticGapKinds.REFLECTION ? UnseenWriteKind.Reflection : UnseenWriteKind.UnknownEffect;
}
