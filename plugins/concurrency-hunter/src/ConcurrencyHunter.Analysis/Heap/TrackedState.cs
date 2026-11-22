using System.Collections;
using System.Runtime.CompilerServices;

namespace ConcurrencyHunter.Heap;

internal sealed class StateKey : IEquatable<StateKey>
{
    internal static readonly StateKey Wildcard = new("*");

    /// <summary>The entry under which a container a map entry holds keeps its contents: they change apart from the entry, which the map
    /// writes only when it binds or unbinds the key.</summary>
    internal static readonly object Contents = new Marker("contents");

    /// <summary>The entry a container writes only when it becomes or stops being empty.</summary>
    internal static readonly object Emptiness = new Marker("emptiness");
    internal string State { get; }
    internal StateKey? Parent { get; }
    internal object? Entry { get; }
    internal StateKey All => _all ??= new(this, AllEntries);
    private static readonly object AllEntries = new();
    private StateKey? _all;
    private readonly int _hash;

    internal StateKey(string state)
    {
        State = state;
        _hash = StringComparer.Ordinal.GetHashCode(state);
    }

    private StateKey(StateKey parent, object entry)
    {
        State = parent.State;
        Parent = parent;
        Entry = entry;
        _hash = HashCode.Combine(parent._hash, entry);
    }

    internal StateKey Child(object entry) => new(this, entry);
    internal StateKey Member(string name)
    {
        var path = new Stack<object>();
        for (var key = this; key.Parent is not null; key = key.Parent)
        {
            WorkCancellation.Check();
            path.Push(key.Entry!);
        }
        var member = new StateKey(name);
        foreach (var entry in path)
        {
            WorkCancellation.Check();
            member = member.Child(entry);
        }
        return member;
    }

    public bool Equals(StateKey? other)
    {
        WorkCancellation.Check();
        return ReferenceEquals(this, other) ||
            other is not null && _hash == other._hash && State == other.State && Equals(Entry, other.Entry) && Equals(Parent, other.Parent);
    }
    public override bool Equals(object? obj)
    {
        WorkCancellation.Check();
        return obj is StateKey key && Equals(key);
    }
    public override int GetHashCode() => _hash;
    public override string ToString() => Parent is null ? State : $"{Parent}/{(ReferenceEquals(Entry, AllEntries) ? "all" : Entry)}";

    private sealed class Marker(string name)
    {
        public override string ToString() => name;
    }
}

/// <summary>What tracked state is attached to: it records each read and write of the state, and is told of every container
/// attached, so that the containers can let go of their keys together once the solve is done (<see cref="TrackedContainer.Freeze"/>).
/// A container gets its key in <see cref="TrackedContainer.Attach"/> alone, and there the owner hears of it: a container with a key
/// is one the owner knows.</summary>
internal interface IStateOwner
{
    /// <summary>Records a read of a state key.</summary>
    /// <param name="key">The key read.</param>
    void ReadKey(StateKey key);

    /// <summary>Records a write of a state key.</summary>
    /// <param name="key">The key written.</param>
    void WroteKey(StateKey key);

    /// <summary>Hears of a container that was just attached.</summary>
    /// <param name="container">The container.</param>
    void Attached(TrackedContainer container);
}

internal interface ITrackedState
{
    void Attach(StateKey key, IStateOwner owner);
}

internal abstract class TrackedContainer : ITrackedState
{
    internal StateKey? Key { get; private set; }
    private IStateOwner? _owner;

    // Whether the solve that attached this container is done: a read records nothing, a change throws.
    private bool _frozen;

    public void Attach(StateKey key, IStateOwner owner)
    {
        if (Key is not null)
            return;
        Key = key;
        _owner = owner;
        owner.Attached(this);
        AttachContents(owner);
    }

    /// <summary>Ends the tracking of an attached container once its solve is done: it lets go of its key and of every key it made for
    /// its entries and items, so that a read, from any thread, writes nothing at all, and a change of it afterwards throws, as a
    /// query's write into the solved heap did through the solve's write record. Its owner freezes every container it heard of; one
    /// never attached is left as it is.</summary>
    public void Freeze()
    {
        if (Key is null)
            return;
        Key = null;
        _owner = null;
        _frozen = true;
        FreezeContents();
    }

    protected virtual void AttachContents(IStateOwner owner) { }

    /// <summary>Lets go of the keys a container made for what it holds.</summary>
    protected virtual void FreezeContents() { }
    protected void Read(StateKey? key = null)
    {
        if (Key is not null)
            _owner!.ReadKey(key ?? Key);
    }

