using System.Collections;
using System.Runtime.CompilerServices;

namespace ConcurrencyHunter.Heap;

internal sealed class StateKey : IEquatable<StateKey>
{
    internal static readonly StateKey Wildcard = new("*");
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
            path.Push(key.Entry!);
        var member = new StateKey(name);
        foreach (var entry in path)
            member = member.Child(entry);
        return member;
    }

    public bool Equals(StateKey? other) => ReferenceEquals(this, other) ||
        other is not null && _hash == other._hash && State == other.State && Equals(Entry, other.Entry) && Equals(Parent, other.Parent);
    public override bool Equals(object? obj) => obj is StateKey key && Equals(key);
    public override int GetHashCode() => _hash;
    public override string ToString() => Parent is null ? State : $"{Parent}/{(ReferenceEquals(Entry, AllEntries) ? "all" : Entry)}";
}

internal interface ITrackedState
{
    void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write);
}

internal abstract class TrackedContainer : ITrackedState
{
    internal StateKey? Key { get; private set; }
    private Action<StateKey>? _read;
    private Action<StateKey>? _write;

    public void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write)
    {
        if (Key is not null)
            return;
        Key = key;
        _read = read;
        _write = write;
        AttachContents(read, write);
    }

    protected virtual void AttachContents(Action<StateKey> read, Action<StateKey> write) { }
    protected void Read(StateKey? key = null)
    {
        if (Key is not null)
            _read!(key ?? Key);
    }
    protected void Write(StateKey? key = null)
    {
        if (Key is not null)
            _write!(key ?? Key);
    }
    protected void AttachValue(object? value, StateKey? key)
    {
        if (key is not null)
            AttachValue(value, key, _read!, _write!);
    }
    internal static void AttachValue(object? value, StateKey key, Action<StateKey> read, Action<StateKey> write)
    {
        if (value is ITrackedState tracked)
            tracked.Attach(key, read, write);
        else if (value is ITuple tuple)
            for (var index = 0; index < tuple.Length; index++)
                AttachValue(tuple[index], key.Child(index), read, write);
    }
}

