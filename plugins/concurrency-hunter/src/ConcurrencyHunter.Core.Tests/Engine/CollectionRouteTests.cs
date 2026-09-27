using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A member of the ADR 0010 table acts on the collection its receiver is, however the receiver got there (R3): a parameter,
/// what a helper returned, a cell of another collection, a merge, a static field or a capture. It makes on each collection the heap
/// resolves the receiver to exactly what it makes when the body loads that collection from the field holding it — the same resources
/// by identity, the cell its key names, its atomicity, the compound operation a check before it makes, and the objects it holds and
/// hands out. A collection no field holds makes no access, as in the body.</summary>
public sealed class CollectionRouteTests
{
    [Fact]
    public void Member_on_a_parameter_acts_on_the_collection_the_argument_is()
    {
        var accesses = AssertSame("Items.Add(1);", "AddOne(Items);");

        Assert.Contains(accesses, access => access.StartsWith("Items write ", StringComparison.Ordinal));
    }

    [Fact]
    public void Member_on_a_helper_result_acts_on_the_collection_returned() =>
        Assert.NotEmpty(AssertSame("Items.Add(1);", "GetItems().Add(1);"));

    /// <summary>A list another list holds in its cells is still the list a field holds, and the member acts on it there.</summary>
    [Fact]
    public void Member_on_a_cell_of_another_collection_acts_on_the_held_collection()
    {
        var run = Analyze(ActionSource("Lists[0].Add(1);"));

        var inner = OnCollections(run).Where(access => access.StartsWith("Inner ", StringComparison.Ordinal)).ToArray();
        Assert.Contains(inner, access => access.Contains(" write ", StringComparison.Ordinal));
        Assert.Equal(OnCollections(Analyze(ActionSource("_ = Lists[0]; Inner.Add(1);"))), OnCollections(run));
    }

    [Fact]
    public void Member_on_a_merge_acts_on_every_collection_it_may_be()
    {
        var accesses = AssertSame("if (Flag) Items.Add(1); else Other.Add(1);", "var list = Flag ? Items : Other; list.Add(1);");

        Assert.Contains(accesses, access => access.StartsWith("Items write ", StringComparison.Ordinal));
        Assert.Contains(accesses, access => access.StartsWith("Other write ", StringComparison.Ordinal));
    }

    [Fact]
    public void Member_on_a_static_field_or_a_capture_acts_as_on_the_field()
    {
        Assert.NotEmpty(AssertSame("Shared.Add(1);", "AddOne(Shared);"));
        Assert.NotEmpty(AssertSame("Items.Add(1);", "var items = Items; void Add() => items.Add(1); Add();"));
        Assert.NotEmpty(AssertSame("Shared.Add(1);", "var shared = Shared; void Add() => shared.Add(1); Add();"));
    }

