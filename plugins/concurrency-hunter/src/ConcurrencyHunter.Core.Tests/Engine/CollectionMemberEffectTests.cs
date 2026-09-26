using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Ir.IrCollectionEffect;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>What every member of the table of ADR 0010 does to the two resources of its collection, written from the ADR and not
/// read from the recognizer: the structure, the cells, whether it names its cell by a key argument, and whether a thread-safe
/// collection performs it atomically (R7, R9a). The list of rows is the table as the ADR states it; a member the recognizer models
/// and the list leaves out, or the reverse, fails <see cref="Effect_rows_are_exactly_the_members_the_table_models"/>.</summary>
public sealed class CollectionMemberEffectTests
{
    private const string LIST = "System.Collections.Generic.List`1";
    private const string DICTIONARY = "System.Collections.Generic.Dictionary`2";
    private const string CONCURRENT_DICTIONARY = "System.Collections.Concurrent.ConcurrentDictionary`2";
    private const string CONCURRENT_QUEUE = "System.Collections.Concurrent.ConcurrentQueue`1";
    private const string CONCURRENT_STACK = "System.Collections.Concurrent.ConcurrentStack`1";
    private const string CONCURRENT_BAG = "System.Collections.Concurrent.ConcurrentBag`1";
    private const string HASH_SET = "System.Collections.Generic.HashSet`1";
    private const string QUEUE = "System.Collections.Generic.Queue`1";
    private const string STACK = "System.Collections.Generic.Stack`1";
    private const string LINKED_LIST = "System.Collections.Generic.LinkedList`1";
    private const string LINKED_LIST_NODE = "System.Collections.Generic.LinkedListNode`1";
    private const string KEY_COLLECTION = "System.Collections.Generic.Dictionary`2+KeyCollection";
    private const string VALUE_COLLECTION = "System.Collections.Generic.Dictionary`2+ValueCollection";
    private const string KEY_VALUE_PAIR = "System.Collections.Generic.KeyValuePair`2";

    // The interfaces a snapshot of a ConcurrentDictionary view is counted and enumerated through, as a list is.
    private const string SNAPSHOT_COLLECTION = "System.Collections.Generic.ICollection`1";
    private const string SNAPSHOT_READ_ONLY = "System.Collections.Generic.IReadOnlyCollection`1";
    private const string SNAPSHOT_ENUMERABLE = "System.Collections.Generic.IEnumerable`1";
    private const string SNAPSHOT_UNTYPED_COLLECTION = "System.Collections.ICollection";
    private const string SNAPSHOT_UNTYPED_ENUMERABLE = "System.Collections.IEnumerable";

    private static readonly string[] TableTypes =
    [
        LIST, DICTIONARY, CONCURRENT_DICTIONARY, CONCURRENT_QUEUE, CONCURRENT_STACK, CONCURRENT_BAG, HASH_SET, QUEUE, STACK, LINKED_LIST,
        LINKED_LIST_NODE, KEY_COLLECTION, VALUE_COLLECTION, KEY_VALUE_PAIR
    ];

    private static readonly string[] SnapshotTypes =
        [SNAPSHOT_COLLECTION, SNAPSHOT_READ_ONLY, SNAPSHOT_ENUMERABLE, SNAPSHOT_UNTYPED_COLLECTION, SNAPSHOT_UNTYPED_ENUMERABLE];

