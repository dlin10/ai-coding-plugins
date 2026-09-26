using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>
/// Every way into every collection of the ADR 0010 table against every way out of it (R9b): a singleton holds the collection, one
/// execution puts a source object in and another takes it out and writes a field of it. The write lands on the object that went in,
/// as <c>array[0].F = v</c> lands on the element object, and never on an object that went into the collection's other storage: a
/// key way out writes the key object, a value way out the value object, and for a factory way in the object the factory creates.
/// </summary>
public sealed class CollectionElementStorageMatrixTests
{
    /// <summary>Which of a collection's storages a way out reads.</summary>
    private enum Storage
    {
        Element,
        Key
    }

    /// <summary>A way in: the statements that put a <c>Source</c> in (and, for a dictionary, a <c>Keyed</c> key with it), and the members
    /// of the table they are a way in by.</summary>
    private sealed record WayIn(string Label, string Code, params string[] Names);

    /// <summary>A way out: the statements that take a held object out and write its <c>Hits</c>, the storage they read, and the members
    /// of the table they are a way out by.</summary>
    private sealed record WayOut(string Label, Storage Storage, string Code, params string[] Names);

    private sealed record Collection(string Name, string Type, IReadOnlyList<WayIn> In, IReadOnlyList<WayOut> Out);

    private const string CONCURRENT = "System.Collections.Concurrent";

    /// <summary>What a replacement or an update needs in the dictionary first: a key equal to the one it names, holding an <c>Other</c>.</summary>
    private const string Present = "_state.Box.TryAdd(new Keyed(), new Other());";

    /// <summary>What a member putting a value next to a node needs in the list first; the way in removes it again, so the list holds
    /// the <c>Source</c> alone.</summary>
    private const string Neighbour = "_state.Box.AddFirst(new Other());";

    // ---- the ways, collection by collection ----

    private static WayIn CopiedFrom(string type) =>
        new("constructor from a collection", $"var source = new List<Item>(); source.Add(new Source()); _state.Box = new {type}(source);",
            $"{Short(type)}.ctor(collection)");

    private static WayIn DictionaryCopiedFrom(string type) =>
        new("constructor from a dictionary",
            $"var source = new Dictionary<Item, Item>(); source.Add(new Keyed(), new Source()); _state.Box = new {type}(source);",
            $"{Short(type)}.ctor(collection)");

    private static string Short(string type) => type[..type.IndexOf('<')];

    private static WayOut Each(string type) =>
        new("foreach", Storage.Element, "foreach (var held in _state.Box) held.Hits = 1;", $"{type}.GetEnumerator");

    /// <summary>The ways out of a dictionary: its value hand-outs, and both storages through its pairs and views.</summary>
    private static WayOut[] DictionaryOut(string type, params WayOut[] values) =>
    [
        .. values,
        new("pair Value", Storage.Element, "foreach (var pair in _state.Box) pair.Value.Hits = 1;", $"{type}.GetEnumerator", "KeyValuePair.get_Value"),
        new("pair Key", Storage.Key, "foreach (var pair in _state.Box) pair.Key.Hits = 1;", $"{type}.GetEnumerator", "KeyValuePair.get_Key"),
        new("deconstructed value", Storage.Element, "foreach (var (key, value) in _state.Box) value.Hits = 1;", "deconstruction value"),
        new("deconstructed key", Storage.Key, "foreach (var (key, value) in _state.Box) key.Hits = 1;", "deconstruction key"),
        new("Deconstruct value", Storage.Element,
            "foreach (var pair in _state.Box) { pair.Deconstruct(out var key, out var value); value.Hits = 1; }", "KeyValuePair.Deconstruct"),
        new("Deconstruct key", Storage.Key,
            "foreach (var pair in _state.Box) { pair.Deconstruct(out var key, out var value); key.Hits = 1; }", "KeyValuePair.Deconstruct"),
        new("Values view", Storage.Element, "foreach (var held in _state.Box.Values) held.Hits = 1;", $"{type}.get_Values", "ValueCollection.GetEnumerator"),
        new("Keys view", Storage.Key, "foreach (var held in _state.Box.Keys) held.Hits = 1;", $"{type}.get_Keys", "KeyCollection.GetEnumerator")
    ];