    [Fact]
    public void Keyed_member_on_a_parameter_keeps_the_cell_its_key_names()
    {
        var accesses = AssertSame("Map[\"a\"] = 1;", "PutA(Map);");

        Assert.Contains(accesses, access => access.StartsWith("Map.[\"a\"] write ", StringComparison.Ordinal));
        Assert.DoesNotContain(accesses, access => access.StartsWith("Map.[?]", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_then_act_on_a_parameter_is_the_compound_operation_it_is_on_the_field()
    {
        var accesses = AssertSame("if (!Map.ContainsKey(\"a\")) Map[\"a\"] = 1;", "AddIfMissing(Map);");

        Assert.Contains(accesses, access => access.StartsWith("Map.[\"a\"] compound-operation ", StringComparison.Ordinal));
        Assert.DoesNotContain(accesses, access => access.StartsWith("Map.[\"a\"] read ", StringComparison.Ordinal));
    }

    /// <summary>A check through one parameter and a change through another are one compound operation where both parameters are the same
    /// collection, as the two members on the field are (review F-0015).</summary>
    [Fact]
    public void Check_then_act_across_two_parameters_on_one_collection_is_the_compound_operation()
    {
        var accesses = AssertSame("if (!Map.ContainsKey(\"a\")) Map[\"a\"] = 1;", "AddIfMissingBetween(Map, Map);");

        Assert.Contains(accesses, access => access.StartsWith("Map.[\"a\"] compound-operation ", StringComparison.Ordinal));
    }

    [Fact]
    public void Member_of_a_thread_safe_collection_on_a_parameter_stays_atomic()
    {
        var accesses = AssertSame("Safe.TryAdd(\"a\", 1);", "TryAddA(Safe);");

        Assert.Contains(accesses, access => access.StartsWith("Safe.[\"a\"] atomic-", StringComparison.Ordinal));
        Assert.All(accesses.Where(access => access.StartsWith("Safe", StringComparison.Ordinal)),
                   access => Assert.Contains(" atomic-", access, StringComparison.Ordinal));
    }

    [Fact]
    public void Member_on_a_parameter_pairs_with_the_same_member_on_the_field()
    {
        var run = Analyze(WorkerSource("AddOne(Items);", "Items.Add(2);"));

        Assert.Contains(run.Pairs.Pairs, pair => Path(pair.Resource) == "Items" &&
                                                 pair.First.Symbol != pair.Second.Symbol &&
                                                 new[] { pair.First.Symbol, pair.Second.Symbol }.Contains("Store.AddOne(List<int>)"));
    }

    [Fact]
    public void Hand_out_member_on_a_parameter_yields_what_the_collection_holds()
    {
        var accesses = AssertSame("Things[0].Value = 1;", "Mark(Things);", onCollectionsOnly: false);

        Assert.Contains(accesses, access => access.StartsWith("Value write ", StringComparison.Ordinal) &&
                                            access.EndsWith(" Escaped", StringComparison.Ordinal));
    }

    [Fact]
    public void Insertion_through_a_parameter_is_held_by_the_collection()
    {
        var accesses = AssertSame("var item = new Item(); Things.Add(item); item.Value = 1;",
                                  "var item = new Item(); AddTo(Things, item); item.Value = 1;", onCollectionsOnly: false);

        Assert.Contains(accesses, access => access.StartsWith("Value write ", StringComparison.Ordinal) &&
                                            access.Contains("Act", StringComparison.Ordinal) &&
                                            access.EndsWith(" Escaped", StringComparison.Ordinal));
    }

    [Fact]
    public void Collection_no_field_holds_makes_no_access_through_a_parameter()
    {
        Assert.Empty(OnCollections(Analyze(ActionSource("var local = new List<int>(); AddOne(local);"))));
        Assert.Empty(OnCollections(Analyze(ActionSource("var local = new List<int>(); local.Add(1);"))));
    }

    /// <summary>A node and a live view reached through a helper already acted on the collection they stand for, and still do.</summary>
    [Fact]
    public void Node_and_view_members_keep_their_accesses()
    {
        var view = AssertSame("_ = Map.Keys.Count;", "_ = CountOf(Map.Keys);");
        var node = AssertSame("Chain.First!.Value = 1;", "SetValue(Chain.First!);");

        Assert.Contains(view, access => access.StartsWith("Map read ", StringComparison.Ordinal));
        Assert.Contains(node, access => access.StartsWith("Chain.[?] write ", StringComparison.Ordinal));
    }

    /// <summary>Asserts that the route makes the accesses the field form makes, and hands them back.</summary>
    private static IReadOnlyList<string> AssertSame(string onField, string route, bool onCollectionsOnly = true)
    {
        var select = onCollectionsOnly ? (Func<EngineRun, IReadOnlyList<string>>)OnCollections : Everything;
        var expected = select(Analyze(ActionSource(onField)));
        Assert.NotEmpty(expected);
        Assert.Equal(expected, select(Analyze(ActionSource(route))));
        return expected;
    }

    /// <summary>The accesses a run makes on collections: path, identity, operation and ownership, whichever body makes them.</summary>
    private static IReadOnlyList<string> OnCollections(EngineRun run) =>
        Describe(run.Collection.Accesses.Where(access => access.Resource.CollectionId is not null));

    /// <summary>The accesses on collections and on every object but the singleton, whose fields a route loads to hand a collection
    /// over where the field form loads them to call it.</summary>
    private static IReadOnlyList<string> Everything(EngineRun run) =>
        Describe(run.Collection.Accesses.Where(access => access.Resource.CollectionId is not null || access.Resource.Region != "di:Store@Singleton"));

    private static IReadOnlyList<string> Describe(IEnumerable<Access> accesses) =>
        accesses.Where(access => !access.IsConstructionLocal)
                .Select(access => $"{Path(access.Resource)} {access.Operation.ToWireName()} {access.Resource.Identity} {access.Ownership}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

    private static string Path(AccessResource resource) => string.Join(".", resource.AccessPath);

    private const string Store = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public sealed class Item
        {
            public int Value;
        }

        public sealed class Store
        {
            public static readonly List<int> Shared = new();
            public readonly List<int> Items = new() { 1 };
            public readonly List<int> Other = new();
            public readonly List<int> Inner = new();
            public readonly List<List<int>> Lists = new();
            public readonly List<Item> Things = new();
            public readonly Dictionary<string, int> Map = new() { ["b"] = 0 };
            public readonly ConcurrentDictionary<string, int> Safe = new();
            public readonly LinkedList<int> Chain = new();
            public bool Flag = Environment.ProcessorCount > 1;

            public Store()
            {
                Lists.Add(Inner);
                Things.Add(new Item());
                Chain.AddLast(0);
            }

            public List<int> GetItems() => Items;

            public static void AddOne(List<int> items) => items.Add(1);

            public static void AddTo(List<Item> things, Item item) => things.Add(item);

            public static void PutA(Dictionary<string, int> map) => map["a"] = 1;

            public static void AddIfMissing(Dictionary<string, int> map)
            {
                if (!map.ContainsKey("a"))
                    map["a"] = 1;
            }

            public static void AddIfMissingBetween(Dictionary<string, int> first, Dictionary<string, int> second)
            {
                if (!first.ContainsKey("a"))
                    second["a"] = 1;
            }

            public static void TryAddA(ConcurrentDictionary<string, int> safe) => safe.TryAdd("a", 1);

            public static void Mark(List<Item> things) => things[0].Value = 1;

            public static int CountOf(Dictionary<string, int>.KeyCollection keys) => keys.Count;

            public static void SetValue(LinkedListNode<int> node) => node.Value = 1;
        """;

    /// <summary>One action of a controller, which may run against itself.</summary>
    private static string ActionSource(string body) => Store + $$"""

            public void Act()
            {
                {{body}}
            }
        }

        public class ActionController : ControllerBase
        {
            private readonly Store _store;
            public ActionController(Store store) => _store = store;
            public void Post() => _store.Act();
        }
        """ + Startup("services.AddSingleton<Store>();");

    /// <summary>Two workers of one singleton, each running its own member of it.</summary>
    private static string WorkerSource(string first, string second) => Store + $$"""

            public void First()
            {
                {{first}}
            }

            public void Second()
            {
                {{second}}
            }
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly Store _store;
            public FirstWorker(Store store) => _store = store;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _store.First();
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly Store _store;
            public SecondWorker(Store store) => _store = store;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _store.Second();
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Store>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");
}
