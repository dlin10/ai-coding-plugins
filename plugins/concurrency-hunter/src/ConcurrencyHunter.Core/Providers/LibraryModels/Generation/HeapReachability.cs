using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Heap;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>A static field of the solved heap where a path may start: the static region and the slot it holds the field in.</summary>
/// <param name="Region">The static region.</param>
/// <param name="Slot">The field's slot, as <see cref="HeapSolution.FieldsOf"/> names it.</param>
public sealed record PathStart(string Region, string Slot);

/// <summary>The one reachability of the model generator (SPEC TD-034b, G-0), which every rule that follows paths calls: an object
/// reaches what its fields point to (<see cref="HeapSolution.FieldsOf"/>, the element slot <c>[]</c> included), what a delegate
/// captures, the sources, elements, keys and delegates a library sequence keeps, and what an iterator object was created with. It
/// over-approximates on purpose: a path too many can only widen a fate.</summary>
public sealed class HeapReachability
{
    private const string CAPTURE = "<capture>";
    private const string SEQUENCE = "<sequence>";
    private const string ITERATOR = "<iterator>";
    private const string ELEMENT = "[]";

    private readonly HeapSolution _heap;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _held;
    private readonly Dictionary<string, IteratorObject> _iterators;
    private readonly Dictionary<string, (string Field, string Target)[]> _edges = new(StringComparer.Ordinal);

    /// <summary>The reachability of one solved heap.</summary>
    /// <param name="heap">The heap.</param>
    /// <param name="held">By static slot, what the slot holds beyond what the heap says: the value an awaited call completes with,
    /// which the heap does not carry through an <c>await</c>; <c>null</c> for nothing more.</param>
    public HeapReachability(HeapSolution heap, IReadOnlyDictionary<string, IReadOnlySet<string>>? held = null)
    {
        _heap = heap;
        _held = held ?? new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        _iterators = heap.IteratorObjects.GroupBy(iterator => iterator.RegionId, StringComparer.Ordinal)
                         .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    /// <summary>The slot of a static field as the heap names it: <c>&lt;assembly&gt;:&lt;type&gt;.&lt;field&gt;</c>.</summary>
    /// <param name="assembly">The declaring assembly.</param>
    /// <param name="type">The declaring type.</param>
    /// <param name="field">The field.</param>
    public static string Slot(string assembly, string type, string field) => $"{assembly}:{type}.{field}";

    /// <summary>Every field of every static region of the heap that holds something, in region and slot order.</summary>
    public IReadOnlyList<PathStart> StaticFields() =>
        _heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static)
             .OrderBy(region => region.Identity, StringComparer.Ordinal)
             .SelectMany(region => _heap.FieldsOf(region.Identity).Union(_held.Keys.Where(slot => Declares(region, slot)), StringComparer.Ordinal)
                                        .Order(StringComparer.Ordinal)
                                        .Select(slot => new PathStart(region.Identity, slot)))
             .ToArray();

    /// <summary>The static field with a slot, or <c>null</c> when no static region holds anything in it.</summary>
    /// <param name="slot">The slot.</param>
    public PathStart? StaticField(string slot) => StaticFields().FirstOrDefault(start => start.Slot == slot);

    /// <summary>What a path start points to.</summary>
    /// <param name="start">The path start.</param>
    public IReadOnlySet<string> Targets(PathStart start) =>
        _held.TryGetValue(start.Slot, out var held)
            ? _heap.PointsTo(start.Region, start.Slot).Union(held, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal)
            : _heap.PointsTo(start.Region, start.Slot);

    /// <summary>Whether a static region is the one of the type declaring a slot: <c>static:&lt;assembly&gt;:&lt;type&gt;</c> for the
    /// slot <c>&lt;assembly&gt;:&lt;type&gt;.&lt;field&gt;</c>.</summary>
    /// <param name="region">The static region.</param>
    /// <param name="slot">The slot.</param>
    private static bool Declares(HeapRegion region, string slot) =>
        slot.LastIndexOf('.') is var dot and > 0 && region.Identity == $"static:{slot[..dot]}";