    private static readonly Collection[] Collections =
    [
        new("List", "List<Item>",
            [
                new("Add", "_state.Box.Add(new Source());", "List.Add"),
                new("Insert", "_state.Box.Insert(0, new Source());", "List.Insert"),
                new("set_Item", "_state.Box.Add(new Other()); _state.Box[0] = new Source();", "List.set_Item"),
                new("AddRange", "var source = new List<Item>(); source.Add(new Source()); _state.Box.AddRange(source);", "List.AddRange"),
                CopiedFrom("List<Item>")
            ],
            [
                new("indexer", Storage.Element, "_state.Box[0].Hits = 1;", "List.get_Item"),
                Each("List")
            ]),
        new("Dictionary", "Dictionary<Item, Item>",
            [
                new("Add", "_state.Box.Add(new Keyed(), new Source());", "Dictionary.Add"),
                new("TryAdd", "_state.Box.TryAdd(new Keyed(), new Source());", "Dictionary.TryAdd"),
                new("set_Item", "_state.Box[new Keyed()] = new Source();", "Dictionary.set_Item"),
                DictionaryCopiedFrom("Dictionary<Item, Item>"),
                new("constructor from pairs",
                    "var pairs = new List<KeyValuePair<Item, Item>>(); pairs.Add(new KeyValuePair<Item, Item>(new Keyed(), new Source())); " +
                    "_state.Box = new Dictionary<Item, Item>(pairs);",
                    "Dictionary.ctor(collection)", "KeyValuePair.ctor")
            ],
            DictionaryOut("Dictionary",
                new WayOut("indexer", Storage.Element, "_state.Box[_state.Probe].Hits = 1;", "Dictionary.get_Item"),
                new WayOut("TryGetValue", Storage.Element, "if (_state.Box.TryGetValue(_state.Probe, out var held)) held.Hits = 1;", "Dictionary.TryGetValue"),
                new WayOut("Remove", Storage.Element, "if (_state.Box.Remove(_state.Probe, out var held)) held.Hits = 1;", "Dictionary.Remove"))),
        new("ConcurrentDictionary", "ConcurrentDictionary<Item, Item>",
            [
                new("TryAdd", "_state.Box.TryAdd(new Keyed(), new Source());", "ConcurrentDictionary.TryAdd"),
                new("set_Item", "_state.Box[new Keyed()] = new Source();", "ConcurrentDictionary.set_Item"),
                new("TryUpdate", $"{Present} _state.Box.TryUpdate(new Keyed(), new Source(), _state.Probe);", "ConcurrentDictionary.TryUpdate"),
                new("GetOrAdd value", "_state.Box.GetOrAdd(new Keyed(), new Source());", "ConcurrentDictionary.GetOrAdd(key, value)"),
                new("GetOrAdd factory", "_state.Box.GetOrAdd(new Keyed(), _ => new Source());", "ConcurrentDictionary.GetOrAdd(key, valueFactory)"),
                new("GetOrAdd factory with argument", "_state.Box.GetOrAdd(new Keyed(), (_, argument) => new Source(), 0);",
                    "ConcurrentDictionary.GetOrAdd(key, valueFactory, factoryArgument)"),
                new("AddOrUpdate value", "_state.Box.AddOrUpdate(new Keyed(), new Source(), (_, old) => old);",
                    "ConcurrentDictionary.AddOrUpdate(key, addValue, updateValueFactory)"),
                new("AddOrUpdate value, update factory", $"{Present} _state.Box.AddOrUpdate(new Keyed(), new Other(), (_, old) => new Source());",
                    "ConcurrentDictionary.AddOrUpdate(key, addValue, updateValueFactory)/update"),
                new("AddOrUpdate add factory", "_state.Box.AddOrUpdate(new Keyed(), _ => new Source(), (_, old) => old);",
                    "ConcurrentDictionary.AddOrUpdate(key, addValueFactory, updateValueFactory)"),
                new("AddOrUpdate update factory", $"{Present} _state.Box.AddOrUpdate(new Keyed(), _ => new Other(), (_, old) => new Source());",
                    "ConcurrentDictionary.AddOrUpdate(key, addValueFactory, updateValueFactory)/update"),
                new("AddOrUpdate add factory with argument",
                    "_state.Box.AddOrUpdate(new Keyed(), (_, argument) => new Source(), (_, old, argument) => old, 0);",
                    "ConcurrentDictionary.AddOrUpdate(key, addValueFactory, updateValueFactory, factoryArgument)"),
                new("AddOrUpdate update factory with argument",
                    $"{Present} _state.Box.AddOrUpdate(new Keyed(), (_, argument) => new Other(), (_, old, argument) => new Source(), 0);",
                    "ConcurrentDictionary.AddOrUpdate(key, addValueFactory, updateValueFactory, factoryArgument)/update"),
                DictionaryCopiedFrom("ConcurrentDictionary<Item, Item>")
            ],
            DictionaryOut("ConcurrentDictionary",
                new WayOut("indexer", Storage.Element, "_state.Box[_state.Probe].Hits = 1;", "ConcurrentDictionary.get_Item"),
                new WayOut("TryGetValue", Storage.Element, "if (_state.Box.TryGetValue(_state.Probe, out var held)) held.Hits = 1;",
                           "ConcurrentDictionary.TryGetValue"),
                new WayOut("TryRemove", Storage.Element, "if (_state.Box.TryRemove(_state.Probe, out var held)) held.Hits = 1;",
                           "ConcurrentDictionary.TryRemove"),
                new WayOut("GetOrAdd result", Storage.Element, "_state.Box.GetOrAdd(_state.Probe, new Other()).Hits = 1;", "ConcurrentDictionary.GetOrAdd"),
                new WayOut("AddOrUpdate result", Storage.Element, "_state.Box.AddOrUpdate(_state.Probe, new Other(), (_, old) => old).Hits = 1;",
                           "ConcurrentDictionary.AddOrUpdate"))),
        new("ConcurrentQueue", "ConcurrentQueue<Item>",
            [new("Enqueue", "_state.Box.Enqueue(new Source());", "ConcurrentQueue.Enqueue"), CopiedFrom("ConcurrentQueue<Item>")],
            [
                new("TryDequeue", Storage.Element, "if (_state.Box.TryDequeue(out var held)) held.Hits = 1;", "ConcurrentQueue.TryDequeue"),
                new("TryPeek", Storage.Element, "if (_state.Box.TryPeek(out var held)) held.Hits = 1;", "ConcurrentQueue.TryPeek"),
                Each("ConcurrentQueue")
            ]),
        new("ConcurrentStack", "ConcurrentStack<Item>",
            [new("Push", "_state.Box.Push(new Source());", "ConcurrentStack.Push"), CopiedFrom("ConcurrentStack<Item>")],
            [
                new("TryPop", Storage.Element, "if (_state.Box.TryPop(out var held)) held.Hits = 1;", "ConcurrentStack.TryPop"),
                new("TryPeek", Storage.Element, "if (_state.Box.TryPeek(out var held)) held.Hits = 1;", "ConcurrentStack.TryPeek"),
                Each("ConcurrentStack")
            ]),
        new("ConcurrentBag", "ConcurrentBag<Item>",
            [new("Add", "_state.Box.Add(new Source());", "ConcurrentBag.Add"), CopiedFrom("ConcurrentBag<Item>")],
            [
                new("TryTake", Storage.Element, "if (_state.Box.TryTake(out var held)) held.Hits = 1;", "ConcurrentBag.TryTake"),
                new("TryPeek", Storage.Element, "if (_state.Box.TryPeek(out var held)) held.Hits = 1;", "ConcurrentBag.TryPeek"),
                Each("ConcurrentBag")
            ]),
        new("HashSet", "HashSet<Item>",
            [new("Add", "_state.Box.Add(new Source());", "HashSet.Add"), CopiedFrom("HashSet<Item>")],
            [
                new("TryGetValue", Storage.Element, "if (_state.Box.TryGetValue(_state.Probe, out var held)) held.Hits = 1;", "HashSet.TryGetValue"),
                Each("HashSet")
            ]),
        new("Queue", "Queue<Item>",
            [new("Enqueue", "_state.Box.Enqueue(new Source());", "Queue.Enqueue"), CopiedFrom("Queue<Item>")],
            [
                new("Peek", Storage.Element, "_state.Box.Peek().Hits = 1;", "Queue.Peek"),
                new("Dequeue", Storage.Element, "_state.Box.Dequeue().Hits = 1;", "Queue.Dequeue"),
                new("TryPeek", Storage.Element, "if (_state.Box.TryPeek(out var held)) held.Hits = 1;", "Queue.TryPeek"),
                new("TryDequeue", Storage.Element, "if (_state.Box.TryDequeue(out var held)) held.Hits = 1;", "Queue.TryDequeue"),
                Each("Queue")
            ]),
        new("Stack", "Stack<Item>",
            [new("Push", "_state.Box.Push(new Source());", "Stack.Push"), CopiedFrom("Stack<Item>")],
            [
                new("Peek", Storage.Element, "_state.Box.Peek().Hits = 1;", "Stack.Peek"),
                new("Pop", Storage.Element, "_state.Box.Pop().Hits = 1;", "Stack.Pop"),
                new("TryPeek", Storage.Element, "if (_state.Box.TryPeek(out var held)) held.Hits = 1;", "Stack.TryPeek"),
                new("TryPop", Storage.Element, "if (_state.Box.TryPop(out var held)) held.Hits = 1;", "Stack.TryPop"),
                Each("Stack")
            ]),
        new("LinkedList", "LinkedList<Item>",
            [
                new("AddFirst value", "_state.Box.AddFirst(new Source());", "LinkedList.AddFirst(value)"),
                new("AddLast value", "_state.Box.AddLast(new Source());", "LinkedList.AddLast(value)"),
                new("AddBefore value", $"{Neighbour} _state.Box.AddBefore(_state.Box.First!, new Source()); _state.Box.RemoveLast();",
                    "LinkedList.AddBefore(node, value)"),
                new("AddAfter value", $"{Neighbour} _state.Box.AddAfter(_state.Box.First!, new Source()); _state.Box.RemoveFirst();",
                    "LinkedList.AddAfter(node, value)"),
                new("AddFirst node", "var node = new LinkedListNode<Item>(new Source()); _state.Box.AddFirst(node);",
                    "LinkedList.AddFirst(node)", "LinkedListNode.ctor"),
                new("AddLast node", "var node = new LinkedListNode<Item>(new Source()); _state.Box.AddLast(node);",
                    "LinkedList.AddLast(node)", "LinkedListNode.ctor"),
                new("AddBefore node",
                    $"{Neighbour} var node = new LinkedListNode<Item>(new Source()); _state.Box.AddBefore(_state.Box.First!, node); _state.Box.RemoveLast();",
                    "LinkedList.AddBefore(node, newNode)", "LinkedListNode.ctor"),
                new("AddAfter node",
                    $"{Neighbour} var node = new LinkedListNode<Item>(new Source()); _state.Box.AddAfter(_state.Box.First!, node); _state.Box.RemoveFirst();",
                    "LinkedList.AddAfter(node, newNode)", "LinkedListNode.ctor"),
                new("node Value setter", $"{Neighbour} _state.Box.First!.Value = new Source();", "LinkedListNode.set_Value"),
                new("node given its value before it is added",
                    "var node = new LinkedListNode<Item>(new Other()); node.Value = new Source(); _state.Box.AddLast(node);",
                    "LinkedListNode.set_Value", "LinkedList.AddLast(node)"),
                CopiedFrom("LinkedList<Item>")
            ],
            [
                new("First", Storage.Element, "_state.Box.First!.Value.Hits = 1;", "LinkedList.get_First", "LinkedListNode.get_Value"),
                new("Last", Storage.Element, "_state.Box.Last!.Value.Hits = 1;", "LinkedList.get_Last", "LinkedListNode.get_Value"),
                new("Find", Storage.Element, "_state.Box.Find(_state.Probe)!.Value.Hits = 1;", "LinkedList.Find", "LinkedListNode.get_Value"),
                new("FindLast", Storage.Element, "_state.Box.FindLast(_state.Probe)!.Value.Hits = 1;", "LinkedList.FindLast", "LinkedListNode.get_Value"),
                // The taker puts a neighbour before or after what the list holds, so the held node is the one Next or Previous reaches.
                new("Next", Storage.Element, "_state.Box.AddFirst(new Other()); _state.Box.First!.Next!.Value.Hits = 1;",
                    "LinkedListNode.get_Next", "LinkedListNode.get_Value"),
                new("Previous", Storage.Element, "_state.Box.AddLast(new Other()); _state.Box.Last!.Previous!.Value.Hits = 1;",
                    "LinkedListNode.get_Previous", "LinkedListNode.get_Value"),
                Each("LinkedList")
            ])
    ];