    /// <summary>Records a change for the readers of the given key, or of the whole container; after <see cref="Freeze"/> a change
    /// throws instead.</summary>
    /// <param name="key">The key the change is to, or null for the whole container.</param>
    protected void Write(StateKey? key = null)
    {
        if (Key is not null)
            _owner!.WroteKey(key ?? Key);
        else if (_frozen)
            throw new InvalidOperationException("A query of the solved heap changed one of its containers: the heap is frozen after its solve.");
    }
    protected void AttachValue(object? value, StateKey? key)
    {
        WorkCancellation.Check();
        if (key is not null)
            AttachValue(value, key, _owner!);
    }
    internal static void AttachValue(object? value, StateKey key, IStateOwner owner)
    {
        WorkCancellation.Check();
        if (value is ITrackedState tracked)
            tracked.Attach(key, owner);
        else if (value is ITuple tuple)
            for (var index = 0; index < tuple.Length; index++)
            {
                WorkCancellation.Check();
                AttachValue(tuple[index], key.Child(index), owner);
            }
    }
}

/// <summary>A set whose membership test reads the key of its item and whose add reads nothing: while the solve runs a set only grows, so
/// an add run again adds nothing and decides nothing another way. A removal from an attached set may undo an add no reader recorded, so it
/// writes the wildcard.</summary>
/// <remarks>Once a solve is done its sets with the same items in the same order share one copy (<see cref="Share"/>); a set that
/// changes after that first makes the items its own, so no other set sees the change.</remarks>
internal sealed class TrackedSet<T> : TrackedContainer, ISet<T>, IReadOnlySet<T>
{
    private HashSet<T> _items;

    // Whether another set may hold _items too: a change then copies them first (Own).
    private bool _shared;
    private StateKey? _emptiness;

    // Whether a membership test has read the key of an item: only then does an add write the key of the item it adds.
    private bool _itemsRead;
    internal TrackedSet() : this((IEqualityComparer<T>?)null) { }
    internal TrackedSet(IEqualityComparer<T>? comparer) => _items = new(comparer);
    internal TrackedSet(IEnumerable<T> items, IEqualityComparer<T>? comparer = null) => _items = new(Source(items), comparer);
    internal IEqualityComparer<T> Comparer => _items.Comparer;
    private static IEnumerable<T> Source(IEnumerable<T> items)
    {
        if (items is not TrackedSet<T> tracked) return items;
        tracked.Read(tracked.Key?.All);
        return tracked._items;
    }

    /// <summary>Makes the items this set's own before it changes them, when another set may hold them.</summary>
    private void Own()
    {
        if (!_shared)
            return;
        _items = new HashSet<T>(_items, _items.Comparer);
        _shared = false;
    }

    /// <summary>Lets the set hold the one copy of its items, in their order, that every set given the same contents holds.</summary>
    /// <param name="contents">The shared contents.</param>
    internal void Share(SharedContents<T> contents)
    {
        _items = contents.Intern(_items);
        _shared = true;
    }
    public int Count { get { Read(Key?.All); return _items.Count; } }

    /// <summary>Whether the set is empty, read apart from its contents: only its first add changes it.</summary>
    internal bool IsEmpty { get { Read(EmptinessKey); return _items.Count == 0; } }
    private StateKey? EmptinessKey => Key is null ? null : _emptiness ??= Key.Child(StateKey.Emptiness);
    public bool IsReadOnly => false;
    public bool Contains(T item)
    {
        WorkCancellation.Check();
        ReadItem(item);
        return _items.Contains(item);
    }
    private void ReadItem(T item)
    {
        if (Key is null) return;
        _itemsRead = true;
        Read(Key.Child(item!));
    }
    public bool Add(T item)
    {
        WorkCancellation.Check();
        if (_shared)
        {
            if (_items.Contains(item)) return false;
            Own();
        }
        if (!_items.Add(item)) return false;
        if (Key is not null)
        {
            if (_itemsRead) Write(Key.Child(item!));
            Grew(_items.Count - 1);
        }
        else
            Write();
        return true;
    }

    protected override void FreezeContents()
    {
        _emptiness = null;
        _itemsRead = false;
    }
    void ICollection<T>.Add(T item)
    {
        WorkCancellation.Check();
        Add(item);
    }

    // The readers of the contents, and for the first item the readers of whether the set is empty.
    private void Grew(int before)
    {
        Write();
        if (before == 0) Write(EmptinessKey);
    }
    private void Shrank() => Write(StateKey.Wildcard);

