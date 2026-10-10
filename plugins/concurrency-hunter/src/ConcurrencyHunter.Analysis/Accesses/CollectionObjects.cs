using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>
/// What an object is to a call of an interface member (ADR 0010, amendment of the phase 5b third run): a collection of the table, by
/// its type's generic definition, since type arguments play no part in what a member does; a live view of a <c>Dictionary</c>; the
/// snapshot a <c>ConcurrentDictionary</c> view hands out, which the heap types a list but which decides only its count and its
/// enumeration; or an array, by whether its rank is one. Any other object is no kind, and the call stays undecided on it.
/// </summary>
/// <remarks>This reads a region's type key in both of its formats, the assembly-qualified key of an allocation and the display
/// string of a synthesized view or snapshot, and follows source bases; it is its own classification, so every other reading of a
/// region's type, <c>CollectionKindOf</c> among them, stays as it is.</remarks>
public static class CollectionObjects
{
    public const string LIST = "System.Collections.Generic.List";
    public const string SNAPSHOT = "snapshot";
    public const string ARRAY = "[]";
    public const string MULTIDIMENSIONAL_ARRAY = "[,]";

    /// <summary>The table's types and the two views of a <c>Dictionary</c>, by their names without type arguments.</summary>
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        LIST, "System.Collections.Generic.Dictionary", "System.Collections.Concurrent.ConcurrentDictionary",
        "System.Collections.Concurrent.ConcurrentQueue", "System.Collections.Concurrent.ConcurrentStack",
        "System.Collections.Concurrent.ConcurrentBag", "System.Collections.Generic.HashSet", "System.Collections.Generic.Queue",
        "System.Collections.Generic.Stack", "System.Collections.Generic.LinkedList", "System.Collections.Generic.Dictionary.KeyCollection",
        "System.Collections.Generic.Dictionary.ValueCollection"
    };

    /// <summary>The kind of the objects of one type, its bases aside: an array by its rank, a type of the table by its name without type
    /// arguments; null for every other type.</summary>
    /// <param name="typeKey">The type key to classify, in either format; null gives null.</param>
    public static string? KindOfType(string? typeKey)
    {
        if (typeKey is null)
            return null;
        if (typeKey.EndsWith(']'))
            return typeKey[typeKey.LastIndexOf('[')..].Contains(',', StringComparison.Ordinal) ? MULTIDIMENSIONAL_ARRAY : ARRAY;

        var name = FieldSlot.WithoutTypeArguments(typeKey[(typeKey.IndexOf(':') + 1)..]);
        return Kinds.Contains(name) ? name : null;
    }

    /// <summary>The kind of a region: that of its type or of the nearest base that has one, a type of the run's own deriving from a
    /// collection being that collection; a list is a snapshot where the call its region was made at is a view of a
    /// <c>ConcurrentDictionary</c>. For a call of <paramref name="interfaceMethod"/>, an object whose type of the run's own implements
    /// that member is no kind: it runs its own body, which is what dispatch does with it.</summary>
    /// <param name="region">The region to classify.</param>
    /// <param name="program">The program index, giving implementations and base types.</param>
    /// <param name="summaries">The method summaries, read to tell whether the region was made by a snapshot view.</param>
    /// <param name="interfaceMethod">The interface member being called, or null when no call is in question.</param>
    public static string? KindOf(HeapRegion region, ProgramIndex program, SummaryCache summaries, string? interfaceMethod = null)
    {
        if (interfaceMethod is not null && region.TypeKey is { } typeKey && program.Implementation(typeKey, interfaceMethod) is { } implementation &&
            summaries.HasBody(implementation))
            return null;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var key = region.TypeKey; key is not null && visited.Add(key); key = program.Type(key)?.BaseTypeKey)
        {
            if (KindOfType(key) is { } kind)
                return kind == LIST && TakesSnapshot(region, summaries) ? SNAPSHOT : kind;
        }

        return null;
    }

    /// <summary>What a call is on an object of <paramref name="kind"/>; null where it decides nothing for it.</summary>
    /// <param name="implementations">The scoped implementations carried by the call.</param>
    /// <param name="kind">The receiver object's kind.</param>
    public static IrImplementation? Decision(IReadOnlyList<IrImplementation> implementations, string? kind) =>
        kind is null ? null : implementations.FirstOrDefault(implementation => implementation.Kind == kind &&
            (implementation.Library is null or { InRange: true, DeclaredOpaque: false }));

    /// <summary>Projects an interface call onto the scoped direct library member on each receiver kind that has one.</summary>
    /// <param name="call">The original interface call.</param>
    /// <param name="resolve">Resolves an abstract receiver to heap regions.</param>
    /// <param name="kindOf">Classifies a receiver, excluding source implementations.</param>
    public static IEnumerable<SummaryOpaqueCall> LibraryCalls(SummaryOpaqueCall call, Func<AbstractValue, IReadOnlySet<string>> resolve,
                                                               Func<string, string?> kindOf)
    {
        // Resolving a receiver creates its allocation region, so a call no library member can stand for resolves nothing.
        if (!call.Implementations.Any(implementation => implementation.Library is not null))
            yield break;
        foreach (var group in call.Receivers.SelectMany(resolve).Distinct(StringComparer.Ordinal)
            .Select(region => (Region: region, Implementation: call.Implementations.FirstOrDefault(implementation => implementation.Kind == kindOf(region))))
            .Where(item => item.Implementation?.Library is not null).GroupBy(item => item.Implementation!))
        {
            var receivers = group.Select(item => (AbstractValue)new RegionValue(item.Region)).ToHashSet();
            yield return call with
            {
                Callee = group.Key.Member.Member,
                Receivers = group.Key.ReceiverParameterOrdinal is null ? receivers : new HashSet<AbstractValue>(),
                Arguments = group.Key.ReceiverParameterOrdinal is int ordinal ? [new CallArgument(ordinal, receivers)] : call.Arguments,
                Library = group.Key.Library,
                DeclaringTypeKey = group.Key.DeclaringTypeKey,
                Implementations = []
            };
        }
    }

    /// <summary>Projects a dispatched interface call onto direct array library members.</summary>
    /// <param name="call">The original dispatched call.</param>
    /// <param name="resolve">Resolves an abstract receiver to heap regions.</param>
    /// <param name="kindOf">Classifies a receiver, excluding source implementations.</param>
    public static IEnumerable<SummaryOpaqueCall> LibraryCalls(CallTransfer call, Func<AbstractValue, IReadOnlySet<string>> resolve,
                                                               Func<string, string?> kindOf) =>
        LibraryCalls(new SummaryOpaqueCall(call.OperationId, call.Callee ?? call.Target, [])
        {
            Receivers = call.Receivers,
            Arguments = call.Arguments,
            Implementations = call.Implementations,
            Provenance = call.Provenance,
            Conditions = call.Conditions,
            HeldLocks = call.HeldLocks
        }, resolve, kindOf);

    /// <summary>The effects and enumerations of a known library call, using its direct parameter bindings and enumeration moments.</summary>
    /// <param name="call">The direct or projected library call.</param>
    /// <param name="heldLocks">The locks held at its call site.</param>
    /// <param name="bind">Optional bindings retaining the direct call's collection and slice information.</param>
    public static IEnumerable<SummaryArgumentEffect> LibraryEffects(SummaryOpaqueCall call, IReadOnlyList<HeldLockValue> heldLocks,
                                                                    Func<IrLibraryEffectKind, int, IEnumerable<SummaryArgumentEffect>>? bind = null)
    {
        if (!call.IsKnown || call.Provenance is null)
            yield break;
        IReadOnlySet<AbstractValue> Values(int ordinal) => ordinal == IrLibraryCall.RECEIVER ? call.Receivers :
            call.Arguments.Where(argument => argument.ParameterOrdinal == ordinal).SelectMany(argument => argument.Values).ToHashSet();
        IEnumerable<SummaryArgumentEffect> Bind(IrLibraryEffectKind kind, int ordinal) => bind is not null ? bind(kind, ordinal) :
            [new SummaryArgumentEffect(kind, call.OperationId, Values(ordinal), null, false, call.Provenance, heldLocks)
            {
                IsSequence = kind is IrLibraryEffectKind.DeepRead or IrLibraryEffectKind.WriteArgument
            }];
        var library = call.Library!;
        var deferred = library.Result?.Leaf.Kind == IrResultKind.Sequence;
        foreach (var effect in library.Effects)
        foreach (var bound in Bind(effect.Kind, effect.ParameterOrdinal))
            yield return bound with { IsDeferred = deferred, Conditions = call.Conditions };
        foreach (var moment in new[] { false, true })
        {
            foreach (var kept in library.EnumeratedKeepers(moment))
                yield return new SummaryArgumentEffect(IrLibraryEffectKind.Enumerate, call.OperationId,
                    new HashSet<AbstractValue> { new PathValue(new LibraryKeeperValue(call.OperationId, kept.KeeperOrdinal, Read: true), [PathValue.KEPT]) },
                    null, false, call.Provenance, heldLocks) { IsDeferred = moment, Conditions = call.Conditions };
            foreach (var ordinal in library.EnumeratedArguments(moment))
            {
                if (deferred == moment && library.Effects.Any(effect => effect.ParameterOrdinal == ordinal && effect.Kind == IrLibraryEffectKind.DeepRead))
                    continue;
                foreach (var bound in Bind(IrLibraryEffectKind.Enumerate, ordinal))
                    yield return bound with { IsDeferred = moment, Conditions = call.Conditions };
            }
            // What the tasks a completion names complete with is enumerated as that value handed over directly would be (R2): the tasks
            // are reached from the call's own values through any elements(…) and completion(…), and a value holding them is enumerated as
            // elements(…) of it is. What a delegate gives back, the heap enumerates (WholeProgram.EnumeratedReturned).
            foreach (var completion in library.EnumeratedCompletions(moment).Where(IrLibraryCall.ReachesCallValues))
            {
                foreach (var holder in IrLibraryCall.CompletionHolders(completion))
                {
                    if (holder is IrModelArgument or IrModelThis)
                    {
                        foreach (var bound in Bind(IrLibraryEffectKind.Enumerate, holder is IrModelArgument argument ? argument.ParameterOrdinal : IrLibraryCall.RECEIVER))
                            yield return bound with { IsDeferred = moment, Conditions = call.Conditions };
                    }
                    else if (Reached(holder) is { Count: > 0 } holders)
                        yield return Enumeration(holders, moment);
                }

                if (Reached(completion) is { Count: > 0 } completed)
                    yield return Enumeration(completed, moment);
            }
        }

        // The values a model value reaches from the call's own values, as paths through what it names the elements and completion of.
        IReadOnlySet<AbstractValue> Reached(IrModelValue value) => value switch
        {
            IrModelArgument argument => Values(argument.ParameterOrdinal),
            IrModelThis => Values(IrLibraryCall.RECEIVER),
            IrModelKept kept => new HashSet<AbstractValue> { new PathValue(new LibraryKeeperValue(call.OperationId, kept.KeeperOrdinal, Read: true), [PathValue.KEPT]) },
            IrModelElements elements => Through(Reached(elements.Source), PathValue.ELEMENT),
            IrModelCompletion inner => Through(Reached(inner.Source), PathValue.COMPLETION),
            _ => new HashSet<AbstractValue>()
        };

        SummaryArgumentEffect Enumeration(IReadOnlySet<AbstractValue> values, bool moment) =>
            new(IrLibraryEffectKind.Enumerate, call.OperationId, values, null, false, call.Provenance!, heldLocks) { IsDeferred = moment, Conditions = call.Conditions };

        static IReadOnlySet<AbstractValue> Through(IReadOnlySet<AbstractValue> values, string segment) =>
            values.Select(value => (AbstractValue)(value is PathValue path ? path with { Segments = [.. path.Segments, segment] } : new PathValue(value, [segment])))
                  .ToHashSet();
    }

    /// <summary>Whether a region was made by a view of a <c>ConcurrentDictionary</c>, called directly or through an interface.</summary>
    /// <param name="region">The region whose allocation site is checked.</param>
    /// <param name="summaries">The method summaries holding the call at that site.</param>
    private static bool TakesSnapshot(HeapRegion region, SummaryCache summaries)
    {
        if (region is not { SiteBodyId: { } body, SiteOperationId: int operation } || summaries.Get(body) is not { } summary)
            return false;

        if (summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == operation) is { } opaque)
            return IsSnapshotView(opaque.Collection) || opaque.Implementations.Any(implementation => IsSnapshotView(implementation.Member));
        return summary.Calls.FirstOrDefault(call => call.OperationId == operation) is { } dispatched &&
               dispatched.Implementations.Any(implementation => IsSnapshotView(implementation.Member));
    }

    private static bool IsSnapshotView(IrCollectionCall? member) => member is { View: not null, IsAtomic: true };
}