internal sealed class TrackedSet<T> : TrackedContainer, ISet<T>, IReadOnlySet<T>
{
    private readonly HashSet<T> _items;
    internal TrackedSet() : this((IEqualityComparer<T>?)null) { }
    internal TrackedSet(IEqualityComparer<T>? comparer) => _items = new(comparer);
    internal TrackedSet(IEnumerable<T> items, IEqualityComparer<T>? comparer = null) => _items = new(Source(items), comparer);
    internal IEqualityComparer<T> Comparer => _items.Comparer;
    private static IEnumerable<T> Source(IEnumerable<T> items)
    {
        if (items is not TrackedSet<T> tracked) return items;
        tracked.Read();
        return tracked._items;
    }
    public int Count { get { Read(Key?.All); return _items.Count; } }
    public bool IsReadOnly => false;
    public bool Contains(T item) { Read(); return _items.Contains(item); }
    public bool Add(T item)
    {
        Read();
        if (!_items.Add(item)) return false;
        Write();
        return true;
    }
    void ICollection<T>.Add(T item) => Add(item);
    public bool Remove(T item)
    {
        Read();
        if (!_items.Remove(item)) return false;
        Write();
        return true;
    }
    public void Clear() { if (_items.Count == 0) return; _items.Clear(); Write(); }
    public int RemoveWhere(Predicate<T> match)
    {
        Read();
        var removed = _items.RemoveWhere(match);
        if (removed != 0) Write();
        return removed;
    }
    public void UnionWith(IEnumerable<T> other)
    {
        Read();
        var before = _items.Count;
        _items.UnionWith(Source(other));
        if (_items.Count != before) Write();
    }
    public void ExceptWith(IEnumerable<T> other)
    {
        Read();
        var before = _items.Count;
        _items.ExceptWith(Source(other));
        if (_items.Count != before) Write();
    }
    public void IntersectWith(IEnumerable<T> other)
    {
        Read();
        var before = _items.Count;
        _items.IntersectWith(Source(other));
        if (_items.Count != before) Write();
    }
    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        Read();
        var before = new HashSet<T>(_items, _items.Comparer);
        _items.SymmetricExceptWith(Source(other));
        if (!before.SetEquals(_items)) Write();
    }
    public bool IsSubsetOf(IEnumerable<T> other) { Read(); return _items.IsSubsetOf(other); }
    public bool IsSupersetOf(IEnumerable<T> other) { Read(); return _items.IsSupersetOf(other); }
    public bool IsProperSupersetOf(IEnumerable<T> other) { Read(); return _items.IsProperSupersetOf(other); }
    public bool IsProperSubsetOf(IEnumerable<T> other) { Read(); return _items.IsProperSubsetOf(other); }
    public bool Overlaps(IEnumerable<T> other) { Read(); return _items.Overlaps(other); }
    public bool SetEquals(IEnumerable<T> other) { Read(); return _items.SetEquals(other); }
    public void CopyTo(T[] array, int arrayIndex) { Read(); _items.CopyTo(array, arrayIndex); }
    public IEnumerator<T> GetEnumerator() { Read(); return _items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
    protected override void AttachContents(Action<StateKey> read, Action<StateKey> write)
    {
        for (var index = 0; index < _items.Count; index++)
            AttachValue(_items[index], Key!.Child(index), read, write);
    }
    public void Add(T item) { AttachValue(item, Key?.Child(_items.Count)); _items.Add(item); Write(); }
    public void AddRange(IEnumerable<T> items) { foreach (var item in items.ToArray()) Add(item); }
    public void Clear() { if (_items.Count == 0) return; _items.Clear(); Write(); }
    public bool Contains(T item) { Read(Key?.All); return _items.Contains(item); }
    public int IndexOf(T item) { Read(Key?.All); return _items.IndexOf(item); }
    public void Insert(int index, T item) { AttachValue(item, Key?.Child(index)); _items.Insert(index, item); Write(); }
    public bool Remove(T item) { if (!_items.Remove(item)) return false; Write(); return true; }
    public void RemoveAt(int index) { _items.RemoveAt(index); Write(); }
    public void CopyTo(T[] array, int arrayIndex) { Read(Key?.All); _items.CopyTo(array, arrayIndex); }
    public IEnumerator<T> GetEnumerator() { Read(Key?.All); return _items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class TrackedMap<TKey, TValue> : TrackedContainer, IReadOnlyDictionary<TKey, TValue>, IDictionary where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _items;
    private readonly Dictionary<TKey, StateKey> _keys;
    internal TrackedMap() : this((IEqualityComparer<TKey>?)null) { }
    internal TrackedMap(IEqualityComparer<TKey>? comparer)
    {
        _items = new(comparer);
        _keys = new(comparer);
    }
    internal TrackedMap(IEnumerable<KeyValuePair<TKey, TValue>> items, IEqualityComparer<TKey>? comparer = null) : this(comparer)
    {
        foreach (var pair in items) _items.Add(pair.Key, pair.Value);
    }
    private StateKey? Entry(TKey key)
    {
        if (Key is null) return null;
        if (!_keys.TryGetValue(key, out var entry)) _keys.Add(key, entry = Key.Child(key));
        return entry;
    }
    protected override void AttachContents(Action<StateKey> read, Action<StateKey> write)
    {
        foreach (var pair in _items) AttachValue(pair.Value, Entry(pair.Key)!, read, write);
    }
    public int Count { get { Read(Key?.All); return _items.Count; } }
    public IEnumerable<TKey> Keys { get { Read(Key?.All); return _items.Keys; } }
    public IEnumerable<TValue> Values { get { Read(Key?.All); return _items.Values; } }
    public TValue this[TKey key]
    {
        get { Read(Entry(key)); return _items[key]; }
        set
        {
            var entry = Entry(key);
            if (_items.TryGetValue(key, out var previous) && EqualityComparer<TValue>.Default.Equals(previous, value)) return;
            AttachValue(value, entry);
            _items[key] = value;
            Write(entry);
        }
    }
    public bool ContainsKey(TKey key) { Read(Entry(key)); return _items.ContainsKey(key); }
    public bool ContainsValue(TValue value) { Read(Key?.All); return _items.ContainsValue(value); }
    public bool TryGetValue(TKey key, out TValue value) { Read(Entry(key)); return _items.TryGetValue(key, out value!); }
    public void Add(TKey key, TValue value)
    {
        var entry = Entry(key);
        Read(entry);
        AttachValue(value, entry);
        _items.Add(key, value);
        Write(entry);
    }
    public bool TryAdd(TKey key, TValue value)
    {
        var entry = Entry(key);
        Read(entry);
        if (_items.ContainsKey(key)) return false;
        AttachValue(value, entry);
        _items.Add(key, value);
        Write(entry);
        return true;
    }
    public bool Remove(TKey key)
    {
        var entry = Entry(key);
        Read(entry);
        if (!_items.Remove(key)) return false;
        Write(entry);
        return true;
    }
    public void Clear()
    {
        foreach (var key in _items.Keys.ToArray()) Remove(key);
    }
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() { Read(Key?.All); return _items.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    bool IDictionary.IsFixedSize => false;
    bool IDictionary.IsReadOnly => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => ((ICollection)_items).SyncRoot;
    ICollection IDictionary.Keys => Keys.ToArray();
    ICollection IDictionary.Values => Values.ToArray();
    object? IDictionary.this[object key] { get => this[(TKey)key]; set => this[(TKey)key] = (TValue)value!; }
    void IDictionary.Add(object key, object? value) => Add((TKey)key, (TValue)value!);
    bool IDictionary.Contains(object key) => key is TKey typed && ContainsKey(typed);
    void IDictionary.Remove(object key) { if (key is TKey typed) Remove(typed); }
    IDictionaryEnumerator IDictionary.GetEnumerator() { Read(Key?.All); return ((IDictionary)_items).GetEnumerator(); }
    void ICollection.CopyTo(Array array, int index) { Read(Key?.All); ((ICollection)_items).CopyTo(array, index); }
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