    // A removal reads what it may remove: run again after an add, it would remove what that add added.
    public bool Remove(T item)
    {
        WorkCancellation.Check();
        ReadItem(item);
        if (!_items.Contains(item)) return false;
        Own();
        _items.Remove(item);
        Shrank();
        return true;
    }
    public void Clear()
    {
        WorkCancellation.Check();
        Read(Key?.All);
        if (_items.Count == 0) return;
        Own();
        _items.Clear();
        Shrank();
    }
    public int RemoveWhere(Predicate<T> match)
    {
        WorkCancellation.Check();
        Read(Key?.All);
        Own();
        var removed = _items.RemoveWhere(match);
        if (removed != 0) Shrank();
        return removed;
    }
    public void UnionWith(IEnumerable<T> other)
    {
        WorkCancellation.Check();
        var source = Source(other);
        var before = _items.Count;
        // A set shared with this one holds the same items: the union adds nothing.
        if (ReferenceEquals(source, _items))
            return;
        Own();
        if (Key is null || !_itemsRead)
            _items.UnionWith(source);
        else
            foreach (var item in source)
            {
                WorkCancellation.Check();
                if (_items.Add(item))
                    Write(Key.Child(item!));
            }
        if (_items.Count != before)
        {
            if (Key is not null) Grew(before);
            else Write();
        }
    }
    public void ExceptWith(IEnumerable<T> other)
    {
        WorkCancellation.Check();
        Read(Key?.All);
        var before = _items.Count;
        var source = Source(other);
        Own();
        _items.ExceptWith(source);
        if (_items.Count != before) Shrank();
    }
    public void IntersectWith(IEnumerable<T> other)
    {
        WorkCancellation.Check();
        Read(Key?.All);
        var before = _items.Count;
        var source = Source(other);
        Own();
        _items.IntersectWith(source);
        if (_items.Count != before) Shrank();
    }
    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        WorkCancellation.Check();
        Read(Key?.All);
        var before = new HashSet<T>(_items, _items.Comparer);
        var source = Source(other);
        Own();
        _items.SymmetricExceptWith(source);
        if (!before.SetEquals(_items)) Shrank();
    }
    public bool IsSubsetOf(IEnumerable<T> other) { WorkCancellation.Check(); Read(Key?.All); return _items.IsSubsetOf(other); }
    public bool IsSupersetOf(IEnumerable<T> other) { WorkCancellation.Check(); Read(Key?.All); return _items.IsSupersetOf(other); }
    public bool IsProperSupersetOf(IEnumerable<T> other) { WorkCancellation.Check(); Read(Key?.All); return _items.IsProperSupersetOf(other); }
    public bool IsProperSubsetOf(IEnumerable<T> other) { WorkCancellation.Check(); Read(Key?.All); return _items.IsProperSubsetOf(other); }
    public bool Overlaps(IEnumerable<T> other) { WorkCancellation.Check(); Read(Key?.All); return _items.Overlaps(other); }
    public bool SetEquals(IEnumerable<T> other) { WorkCancellation.Check(); Read(Key?.All); return _items.SetEquals(other); }
    public void CopyTo(T[] array, int arrayIndex) { WorkCancellation.Check(); Read(Key?.All); _items.CopyTo(array, arrayIndex); }
    public IEnumerator<T> GetEnumerator() { WorkCancellation.Check(); Read(Key?.All); return _items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator()
    {
        WorkCancellation.Check();
        return GetEnumerator();
    }
}

/// <summary>One copy of each sequence of items sets hold (<see cref="TrackedSet{T}.Share"/>): sets with the same items in the same order
/// and the same comparer keep the first of them, so they enumerate as they did.</summary>
internal sealed class SharedContents<T>
{
    private readonly Dictionary<ulong, List<HashSet<T>>> _byHash = [];

    /// <summary>The copy of the items every set holding them in this order keeps: the first such items given.</summary>
    /// <param name="items">The items.</param>
    internal HashSet<T> Intern(HashSet<T> items)
    {
        var comparer = items.Comparer;
        var hash = (ulong)items.Count;
        foreach (var item in items)
        {
            WorkCancellation.Check();
            hash = (hash ^ (uint)(item is null ? 0 : comparer.GetHashCode(item))) * 0x9E3779B97F4A7C15UL;
            hash ^= hash >> 29;
        }
        if (!_byHash.TryGetValue(hash, out var candidates))
            _byHash.Add(hash, candidates = []);
        foreach (var candidate in candidates)
        {
            WorkCancellation.Check();
            if (candidate.Count == items.Count && ReferenceEquals(candidate.Comparer, comparer) && candidate.SequenceEqual(items, comparer))
                return candidate;
        }
        candidates.Add(items);
        return items;
    }
}

