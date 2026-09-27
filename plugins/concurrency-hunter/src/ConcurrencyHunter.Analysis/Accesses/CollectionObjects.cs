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
    public static string? KindOf(HeapRegion region, ProgramIndex program, SummaryCache summaries, string? interfaceMethod = null)
    {
        if (interfaceMethod is not null && region.TypeKey is { } typeKey && program.Implementation(typeKey, interfaceMethod) is { HasSourceBody: true })
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
    public static IrImplementation? Decision(IReadOnlyList<IrImplementation> implementations, string? kind) =>
        kind is null ? null : implementations.FirstOrDefault(implementation => implementation.Kind == kind);

    /// <summary>Whether a region was made by a view of a <c>ConcurrentDictionary</c>, called directly or through an interface.</summary>
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