    /// <summary>The edges out of an object: each with the field it goes through, or a marker for a capture, a library sequence's
    /// keeping and an iterator's creation.</summary>
    /// <param name="region">The object.</param>
    public IReadOnlyList<(string Field, string Target)> Edges(string region)
    {
        if (_edges.TryGetValue(region, out var cached))
            return cached;
        var edges = new List<(string Field, string Target)>();
        foreach (var field in _heap.FieldsOf(region))
            edges.AddRange(_heap.PointsTo(region, field).Select(target => (field, target)));
        edges.AddRange(_heap.DelegateCaptures(region).Select(target => (CAPTURE, target)));
        if (_heap.LibrarySequences.TryGetValue(region, out var sequence))
        {
            edges.AddRange(sequence.Sources.Concat(sequence.Yields).Concat(sequence.Keys).Select(target => (SEQUENCE, target)));
            if (_heap.Instances.TryGetValue(sequence.CreatorInstance, out var creator) &&
                creator.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == sequence.CreatorOperation) is { } call)
            {
                var values = call.Arguments.SelectMany(argument => argument.Values).Concat(call.Receivers).Concat(call.Delegates);
                edges.AddRange(values.SelectMany(value => _heap.Resolve(creator.Id, value)).Select(target => (SEQUENCE, target)));
            }
        }

        if (_iterators.TryGetValue(region, out var iterator) && _heap.Instances.TryGetValue(iterator.Creation.CalleeInstance, out var callee))
        {
            edges.AddRange(callee.Parameters.Values.SelectMany(values => values).Concat(callee.Receivers)
                                 .Select(target => (ITERATOR, target)));
        }

        var result = edges.Where(edge => _heap.Regions.ContainsKey(edge.Target)).Distinct().ToArray();
        _edges[region] = result;
        return result;
    }

    /// <summary>The objects an access writes into: the object its access path leads to before its last segment, which is the field or
    /// cell written; an element segment (<c>[?]</c>, <c>[k]</c>) follows the element slot <c>[]</c>.</summary>
    /// <param name="access">An access whose resource names its region.</param>
    public IReadOnlySet<string> WrittenObjects(Access access)
    {
        IReadOnlySet<string> objects = access.Resource.RegionId is { } root && _heap.Regions.ContainsKey(root)
            ? new HashSet<string>(StringComparer.Ordinal) { root }
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in access.Resource.AccessPath.SkipLast(1))
        {
            var field = segment.StartsWith('[') ? ELEMENT : segment;
            objects = objects.SelectMany(region => _heap.PointsTo(region, field)).ToHashSet(StringComparer.Ordinal);
        }

        return objects;
    }

    /// <summary>Every object reachable from some objects, those objects included.</summary>
    /// <param name="regions">The objects.</param>
    public IReadOnlySet<string> From(IEnumerable<string> regions)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(regions.Where(_heap.Regions.ContainsKey));
        while (pending.TryPop(out var region))
        {
            if (!reached.Add(region))
                continue;
            foreach (var (_, target) in Edges(region))
                pending.Push(target);
        }

        return reached;
    }

    /// <summary>Whether a path from a start reaches one of the targets without entering <paramref name="avoid"/>. With
    /// <paramref name="counted"/> false the path counts only once it leaves an object <paramref name="written"/> holds: the start is the
    /// value's own field, and only something an execution other than setup wrote after it says the member kept anything.</summary>
    /// <param name="start">The path start.</param>
    /// <param name="targets">The objects sought.</param>
    /// <param name="avoid">An object no counted path may pass, or <c>null</c>.</param>
    /// <param name="counted">Whether the path counts from its start.</param>
    /// <param name="written">The objects an execution other than setup writes.</param>
    public bool Reaches(PathStart start, IReadOnlySet<string> targets, string? avoid, bool counted, IReadOnlySet<string> written)
    {
        var seen = new HashSet<(string Region, bool Counted)>();
        var pending = new Stack<(string Region, bool Counted)>(Targets(start).Where(target => target != avoid).Select(target => (target, counted)));
        while (pending.TryPop(out var state))
        {
            if (!seen.Add(state))
                continue;
            if (state.Counted && targets.Contains(state.Region))
                return true;
            var next = state.Counted || written.Contains(state.Region);
            foreach (var (_, target) in Edges(state.Region))
            {
                if (target != avoid)
                    pending.Push((target, next));
            }
        }

        return false;
    }
}
