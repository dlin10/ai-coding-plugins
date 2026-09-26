using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Frontend;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A dictionary holds its keys apart from its values (R3), and its views and the copies of a collection carry what it holds
/// over with the effects the phase 5b second-run amendment of ADR 0010 gives them (R4).</summary>
public sealed class DictionaryKeyStorageTests
{
    private const string KEY = "alloc:State..ctor()#Key";
    private const string LOOSE_KEY = "alloc:State..ctor()#LooseKey";
    private const string CONCURRENT_LOOSE_KEY = "alloc:State..ctor()#LooseKey#2";
    private const string ITEM = "alloc:State..ctor()#Item";
    private const string SPARE = "alloc:State..ctor()#Spare";

    // ---- pairs and views hand out one storage each ----

    [Fact]
    public void Pair_Key_yields_keys_and_not_values()
    {
        var regions = WrittenRegions(Run("foreach (var pair in _state.Map) pair.Key.Hits = 1;"));

        Assert.Equal([KEY, LOOSE_KEY], regions);
    }

    [Fact]
    public void Pair_Value_yields_values_and_not_keys()
    {
        var regions = WrittenRegions(Run("foreach (var pair in _state.Map) pair.Value.Hits = 1;"));

        Assert.Equal([ITEM], regions);
    }