internal sealed class TrackedList<T> : TrackedContainer, IList<T>, IReadOnlyList<T>
{
    private readonly List<T> _items;
    internal TrackedList() => _items = [];
    internal TrackedList(IEnumerable<T> items) => _items = new(items);
    public int Count { get { Read(Key?.All); return _items.Count; } }
    public bool IsReadOnly => false;
    public T this[int index]
    {
        get { Read(Key?.All); return _items[index]; }
        set
        {
            if (EqualityComparer<T>.Default.Equals(_items[index], value)) return;
            AttachValue(value, Key?.Child(index));
            _items[index] = value;
            Write();
        }
    }
    protected override void AttachContents(IStateOwner owner)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            WorkCancellation.Check();
            AttachValue(_items[index], Key!.Child(index), owner);
        }
    }
    public void Add(T item) { WorkCancellation.Check(); AttachValue(item, Key?.Child(_items.Count)); _items.Add(item); Write(); }
    public void AddRange(IEnumerable<T> items) { foreach (var item in items.ToArray()) {
        WorkCancellation.Check();
        Add(item);
    } }
    public void Clear() { WorkCancellation.Check(); if (_items.Count == 0) return; _items.Clear(); Write(); }
    public bool Contains(T item) { WorkCancellation.Check(); Read(Key?.All); return _items.Contains(item); }
    public int IndexOf(T item) { WorkCancellation.Check(); Read(Key?.All); return _items.IndexOf(item); }
    public void Insert(int index, T item) { WorkCancellation.Check(); AttachValue(item, Key?.Child(index)); _items.Insert(index, item); Write(); }
    public bool Remove(T item) { WorkCancellation.Check(); if (!_items.Remove(item)) return false; Write(); return true; }
    public void RemoveAt(int index) { WorkCancellation.Check(); _items.RemoveAt(index); Write(); }
    public void CopyTo(T[] array, int arrayIndex) { WorkCancellation.Check(); Read(Key?.All); _items.CopyTo(array, arrayIndex); }
    public IEnumerator<T> GetEnumerator() { WorkCancellation.Check(); Read(Key?.All); return _items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator()
    {
        WorkCancellation.Check();
        return GetEnumerator();
    }
}