    /// <summary>One row per member: its structure effect, its cell effect, whether a key argument names its cell, and whether it is
    /// atomic. An insertion writes the structure and the cell it lands in; a removal and <c>Clear</c> write both; a lookup, an
    /// indexer get, <c>TryGetValue</c> and <c>Peek</c> read both; <c>Count</c> and <c>ContainsKey</c> read the structure alone;
    /// <c>Contains</c>, <c>Find</c> and enumeration read the structure and every cell; a member that shifts its neighbours writes
    /// every cell; <c>GetOrAdd</c>, <c>AddOrUpdate</c> and <c>TryUpdate</c> read and write their cell; a node is a cell of its list,
    /// moving to a neighbour reads the structure and its value is the cell alone. Only the four concurrent collections are atomic.
    /// Since the phase 5b second run: <c>AddRange</c> writes the structure and a cell nobody names; a constructor touches nothing of
    /// the collection it makes; taking a view of a <c>Dictionary</c> touches nothing, enumerating one reads the structure and every
    /// cell and counting it the structure; a view of a <c>ConcurrentDictionary</c> is an atomic read of both; a pair touches nothing.</summary>
    private static readonly (string Type, string Member, IrCollectionEffect Structure, IrCollectionEffect Element, bool NamesCell, bool Atomic)[] Rows =
    [
        (LIST, "get_Count", Read, None, false, false),
        (LIST, "get_Item", Read, Read, true, false),
        (LIST, "set_Item", Write, Write, true, false),
        (LIST, "Add", Write, Write, false, false),
        (LIST, "Insert", Write, Write, false, false),
        (LIST, "Remove", Write, Write, false, false),
        (LIST, "RemoveAt", Write, Write, false, false),
        (LIST, "Clear", Write, Write, false, false),
        (LIST, "Contains", Read, Read, false, false),
        (LIST, "GetEnumerator", Read, Read, false, false),
        (LIST, "AddRange", Write, Write, false, false),
        (LIST, ".ctor", None, None, false, false),

        (DICTIONARY, "get_Count", Read, None, false, false),
        (DICTIONARY, "get_Item", Read, Read, true, false),
        (DICTIONARY, "set_Item", Write, Write, true, false),
        (DICTIONARY, "Add", Write, Write, true, false),
        (DICTIONARY, "TryAdd", Write, Write, true, false),
        (DICTIONARY, "Remove", Write, Write, true, false),
        (DICTIONARY, "TryGetValue", Read, Read, true, false),
        (DICTIONARY, "ContainsKey", Read, None, true, false),
        (DICTIONARY, "Clear", Write, Write, false, false),
        (DICTIONARY, "GetEnumerator", Read, Read, false, false),
        (DICTIONARY, ".ctor", None, None, false, false),
        (DICTIONARY, "get_Keys", None, None, false, false),
        (DICTIONARY, "get_Values", None, None, false, false),

        (CONCURRENT_DICTIONARY, "get_Count", Read, None, false, true),
        (CONCURRENT_DICTIONARY, "get_Item", Read, Read, true, true),
        (CONCURRENT_DICTIONARY, "set_Item", Write, Write, true, true),
        (CONCURRENT_DICTIONARY, "TryAdd", Write, Write, true, true),
        (CONCURRENT_DICTIONARY, "TryRemove", Write, Write, true, true),
        (CONCURRENT_DICTIONARY, "TryGetValue", Read, Read, true, true),
        (CONCURRENT_DICTIONARY, "ContainsKey", Read, None, true, true),
        (CONCURRENT_DICTIONARY, "GetOrAdd", Write, ReadWrite, true, true),
        (CONCURRENT_DICTIONARY, "AddOrUpdate", Write, ReadWrite, true, true),
        (CONCURRENT_DICTIONARY, "TryUpdate", Read, ReadWrite, true, true),
        (CONCURRENT_DICTIONARY, "Clear", Write, Write, false, true),
        (CONCURRENT_DICTIONARY, "GetEnumerator", Read, Read, false, true),
        (CONCURRENT_DICTIONARY, ".ctor", None, None, false, true),
        (CONCURRENT_DICTIONARY, "get_Keys", Read, Read, false, true),
        (CONCURRENT_DICTIONARY, "get_Values", Read, Read, false, true),

        (CONCURRENT_QUEUE, "get_Count", Read, None, false, true),
        (CONCURRENT_QUEUE, "Enqueue", Write, Write, false, true),
        (CONCURRENT_QUEUE, "TryDequeue", Write, Write, false, true),
        (CONCURRENT_QUEUE, "TryPeek", Read, Read, false, true),
        (CONCURRENT_QUEUE, "Clear", Write, Write, false, true),
        (CONCURRENT_QUEUE, "GetEnumerator", Read, Read, false, true),
        (CONCURRENT_QUEUE, ".ctor", None, None, false, true),

        (CONCURRENT_STACK, "get_Count", Read, None, false, true),
        (CONCURRENT_STACK, "Push", Write, Write, false, true),
        (CONCURRENT_STACK, "TryPop", Write, Write, false, true),
        (CONCURRENT_STACK, "TryPeek", Read, Read, false, true),
        (CONCURRENT_STACK, "Clear", Write, Write, false, true),
        (CONCURRENT_STACK, "GetEnumerator", Read, Read, false, true),
        (CONCURRENT_STACK, ".ctor", None, None, false, true),

        (CONCURRENT_BAG, "get_Count", Read, None, false, true),
        (CONCURRENT_BAG, "Add", Write, Write, false, true),
        (CONCURRENT_BAG, "TryTake", Write, Write, false, true),
        (CONCURRENT_BAG, "TryPeek", Read, Read, false, true),
        (CONCURRENT_BAG, "Clear", Write, Write, false, true),
        (CONCURRENT_BAG, "GetEnumerator", Read, Read, false, true),
        (CONCURRENT_BAG, ".ctor", None, None, false, true),

        (HASH_SET, "get_Count", Read, None, false, false),
        (HASH_SET, "Add", Write, Write, false, false),
        (HASH_SET, "Remove", Write, Write, false, false),
        (HASH_SET, "Clear", Write, Write, false, false),
        (HASH_SET, "Contains", Read, Read, false, false),
        (HASH_SET, "TryGetValue", Read, Read, false, false),
        (HASH_SET, "GetEnumerator", Read, Read, false, false),
        (HASH_SET, ".ctor", None, None, false, false),

        (QUEUE, "get_Count", Read, None, false, false),
        (QUEUE, "Enqueue", Write, Write, false, false),
        (QUEUE, "Dequeue", Write, Write, false, false),
        (QUEUE, "TryDequeue", Write, Write, false, false),
        (QUEUE, "Peek", Read, Read, false, false),
        (QUEUE, "TryPeek", Read, Read, false, false),
        (QUEUE, "Clear", Write, Write, false, false),
        (QUEUE, "Contains", Read, Read, false, false),
        (QUEUE, "GetEnumerator", Read, Read, false, false),
        (QUEUE, ".ctor", None, None, false, false),

        (STACK, "get_Count", Read, None, false, false),
        (STACK, "Push", Write, Write, false, false),
        (STACK, "Pop", Write, Write, false, false),
        (STACK, "TryPop", Write, Write, false, false),
        (STACK, "Peek", Read, Read, false, false),
        (STACK, "TryPeek", Read, Read, false, false),
        (STACK, "Clear", Write, Write, false, false),
        (STACK, "Contains", Read, Read, false, false),
        (STACK, "GetEnumerator", Read, Read, false, false),
        (STACK, ".ctor", None, None, false, false),

        (LINKED_LIST, "get_Count", Read, None, false, false),
        (LINKED_LIST, "AddFirst", Write, Write, false, false),
        (LINKED_LIST, "AddLast", Write, Write, false, false),
        (LINKED_LIST, "AddBefore", Write, Write, false, false),
        (LINKED_LIST, "AddAfter", Write, Write, false, false),
        (LINKED_LIST, "Remove", Write, Write, false, false),
        (LINKED_LIST, "RemoveFirst", Write, Write, false, false),
        (LINKED_LIST, "RemoveLast", Write, Write, false, false),
        (LINKED_LIST, "Clear", Write, Write, false, false),
        (LINKED_LIST, "get_First", Read, None, false, false),
        (LINKED_LIST, "get_Last", Read, None, false, false),
        (LINKED_LIST, "Find", Read, Read, false, false),
        (LINKED_LIST, "FindLast", Read, Read, false, false),
        (LINKED_LIST, "Contains", Read, Read, false, false),
        (LINKED_LIST, "GetEnumerator", Read, Read, false, false),
        (LINKED_LIST, ".ctor", None, None, false, false),

        (LINKED_LIST_NODE, ".ctor", None, None, false, false),
        (LINKED_LIST_NODE, "get_Value", None, Read, false, false),
        (LINKED_LIST_NODE, "set_Value", None, Write, false, false),
        (LINKED_LIST_NODE, "get_Next", Read, None, false, false),
        (LINKED_LIST_NODE, "get_Previous", Read, None, false, false),

        (KEY_COLLECTION, "GetEnumerator", Read, Read, false, false),
        (KEY_COLLECTION, "get_Count", Read, None, false, false),
        (VALUE_COLLECTION, "GetEnumerator", Read, Read, false, false),
        (VALUE_COLLECTION, "get_Count", Read, None, false, false),

        (KEY_VALUE_PAIR, ".ctor", None, None, false, false),
        (KEY_VALUE_PAIR, "get_Key", None, None, false, false),
        (KEY_VALUE_PAIR, "get_Value", None, None, false, false),
        (KEY_VALUE_PAIR, "Deconstruct", None, None, false, false),

        (SNAPSHOT_COLLECTION, "get_Count", Read, None, false, false),
        (SNAPSHOT_READ_ONLY, "get_Count", Read, None, false, false),
        (SNAPSHOT_UNTYPED_COLLECTION, "get_Count", Read, None, false, false),
        (SNAPSHOT_ENUMERABLE, "GetEnumerator", Read, Read, false, false),
        (SNAPSHOT_UNTYPED_ENUMERABLE, "GetEnumerator", Read, Read, false, false)
    ];