    [Fact]
    public void Deconstructed_pair_yields_key_and_value_apart()
    {
        var run = Run("foreach (var (key, value) in _state.Map) { key.Hits = 1; value.Hits = 2; }");

        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(run, "key.Hits = 1"));
        Assert.Equal([ITEM], WrittenRegions(run, "value.Hits = 2"));
    }

    [Fact]
    public void Keys_view_yields_keys()
    {
        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(Run("foreach (var key in _state.Map.Keys) key.Hits = 1;")));
    }

    [Fact]
    public void Values_view_yields_values()
    {
        Assert.Equal([ITEM], WrittenRegions(Run("foreach (var value in _state.Map.Values) value.Hits = 1;")));
    }

    // ---- what a view does to its dictionary ----

    [Fact]
    public void Taking_a_live_view_makes_no_access()
    {
        var run = Run("var keys = _state.Map.Keys; var values = _state.Map.Values;");

        Assert.Empty(WorkerOn(run, "Map"));
    }

    [Fact]
    public void Enumerating_a_live_view_pairs_with_an_insertion_on_the_structure_and_the_cells()
    {
        var run = Run("foreach (var key in _state.Map.Keys) { }", "_state.Map.Add(new Key(), new Item());");

        var reads = WorkerOn(run, "Map");
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read => Assert.Equal(AccessOperation.Read, read.Operation));
        var pairs = run.Run.Pairs.Pairs.Where(pair => pair.Resource.AccessPath[0] == "Map").ToArray();
        Assert.Contains(pairs, pair => pair.Resource.Selector is null && Involves(pair, reads));
        Assert.Contains(pairs, pair => pair.Resource.Selector is not null && Involves(pair, reads));
    }

    [Fact]
    public void Count_of_a_live_view_reads_only_the_structure()
    {
        var run = Run("_ = _state.Map.Keys.Count; _ = _state.Map.Values.Count;");

        var reads = WorkerOn(run, "Map");
        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.Equal((AccessOperation.Read, (ElementSelector?)null), (read.Operation, read.Resource.Selector)));
    }

    [Fact]
    public void Concurrent_dictionary_views_are_atomic_snapshots()
    {
        var run = Run("var keys = _state.Concurrent.Keys; foreach (var key in keys) key.Hits = 1; _ = _state.Concurrent.Values.Count;");

        // Taking a snapshot reads the structure and every cell at once; what it holds is the keys; counting it touches the snapshot
        // alone, and is no unresolved call.
        var reads = WorkerOn(run, "Concurrent");
        Assert.Contains(reads, read => read.Resource.Selector is null);
        Assert.Contains(reads, read => read.Resource.Selector == ElementSelector.Unknown);
        Assert.All(reads, read => Assert.Equal(AccessOperation.AtomicRead, read.Operation));
        Assert.Equal([KEY, CONCURRENT_LOOSE_KEY], WrittenRegions(run));
        Assert.DoesNotContain(run.Run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
    }

    [Fact]
    public void Snapshot_kept_in_a_local_is_counted_and_enumerated_as_a_list()
    {
        var run = Run("var keys = _state.Concurrent.Keys; _ = keys.Count; var values = _state.Concurrent.Values; _ = values.Count; " +
                      "foreach (var value in values) value.Hits = 1;");

        // A local is the snapshot it was given: counting it is no unresolved call, whose unknown effect would reach what it holds.
        Assert.DoesNotContain(run.Run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Equal([ITEM], WrittenRegions(run));
    }

    [Fact]
    public void Snapshot_used_after_a_branch_or_merged_from_two_views_is_counted_and_enumerated_as_a_list()
    {
        // The snapshot is used in another block than the one that took it, and one value may be either of two snapshots: each is
        // still counted and enumerated as a list, and neither is an unresolved call whose unknown effect would reach what it holds.
        var branched = Run("var keys = _state.Concurrent.Keys; if (stoppingToken.IsCancellationRequested) return Task.CompletedTask; " +
                           "_ = keys.Count; foreach (var one in keys) one.Hits = 1;");
        var merged = Run("var both = stoppingToken.CanBeCanceled ? (IReadOnlyCollection<Tagged>)_state.Concurrent.Keys : " +
                         "(IReadOnlyCollection<Tagged>)_state.Concurrent.Values; _ = both.Count; foreach (var item in both) item.Hits = 2;");

        Assert.DoesNotContain(branched.Run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Equal([KEY, CONCURRENT_LOOSE_KEY], WrittenRegions(branched, "one.Hits = 1"));
        Assert.DoesNotContain(merged.Run.Collection.Accesses, access => access.Operation.IsUnknownEffect());
        Assert.Equal([ITEM, KEY, CONCURRENT_LOOSE_KEY], WrittenRegions(merged, "item.Hits = 2"));
    }

    [Fact]
    public void Snapshot_makes_the_accesses_a_list_made_in_the_body_makes()
    {
        var snapshot = Run("var taken = _state.Concurrent.Keys; _ = taken.Count; foreach (var one in taken) { }");
        var list = Run("var taken = new List<Key>(); _ = taken.Count; foreach (var one in taken) { }");

        // Counting and enumerating a collection no field holds touches no resource: the snapshot is such a list, and only taking it
        // reads the dictionary.
        foreach (var run in new[] { snapshot, list })
        {
            Assert.Empty(OnCollectionsAt(run, "_ = taken.Count;"));
            Assert.Empty(OnCollectionsAt(run, "foreach (var one in taken)"));
        }

        Assert.NotEmpty(WorkerOn(snapshot, "Concurrent"));
        Assert.All(WorkerOn(snapshot, "Concurrent"), read => Assert.Equal(Line(snapshot.Text, "var taken = _state.Concurrent.Keys;"), read.Source.StartLine));
    }

    [Fact]
    public void Live_view_kept_in_a_field_or_handed_to_a_helper_reads_its_dictionary()
    {
        var run = Run("_state.KeptKeys = _state.Map.Keys; _ = _state.KeptKeys.Count; foreach (var key in _state.KeptKeys) { } " +
                      "_ = _state.CountOf(_state.Map.Keys); _ = _state.CountOf(_state.KeptKeys); _state.Walk(_state.Map.Keys);",
                      "_state.Map.Add(new Key(), new Item());");

        var heap = run.Run.Execution.Heap;
        var map = Assert.Single(heap.Heap.PointsTo(heap.Region("di:State@Singleton").Identity, "Fixture:State.Map"));
        foreach (var (at, cells) in new[] { ("_ = _state.KeptKeys.Count;", false), ("foreach (var key in _state.KeptKeys)", true),
                                            ("=> keys.Count;", false), ("foreach (var key in keys)", true) })
        {
            var reads = OnCollectionsAt(run, at).Where(access => access.Resource.CollectionId == map).ToArray();
            Assert.Contains(reads, read => read.Resource.Selector is null);
            Assert.Equal(cells, reads.Any(read => read.Resource.Selector is not null));
            Assert.All(reads, read => Assert.Equal(AccessOperation.Read, read.Operation));
            Assert.Contains(run.Run.Pairs.Pairs, pair => reads.Any(read => ReferenceEquals(pair.First, read) || ReferenceEquals(pair.Second, read)));
        }
    }

    // ---- what the key storage is to every other rule ----

    [Fact]
    public void Key_of_a_shared_dictionary_escapes_through_the_key_storage()
    {
        var run = Run("_ = _state.Map.Count;");

        var ownership = run.Run.Execution.Ownership(LOOSE_KEY);
        Assert.Equal(OwnershipKind.Escaped, ownership.Kind);
        Assert.Contains(ownership.Evidence, hop => hop.Contains("is stored into [keys] of", StringComparison.Ordinal) &&
                                                   hop.EndsWith($"at Case.cs:{Line(run.Text, "Map.Add(new LooseKey(), First);")}.",
                                                                StringComparison.Ordinal));
    }

    [Fact]
    public void Deep_read_and_unknown_effect_on_a_shared_dictionary_reach_the_key_object()
    {
        var read = Run("System.Text.Json.JsonSerializer.Serialize(_state.Map);");
        var touched = Run("Opaque.Lib.Touch(_state.Map);");

        Assert.Contains(Worker(read, "Hits"), access => access.Resource.Region == LOOSE_KEY && access.Operation == AccessOperation.Read);
        Assert.Contains(Worker(touched, "Hits"), access => access.Resource.Region == LOOSE_KEY && access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Key_lookup_reads_only_the_structure()
    {
        var run = Run("_ = _state.Map.ContainsKey(_state.TheKey);");

        var read = Assert.Single(WorkerOn(run, "Map"));
        Assert.Equal((AccessOperation.Read, (ElementSelector?)null), (read.Operation, read.Resource.Selector));
        // A key is never a cell, nor an object the lookup reads.
        Assert.Empty(Worker(run, "Hits"));
    }

    // ---- copies ----

    [Fact]
    public void AddRange_writes_the_structure_and_an_unknown_cell_and_reads_the_source()
    {
        var run = Run("_state.Items.AddRange(_state.Others);");

        var writes = WorkerOn(run, "Items");
        Assert.Equal(2, writes.Count);
        Assert.All(writes, write => Assert.Equal(AccessOperation.Write, write.Operation));
        Assert.Contains(writes, write => write.Resource.Selector is null);
        Assert.Contains(writes, write => write.Resource.Selector == ElementSelector.Unknown);
        AssertReadsStructureAndEveryCell(WorkerOn(run, "Others"));
    }

    [Fact]
    public void AddRange_copies_what_the_source_holds()
    {
        var run = Run("_state.Items.AddRange(_state.Others); _state.Items[0].Hits = 1; " +
                      "var keys = new List<Tagged>(); keys.AddRange(_state.Map.Keys); keys[0].Hits = 2;");

        Assert.Contains(SPARE, WrittenRegions(run, "Items[0].Hits = 1"));
        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(run, "keys[0].Hits = 2"));
    }

    [Fact]
    public void Constructor_from_a_collection_reads_the_source_structure_and_every_cell()
    {
        var run = Run("var copy = new List<Item>(_state.Others); var keys = new HashSet<Key>(_state.Map.Keys);");

        AssertReadsStructureAndEveryCell(WorkerOn(run, "Others"));
        // A live view a copy reads is its dictionary read.
        AssertReadsStructureAndEveryCell(WorkerOn(run, "Map"));
    }

    [Fact]
    public void Constructor_from_a_collection_copies_what_the_source_holds()
    {
        var run = Run("var copy = new List<Item>(_state.Others); copy[0].Hits = 1; new Queue<Item>(_state.Others).Peek().Hits = 2; " +
                      "foreach (var pair in new List<KeyValuePair<Key, Item>>(_state.Map)) pair.Key.Hits = 3;");

        Assert.Equal([SPARE], WrittenRegions(run, "copy[0].Hits = 1"));
        Assert.Equal([SPARE], WrittenRegions(run, "Peek().Hits = 2"));
        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(run, "pair.Key.Hits = 3"));
    }

    [Fact]
    public void Dictionary_copied_from_a_dictionary_keeps_keys_and_values_apart()
    {
        var run = Run("var copy = new Dictionary<Key, Item>(_state.Map); " +
                      "foreach (var key in copy.Keys) key.Hits = 1; foreach (var value in copy.Values) value.Hits = 2; " +
                      "var pairs = new List<KeyValuePair<Key, Item>>(_state.Map); var built = new Dictionary<Key, Item>(pairs); " +
                      "foreach (var key in built.Keys) key.Hits = 3; foreach (var value in built.Values) value.Hits = 4;");

        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(run, "key.Hits = 1"));
        Assert.Equal([ITEM], WrittenRegions(run, "value.Hits = 2"));
        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(run, "key.Hits = 3"));
        Assert.Equal([ITEM], WrittenRegions(run, "value.Hits = 4"));
    }

    [Fact]
    public void Dictionary_copied_through_a_sequence_parameter_keeps_keys_and_values_apart()
    {
        // The copies are made where the source is declared a sequence of pairs; the object it is, a dictionary, decides what it yields.
        var run = Run("foreach (var key in _state.DictionaryOf(_state.Map).Keys) key.Hits = 1; " +
                      "foreach (var value in _state.DictionaryOf(_state.Map).Values) value.Hits = 2; " +
                      "foreach (var pair in _state.PairsOf(_state.Map)) pair.Key.Hits = 3; " +
                      "foreach (var pair in _state.PairsOf(_state.Map)) pair.Value.Hits = 4;");

        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(run, "key.Hits = 1"));
        Assert.Equal([ITEM], WrittenRegions(run, "value.Hits = 2"));
        Assert.Equal([KEY, LOOSE_KEY], WrittenRegions(run, "pair.Key.Hits = 3"));
        Assert.Equal([ITEM], WrittenRegions(run, "pair.Value.Hits = 4"));
    }

    [Fact]
    public void Constructor_without_a_collection_copies_nothing()
    {
        var run = Run("var sized = new List<Item>(4); sized[0].Hits = 1; " +
                      "var compared = new Dictionary<Key, Item>(EqualityComparer<Key>.Default); foreach (var key in compared.Keys) key.Hits = 2;");

        Assert.Empty(Worker(run, "Hits"));
        Assert.DoesNotContain(run.Run.Collection.Accesses, access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) &&
                                                                     access.Resource.AccessPath[0] is "Others" or "Items" or "Map");
    }

    [Fact]
    public async Task Other_members_of_a_view_stay_unresolved()
    {
        var compilation = (await FixtureSolution.Create(("Case.cs", "public sealed class Placeholder { }")).Projects.Single().GetCompilationAsync())!;
        var contains = Assert.Single(compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2+KeyCollection")!
                                                .GetMembers("Contains").OfType<IMethodSymbol>());
        var run = Run("_ = _state.Map.Keys.Contains(_state.TheKey);");

        Assert.Null(IrLowering.Collections.Of(contains));
        // Unresolved, its unknown effect reaches what the view holds, and it makes none of the accesses a member of the view would.
        Assert.Contains(Worker(run, "Hits"), access => access.Resource.Region == LOOSE_KEY && access.Operation == AccessOperation.UnknownEffect);
        Assert.Empty(WorkerOn(run, "Map"));
    }

    // ---- helpers ----

    /// <summary>A run with the text of the case file it analysed.</summary>
    private sealed record Case(EngineRun Run, string Text);

    private static IReadOnlyList<Access> Worker(Case run, string member) =>
        run.Run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();

    /// <summary>The accesses the worker makes on the collection a field of the state holds: its structure and its cells.</summary>
    private static IReadOnlyList<Access> WorkerOn(Case run, string field) =>
        run.Run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && !access.IsConstructionLocal &&
                                                    access.Resource.AccessPath[0] == field)
           .ToArray();

    /// <summary>The objects the worker writes <c>Hits</c> of, at the statement containing <paramref name="at"/> or anywhere.</summary>
    private static string[] WrittenRegions(Case run, string? at = null) =>
        Worker(run, "Hits").Where(access => access.Operation == AccessOperation.Write && (at is null || access.Source.StartLine == Line(run.Text, at)))
                           .Select(access => access.Resource.Region)
                           .Distinct()
                           .Order(StringComparer.Ordinal)
                           .ToArray();

    private static void AssertReadsStructureAndEveryCell(IReadOnlyList<Access> reads)
    {
        Assert.Contains(reads, read => read.Resource.Selector is null && read.Operation == AccessOperation.Read);
        Assert.Contains(reads, read => read.Resource.Selector == ElementSelector.Unknown && read.Operation == AccessOperation.Read);
    }

    /// <summary>The accesses on a collection, by anyone, at the line containing <paramref name="at"/>.</summary>
    private static IReadOnlyList<Access> OnCollectionsAt(Case run, string at) =>
        run.Run.Collection.Accesses.Where(access => access.Resource.CollectionId is not null && access.Source.StartLine == Line(run.Text, at)).ToArray();

    private static bool Involves(AccessPair pair, IReadOnlyList<Access> accesses) =>
        accesses.Any(access => ReferenceEquals(pair.First, access) || ReferenceEquals(pair.Second, access));

    /// <summary>The line of the case file the first line containing <paramref name="text"/> is.</summary>
    private static int Line(string file, string text) =>
        Array.FindIndex(file.Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{text}'");

    private static Case Run(string work, string other = "")
    {
        var text = Usings + Source(work, other);
        return new Case(AnalyzeScope(FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", text)),
                                     "scope:Fixture"),
                        text);
    }

    /// <summary>A singleton <c>State</c> holding a dictionary keyed by <c>Key</c> objects with <c>Item</c> values, a
    /// <c>ConcurrentDictionary</c> keyed the same, a list of items and a list of spares, all filled at construction; a worker does
    /// <paramref name="work"/> on it, one statement a line, while another does <paramref name="other"/>.</summary>
    private static string Source(string work, string other) => $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public class Tagged { public int Hits; }
        public class Key : Tagged { }
        public sealed class LooseKey : Key { }
        public class Item : Tagged { }
        public sealed class Spare : Item { }

        public sealed class State
        {
            public readonly Key TheKey = new Key();
            public readonly Item First = new Item();
            public readonly Dictionary<Key, Item> Map = new();
            public readonly ConcurrentDictionary<Key, Item> Concurrent = new();
            public readonly List<Item> Items = new();
            public readonly List<Item> Others = new();
            public Dictionary<Key, Item>.KeyCollection? KeptKeys;

            public State()
            {
                Map.Add(TheKey, First);
                Map.Add(new LooseKey(), First);
                Concurrent.TryAdd(TheKey, First);
                Concurrent.TryAdd(new LooseKey(), First);
                Items.Add(First);
                Others.Add(new Spare());
            }

            public int CountOf(Dictionary<Key, Item>.KeyCollection keys) => keys.Count;

            public void Walk(Dictionary<Key, Item>.KeyCollection keys)
            {
                foreach (var key in keys) { }
            }

            public Dictionary<Key, Item> DictionaryOf(IEnumerable<KeyValuePair<Key, Item>> pairs) => new Dictionary<Key, Item>(pairs);

            public List<KeyValuePair<Key, Item>> PairsOf(IEnumerable<KeyValuePair<Key, Item>> pairs) => new List<KeyValuePair<Key, Item>>(pairs);
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{string.Join(Environment.NewLine, work.Split("; ", StringSplitOptions.RemoveEmptyEntries).Select(statement => statement.TrimEnd(';') + ";"))}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Touch(object value) { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