/// <summary>A map whose entry key changes only when the map binds or unbinds the key: a container it holds keeps its contents under an
/// entry of its own (<see cref="StateKey.Contents"/>), so finding the container does not read what it holds.</summary>
internal sealed class TrackedMap<TKey, TValue> : TrackedContainer, IReadOnlyDictionary<TKey, TValue>, IDictionary where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _items;
    private readonly Dictionary<TKey, StateKey> _keys;
    private StateKey? _emptiness;
    internal TrackedMap() : this((IEqualityComparer<TKey>?)null) { }
    internal TrackedMap(IEqualityComparer<TKey>? comparer)
    {
        _items = new(comparer);
        _keys = new(comparer);
    }
    internal TrackedMap(IEnumerable<KeyValuePair<TKey, TValue>> items, IEqualityComparer<TKey>? comparer = null) : this(comparer)
    {
        foreach (var pair in items) {
            WorkCancellation.Check();
            _items.Add(pair.Key, pair.Value);
        }
    }
    private StateKey? Entry(TKey key)
    {
        if (Key is null) return null;
        if (!_keys.TryGetValue(key, out var entry)) _keys.Add(key, entry = Key.Child(key));
        return entry;
    }
    private static StateKey? ValueKey(TValue value, StateKey? entry) =>
        value is TrackedContainer && entry is not null ? entry.Child(StateKey.Contents) : entry;
    private StateKey? EmptinessKey => Key is null ? null : _emptiness ??= Key.Child(StateKey.Emptiness);
    protected override void AttachContents(IStateOwner owner)
    {
        foreach (var pair in _items) {
            WorkCancellation.Check();
            AttachValue(pair.Value, ValueKey(pair.Value, Entry(pair.Key))!, owner);
        }
    }
    protected override void FreezeContents()
    {
        _keys.Clear();
        _keys.TrimExcess();
        _emptiness = null;
    }
    public int Count { get { Read(Key?.All); return _items.Count; } }

    /// <summary>Whether the map is empty, read apart from its entries: only its first binding changes it.</summary>
    internal bool IsEmpty { get { Read(EmptinessKey); return _items.Count == 0; } }
    public IEnumerable<TKey> Keys { get { Read(Key?.All); return _items.Keys; } }
    public IEnumerable<TValue> Values { get { Read(Key?.All); return _items.Values; } }
    public TValue this[TKey key]
    {
        get { Read(Entry(key)); return _items[key]; }
        set
        {
            var entry = Entry(key);
            var bound = _items.TryGetValue(key, out var previous);
            if (bound && EqualityComparer<TValue>.Default.Equals(previous, value)) return;
            AttachValue(value, ValueKey(value, entry));
            _items[key] = value;
            Bound(entry, !bound);
        }
    }
    public bool ContainsKey(TKey key) { WorkCancellation.Check(); Read(Entry(key)); return _items.ContainsKey(key); }
    public bool ContainsValue(TValue value) { WorkCancellation.Check(); Read(Key?.All); return _items.ContainsValue(value); }
    public bool TryGetValue(TKey key, out TValue value) { WorkCancellation.Check(); Read(Entry(key)); return _items.TryGetValue(key, out value!); }
    public void Add(TKey key, TValue value)
    {
        WorkCancellation.Check();
        var entry = Entry(key);
        Read(entry);
        AttachValue(value, ValueKey(value, entry));
        _items.Add(key, value);
        Bound(entry, true);
    }
    public bool TryAdd(TKey key, TValue value)
    {
        var entry = Entry(key);
        Read(entry);
        if (_items.ContainsKey(key)) return false;
        AttachValue(value, ValueKey(value, entry));
        _items.Add(key, value);
        Bound(entry, true);
        return true;
    }

    // The readers of the entry, and, when the map gains its first entry, the readers of whether it is empty.
    private void Bound(StateKey? entry, bool added)
    {
        Write(entry);
        if (added && _items.Count == 1) Write(EmptinessKey);
    }
    public bool Remove(TKey key)
    {
        WorkCancellation.Check();
        var entry = Entry(key);
        Read(entry);
        if (!_items.Remove(key)) return false;
        Write(entry);
        if (_items.Count == 0) Write(EmptinessKey);
        return true;
    }
    public void Clear()
    {
        foreach (var key in _items.Keys.ToArray()) {
            WorkCancellation.Check();
            Remove(key);
        }
    }
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() { WorkCancellation.Check(); Read(Key?.All); return _items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator()
    {
        WorkCancellation.Check();
        return GetEnumerator();
    }
    bool IDictionary.IsFixedSize => false;
    bool IDictionary.IsReadOnly => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => ((ICollection)_items).SyncRoot;
    ICollection IDictionary.Keys => Keys.ToArray();
    ICollection IDictionary.Values => Values.ToArray();
    object? IDictionary.this[object key] { get => this[(TKey)key]; set => this[(TKey)key] = (TValue)value!; }
    void IDictionary.Add(object key, object? value)
    {
        WorkCancellation.Check();
        Add((TKey)key, (TValue)value!);
    }
    bool IDictionary.Contains(object key) => key is TKey typed && ContainsKey(typed);
    void IDictionary.Remove(object key) { WorkCancellation.Check(); if (key is TKey typed) Remove(typed); }
    IDictionaryEnumerator IDictionary.GetEnumerator() { WorkCancellation.Check(); Read(Key?.All); return ((IDictionary)_items).GetEnumerator(); }
    void ICollection.CopyTo(Array array, int index) { WorkCancellation.Check(); Read(Key?.All); ((ICollection)_items).CopyTo(array, index); }
}

internal sealed class TrackedValue<T>(T initial = default!) : TrackedContainer
{
    private T _value = initial;
    internal T Value
    {
        get { Read(); return _value; }
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            Write();
        }
    }
}

internal static class TrackedCollections
{
    internal static TrackedSet<T> ToTrackedSet<T>(this IEnumerable<T> items, IEqualityComparer<T>? comparer = null) => new(items, comparer);
    internal static TrackedList<T> ToTrackedList<T>(this IEnumerable<T> items) => new(items);
    internal static TrackedMap<TKey, TValue> ToTrackedMap<T, TKey, TValue>(this IEnumerable<T> items, Func<T, TKey> key,
                                                                        Func<T, TValue> value, IEqualityComparer<TKey>? comparer = null) where TKey : notnull =>
        new(items.Select(item => new KeyValuePair<TKey, TValue>(key(item), value(item))), comparer);
    internal static TrackedMap<TKey, T> ToTrackedMap<T, TKey>(this IEnumerable<T> items, Func<T, TKey> key,
                                                            IEqualityComparer<TKey>? comparer = null) where TKey : notnull =>
        new(items.Select(item => new KeyValuePair<TKey, T>(key(item), item)), comparer);
}