    /// <summary>The closed type of the collection a row's member is called on, or through which its receiver is reached.</summary>
    private static string BoxOf(string type) => type switch
    {
        LIST => "List<Item>",
        DICTIONARY or KEY_COLLECTION or VALUE_COLLECTION or KEY_VALUE_PAIR => "Dictionary<string, Item>",
        CONCURRENT_DICTIONARY => "ConcurrentDictionary<string, Item>",
        CONCURRENT_QUEUE => "ConcurrentQueue<Item>",
        CONCURRENT_STACK => "ConcurrentStack<Item>",
        CONCURRENT_BAG => "ConcurrentBag<Item>",
        HASH_SET => "HashSet<Item>",
        QUEUE => "Queue<Item>",
        STACK => "Stack<Item>",
        LINKED_LIST or LINKED_LIST_NODE => "LinkedList<Item>",
        _ => "ConcurrentDictionary<string, Item>"
    };

    /// <summary>Each row's call, written from the member's signature: the statements before the last set its receiver up, and the last
    /// calls the member on a line of its own. A constructor makes a collection of its own from <c>Spare</c>, and a pair is one of its own:
    /// neither touches <c>Box</c>.</summary>
    private static readonly Dictionary<string, string[]> Calls = new(StringComparer.Ordinal)
    {
        [$"{LIST}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{LIST}.get_Item"] = ["_ = _state.Box[0];"],
        [$"{LIST}.set_Item"] = ["_state.Box[0] = _state.First;"],
        [$"{LIST}.Add"] = ["_state.Box.Add(_state.First);"],
        [$"{LIST}.Insert"] = ["_state.Box.Insert(0, _state.First);"],
        [$"{LIST}.Remove"] = ["_state.Box.Remove(_state.First);"],
        [$"{LIST}.RemoveAt"] = ["_state.Box.RemoveAt(0);"],
        [$"{LIST}.Clear"] = ["_state.Box.Clear();"],
        [$"{LIST}.Contains"] = ["_ = _state.Box.Contains(_state.First);"],
        [$"{LIST}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{LIST}.AddRange"] = ["_state.Box.AddRange(_state.Others);"],
        [$"{LIST}..ctor"] = ["_ = new List<Item>(_state.Spare);"],

        [$"{DICTIONARY}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{DICTIONARY}.get_Item"] = ["_ = _state.Box[\"a\"];"],
        [$"{DICTIONARY}.set_Item"] = ["_state.Box[\"a\"] = _state.First;"],
        [$"{DICTIONARY}.Add"] = ["_state.Box.Add(\"a\", _state.First);"],
        [$"{DICTIONARY}.TryAdd"] = ["_state.Box.TryAdd(\"a\", _state.First);"],
        [$"{DICTIONARY}.Remove"] = ["_state.Box.Remove(\"a\");"],
        [$"{DICTIONARY}.TryGetValue"] = ["_state.Box.TryGetValue(\"a\", out _);"],
        [$"{DICTIONARY}.ContainsKey"] = ["_ = _state.Box.ContainsKey(\"a\");"],
        [$"{DICTIONARY}.Clear"] = ["_state.Box.Clear();"],
        [$"{DICTIONARY}.GetEnumerator"] = ["foreach (var pair in _state.Box) { }"],
        [$"{DICTIONARY}..ctor"] = ["_ = new Dictionary<string, Item>(_state.Spare);"],
        [$"{DICTIONARY}.get_Keys"] = ["_ = _state.Box.Keys;"],
        [$"{DICTIONARY}.get_Values"] = ["_ = _state.Box.Values;"],

        [$"{CONCURRENT_DICTIONARY}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{CONCURRENT_DICTIONARY}.get_Item"] = ["_ = _state.Box[\"a\"];"],
        [$"{CONCURRENT_DICTIONARY}.set_Item"] = ["_state.Box[\"a\"] = _state.First;"],
        [$"{CONCURRENT_DICTIONARY}.TryAdd"] = ["_state.Box.TryAdd(\"a\", _state.First);"],
        [$"{CONCURRENT_DICTIONARY}.TryRemove"] = ["_state.Box.TryRemove(\"a\", out _);"],
        [$"{CONCURRENT_DICTIONARY}.TryGetValue"] = ["_state.Box.TryGetValue(\"a\", out _);"],
        [$"{CONCURRENT_DICTIONARY}.ContainsKey"] = ["_ = _state.Box.ContainsKey(\"a\");"],
        [$"{CONCURRENT_DICTIONARY}.GetOrAdd"] = ["_ = _state.Box.GetOrAdd(\"a\", _state.First);"],
        [$"{CONCURRENT_DICTIONARY}.AddOrUpdate"] = ["_ = _state.Box.AddOrUpdate(\"a\", _state.First, (_, old) => old);"],
        [$"{CONCURRENT_DICTIONARY}.TryUpdate"] = ["_state.Box.TryUpdate(\"a\", _state.First, _state.Second);"],
        [$"{CONCURRENT_DICTIONARY}.Clear"] = ["_state.Box.Clear();"],
        [$"{CONCURRENT_DICTIONARY}.GetEnumerator"] = ["foreach (var pair in _state.Box) { }"],
        [$"{CONCURRENT_DICTIONARY}..ctor"] = ["_ = new ConcurrentDictionary<string, Item>(_state.Spare);"],
        [$"{CONCURRENT_DICTIONARY}.get_Keys"] = ["_ = _state.Box.Keys;"],
        [$"{CONCURRENT_DICTIONARY}.get_Values"] = ["_ = _state.Box.Values;"],

        [$"{CONCURRENT_QUEUE}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{CONCURRENT_QUEUE}.Enqueue"] = ["_state.Box.Enqueue(_state.First);"],
        [$"{CONCURRENT_QUEUE}.TryDequeue"] = ["_state.Box.TryDequeue(out _);"],
        [$"{CONCURRENT_QUEUE}.TryPeek"] = ["_state.Box.TryPeek(out _);"],
        [$"{CONCURRENT_QUEUE}.Clear"] = ["_state.Box.Clear();"],
        [$"{CONCURRENT_QUEUE}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{CONCURRENT_QUEUE}..ctor"] = ["_ = new ConcurrentQueue<Item>(_state.Spare);"],

        [$"{CONCURRENT_STACK}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{CONCURRENT_STACK}.Push"] = ["_state.Box.Push(_state.First);"],
        [$"{CONCURRENT_STACK}.TryPop"] = ["_state.Box.TryPop(out _);"],
        [$"{CONCURRENT_STACK}.TryPeek"] = ["_state.Box.TryPeek(out _);"],
        [$"{CONCURRENT_STACK}.Clear"] = ["_state.Box.Clear();"],
        [$"{CONCURRENT_STACK}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{CONCURRENT_STACK}..ctor"] = ["_ = new ConcurrentStack<Item>(_state.Spare);"],

        [$"{CONCURRENT_BAG}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{CONCURRENT_BAG}.Add"] = ["_state.Box.Add(_state.First);"],
        [$"{CONCURRENT_BAG}.TryTake"] = ["_state.Box.TryTake(out _);"],
        [$"{CONCURRENT_BAG}.TryPeek"] = ["_state.Box.TryPeek(out _);"],
        [$"{CONCURRENT_BAG}.Clear"] = ["_state.Box.Clear();"],
        [$"{CONCURRENT_BAG}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{CONCURRENT_BAG}..ctor"] = ["_ = new ConcurrentBag<Item>(_state.Spare);"],

        [$"{HASH_SET}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{HASH_SET}.Add"] = ["_state.Box.Add(_state.First);"],
        [$"{HASH_SET}.Remove"] = ["_state.Box.Remove(_state.First);"],
        [$"{HASH_SET}.Clear"] = ["_state.Box.Clear();"],
        [$"{HASH_SET}.Contains"] = ["_ = _state.Box.Contains(_state.First);"],
        [$"{HASH_SET}.TryGetValue"] = ["_state.Box.TryGetValue(_state.First, out _);"],
        [$"{HASH_SET}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{HASH_SET}..ctor"] = ["_ = new HashSet<Item>(_state.Spare);"],

        [$"{QUEUE}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{QUEUE}.Enqueue"] = ["_state.Box.Enqueue(_state.First);"],
        [$"{QUEUE}.Dequeue"] = ["_ = _state.Box.Dequeue();"],
        [$"{QUEUE}.TryDequeue"] = ["_state.Box.TryDequeue(out _);"],
        [$"{QUEUE}.Peek"] = ["_ = _state.Box.Peek();"],
        [$"{QUEUE}.TryPeek"] = ["_state.Box.TryPeek(out _);"],
        [$"{QUEUE}.Clear"] = ["_state.Box.Clear();"],
        [$"{QUEUE}.Contains"] = ["_ = _state.Box.Contains(_state.First);"],
        [$"{QUEUE}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{QUEUE}..ctor"] = ["_ = new Queue<Item>(_state.Spare);"],

        [$"{STACK}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{STACK}.Push"] = ["_state.Box.Push(_state.First);"],
        [$"{STACK}.Pop"] = ["_ = _state.Box.Pop();"],
        [$"{STACK}.TryPop"] = ["_state.Box.TryPop(out _);"],
        [$"{STACK}.Peek"] = ["_ = _state.Box.Peek();"],
        [$"{STACK}.TryPeek"] = ["_state.Box.TryPeek(out _);"],
        [$"{STACK}.Clear"] = ["_state.Box.Clear();"],
        [$"{STACK}.Contains"] = ["_ = _state.Box.Contains(_state.First);"],
        [$"{STACK}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{STACK}..ctor"] = ["_ = new Stack<Item>(_state.Spare);"],

        [$"{LINKED_LIST}.get_Count"] = ["_ = _state.Box.Count;"],
        [$"{LINKED_LIST}.AddFirst"] = ["_state.Box.AddFirst(_state.First);"],
        [$"{LINKED_LIST}.AddLast"] = ["_state.Box.AddLast(_state.First);"],
        [$"{LINKED_LIST}.AddBefore"] = ["_state.Box.AddBefore(_state.Node, _state.First);"],
        [$"{LINKED_LIST}.AddAfter"] = ["_state.Box.AddAfter(_state.Node, _state.First);"],
        [$"{LINKED_LIST}.Remove"] = ["_state.Box.Remove(_state.First);"],
        [$"{LINKED_LIST}.RemoveFirst"] = ["_state.Box.RemoveFirst();"],
        [$"{LINKED_LIST}.RemoveLast"] = ["_state.Box.RemoveLast();"],
        [$"{LINKED_LIST}.Clear"] = ["_state.Box.Clear();"],
        [$"{LINKED_LIST}.get_First"] = ["_ = _state.Box.First;"],
        [$"{LINKED_LIST}.get_Last"] = ["_ = _state.Box.Last;"],
        [$"{LINKED_LIST}.Find"] = ["_ = _state.Box.Find(_state.First);"],
        [$"{LINKED_LIST}.FindLast"] = ["_ = _state.Box.FindLast(_state.First);"],
        [$"{LINKED_LIST}.Contains"] = ["_ = _state.Box.Contains(_state.First);"],
        [$"{LINKED_LIST}.GetEnumerator"] = ["foreach (var item in _state.Box) { }"],
        [$"{LINKED_LIST}..ctor"] = ["_ = new LinkedList<Item>(_state.Spare);"],

        [$"{LINKED_LIST_NODE}..ctor"] = ["_ = new LinkedListNode<Item>(_state.First);"],
        [$"{LINKED_LIST_NODE}.get_Value"] = ["var node = _state.Box.First!;", "_ = node.Value;"],
        [$"{LINKED_LIST_NODE}.set_Value"] = ["var node = _state.Box.First!;", "node.Value = _state.First;"],
        [$"{LINKED_LIST_NODE}.get_Next"] = ["var node = _state.Box.First!;", "_ = node.Next;"],
        [$"{LINKED_LIST_NODE}.get_Previous"] = ["var node = _state.Box.First!;", "_ = node.Previous;"],

        [$"{KEY_COLLECTION}.GetEnumerator"] = ["foreach (var key in _state.Box.Keys) { }"],
        [$"{KEY_COLLECTION}.get_Count"] = ["_ = _state.Box.Keys.Count;"],
        [$"{VALUE_COLLECTION}.GetEnumerator"] = ["foreach (var value in _state.Box.Values) { }"],
        [$"{VALUE_COLLECTION}.get_Count"] = ["_ = _state.Box.Values.Count;"],

        [$"{KEY_VALUE_PAIR}..ctor"] = ["_ = new KeyValuePair<string, Item>(\"a\", _state.First);"],
        [$"{KEY_VALUE_PAIR}.get_Key"] = ["var pair = new KeyValuePair<string, Item>(\"a\", _state.First);", "_ = pair.Key;"],
        [$"{KEY_VALUE_PAIR}.get_Value"] = ["var pair = new KeyValuePair<string, Item>(\"a\", _state.First);", "_ = pair.Value;"],
        [$"{KEY_VALUE_PAIR}.Deconstruct"] = ["var pair = new KeyValuePair<string, Item>(\"a\", _state.First);", "pair.Deconstruct(out _, out _);"],

        [$"{SNAPSHOT_COLLECTION}.get_Count"] = ["var keys = _state.Box.Keys;", "_ = keys.Count;"],
        [$"{SNAPSHOT_READ_ONLY}.get_Count"] = ["var keys = (IReadOnlyCollection<string>)_state.Box.Keys;", "_ = keys.Count;"],
        [$"{SNAPSHOT_UNTYPED_COLLECTION}.get_Count"] = ["var keys = (System.Collections.ICollection)_state.Box.Keys;", "_ = keys.Count;"],
        [$"{SNAPSHOT_ENUMERABLE}.GetEnumerator"] = ["var values = _state.Box.Values;", "foreach (var value in values) { }"],
        [$"{SNAPSHOT_UNTYPED_ENUMERABLE}.GetEnumerator"] = ["var values = (System.Collections.IEnumerable)_state.Box.Values;", "foreach (var value in values) { }"]
    };

