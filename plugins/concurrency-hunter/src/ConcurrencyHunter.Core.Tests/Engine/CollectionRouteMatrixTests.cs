using System.Collections.Concurrent;
using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// One systematic pass over every way a member of the ADR 0010 table, a node, a snapshot, a factory and a compound form reaches what
/// it acts on (question 32, R6). Each case is run once, with every way of it as a root of its own, and each way is compared with the
/// canonical one: a member called on the field that holds its collection, a node used where it was taken, a snapshot against a list
/// at the same place, a factory given as a lambda, a compound form against its long form with the receiver and the index taken into
/// locals once. Two sides agree when the accesses they make are the same by resource identity and operation — which carries the
/// atomicity and the compound flag — the objects a write through a handed-out value lands on are the same, and neither has an unknown
/// effect or a semantic gap the other lacks.
/// </summary>
public sealed class CollectionRouteMatrixTests
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

    /// <summary>The line a case's statement stands on in every way of it: only what that line does is compared.</summary>
    private const string MARK = "/*M*/";

    /// <summary>The ways of R3 a receiver reaches a member by, the field it is held in first; each is a root of its own.</summary>
    private static readonly string[] ReceiverRoutes = ["local", "parameter", "helper result", "cell of another collection", "merge", "static field", "capture"];

    // ---- the member cases ----

    /// <summary>A member of the table and the statement calling it on <c>{R}</c>, with the number of arguments the call passes. A case
    /// written through an interface of its own names the interface member it stands for.</summary>
    private sealed record MemberCase(string Type, string Member, int Arity, string Statement)
    {
        public string? Through { get; init; }

        /// <summary>The statement through the non-generic <c>IDictionary</c>, whose enumeration hands out <c>DictionaryEntry</c> values
        /// where every other one hands out pairs; null where the case's own statement serves there too.</summary>
        public string? EntryStatement { get; init; }

        public string Name => Through is null ? $"{Short(Type)}.{Member}" : $"{Short(Type)} through {Through[(Through.IndexOf(':') + 2)..]}";
    }

    private static MemberCase M(string type, string member, int arity, string statement) => new(type, member, arity, statement);

    /// <summary>A write through the value of each pair a dictionary's enumeration hands out, which lands on the object the dictionary
    /// holds only where the pair is filled from the dictionary's storage; the pair is taken as an object first, since a non-generic
    /// enumeration hands out objects.</summary>
    private const string PAIR_VALUE_WRITE = "foreach (var pair in {R}) (((KeyValuePair<string, Item>)(object)pair!).Value as Item)!.Hits = 1;";

    /// <summary>The same write through the non-generic <c>IDictionary</c>, whose enumeration hands out a <c>DictionaryEntry</c> for each
    /// pair (review F-0043).</summary>
    private const string ENTRY_VALUE_WRITE = "foreach (System.Collections.DictionaryEntry entry in {R}) (entry.Value as Item)!.Hits = 1;";

    private const string NON_GENERIC_DICTIONARY = "System.Collections.IDictionary";

    private static readonly MemberCase[] MemberCases =
    [
        M(LIST, "get_Count", 0, "_ = {R}.Count;"),
        M(LIST, "get_Item", 1, "({R}[0] as Item)!.Hits = 1;"),
        M(LIST, "set_Item", 2, "{R}[0] = First;"),
        M(LIST, "Add", 1, "{R}.Add(First);"),
        M(LIST, "Insert", 2, "{R}.Insert(0, First);"),
        M(LIST, "Remove", 1, "{R}.Remove(First);"),
        M(LIST, "RemoveAt", 1, "{R}.RemoveAt(0);"),
        M(LIST, "Clear", 0, "{R}.Clear();"),
        M(LIST, "Contains", 1, "_ = {R}.Contains(First);"),
        M(LIST, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),
        M(LIST, "AddRange", 1, "{R}.AddRange(Others);"),
        M(LIST, "ForEach", 1, "{R}.ForEach(item => item.Hits = 1);"),
        M(LIST, "Exists", 1, "_ = {R}.Exists(item => item.Hits > 0);"),
        M(LIST, "TrueForAll", 1, "_ = {R}.TrueForAll(item => item.Hits > 0);"),
        M(LIST, "Find", 1, "{R}.Find(item => true)!.Hits = 1;"),
        M(LIST, "FindLast", 1, "{R}.FindLast(item => true)!.Hits = 1;"),
        M(LIST, "FindIndex", 1, "_ = {R}.FindIndex(item => item.Hits > 0);"),
        M(LIST, "FindLastIndex", 1, "_ = {R}.FindLastIndex(item => item.Hits > 0);"),
        M(LIST, "FindAll", 1, "{R}.FindAll(item => true)[0].Hits = 1;"),
        M(LIST, "ConvertAll", 1, "{R}.ConvertAll(item => item)[0].Hits = 1;"),
        M(LIST, "RemoveAll", 1, "{R}.RemoveAll(item => item.Hits > 0);"),
        M(LIST, "Sort", 1, "{R}.Sort((a, b) => a.Hits - b.Hits);"),

        M(DICTIONARY, "get_Count", 0, "_ = {R}.Count;"),
        M(DICTIONARY, "get_Item", 1, "({R}[\"a\"] as Item)!.Hits = 1;"),
        M(DICTIONARY, "set_Item", 2, "{R}[\"a\"] = First;"),
        M(DICTIONARY, "Add", 2, "{R}.Add(\"a\", First);"),
        M(DICTIONARY, "TryAdd", 2, "{R}.TryAdd(\"a\", First);"),
        M(DICTIONARY, "Remove", 1, "{R}.Remove(\"a\");"),
        M(DICTIONARY, "TryGetValue", 2, "if ({R}.TryGetValue(\"a\", out var held)) (held as Item)!.Hits = 1;"),
        M(DICTIONARY, "ContainsKey", 1, "_ = {R}.ContainsKey(\"a\");"),
        M(DICTIONARY, "Clear", 0, "{R}.Clear();"),
        M(DICTIONARY, "GetEnumerator", 0, PAIR_VALUE_WRITE) with { EntryStatement = ENTRY_VALUE_WRITE },
        M(DICTIONARY, "get_Keys", 0, "foreach (var key in {R}.Keys) { }"),
        M(DICTIONARY, "get_Values", 0, "foreach (var value in {R}.Values) (value as Item)!.Hits = 1;"),
        M(DICTIONARY, "Add", 1, "((ICollection<KeyValuePair<string, Item>>){R}).Add(new KeyValuePair<string, Item>(\"a\", First));") with
        {
            Through = "System.Collections.Generic.Dictionary: System.Collections.Generic.ICollection<T>.Add"
        },
        M(DICTIONARY, "Remove", 1, "((ICollection<KeyValuePair<string, Item>>){R}).Remove(new KeyValuePair<string, Item>(\"a\", First));") with
        {
            Through = "System.Collections.Generic.Dictionary: System.Collections.Generic.ICollection<T>.Remove"
        },

        M(CONCURRENT_DICTIONARY, "get_Count", 0, "_ = {R}.Count;"),
        M(CONCURRENT_DICTIONARY, "get_Item", 1, "({R}[\"a\"] as Item)!.Hits = 1;"),
        M(CONCURRENT_DICTIONARY, "set_Item", 2, "{R}[\"a\"] = First;"),
        M(CONCURRENT_DICTIONARY, "TryAdd", 2, "{R}.TryAdd(\"a\", First);"),
        M(CONCURRENT_DICTIONARY, "TryRemove", 2, "if ({R}.TryRemove(\"a\", out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_DICTIONARY, "TryGetValue", 2, "if ({R}.TryGetValue(\"a\", out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_DICTIONARY, "ContainsKey", 1, "_ = {R}.ContainsKey(\"a\");"),
        M(CONCURRENT_DICTIONARY, "GetOrAdd", 2, "({R}.GetOrAdd(\"a\", First) as Item)!.Hits = 1;"),
        M(CONCURRENT_DICTIONARY, "AddOrUpdate", 3, "({R}.AddOrUpdate(\"a\", First, (_, old) => old) as Item)!.Hits = 1;"),
        M(CONCURRENT_DICTIONARY, "TryUpdate", 3, "{R}.TryUpdate(\"a\", First, Second);"),
        M(CONCURRENT_DICTIONARY, "Clear", 0, "{R}.Clear();"),
        M(CONCURRENT_DICTIONARY, "GetEnumerator", 0, PAIR_VALUE_WRITE) with { EntryStatement = ENTRY_VALUE_WRITE },
        M(CONCURRENT_DICTIONARY, "get_Keys", 0, "foreach (var key in {R}.Keys) { }"),
        M(CONCURRENT_DICTIONARY, "get_Values", 0, "foreach (var value in {R}.Values) (value as Item)!.Hits = 1;"),
        M(CONCURRENT_DICTIONARY, "TryAdd", 1, "((ICollection<KeyValuePair<string, Item>>){R}).Add(new KeyValuePair<string, Item>(\"a\", First));") with
        {
            Through = "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.Generic.ICollection<T>.Add"
        },
        M(CONCURRENT_DICTIONARY, "TryRemove", 1, "((ICollection<KeyValuePair<string, Item>>){R}).Remove(new KeyValuePair<string, Item>(\"a\", First));") with
        {
            Through = "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.Generic.ICollection<T>.Remove"
        },
        M(CONCURRENT_DICTIONARY, "TryRemove", 1, "((IDictionary<string, Item>){R}).Remove(\"a\");") with
        {
            Through = "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.Generic.IDictionary<TKey, TValue>.Remove"
        },
        M(CONCURRENT_DICTIONARY, "TryRemove", 1, "((System.Collections.IDictionary){R}).Remove(\"a\");") with
        {
            Through = "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.IDictionary.Remove"
        },

        M(CONCURRENT_QUEUE, "get_Count", 0, "_ = {R}.Count;"),
        M(CONCURRENT_QUEUE, "Enqueue", 1, "{R}.Enqueue(First);"),
        M(CONCURRENT_QUEUE, "TryDequeue", 1, "if ({R}.TryDequeue(out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_QUEUE, "TryPeek", 1, "if ({R}.TryPeek(out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_QUEUE, "Clear", 0, "{R}.Clear();"),
        M(CONCURRENT_QUEUE, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),

        M(CONCURRENT_STACK, "get_Count", 0, "_ = {R}.Count;"),
        M(CONCURRENT_STACK, "Push", 1, "{R}.Push(First);"),
        M(CONCURRENT_STACK, "TryPop", 1, "if ({R}.TryPop(out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_STACK, "TryPeek", 1, "if ({R}.TryPeek(out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_STACK, "Clear", 0, "{R}.Clear();"),
        M(CONCURRENT_STACK, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),

        M(CONCURRENT_BAG, "get_Count", 0, "_ = {R}.Count;"),
        M(CONCURRENT_BAG, "Add", 1, "{R}.Add(First);"),
        M(CONCURRENT_BAG, "TryTake", 1, "if ({R}.TryTake(out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_BAG, "TryPeek", 1, "if ({R}.TryPeek(out var held)) (held as Item)!.Hits = 1;"),
        M(CONCURRENT_BAG, "Clear", 0, "{R}.Clear();"),
        M(CONCURRENT_BAG, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),

        M(HASH_SET, "get_Count", 0, "_ = {R}.Count;"),
        M(HASH_SET, "Add", 1, "{R}.Add(First);"),
        M(HASH_SET, "Remove", 1, "{R}.Remove(First);"),
        M(HASH_SET, "Clear", 0, "{R}.Clear();"),
        M(HASH_SET, "Contains", 1, "_ = {R}.Contains(First);"),
        M(HASH_SET, "TryGetValue", 2, "if ({R}.TryGetValue(First, out var held)) (held as Item)!.Hits = 1;"),
        M(HASH_SET, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),
        M(HASH_SET, "RemoveWhere", 1, "{R}.RemoveWhere(item => item.Hits > 0);"),

        M(QUEUE, "get_Count", 0, "_ = {R}.Count;"),
        M(QUEUE, "Enqueue", 1, "{R}.Enqueue(First);"),
        M(QUEUE, "Dequeue", 0, "({R}.Dequeue() as Item)!.Hits = 1;"),
        M(QUEUE, "TryDequeue", 1, "if ({R}.TryDequeue(out var held)) (held as Item)!.Hits = 1;"),
        M(QUEUE, "Peek", 0, "({R}.Peek() as Item)!.Hits = 1;"),
        M(QUEUE, "TryPeek", 1, "if ({R}.TryPeek(out var held)) (held as Item)!.Hits = 1;"),
        M(QUEUE, "Clear", 0, "{R}.Clear();"),
        M(QUEUE, "Contains", 1, "_ = {R}.Contains(First);"),
        M(QUEUE, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),

        M(STACK, "get_Count", 0, "_ = {R}.Count;"),
        M(STACK, "Push", 1, "{R}.Push(First);"),
        M(STACK, "Pop", 0, "({R}.Pop() as Item)!.Hits = 1;"),
        M(STACK, "TryPop", 1, "if ({R}.TryPop(out var held)) (held as Item)!.Hits = 1;"),
        M(STACK, "Peek", 0, "({R}.Peek() as Item)!.Hits = 1;"),
        M(STACK, "TryPeek", 1, "if ({R}.TryPeek(out var held)) (held as Item)!.Hits = 1;"),
        M(STACK, "Clear", 0, "{R}.Clear();"),
        M(STACK, "Contains", 1, "_ = {R}.Contains(First);"),
        M(STACK, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),

        M(LINKED_LIST, "get_Count", 0, "_ = {R}.Count;"),
        M(LINKED_LIST, "AddFirst", 1, "{R}.AddFirst(First);"),
        M(LINKED_LIST, "AddLast", 1, "{R}.AddLast(First);"),
        M(LINKED_LIST, "AddBefore", 2, "{R}.AddBefore({R}.First!, First);"),
        M(LINKED_LIST, "AddAfter", 2, "{R}.AddAfter({R}.First!, First);"),
        M(LINKED_LIST, "Remove", 1, "{R}.Remove(First);"),
        M(LINKED_LIST, "RemoveFirst", 0, "{R}.RemoveFirst();"),
        M(LINKED_LIST, "RemoveLast", 0, "{R}.RemoveLast();"),
        M(LINKED_LIST, "Clear", 0, "{R}.Clear();"),
        M(LINKED_LIST, "get_First", 0, "({R}.First!.Value as Item)!.Hits = 1;"),
        M(LINKED_LIST, "get_Last", 0, "({R}.Last!.Value as Item)!.Hits = 1;"),
        M(LINKED_LIST, "Find", 1, "({R}.Find(First)!.Value as Item)!.Hits = 1;"),
        M(LINKED_LIST, "FindLast", 1, "({R}.FindLast(First)!.Value as Item)!.Hits = 1;"),
        M(LINKED_LIST, "Contains", 1, "_ = {R}.Contains(First);"),
        M(LINKED_LIST, "GetEnumerator", 0, "foreach (var item in {R}) (item as Item)!.Hits = 1;"),

        M(KEY_COLLECTION, "get_Count", 0, "_ = {R}.Count;"),
        M(KEY_COLLECTION, "GetEnumerator", 0, "foreach (var key in {R}) { }"),
        M(VALUE_COLLECTION, "get_Count", 0, "_ = {R}.Count;"),
        M(VALUE_COLLECTION, "GetEnumerator", 0, "foreach (var value in {R}) (value as Item)!.Hits = 1;")
    ];

    /// <summary>An interface member an array decides, or leaves to the direct call it is (R5): the code on the array directly, and the
    /// code through each interface of the array that has the member, both over a receiver <c>{R}</c> and an index <c>{I}</c>, so that
    /// every route of R3 runs both and compares them there.</summary>
    /// <param name="Rank">The receiver array's rank.</param>
    /// <param name="Member">The interface member name.</param>
    /// <param name="Decided">Whether the scoped map decides the call.</param>
    /// <param name="Canonical">The direct array statement.</param>
    /// <param name="Statement">The statement through an interface.</param>
    /// <param name="DirectCall">Whether the row retains the label of a direct-library counterpart.</param>
    private sealed record ArrayCase(int Rank, string Member, bool Decided, string Canonical, string Statement, bool DirectCall = false)
    {
        public string Kind => Rank == 1 ? CollectionObjects.ARRAY : CollectionObjects.MULTIDIMENSIONAL_ARRAY;

        public string Name => $"{(Rank == 1 ? "Item[]" : "int[,]")}.{Member}{(DirectCall ? " (direct call)" : "")}";
    }

    private static readonly ArrayCase[] ArrayCases =
    [
        new(1, "get_Item", true, "({R}[{I}] as Item)!.Hits = 1;", "({R}[{I}] as Item)!.Hits = 1;"),
        new(1, "set_Item", true, "{R}[{I}] = First;", "{R}[{I}] = First;"),
        new(1, "get_Count", true, "_ = {R};", "_ = {R}.Count;"),
        new(1, "get_IsReadOnly", true, "_ = {R};", "_ = {R}.IsReadOnly;"),
        new(1, "Add", true, "_ = {R}; _ = First;", "{R}.Add(First);"),
        new(1, "Insert", true, "_ = {R}; _ = First;", "{R}.Insert({I}, First);"),
        new(1, "Remove", true, "_ = {R}; _ = First;", "{R}.Remove(First);"),
        new(1, "RemoveAt", true, "_ = {R};", "{R}.RemoveAt({I});"),
        new(1, "Clear", true, "_ = {R};", "{R}.Clear();"),
        new(1, "Contains", true, "_ = First; foreach (var item in {R}) { }", "_ = {R}.Contains(First);"),
        new(1, "IndexOf", true, "_ = First; foreach (var item in {R}) { }", "_ = {R}.IndexOf(First);"),
        new(1, "GetEnumerator", true, "foreach (var item in {R}) (item as Item)!.Hits = 1;", "foreach (var item in {R}) (item as Item)!.Hits = 1;"),
        new(1, "CopyTo", true, "{R}.CopyTo(Target, {I});", "{R}.CopyTo(Target, {I});", true),
        new(1, "Clear", true, "Array.Clear({R});", "{R}.Clear();", true),

        new(2, "get_Item", true, "_ = {R};", "_ = {R}[{I}];"),
        new(2, "set_Item", true, "_ = {R};", "{R}[{I}] = 1;"),
        new(2, "get_Count", true, "_ = {R};", "_ = {R}.Count;"),
        new(2, "get_IsReadOnly", true, "_ = {R};", "_ = {R}.IsReadOnly;"),
        new(2, "Add", true, "_ = {R};", "{R}.Add(1);"),
        new(2, "Insert", true, "_ = {R};", "{R}.Insert({I}, 1);"),
        new(2, "Remove", true, "_ = {R};", "{R}.Remove(1);"),
        new(2, "RemoveAt", true, "_ = {R};", "{R}.RemoveAt({I});"),
        new(2, "Contains", true, "_ = {R};", "_ = {R}.Contains(1);"),
        new(2, "IndexOf", true, "_ = {R};", "_ = {R}.IndexOf(1);"),
        new(2, "GetEnumerator", true, "foreach (var cell in {R}) { }", "foreach (var cell in {R}) { }"),
        new(2, "CopyTo", true, "{R}.CopyTo(Target, {I});", "{R}.CopyTo(Target, {I});", true),
        new(2, "Clear", true, "Array.Clear({R});", "{R}.Clear();", true)
    ];

    /// <summary>A way of a case: its label, the id of its root, the code of its root's body and helpers, the interface member it goes
    /// through, where it goes through one, and whether the singleton's own fields its statement reads are compared too.</summary>
    private sealed record Way(string Label, string Id, string Code, string? Through = null, bool OwnFields = true);

    /// <summary>A case of the member theory: the collection the singleton holds in <c>Box</c>, how it is made and filled, its canonical
    /// way and its other ways.</summary>
    private sealed record Case(string Name, string BoxType, string Make, string Fill, Way Canonical, IReadOnlyList<Way> Ways);

    private static readonly Lazy<IReadOnlyDictionary<string, Case>> Cases = new(BuildCases);

    private static IReadOnlyDictionary<string, Case> BuildCases()
    {
        var cases = new List<Case>();
        foreach (var memberCase in MemberCases)
        {
            var (boxType, make, fill) = BoxOf(memberCase.Type);
            string Line(string receiver, string _) => $"{MARK} {memberCase.Statement.Replace("{R}", receiver, StringComparison.Ordinal)}";
            var ways = ReceiverWays("", "", boxType, Line).ToList();
            if (memberCase.Through is null)
            {
                ways.AddRange(InterfaceWays(memberCase).Select((way, index) =>
                {
                    var receiver = $"(({way.Interface})Box)";
                    var line = way.Interface == NON_GENERIC_DICTIONARY && memberCase.EntryStatement is { } entry
                        ? $"{MARK} {entry.Replace("{R}", receiver, StringComparison.Ordinal)}"
                        : Line(receiver, "0");
                    return new Way(way.Label, $"I{index}", Body($"I{index}", line.Replace($".{memberCase.Member}(", $".{way.Name}(", StringComparison.Ordinal)),
                                   way.Through);
                }));
            }

            cases.Add(new Case(memberCase.Name, boxType, make, fill, new Way("field", "Field", Body("Field", Line("Box", "0")), memberCase.Through),
                               ways));
        }

        // An array is compared with the same code on the array directly in the field that holds it (R5, R6). A route takes the array
        // on a line of its own, where the code on the field reads the field in the statement: the singleton's own fields are left out
        // on both sides there, and the index a parameter carries is bound where the call stands.
        foreach (var arrayCase in ArrayCases)
        {
            var (boxType, make) = arrayCase.Rank == 1 ? ("Item[]", "new Item[] { new Item() }") : ("int[,]", "new int[2, 2]");
            string Direct(string receiver, string index) =>
                $"{MARK} {arrayCase.Canonical.Replace("{R}", receiver, StringComparison.Ordinal).Replace("{I}", index, StringComparison.Ordinal)}";
            var ways = new List<Way>();
            foreach (var (way, index) in ArrayInterfaces(arrayCase).Select((way, index) => (way, index)))
            {
                var prefix = $"I{index}";
                string Through(string receiver, string at) =>
                    $"{MARK} {arrayCase.Statement.Replace("{R}", $"(({way.Interface}){receiver})", StringComparison.Ordinal).Replace("{I}", at, StringComparison.Ordinal)}";
                ways.Add(new Way(way.Label, prefix, Body(prefix, Through("Box", "0")), way.Through));
                ways.AddRange(ReceiverWays(prefix, $" through {way.Label}", boxType, Through)
                                  .Select(route => route with { Through = way.Through, OwnFields = false }));
            }

            cases.Add(new Case(arrayCase.Name, boxType, make, "", new Way("array", "Field", Body("Field", Direct("Box", "0"))), ways));
        }

        return cases.ToDictionary(@case => @case.Name, StringComparer.Ordinal);
    }

    /// <summary>The routes of R3 a receiver takes to its statement, <paramref name="line"/> written over a receiver and an index: a local,
    /// a parameter — which takes the index as a parameter of its own too — a helper's result, a cell of another collection, a merge, a
    /// static field and a capture. Ids and labels are prefixed and suffixed as given.</summary>
    private static IEnumerable<Way> ReceiverWays(string prefix, string suffix, string boxType, Func<string, string, string> line)
    {
        Way Of(string label, string id, string code) => new(label + suffix, prefix + id, code);

        yield return Of("local", "Local", Body($"{prefix}Local", $"var r = Box;\n{line("r", "0")}"));
        yield return Of("parameter", "Parameter", $"public void Route_{prefix}Parameter() => Route_{prefix}Parameter_Use(Box, 0);\n" +
                                                  $"public void Route_{prefix}Parameter_Use({boxType} r, int i)\n{{\n{line("r", "i")}\n}}");
        yield return Of("helper result", "Helper", Body($"{prefix}Helper", $"var r = Route_{prefix}Helper_Get();\n{line("r", "0")}") +
                                                   $"\npublic {boxType} Route_{prefix}Helper_Get() => Box;");
        yield return Of("cell of another collection", "Cell", Body($"{prefix}Cell", $"var r = Holders[0];\n{line("r", "0")}"));
        yield return Of("merge", "Merge", Body($"{prefix}Merge", $"var r = Flag ? Box : Route_{prefix}Merge_Get();\n{line("r", "0")}") +
                                          $"\npublic {boxType} Route_{prefix}Merge_Get() => Box;");
        yield return Of("static field", "Static", Body($"{prefix}Static", line("Shared!", "0")));
        yield return Of("capture", "Capture", Body($"{prefix}Capture", $"var r = Box;\nvoid Local()\n{{\n{line("r", "0")}\n}}\nLocal();"));
    }

    private static string Body(string id, string code) => $"public void Route_{id}()\n{{\n{code}\n}}";

    private static (string Type, string Make, string Fill) BoxOf(string type) => type switch
    {
        LIST => ("List<Item>", "new List<Item>()", "Box.Add(new Item());"),
        DICTIONARY => ("Dictionary<string, Item>", "new Dictionary<string, Item>()", "Box[\"a\"] = new Item();"),
        CONCURRENT_DICTIONARY => ("ConcurrentDictionary<string, Item>", "new ConcurrentDictionary<string, Item>()", "Box[\"a\"] = new Item();"),
        CONCURRENT_QUEUE => ("ConcurrentQueue<Item>", "new ConcurrentQueue<Item>()", "Box.Enqueue(new Item());"),
        CONCURRENT_STACK => ("ConcurrentStack<Item>", "new ConcurrentStack<Item>()", "Box.Push(new Item());"),
        CONCURRENT_BAG => ("ConcurrentBag<Item>", "new ConcurrentBag<Item>()", "Box.Add(new Item());"),
        HASH_SET => ("HashSet<Item>", "new HashSet<Item>()", "Box.Add(new Item());"),
        QUEUE => ("Queue<Item>", "new Queue<Item>()", "Box.Enqueue(new Item());"),
        STACK => ("Stack<Item>", "new Stack<Item>()", "Box.Push(new Item());"),
        LINKED_LIST => ("LinkedList<Item>", "new LinkedList<Item>()", "Box.AddLast(new Item()); Box.AddLast(First);"),
        KEY_COLLECTION => ("Dictionary<string, Item>.KeyCollection", "Map.Keys", ""),
        VALUE_COLLECTION => ("Dictionary<string, Item>.ValueCollection", "Map.Values", ""),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static TheoryData<string, string> MemberRows()
    {
        var data = new TheoryData<string, string>();
        foreach (var @case in Cases.Value.Values)
        foreach (var way in @case.Ways)
            data.Add(@case.Name, way.Label);
        return data;
    }

    [Theory]
    [MemberData(nameof(MemberRows))]
    public void Member_reached_any_way_makes_what_it_makes_on_the_field(string name, string way)
    {
        var @case = Cases.Value[name];
        var run = Run(name, () => MemberSource(@case));
        var chosen = @case.Ways.Single(candidate => candidate.Label == way);
        var canonical = Seen(run, "Field", marked: true);

        // A way never agrees with a canonical way that does nothing: a view of a Dictionary is enumerated where it is taken, and the code
        // on an array a member it refuses or answers without touching it stands for still reads the array and its arguments.
        Assert.True(canonical.Any(access => !access.StartsWith("gaps ", StringComparison.Ordinal)), $"{name} does nothing on the field");
        AssertSame(chosen.OwnFields ? canonical : Seen(run, "Field", marked: true, ownFields: false),
                   Seen(run, chosen.Id, marked: true, ownFields: chosen.OwnFields));
    }

    /// <summary>The singleton of a member case: the collection in <c>Box</c>, another collection holding it in its cells, a static field
    /// holding it, and one root per way.</summary>
    private static string MemberSource(Case @case)
    {
        var ways = new[] { @case.Canonical }.Concat(@case.Ways).ToArray();
        return $$"""
            using System.Collections.Concurrent;
            using System.Collections.Generic;

            public class Item
            {
                public int Hits;
            }

            public sealed class State
            {
                public static {{@case.BoxType}}? Shared;
                public readonly Item First = new();
                public readonly Item Second = new();
                public readonly List<Item> Others = new() { new Item() };
                public readonly Dictionary<string, Item> Map = new() { ["a"] = new Item() };
                public readonly Item[] Target = new Item[4];
                public readonly {{@case.BoxType}} Box;
                public readonly List<{{@case.BoxType}}> Holders = new();
                public bool Flag = Environment.ProcessorCount > 1;

                public State()
                {
                    Box = {{@case.Make}};
                    {{@case.Fill}}
                    Holders.Add(Box);
                    Shared = Box;
                }

            {{string.Join("\n\n", ways.Select(way => way.Code))}}
            }

            {{Controller(ways.Select(way => way.Id))}}
            """ + Startup("services.AddSingleton<State>();");
    }

    // ---- interfaces a receiver exposes ----

    private static readonly Lazy<Compilation> Probe = new(() =>
        FixtureSolution.Create(("Case.cs", "public class Item { public int Hits; }")).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!);

    /// <summary>The closed type a case's receiver is.</summary>
    private static INamedTypeSymbol Closed(string type)
    {
        var compilation = Probe.Value;
        var item = compilation.GetTypeByMetadataName("Item")!;
        var @string = compilation.GetSpecialType(SpecialType.System_String);
        var definition = compilation.GetTypeByMetadataName(type)!;
        if (type is KEY_COLLECTION or VALUE_COLLECTION)
            return compilation.GetTypeByMetadataName(DICTIONARY)!.Construct(@string, item).GetTypeMembers(definition.Name).Single();
        return definition.Arity == 2 ? definition.Construct(@string, item) : definition.Construct(item);
    }

    /// <summary>What an object of <paramref name="type"/> is to the map of R4.</summary>
    private static string KindOf(string type) => type switch
    {
        KEY_COLLECTION => "System.Collections.Generic.Dictionary.KeyCollection",
        VALUE_COLLECTION => "System.Collections.Generic.Dictionary.ValueCollection",
        _ => type[..type.IndexOf('`')]
    };

    private static string MemberId(string kind, IMethodSymbol member) => $"{kind}: {member.ContainingType.OriginalDefinition.ToDisplayString()}.{member.Name}";

    /// <summary>The interfaces through which the map decides a call as this case's member, whose member takes the case's arguments, each
    /// with the name its member has there: an explicit implementation may call a public member of another name, as
    /// <c>IDictionary.Contains</c> calls <c>ContainsKey</c>.</summary>
    private static IEnumerable<(string Label, string Interface, string Through, string Name)> InterfaceWays(MemberCase memberCase)
    {
        var closed = Closed(memberCase.Type);
        var kind = KindOf(memberCase.Type);
        var publics = closed.GetMembers(memberCase.Member).OfType<IMethodSymbol>().Where(method => method.Parameters.Length == memberCase.Arity).ToArray();
        foreach (var @interface in closed.AllInterfaces)
        {
            var member = @interface.GetMembers().OfType<IMethodSymbol>()
                                   .FirstOrDefault(candidate => Decides(candidate, kind) == $"{memberCase.Type}.{memberCase.Member}" &&
                                                                candidate.Parameters.Length == memberCase.Arity &&
                                                                publics.Any(method => Takes(candidate, method)));
            if (member is not null)
                yield return (@interface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), @interface.ToDisplayString(), MemberId(kind, member),
                              member.Name);
        }
    }

    /// <summary>The interfaces of an array through which a member is decided as the case says, or left undecided where it says so.</summary>
    /// <param name="arrayCase">The array member case whose interface routes are selected.</param>
    private static IEnumerable<(string Label, string Interface, string Through)> ArrayInterfaces(ArrayCase arrayCase)
    {
        var compilation = Probe.Value;
        var array = arrayCase.Rank == 1
            ? compilation.CreateArrayTypeSymbol(compilation.GetTypeByMetadataName("Item")!, 1)
            : compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Int32), 2);
        foreach (var @interface in array.AllInterfaces)
        {
            if (@interface.GetMembers(arrayCase.Member).OfType<IMethodSymbol>().FirstOrDefault() is { } member &&
                Decides(member, arrayCase.Kind) is not null == arrayCase.Decided &&
                (arrayCase.Member != "Clear" ||
                    (CollectionObjects.Decision(IrLowering.Collections.ImplementationsOf(member, compilation), arrayCase.Kind)?.Library is not null) == arrayCase.DirectCall))
            {
                yield return (@interface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), @interface.ToDisplayString(), MemberId(arrayCase.Kind, member));
            }
        }
    }

    private static string? Decides(IMethodSymbol member, string kind) =>
        CollectionObjects.Decision(IrLowering.Collections.ImplementationsOf(member, Probe.Value), kind)?.Member.Member;

    /// <summary>Whether a call of the interface member with the arguments the public member takes compiles: each parameter is the same
    /// type, or <c>object</c>.</summary>
    private static bool Takes(IMethodSymbol member, IMethodSymbol method) =>
        member.Parameters.Zip(method.Parameters).All(pair => SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type) ||
                                                            pair.First.Type.SpecialType == SpecialType.System_Object);

    // ---- nodes ----

    private static readonly (string Member, string Statement)[] NodeMembers =
    [
        ("get_Value", "({N}.Value as Item)!.Hits = 1;"),
        ("set_Value", "{N}.Value = Second;"),
        ("get_Next", "_ = {N}.Next;"),
        ("get_Previous", "_ = {N}.Previous;")
    ];

    /// <summary>The ways a node reaches a member of it, the one where it was taken first: the members that hand one out, a helper's
    /// result, a field, a parameter, a merge and a capture.</summary>
    private static readonly (string Label, string Id, Func<Func<string, string>, string> Code)[] NodeWays =
    [
        ("where it was taken", "Field", line => Body("Field", $"var n = Box.First!;\n{line("n")}")),
        ("handed out by Last", "Last", line => Body("Last", $"var n = Box.Last!;\n{line("n")}")),
        ("handed out by Find", "Find", line => Body("Find", $"var n = Box.Find(First)!;\n{line("n")}")),
        ("handed out by AddLast", "Added", line => Body("Added", $"var n = Box.AddLast(Second);\n{line("n")}")),
        ("helper result", "Helper", line => Body("Helper", $"var n = Route_Helper_Get();\n{line("n")}") + "\npublic LinkedListNode<Item> Route_Helper_Get() => Box.First!;"),
        ("field", "Kept", line => Body("Kept", line("Kept"))),
        ("parameter", "Parameter", line => "public void Route_Parameter() => Route_Parameter_Use(Box.First!);\n" +
                                            $"public void Route_Parameter_Use(LinkedListNode<Item> n)\n{{\n{line("n")}\n}}"),
        ("merge", "Merge", line => Body("Merge", $"var n = Flag ? Box.First! : Box.Last!;\n{line("n")}")),
        ("capture", "Capture", line => Body("Capture", $"var n = Box.First!;\nvoid Local()\n{{\n{line("n")}\n}}\nLocal();"))
    ];

    public static TheoryData<string, string> NodeRows()
    {
        var data = new TheoryData<string, string>();
        foreach (var (member, _) in NodeMembers)
        foreach (var way in NodeWays.Skip(1))
            data.Add(member, way.Label);
        return data;
    }

    [Theory]
    [MemberData(nameof(NodeRows))]
    public void Node_reached_any_way_makes_what_it_makes_where_it_was_taken(string member, string way)
    {
        var statement = NodeMembers.Single(candidate => candidate.Member == member).Statement;
        var run = Run($"node {member}", () =>
        {
            var line = (string node) => $"{MARK} {statement.Replace("{N}", node, StringComparison.Ordinal)}";
            return $$"""
                using System.Collections.Generic;

                public class Item
                {
                    public int Hits;
                }

                public sealed class State
                {
                    public readonly Item First = new();
                    public readonly Item Second = new();
                    public readonly LinkedList<Item> Box = new();
                    public readonly LinkedListNode<Item> Kept;
                    public bool Flag = Environment.ProcessorCount > 1;

                    public State()
                    {
                        Box.AddLast(new Item());
                        Box.AddLast(First);
                        Kept = Box.First!;
                    }

                {{string.Join("\n\n", NodeWays.Select(candidate => candidate.Code(line)))}}
                }

                {{Controller(NodeWays.Select(candidate => candidate.Id))}}
                """ + Startup("services.AddSingleton<State>();");
        });

        var taken = Seen(run, "Field", marked: true);
        Assert.Contains(taken, access => !access.StartsWith("gaps ", StringComparison.Ordinal));
        AssertSame(taken, Seen(run, NodeWays.Single(candidate => candidate.Label == way).Id, marked: true));
    }

    // ---- snapshots ----

    /// <summary>The line a snapshot is used on in the body that took it, which each of its ways is compared with.</summary>
    private const string TAKEN = "/*T*/";

    /// <summary>What a snapshot is counted and enumerated by, over <c>{S}</c>.</summary>
    private static readonly (string Member, string Statement)[] SnapshotMembers =
    [
        ("Count", "_ = {S}.Count;"),
        ("enumeration", "foreach (var key in {S}) { }")
    ];

    /// <summary>A way the snapshot a body took reaches its count or its enumeration: its label, the id of its root, the member it is a way
    /// of (null for both), the code after the use in the taking body — given the root's id and the marked use over a receiver — with any
    /// helper it needs, and the interface member it goes through where it names one of its own.</summary>
    private sealed record SnapshotRoute(string Label, string Id, string? Only, Func<string, Func<string, string>, (string After, string Helper)> Code,
                                        string? Through = null);

    private static readonly SnapshotRoute[] SnapshotRoutes =
    [
        new("local", "Local", null, (_, use) => ($"var copy = s;\n{use("copy")}", "")),
        new("merge", "Merge", null, (id, use) => ($"var merged = Flag ? s : Route_{id}_Same(s);\n{use("merged")}",
                                                  $"public ICollection<string> Route_{id}_Same(ICollection<string> x) => x;")),
        new("field", "Field", null, (id, use) => (use($"Kept{id}!"), "")),
        new("ICollection parameter", "Parameter", null, (id, use) => ($"Route_{id}_Use(s);",
                                                                      $"public void Route_{id}_Use(ICollection<string> x)\n{{\n{use("x")}\n}}")),
        new("IEnumerable parameter", "Enumerable", "enumeration", (id, use) => ($"Route_{id}_Use(s);",
                                                                                $"public void Route_{id}_Use(IEnumerable<string> x)\n{{\n{use("x")}\n}}")),
        new("helper result", "Helper", null, (id, use) => ($"var got = Route_{id}_Get();\n{use("got")}",
                                                          $"public ICollection<string> Route_{id}_Get() => Kept{id}!;")),
        new("IReadOnlyCollection", "ReadOnly", "Count", (_, use) => (use("((IReadOnlyCollection<string>)s)"), ""),
            $"{CollectionObjects.SNAPSHOT}: System.Collections.Generic.IReadOnlyCollection<T>.get_Count"),
        new("ICollection", "Untyped", "Count", (_, use) => (use("((System.Collections.ICollection)s)"), ""),
            $"{CollectionObjects.SNAPSHOT}: System.Collections.ICollection.get_Count"),
        new("IEnumerable", "UntypedEnumerable", "enumeration", (_, use) => (use("((System.Collections.IEnumerable)s)"), ""),
            $"{CollectionObjects.SNAPSHOT}: System.Collections.IEnumerable.GetEnumerator")
    ];

    /// <summary>What the snapshot rows name of the map: a snapshot's count through <c>ICollection&lt;T&gt;</c> and its enumeration
    /// through <c>IEnumerable&lt;T&gt;</c>, which the use in the taking body and every way but the untyped ones call.</summary>
    private static readonly string[] SnapshotThrough =
    [
        $"{CollectionObjects.SNAPSHOT}: System.Collections.Generic.ICollection<T>.get_Count",
        $"{CollectionObjects.SNAPSHOT}: System.Collections.Generic.IEnumerable<T>.GetEnumerator"
    ];

    public static TheoryData<string, string> SnapshotRows()
    {
        var data = new TheoryData<string, string>();
        foreach (var (member, _) in SnapshotMembers)
        foreach (var route in SnapshotRoutes.Where(route => route.Only is null || route.Only == member))
            data.Add(member, route.Label);
        return data;
    }

    /// <summary>Each way takes a snapshot of its own and has a field of its own hold it, so that the snapshot's list is a resource; it uses
    /// the snapshot in the body that took it, then through the way. The two uses are of one object, and make the same accesses by
    /// identity: the snapshot's own list, never the dictionary it was taken from, nor another collection that looks alike.</summary>
    [Theory]
    [MemberData(nameof(SnapshotRows))]
    public void Snapshot_reached_any_way_is_counted_and_enumerated_as_its_own_list(string member, string way)
    {
        var id = SnapshotRoutes.Single(candidate => candidate.Label == way).Id;
        var run = Run($"snapshot {member}", () => SnapshotSource(member));
        // The field that holds the snapshot is read where the field way takes it, never where the taking body uses its local.
        var taken = Seen(run, id, marked: true, ownFields: false, taken: true);

        Assert.Contains(taken, access => !access.StartsWith("gaps ", StringComparison.Ordinal));
        Assert.DoesNotContain(taken, access => access.Contains("Concurrent", StringComparison.Ordinal));
        AssertSame(taken, Seen(run, id, marked: true, ownFields: false));
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Root.Symbol.EndsWith($".{id}()", StringComparison.Ordinal) &&
                                                                 access.Operation.IsUnknownEffect());
        Assert.Contains("gaps 0", taken);
    }

    private static string SnapshotSource(string member)
    {
        var statement = SnapshotMembers.Single(candidate => candidate.Member == member).Statement;
        var routes = SnapshotRoutes.Where(route => route.Only is null || route.Only == member).ToArray();
        var methods = routes.Select(route =>
        {
            var (rest, helper) = route.Code(route.Id, receiver => $"{MARK} {statement.Replace("{S}", receiver, StringComparison.Ordinal)}");
            return $"public ICollection<string>? Kept{route.Id};\n\n" +
                   Body(route.Id, $"var s = Concurrent.Keys;\nKept{route.Id} = s;\n{TAKEN} {statement.Replace("{S}", "s", StringComparison.Ordinal)}\n{rest}") +
                   (helper.Length == 0 ? "" : $"\n{helper}");
        });
        return $$"""
            using System.Collections.Concurrent;
            using System.Collections.Generic;

            public class Item
            {
                public int Hits;
            }

            public sealed class State
            {
                public readonly ConcurrentDictionary<string, Item> Concurrent = new();
                public bool Flag = Environment.ProcessorCount > 1;

                public State() => Concurrent["a"] = new Item();

            {{string.Join("\n\n", methods)}}
            }

            {{Controller(routes.Select(route => route.Id))}}
            """ + Startup("services.AddSingleton<State>();");
    }

    // ---- factories ----

    /// <summary>Each overload of <c>GetOrAdd</c> and <c>AddOrUpdate</c> that takes a factory, with <c>{F}</c> an add factory,
    /// <c>{G}</c> one taking the overload's argument, <c>{U}</c> an update factory and <c>{V}</c> one taking the argument.</summary>
    private static readonly (string Overload, string Statement)[] FactoryOverloads =
    [
        ("GetOrAdd(key, valueFactory)", "(Box.GetOrAdd(\"a\", {F}) as Item)!.Hits = 1;"),
        ("GetOrAdd(key, valueFactory, factoryArgument)", "(Box.GetOrAdd(\"a\", {G}, 0) as Item)!.Hits = 1;"),
        ("AddOrUpdate(key, addValueFactory, updateValueFactory)", "(Box.AddOrUpdate(\"a\", {F}, {U}) as Item)!.Hits = 1;"),
        ("AddOrUpdate(key, addValue, updateValueFactory)", "(Box.AddOrUpdate(\"a\", First, {U}) as Item)!.Hits = 1;"),
        ("AddOrUpdate(key, addValueFactory, updateValueFactory, factoryArgument)", "(Box.AddOrUpdate(\"a\", {G}, {V}, 0) as Item)!.Hits = 1;")
    ];

    private const string LAMBDA_F = "key => Build(key)";
    private const string LAMBDA_G = "(key, argument) => Build(key)";
    private const string LAMBDA_U = "(key, old) => Build(key)";
    private const string LAMBDA_V = "(key, old, argument) => Build(key)";

    /// <summary>The ways a factory is given, the lambda first: each fills the four slots of a statement.</summary>
    private static readonly (string Label, string Id, Func<string, string> Code)[] FactoryWays =
    [
        ("lambda", "Lambda", statement => Body("Lambda", Line(statement, LAMBDA_F, LAMBDA_G, LAMBDA_U, LAMBDA_V))),
        ("static lambda", "Static", statement => Body("Static", Line(statement, $"static {LAMBDA_F}", $"static {LAMBDA_G}", $"static {LAMBDA_U}", $"static {LAMBDA_V}"))),
        ("instance method group", "Instance", statement => Body("Instance", Line(statement, "MakeF", "MakeG", "MakeU", "MakeV"))),
        ("static method group", "StaticGroup", statement => Body("StaticGroup", Line(statement, "StaticF", "StaticG", "StaticU", "StaticV"))),
        ("local function", "LocalFunction", statement => Body("LocalFunction",
            "Item LocalF(string key) => Build(key);\nItem LocalG(string key, int argument) => Build(key);\n" +
            "Item LocalU(string key, Item old) => Build(key);\nItem LocalV(string key, Item old, int argument) => Build(key);\n" +
            Line(statement, "LocalF", "LocalG", "LocalU", "LocalV"))),
        ("delegate from a field", "Field", statement => Body("Field", Line(statement, "FieldF", "FieldG", "FieldU", "FieldV"))),
        ("delegate from a parameter", "Parameter", statement =>
            $"public void Route_Parameter() => Route_Parameter_Use({LAMBDA_F}, {LAMBDA_G}, {LAMBDA_U}, {LAMBDA_V});\n" +
            "public void Route_Parameter_Use(Func<string, Item> f, Func<string, int, Item> g, Func<string, Item, Item> u, Func<string, Item, int, Item> v)\n" +
            $"{{\n{Line(statement, "f", "g", "u", "v")}\n}}"),
        ("delegate from a helper's result", "Helper", statement => Body("Helper", Line(statement, "GetF()", "GetG()", "GetU()", "GetV()")))
    ];

    private static string Line(string statement, string f, string g, string u, string v) =>
        $"{MARK} {statement.Replace("{F}", f).Replace("{G}", g).Replace("{U}", u).Replace("{V}", v)}";

    public static TheoryData<string, string> FactoryRows()
    {
        var data = new TheoryData<string, string>();
        foreach (var (overload, _) in FactoryOverloads)
        foreach (var way in FactoryWays.Skip(1))
            data.Add(overload, way.Label);
        return data;
    }

    [Theory]
    [MemberData(nameof(FactoryRows))]
    public void Factory_given_any_way_runs_as_a_lambda_does(string overload, string way)
    {
        var statement = FactoryOverloads.Single(candidate => candidate.Overload == overload).Statement;
        var run = Run($"factory {overload}", () => $$"""
            using System.Collections.Concurrent;

            public class Item
            {
                public int Hits;
            }

            public sealed class Tally
            {
                public int Count;
            }

            public sealed class State
            {
                public static readonly Tally Counter = new();
                public readonly Item First = new();
                public readonly ConcurrentDictionary<string, Item> Box = new();
                public readonly Func<string, Item> FieldF = {{LAMBDA_F}};
                public readonly Func<string, int, Item> FieldG = {{LAMBDA_G}};
                public readonly Func<string, Item, Item> FieldU = {{LAMBDA_U}};
                public readonly Func<string, Item, int, Item> FieldV = {{LAMBDA_V}};

                public State() => Box["a"] = new Item();

                public static Item Build(string key)
                {
                    Counter.Count++;
                    return new Item();
                }

                public Item MakeF(string key) => Build(key);
                public Item MakeG(string key, int argument) => Build(key);
                public Item MakeU(string key, Item old) => Build(key);
                public Item MakeV(string key, Item old, int argument) => Build(key);
                public static Item StaticF(string key) => Build(key);
                public static Item StaticG(string key, int argument) => Build(key);
                public static Item StaticU(string key, Item old) => Build(key);
                public static Item StaticV(string key, Item old, int argument) => Build(key);
                public Func<string, Item> GetF() => {{LAMBDA_F}};
                public Func<string, int, Item> GetG() => {{LAMBDA_G}};
                public Func<string, Item, Item> GetU() => {{LAMBDA_U}};
                public Func<string, Item, int, Item> GetV() => {{LAMBDA_V}};

            {{string.Join("\n\n", FactoryWays.Select(candidate => candidate.Code(statement)))}}
            }

            {{Controller(FactoryWays.Select(candidate => candidate.Id))}}
            """ + Startup("services.AddSingleton<State>();"));

        // What a factory does runs in its own body, and what it makes is made there, an object of the factory that made it: the whole
        // root is compared by identity. Every way adds to one box, so each root writes through what the box hands out on the object each
        // factory of every way made, one apiece, and a factory whose object never reaches the box fails every row (review F-0041).
        var lambda = Seen(run, "Lambda", marked: false, ownFields: false);
        AssertSame(lambda, Seen(run, FactoryWays.Single(candidate => candidate.Label == way).Id, marked: false, ownFields: false));
        Assert.Contains(lambda, access => access.Contains("Tally.Count:Field ", StringComparison.Ordinal));
        var factories = new[] { "{F}", "{G}", "{U}", "{V}" }.Count(slot => statement.Contains(slot, StringComparison.Ordinal));
        Assert.Equal(factories * FactoryWays.Length,
                     lambda.Count(access => access.Contains("State.Build(System.String)", StringComparison.Ordinal) &&
                                            access.EndsWith("Item.Hits:Field write", StringComparison.Ordinal)));
    }

    // ---- compound forms ----

    /// <summary>A target kind of R1: the field holding its receiver and its type, the target on a receiver <c>{r}</c>, and its long form
    /// with the receiver and the index taken into locals once, <c>{op}</c> the operator and <c>{v}</c> the operand.</summary>
    private sealed record Target(string Kind, string Field, string Type, string Place, string LongForm);

    private static readonly Target[] Targets =
    [
        new("array element", "Cells", "int[]", "{r}[Index]", "var a = {r}; var i = Index; a[i] = a[i] {op} {v};"),
        new("multidimensional array element", "Grid", "int[,]", "{r}[Index, 1]", "var a = {r}; var i = Index; a[i, 1] = a[i, 1] {op} {v};"),
        new("List indexer", "Numbers", "List<int>", "{r}[Index]", "var a = {r}; var i = Index; a[i] = a[i] {op} {v};"),
        new("Dictionary indexer", "Counts", "Dictionary<string, int>", "{r}[\"a\"]", "var d = {r}; d[\"a\"] = d[\"a\"] {op} {v};"),
        new("ConcurrentDictionary indexer", "Safe", "ConcurrentDictionary<string, int>", "{r}[\"a\"]", "var d = {r}; d[\"a\"] = d[\"a\"] {op} {v};"),
        new("source indexer", "Board", "Board", "{r}[Index]", "var b = {r}; var i = Index; b[i] = b[i] {op} {v};"),
        new("property with accessor bodies", "Gauge", "Gauge", "{r}.Level", "var g = {r}; g.Level = g.Level {op} {v};"),
        new("virtual property", "Virtual", "Base", "{r}.Level", "var g = {r}; g.Level = g.Level {op} {v};"),
        new("abstract property", "Abstract", "Shape", "{r}.Level", "var g = {r}; g.Level = g.Level {op} {v};"),
        new("interface property", "Iface", "IGauge", "{r}.Level", "var g = {r}; g.Level = g.Level {op} {v};"),
        new("static computed property", "", "", "Total", "Total = Total {op} {v};")
    ];

    /// <summary>The operators, as the compound form of a target <c>{t}</c> and the operator and operand of its long form.</summary>
    private static readonly (string Label, string Compound, string Operator, string Operand)[] Operators =
    [
        ("prefix ++", "++{t};", "+", "1"),
        ("postfix ++", "{t}++;", "+", "1"),
        ("prefix --", "--{t};", "-", "1"),
        ("postfix --", "{t}--;", "-", "1"),
        ("+=", "{t} += 2;", "+", "2"),
        ("-=", "{t} -= 2;", "-", "2"),
        ("|=", "{t} |= 2;", "|", "2")
    ];

    /// <summary>The routes a receiver of a compound form takes: the field, and every route of R3.</summary>
    private static readonly string[] CompoundRoutes = ["field", .. ReceiverRoutes];

    public static TheoryData<string, string, string> CompoundRows()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var target in Targets)
        foreach (var (label, _, _, _) in Operators)
        foreach (var route in target.Field.Length == 0 ? CompoundRoutes.Take(1) : CompoundRoutes)
            data.Add(target.Kind, label, route);
        return data;
    }

    [Theory]
    [MemberData(nameof(CompoundRows))]
    public void Compound_form_makes_what_its_long_form_makes(string kind, string @operator, string route)
    {
        var target = Targets.Single(candidate => candidate.Kind == kind);
        var run = Run($"compound {kind}", () => CompoundSource(target));
        var id = CompoundId(Array.FindIndex(Operators, candidate => candidate.Label == @operator), Array.IndexOf(CompoundRoutes, route));

        var compound = Seen(run, $"{id}_Compound", marked: false);
        Assert.NotEmpty(compound);
        AssertSame(Seen(run, $"{id}_Long", marked: false), compound);
    }

    private static string CompoundId(int @operator, int route) => $"C{@operator}R{route}";

    private static string CompoundSource(Target target)
    {
        var methods = new List<string>();
        var ids = new List<string>();
        for (var @operator = 0; @operator < Operators.Length; @operator++)
        for (var route = 0; route < (target.Field.Length == 0 ? 1 : CompoundRoutes.Length); route++)
        {
            var (_, compound, symbol, operand) = Operators[@operator];
            foreach (var form in new[] { "Compound", "Long" })
            {
                var id = $"{CompoundId(@operator, route)}_{form}";
                // The receiver as each route hands it over: the field, a local, a parameter, a helper's result, a cell of another
                // collection, a merge, a static field and a capture.
                var receiver = CompoundRoutes[route] switch
                {
                    "field" => target.Field,
                    "helper result" => $"Get{target.Field}()",
                    "static field" => "Shared!",
                    _ => "r"
                };
                var statement = form == "Compound"
                    ? compound.Replace("{t}", target.Place.Replace("{r}", receiver))
                    : target.LongForm.Replace("{r}", receiver).Replace("{op}", symbol).Replace("{v}", operand);
                ids.Add(id);
                methods.Add(CompoundRoutes[route] switch
                {
                    "local" => Body(id, $"var r = {target.Field};\n{statement}"),
                    "parameter" => $"public void Route_{id}() => Route_{id}_Use({target.Field});\npublic void Route_{id}_Use({target.Type} r)\n{{\n{statement}\n}}",
                    "cell of another collection" => Body(id, $"var r = Holders[0];\n{statement}"),
                    "merge" => Body(id, $"var r = Flag ? {target.Field} : Get{target.Field}();\n{statement}"),
                    "capture" => Body(id, $"var r = {target.Field};\nvoid Local()\n{{\n{statement}\n}}\nLocal();"),
                    _ => Body(id, statement)
                });
            }
        }

        return $$"""
            using System.Collections.Concurrent;
            using System.Collections.Generic;

            public sealed class Board
            {
                private readonly int[] _cells = new int[4];
                public int this[int index] { get => _cells[index]; set => _cells[index] = value; }
            }

            public sealed class Gauge
            {
                private int _level;
                public int Level { get { return _level; } set { _level = value; } }
            }

            public class Base
            {
                private int _level;
                public virtual int Level { get => _level; set => _level = value; }
            }

            public interface IGauge
            {
                int Level { get; set; }
            }

            public sealed class OtherGauge : IGauge
            {
                private int _level;
                public int Level { get => _level; set => _level = value; }
            }

            public abstract class Shape
            {
                public abstract int Level { get; set; }
            }

            public sealed class Square : Shape
            {
                private int _level;
                public override int Level { get => _level; set => _level = value; }
            }

            public sealed class State
            {
                private static int _total;
                public bool Flag = Environment.ProcessorCount > 1;
                public readonly int[] Cells = new int[4];
                public readonly int[,] Grid = new int[4, 4];
                public readonly List<int> Numbers = new() { 0, 0, 0, 0 };
                public readonly Dictionary<string, int> Counts = new() { ["a"] = 0 };
                public readonly ConcurrentDictionary<string, int> Safe = new();
                public readonly Board Board = new();
                public readonly Gauge Gauge = new();
                public readonly Base Virtual = new();
                public readonly IGauge Iface = new OtherGauge();
                public readonly Shape Abstract = new Square();
                public int Index = Environment.ProcessorCount % 4;
                {{(target.Field.Length == 0 ? "" : $"public static {target.Type}? Shared;\n    public readonly List<{target.Type}> Holders = new();")}}

                public State()
                {
                    Safe["a"] = 0;
                    {{(target.Field.Length == 0 ? "" : $"Holders.Add({target.Field});\n        Shared = {target.Field};")}}
                }

                public static int Total { get => _total; set => _total = value; }

                {{(target.Field.Length == 0 ? "" : $"public {target.Type} Get{target.Field}() => {target.Field};")}}

            {{string.Join("\n\n", methods)}}
            }

            {{Controller(ids)}}
            """ + Startup("services.AddSingleton<State>();");
    }

    // ---- the facts ----

    /// <summary>Every target R1 names has a compound row for every operator and every route its receiver allows — a static computed
    /// property has no receiver, and only the field route. R1's list is written here apart from the targets the rows are built from, so a
    /// target missing from those is a row missing here.</summary>
    [Fact]
    public void Compound_matrix_names_every_target_operator_and_route()
    {
        string[] kinds =
        [
            "array element", "multidimensional array element", "List indexer", "Dictionary indexer", "ConcurrentDictionary indexer", "source indexer",
            "property with accessor bodies", "virtual property", "abstract property", "interface property", "static computed property"
        ];
        string[] operators = ["prefix ++", "postfix ++", "prefix --", "postfix --", "+=", "-=", "|="];
        string[] routes = ["field", "local", "parameter", "helper result", "cell of another collection", "merge", "static field", "capture"];
        var rows = CompoundRows().Select(row => $"{row[0]} | {row[1]} | {row[2]}").ToHashSet(StringComparer.Ordinal);

        var missing = kinds.SelectMany(kind => operators.SelectMany(@operator => (kind == "static computed property" ? routes.Take(1) : routes)
                                                                              .Select(route => $"{kind} | {@operator} | {route}")))
                           .Where(row => !rows.Contains(row))
                           .ToArray();
        Assert.True(missing.Length == 0, $"compound rows missing: {string.Join("; ", missing)}");
    }

    /// <summary>Every member the table models, on every type and view, has a member case or a node row; constructors, the members of a
    /// pair and the node's constructor have no receiver to take a route, and are left to <c>CollectionMemberEffectTests</c>.</summary>
    [Fact]
    public void Matrix_names_every_member_the_table_models()
    {
        var types = new[] { LIST, DICTIONARY, CONCURRENT_DICTIONARY, CONCURRENT_QUEUE, CONCURRENT_STACK, CONCURRENT_BAG, HASH_SET, QUEUE, STACK, LINKED_LIST,
                            LINKED_LIST_NODE, KEY_COLLECTION, VALUE_COLLECTION };
        var modelled = types.SelectMany(type => Probe.Value.GetTypeByMetadataName(type)!.GetMembers().OfType<IMethodSymbol>()
                                                       .Where(method => method is { DeclaredAccessibility: Accessibility.Public, IsStatic: false } &&
                                                                        method.MethodKind != MethodKind.Constructor && IrLowering.Collections.Of(method) is not null)
                                                       .Select(method => $"{type}.{method.Name}"))
                            .ToHashSet(StringComparer.Ordinal);
        var named = MemberCases.Select(memberCase => $"{memberCase.Type}.{memberCase.Member}")
                               .Concat(NodeMembers.Select(node => $"{LINKED_LIST_NODE}.{node.Member}"))
                               .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(modelled.Except(named).Order(StringComparer.Ordinal));
        Assert.Empty(named.Except(modelled).Order(StringComparer.Ordinal));
    }

    /// <summary>Every interface member the map decides, on every kind of object — a type of the table, a view, a snapshot and an array of
    /// either rank — is gone through by some row.</summary>
    [Fact]
    public void Matrix_names_every_interface_member_the_map_decides()
    {
        var compilation = Probe.Value;
        var kinds = new[] { LIST, DICTIONARY, CONCURRENT_DICTIONARY, CONCURRENT_QUEUE, CONCURRENT_STACK, CONCURRENT_BAG, HASH_SET, QUEUE, STACK, LINKED_LIST,
                            KEY_COLLECTION, VALUE_COLLECTION }
            .Select(type => (Kind: KindOf(type), Type: (ITypeSymbol)Closed(type)))
            .Append((CollectionObjects.SNAPSHOT, Closed(LIST)))
            .Append((CollectionObjects.ARRAY, compilation.CreateArrayTypeSymbol(compilation.GetTypeByMetadataName("Item")!, 1)))
            .Append((CollectionObjects.MULTIDIMENSIONAL_ARRAY, compilation.CreateArrayTypeSymbol(compilation.GetSpecialType(SpecialType.System_Int32), 2)));
        var decided = kinds.SelectMany(kind => kind.Type.AllInterfaces.SelectMany(@interface => @interface.GetMembers().OfType<IMethodSymbol>())
                                                   .Where(member => Decides(member, kind.Kind) is not null)
                                                   .Select(member => MemberId(kind.Kind, member)))
                           .ToHashSet(StringComparer.Ordinal);
        var named = Cases.Value.Values.SelectMany(@case => @case.Ways.Append(@case.Canonical)).Select(way => way.Through).OfType<string>()
                         .Concat(SnapshotRoutes.Select(route => route.Through).OfType<string>())
                         .Concat(SnapshotThrough)
                         .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(decided.Except(named).Order(StringComparer.Ordinal));
    }

    /// <summary>Every member case runs every route of R3 and, by the name and the number of arguments alone, every interface of its
    /// receiver's type the map decides it through; an array case every interface of the array that has its member.</summary>
    [Fact]
    public void Every_member_case_runs_every_route_its_receiver_allows()
    {
        // A call through an interface whose member takes other arguments than the public one it is is a case of its own.
        var through = MemberCases.Select(memberCase => memberCase.Through).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var memberCase in MemberCases)
        {
            var @case = Cases.Value[memberCase.Name];
            Assert.All(ReceiverRoutes, route => Assert.Contains(@case.Ways, way => way.Label == route));
            if (memberCase.Through is not null)
                continue;
            var closed = Closed(memberCase.Type);
            var kind = KindOf(memberCase.Type);
            foreach (var @interface in closed.AllInterfaces)
            {
                var allowed = @interface.GetMembers(memberCase.Member).OfType<IMethodSymbol>()
                                        .Any(member => member.Parameters.Length == memberCase.Arity && Decides(member, kind) == $"{memberCase.Type}.{memberCase.Member}" &&
                                                       !through.Contains(MemberId(kind, member)));
                var label = @interface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                Assert.True(!allowed || @case.Ways.Any(way => way.Label == label), $"{memberCase.Name} has no row through {label}");
            }
        }

        // An array case runs every interface of the array that has its member, each on the field and through every route of R3.
        foreach (var arrayCase in ArrayCases)
        {
            var @case = Cases.Value[arrayCase.Name];
            var interfaces = ArrayInterfaces(arrayCase).Select(way => way.Label).ToArray();
            Assert.NotEmpty(interfaces);
            foreach (var label in interfaces)
            {
                Assert.Contains(@case.Ways, way => way.Label == label);
                Assert.All(ReceiverRoutes, route => Assert.True(@case.Ways.Any(way => way.Label == $"{route} through {label}"),
                                                                $"{arrayCase.Name} has no row {route} through {label}"));
            }
        }
    }

    // ---- running and comparing ----

    private sealed record Collected(InterproceduralCollection Collection, IReadOnlySet<int> Marked, IReadOnlySet<int> Taken);

    private static readonly ConcurrentDictionary<string, Lazy<Collected>> Runs = new(StringComparer.Ordinal);

    /// <summary>A case's run, made once and reused by every row of it.</summary>
    private static Collected Run(string key, Func<string> source) =>
        Runs.GetOrAdd(key, _ => new Lazy<Collected>(() =>
        {
            var text = source();
            var lines = (Usings + text).Split('\n');
            HashSet<int> LinesWith(string marker) => lines.Select((line, index) => (line, index)).Where(pair => pair.line.Contains(marker, StringComparison.Ordinal))
                                                          .Select(pair => pair.index + 1).ToHashSet();
            return new Collected(Collect(text), LinesWith(MARK), LinesWith(TAKEN));
        })).Value;

    /// <summary>The controller whose actions are the roots, one per way.</summary>
    private static string Controller(IEnumerable<string> ids)
    {
        var text = new StringBuilder("public sealed class RouteController : ControllerBase\n{\n    private readonly State _state;\n\n" +
                                     "    public RouteController(State state) => _state = state;\n");
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
            text.Append($"\n    public void {id}() => _state.Route_{id}();\n");
        return text.Append('}').ToString();
    }

    /// <summary>What a way's root does, as resource identity and operation: at the statement's line where <paramref name="marked"/>,
    /// and everywhere under the root otherwise — the singleton's own fields among them, whose read a member folds where the same member
    /// on the field folds it (review F-0018), unless <paramref name="ownFields"/> is false, as for a factory, whose ways differ only in
    /// the field they take the delegate from — and how many semantic gaps have a site in the way's bodies. <paramref name="taken"/>
    /// reads the line a snapshot is used on in the body that took it instead of the statement's.</summary>
    private static IReadOnlyList<string> Seen(Collected run, string id, bool marked, bool ownFields = true, bool taken = false)
    {
        // The controller's field is its root's own, and each root is an object of its own.
        var lines = taken ? run.Taken : run.Marked;
        var accesses = run.Collection.Accesses
                          .Where(access => access.Root.Symbol.EndsWith($".{id}()", StringComparison.Ordinal) && !access.IsConstructionLocal &&
                                           (!marked || lines.Contains(access.Source.StartLine)) &&
                                           access.Resource.Member.DeclaringType != "RouteController" &&
                                           !(!ownFields && access.Resource.CollectionId is null && access.Resource.Member.DeclaringType == "State"))
                          .Select(access => $"{access.Resource.Identity} {access.Operation.ToWireName()}");
        // A body without parameters has none in its id, and a local function's follows its method's after a '#'.
        var gaps = run.Collection.Coverage.Gaps.Count(gap => gap.Sites.Any(site => site.BodyId.EndsWith($".Route_{id}", StringComparison.Ordinal) ||
                                                                                   site.BodyId.Contains($".Route_{id}(", StringComparison.Ordinal) ||
                                                                                   site.BodyId.Contains($".Route_{id}#", StringComparison.Ordinal) ||
                                                                                   site.BodyId.Contains($".Route_{id}_", StringComparison.Ordinal)));
        return accesses.Append($"gaps {gaps}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Asserts that two ways make the same accesses, naming each side's own.</summary>
    private static void AssertSame(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var missing = expected.Except(actual).ToArray();
        var extra = actual.Except(expected).ToArray();
        Assert.True(missing.Length == 0 && extra.Length == 0,
                    $"only on the canonical way: {string.Join("; ", missing)}{Environment.NewLine}only on this way: {string.Join("; ", extra)}");
    }

    private static string Short(string type)
    {
        var name = type[(type.LastIndexOf('.') + 1)..];
        return (name.Contains('+') ? name[(name.IndexOf('+') + 1)..] : name[..name.IndexOf('`')]);
    }
}