    public static TheoryData<string, string, string> Rows()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var collection in Collections)
        foreach (var wayIn in collection.In)
        foreach (var wayOut in collection.Out)
            data.Add(collection.Name, wayIn.Label, wayOut.Label);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Collection_hands_out_what_it_holds_as_an_array_does(string collection, string wayIn, string wayOut)
    {
        var box = Collections.Single(candidate => candidate.Name == collection);
        var into = box.In.Single(way => way.Label == wayIn);
        var outOf = box.Out.Single(way => way.Label == wayOut);

        AssertWriteOnWhatWentIn(Taken(box.Type, into.Code, outOf.Code), outOf.Storage);
    }

    [Fact]
    public void Array_fixture_gives_a_write_on_the_element_object()
    {
        AssertWriteOnWhatWentIn(Taken("Item[]", "_state.Box[0] = new Source();", "_state.Box[0].Hits = 1;"), Storage.Element);
    }

    /// <summary>Every way into and out of the table the recognizer models, read from it member by member and overload by overload, and
    /// the ways R2-R4 and R11 name besides its members, each appear in some row.</summary>
    [Fact]
    public async Task Matrix_names_every_way_in_and_out()
    {
        var compilation = (await FixtureSolution.Create(("Case.cs", "public sealed class Placeholder { }")).Projects.Single().GetCompilationAsync())!;
        var required = new SortedSet<string>(StringComparer.Ordinal)
        {
            "deconstruction key",
            "deconstruction value"
        };
        foreach (var metadataName in TableTypes)
        {
            var type = compilation.GetTypeByMetadataName(metadataName)!;
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(method => method.DeclaredAccessibility == Accessibility.Public && !method.IsStatic))
            {
                if (IrLowering.Collections.Of(method) is not { } call)
                    continue;
                var name = method.MethodKind == MethodKind.Constructor ? "ctor" : method.Name;
                var parameters = method.OriginalDefinition.Parameters;
                var overload = $"{type.Name}.{name}({string.Join(", ", parameters.Select(parameter => parameter.Name))})";
                // A way in: a member that holds an argument or copies a collection.
                if (call.Source is not null)
                    required.Add(name == "ctor" ? $"{type.Name}.ctor(collection)" : $"{type.Name}.{name}");
                else if (parameters.Any(parameter => InterproceduralAccesses.IsHeldArgument(call, parameter.Ordinal)))
                {
                    var separateOverloads = name is "GetOrAdd" or "AddOrUpdate" or "AddFirst" or "AddLast" or "AddBefore" or "AddAfter";
                    required.Add(separateOverloads ? overload : $"{type.Name}.{name}");
                    // Each factory of AddOrUpdate that updates is a way in of its own.
                    if (name == "AddOrUpdate")
                        required.Add($"{overload}/update");
                }

                // A way out: a member that hands a held value out, through its result or an out parameter, a view, an enumeration and a
                // node of a linked list.
                var (result, @out) = InterproceduralAccesses.HandsOutHeld(call);
                if (result && !method.ReturnsVoid || @out && parameters.Any(parameter => parameter.RefKind == RefKind.Out) || call.View is not null ||
                    name == "GetEnumerator" || call is { HandsOutCell: true, Structure: IrCollectionEffect.Read })
                {
                    required.Add($"{type.Name}.{name}");
                }
            }
        }

        var named = Collections.SelectMany(collection => collection.In.SelectMany(way => way.Names).Concat(collection.Out.SelectMany(way => way.Names)))
                               .ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(way => !named.Contains(way)).ToArray();
        Assert.True(missing.Length == 0, $"ways no row names: {string.Join("; ", missing)}");
    }

    private static readonly string[] TableTypes =
    [
        "System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2", $"{CONCURRENT}.ConcurrentDictionary`2",
        $"{CONCURRENT}.ConcurrentQueue`1", $"{CONCURRENT}.ConcurrentStack`1", $"{CONCURRENT}.ConcurrentBag`1", "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.Queue`1", "System.Collections.Generic.Stack`1", "System.Collections.Generic.LinkedList`1",
        "System.Collections.Generic.LinkedListNode`1", "System.Collections.Generic.KeyValuePair`2",
        "System.Collections.Generic.Dictionary`2+KeyCollection", "System.Collections.Generic.Dictionary`2+ValueCollection"
    ];

    // ---- the fixture ----

    /// <summary>The taker's accesses of <c>Hits</c>: a plain write on the object that went into the storage the way out reads, and no
    /// access at all on an object that went into the other storage.</summary>
    private static void AssertWriteOnWhatWentIn(EngineRun run, Storage storage)
    {
        var (wentIn, other) = storage == Storage.Key ? ("#Keyed", "#Source") : ("#Source", "#Keyed");
        var taken = run.Of("Hits").Where(access => access.Symbol.StartsWith("Taker.", StringComparison.Ordinal)).ToArray();

        Assert.Contains(taken, access => access.Operation == AccessOperation.Write && access.Resource.Region.Contains(wentIn, StringComparison.Ordinal));
        Assert.DoesNotContain(taken, access => access.Resource.Region.Contains(other, StringComparison.Ordinal));
    }

    private static EngineRun Taken(string type, string put, string take) =>
        AnalyzeScope(FixtureSolution.Create(("Case.cs", Usings + Source(type, put, take))), "scope:Fixture");

    /// <summary>A singleton holding a collection of <paramref name="type"/>, a worker that puts objects in with <paramref name="put"/> and
    /// another that takes one out and writes it with <paramref name="take"/>. Every item equals every other, so the probe a way out
    /// looks up by, the key a way in files under and the value a replacement compares against find one another when the program runs,
    /// and each row takes out what was put in.</summary>
    private static string Source(string type, string put, string take) => $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public class Item
        {
            public int Hits;
            public override bool Equals(object? other) => other is Item;
            public override int GetHashCode() => 0;
        }
        public sealed class Source : Item { }
        public sealed class Keyed : Item { }
        public sealed class Other : Item { }
        public sealed class Probe : Item { }

        public sealed class State
        {
            public {{type}} Box = {{(type.EndsWith("[]", StringComparison.Ordinal) ? "new Item[1]" : "new()")}};
            public readonly Probe Probe = new Probe();
        }

        public sealed class Putter(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{put}}
                return Task.CompletedTask;
            }
        }

        public sealed class Taker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{take}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Putter>(); services.AddHostedService<Taker>();");
}