    private static readonly Lazy<Compilation> TestCompilation = new(() =>
        FixtureSolution.Create(("Case.cs", "public sealed class Placeholder { }")).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!);

    public static TheoryData<string, string, IrCollectionEffect, IrCollectionEffect, bool, bool> Members()
    {
        var data = new TheoryData<string, string, IrCollectionEffect, IrCollectionEffect, bool, bool>();
        foreach (var row in Rows)
            data.Add(row.Type, row.Member, row.Structure, row.Element, row.NamesCell, row.Atomic);
        return data;
    }

    /// <summary>Every overload of the member is modelled with the row's effects, and the engine makes exactly the row's accesses when the
    /// member is called on a collection a singleton's field holds: one on the structure and one on the cells for each effect that is
    /// not none, atomic where the row is, on the cell a key argument names or on every cell. A snapshot is a collection no field
    /// holds, as a list made in the body is: it makes no access and is no unresolved call (R7, R9a).</summary>
    [Theory]
    [MemberData(nameof(Members))]
    public void Member_makes_the_accesses_the_table_gives_it(string type, string member, IrCollectionEffect structure,
                                                             IrCollectionEffect element, bool namesCell, bool atomic)
    {
        var overloads = PublicMethods(type).Where(method => method.Name == member).ToArray();

        Assert.NotEmpty(overloads);
        // The effect is the member's, so every overload of it makes the same accesses.
        foreach (var method in overloads)
        {
            var call = Recognized(type, method);
            Assert.NotNull(call);
            Assert.Equal((structure, element, namesCell, atomic), (call.Structure, call.Element, call.KeyArgument is not null, call.IsAtomic));
        }

        var statements = Calls[$"{type}.{member}"];
        var text = EngineFixture.Usings + Source(BoxOf(type), statements);
        var run = EngineFixture.AnalyzeScope(FixtureSolution.Create(("Case.cs", text)), "scope:Fixture");
        var line = Array.FindIndex(text.Split('\n'), candidate => candidate.Contains(statements[^1], StringComparison.Ordinal)) + 1;
        var onBox = run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) &&
                                                            access.Source.StartLine == line && access.Resource.CollectionId is not null &&
                                                            access.Resource.AccessPath[0] == "Box")
                       .ToArray();
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        if (SnapshotTypes.Contains(type))
        {
            Assert.Empty(onBox);
            return;
        }

        var expected = new List<string>();
        if (structure != None)
            expected.Add($"structure {Operation(structure, atomic)}");
        if (element != None)
            expected.Add($"cell {Operation(element, atomic)}");
        Assert.Equal(expected.Order(StringComparer.Ordinal),
                     onBox.Select(access => $"{(access.Resource.Selector is null ? "structure" : "cell")} {access.Operation}").Distinct().Order(StringComparer.Ordinal));
        Assert.All(onBox.Where(access => access.Resource.Selector is not null),
                   cell => Assert.Equal(namesCell, cell.Resource.Selector != ElementSelector.Unknown));
    }

    [Fact]
    public void Effect_rows_are_exactly_the_members_the_table_models()
    {
        var modelled = TableTypes.Concat(SnapshotTypes)
                                 .SelectMany(type => PublicMethods(type).Where(method => Recognized(type, method) is not null)
                                                                        .Select(method => $"{type}.{method.Name}"))
                                 .ToHashSet(StringComparer.Ordinal);
        var listed = Rows.Select(row => $"{row.Type}.{row.Member}").ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Rows.Length, listed.Count);
        Assert.Empty(modelled.Except(listed).Order(StringComparer.Ordinal));
        Assert.Empty(listed.Except(modelled).Order(StringComparer.Ordinal));
        Assert.Empty(listed.Except(Calls.Keys).Order(StringComparer.Ordinal));
    }

    /// <summary>What the recognizer models a member as: a member of the table, or one of the interface a snapshot is used through.</summary>
    private static IrCollectionCall? Recognized(string type, IMethodSymbol method) =>
        SnapshotTypes.Contains(type) ? IrLowering.Collections.OfSnapshot(method) : IrLowering.Collections.Of(method);

    private static AccessOperation Operation(IrCollectionEffect effect, bool atomic) => (effect, atomic) switch
    {
        (Read, false) => AccessOperation.Read,
        (Read, true) => AccessOperation.AtomicRead,
        (Write, false) => AccessOperation.Write,
        (Write, true) => AccessOperation.AtomicWrite,
        (_, false) => AccessOperation.ReadModifyWrite,
        _ => AccessOperation.AtomicReadModifyWrite
    };

    /// <summary>A singleton whose <c>Box</c> and <c>Spare</c> are collections of <paramref name="box"/>, and a worker doing
    /// <paramref name="statements"/>, one a line.</summary>
    private static string Source(string box, IReadOnlyList<string> statements) => $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public class Item { public int Hits; }

        public sealed class State
        {
            public {{box}} Box = new();
            public {{box}} Spare = new();
            public readonly Item First = new Item();
            public readonly Item Second = new Item();
            public readonly List<Item> Others = new();
            public readonly LinkedListNode<Item> Node = new(new Item());
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{string.Join(Environment.NewLine, statements)}}
                return Task.CompletedTask;
            }
        }
        """ + EngineFixture.Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();");

    private static IEnumerable<IMethodSymbol> PublicMethods(string type) =>
        Assert.IsAssignableFrom<INamedTypeSymbol>(TestCompilation.Value.GetTypeByMetadataName(type))
              .GetMembers()
              .OfType<IMethodSymbol>()
              .Where(method => method.DeclaredAccessibility == Accessibility.Public && !method.IsStatic);
}

