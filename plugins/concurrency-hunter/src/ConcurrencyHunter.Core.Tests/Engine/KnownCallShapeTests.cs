using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The shapes of storage a known call's effect reaches its objects through (R3): collections inside collections, static
/// fields, slices of slices, members that take out rather than put in, collections of mixed kinds, the run's own collection
/// types, immutable objects the container makes, and a record's positional properties.</summary>
public sealed class KnownCallShapeTests
{
    // ---- collections inside collections ----

    [Theory]
    [InlineData("_state.Jagged")]
    [InlineData("_state.Nested")]
    public async Task Object_in_a_collection_inside_a_collection_is_read_deep(string argument)
    {
        var finding = Assert.Single(await Findings($"System.Text.Json.JsonSerializer.Serialize({argument});", "_state.First.Value = 1;"));

        Assert.Equal(["Value"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Collection_in_the_cells_of_another_is_read_as_a_collection()
    {
        const string Read = "System.Text.Json.JsonSerializer.Serialize(_state.Nested);";
        var ordinary = await Findings("_ = _state.Inner.Count;", "_state.Inner.Add(new Item());");
        var deep = await Findings(Read, "_state.Inner.Add(new Item());");

        // Its identity is the collection itself, whichever field reached it: the ordinary read and the deep one meet the same Add.
        Assert.Contains(ordinary, finding => finding.Resource.CollectionId is not null && finding.Resource.Selector is null);
        var finding = Assert.Single(deep, finding => finding.Resource.CollectionId is not null && finding.Resource.Selector is null);
        Assert.Equal(ordinary.Single(item => item.Resource.CollectionId is not null && item.Resource.Selector is null).Resource.CollectionId,
                     finding.Resource.CollectionId);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Collection_handed_over_through_a_local_is_read_as_the_field_that_holds_it()
    {
        // The local is the list the singleton now holds: the known call reads that list, however the argument reached it.
        var finding = Assert.Single(await Findings("var items = new List<Item>(); _state.Items = items; System.Linq.Enumerable.Count(items);",
                                                   "_state.Items.Add(new Item());"),
                                    finding => finding.Resource.CollectionId is not null && finding.Resource.Selector is null);

        AssertTwoSides(finding);
    }

    // ---- static holding fields ----

    [Fact]
    public async Task Enumerable_Count_over_a_static_List_conflicts_with_Add_on_the_structure()
    {
        var finding = Assert.Single(await Findings("System.Linq.Enumerable.Count(Holder.Items);", "Holder.Items.Add(new Item());"),
                                    item => item.Resource.Selector is null);

        Assert.NotNull(finding.Resource.CollectionId);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Ready_slice_of_a_static_array_pairs_on_the_cell_and_is_no_unproven_reference()
    {
        const string Read = "System.ReadOnlySpan<string> names = Holder.Names; string.Concat(names);";
        var finding = Assert.Single(await Findings(Read, "Holder.Names[0] = \"x\";"));
        var run = Run(Read, "");
        var without = Run("System.ReadOnlySpan<string> names = Holder.Names;", "");

        Assert.Equal("Names", finding.Resource.AccessPath[0]);
        AssertTwoSides(finding);
        Assert.Equal(without.Counter(CoverageCounters.UNPROVEN_REFERENCE), run.Counter(CoverageCounters.UNPROVEN_REFERENCE));
    }

    // ---- a slice of a slice ----

    [Fact]
    public async Task Slice_cut_in_a_helper_composes_with_the_callers_slice()
    {
        const string Read = "System.ReadOnlySpan<string> names = _state.Names; Concat(names.Slice(1, 2));";

        // The helper reads [1..2) of [1..3), which is the cell 2 of the array and not the cell 1.
        Assert.Empty(await Findings(Read, "_state.Names[1] = \"x\";"));
        var finding = Assert.Single(await Findings(Read, "_state.Names[2] = \"x\";"));
        Assert.Equal("Names", finding.Resource.AccessPath[0]);
    }

    // ---- what a collection holds ----

    [Fact]
    public async Task Object_a_member_takes_out_is_not_held()
    {
        Assert.Empty(await Findings("System.Text.Json.JsonSerializer.Serialize(_state.Emptied);", "_state.First.Value = 1;"));
    }

    [Fact]
    public async Task Value_TryUpdate_compares_against_is_not_held_and_the_value_it_puts_in_is()
    {
        const string Read = "System.Text.Json.JsonSerializer.Serialize(_state.Map);";

        Assert.DoesNotContain(await Findings(Read, "_state.Compared.Value = 1;"), finding => finding.Resource.AccessPath is ["Value"]);
        Assert.Single(await Findings(Read, "_state.Replacement.Value = 1;"), finding => finding.Resource.AccessPath is ["Value"]);
    }

    [Fact]
    public async Task Argument_a_generic_factory_is_handed_is_not_held()
    {
        // GetOrAdd and AddOrUpdate hand it to their factories, which make a new item: the map never holds it.
        Assert.DoesNotContain(await Findings("System.Text.Json.JsonSerializer.Serialize(_state.Map);", "_state.Handed.Value = 1;"),
                              finding => finding.Resource.AccessPath is ["Value"]);
    }

    [Fact]
    public void Factory_GetOrAdd_is_given_is_not_held()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state.Map);", "");

        // The objects of the map are the items put in; a factory that makes one is no object of it, whether it is created at the
        // call or read from the field that keeps it.
        Assert.DoesNotContain(AtCall(run), access => access.Resource.IsWildcard);
        Assert.All(AtCall(run).Where(access => access.Resource.AccessPath is not ["Map", ..]),
                   access => Assert.Equal(["Value"], access.Resource.AccessPath));
    }

    // ---- collections of mixed kinds ----

    [Fact]
    public void Enumeration_is_atomic_on_the_thread_safe_collection_and_ordinary_on_the_other()
    {
        var run = Run("System.Linq.Enumerable.Count(_state.Mixed);", "");
        var structures = AtCall(run).Where(access => access.Resource.AccessPath is ["Mixed"] && access.Resource.CollectionId is not null).ToArray();

        Assert.Contains(structures, access => access.Resource.CollectionId!.Contains("ConcurrentQueue", StringComparison.Ordinal) &&
                                              access.Operation == AccessOperation.AtomicRead);
        Assert.Contains(structures, access => access.Resource.CollectionId!.Contains("List", StringComparison.Ordinal) &&
                                              access.Operation == AccessOperation.Read);
        Assert.DoesNotContain(structures, access => access.Resource.CollectionId!.Contains("ConcurrentQueue", StringComparison.Ordinal) &&
                                                    access.Operation == AccessOperation.Read);
    }

    // ---- the run's own collection types ----

    [Fact]
    public async Task Enumerable_Count_over_a_derived_List_conflicts_with_Add_and_with_a_write_of_its_object()
    {
        const string Read = "System.Linq.Enumerable.Count(_state.Own);";

        Assert.Single(await Findings(Read, "_state.Own.Add(new Item());"),
                      finding => finding.Resource.CollectionId is not null && finding.Resource.Selector is null);
        var finding = Assert.Single(await Findings(Read, "_state.First.Value = 1;"));
        Assert.Equal(["Value"], finding.Resource.AccessPath);
    }

    [Theory]
    [InlineData("System.Text.Json.JsonSerializer.Serialize(_state.Own);")]
    [InlineData("System.Text.Json.JsonSerializer.Serialize(_state);")]
    public async Task Field_of_a_derived_List_is_read_deep(string read)
    {
        var finding = Assert.Single(await Findings(read, "_state.Own.Counter = 1;"));

        Assert.Equal(["Counter"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    // ---- objects of types from metadata ----

    [Fact]
    public async Task Object_a_library_object_holds_is_read_past_its_wildcard()
    {
        var finding = Assert.Single(await Findings("System.Text.Json.JsonSerializer.Serialize(_state.Box);", "_state.Boxed.Value = 1;"),
                                    finding => finding.Resource.AccessPath is ["Value"]);

        AssertTwoSides(finding);
    }

    // ---- backing fields a property's accessors name ----

    [Fact]
    public async Task Field_keyword_backing_field_is_read_deep_and_written_by_an_argument_write()
    {
        var deep = Assert.Single(await Findings("System.Text.Json.JsonSerializer.Serialize(_state.Tally);", "_state.Tally.Counter = 1;"));
        var written = Assert.Single(await Findings("_ = _state.Tally.Counter;", "_shop.Add(_state.Tally);"));

        Assert.Equal(["<Counter>k__BackingField"], deep.Resource.AccessPath);
        Assert.Equal(["<Counter>k__BackingField"], written.Resource.AccessPath);
        AssertTwoSides(deep);
        // The reader's side of the second is the getter's body, which reads the field; the other is the writer's known call.
        Assert.Contains(new[] { written.AccessA, written.AccessB },
                        access => access.Symbol.StartsWith("Writer.", StringComparison.Ordinal) && access.Operation == AccessOperation.Write);
    }

    // ---- immutable objects and positional properties ----

    [Fact]
    public void Immutable_object_the_container_makes_is_not_read()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state);", "");

        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Version"]);
        Assert.DoesNotContain(AtCall(run), access => access.Resource.Region.StartsWith("di:System.Version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Positional_property_of_a_record_is_read_deep_and_written_by_an_argument_write()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state.Data);", "");
        var finding = Assert.Single(await Findings("System.Text.Json.JsonSerializer.Serialize(_state.Data);", "_shop.Add(_state.Data);"));

        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Value"] && access.Operation == AccessOperation.Read);
        Assert.Equal(["Value"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    // ---- helpers ----

    private static readonly FixtureOptions Packages = new() { MetadataReferences = LibraryPackages.BesideStubs };

    private static void AssertTwoSides(Finding finding)
    {
        Assert.NotEqual(finding.AccessA.Symbol, finding.AccessB.Symbol);
        Assert.Contains(new[] { finding.AccessA, finding.AccessB }, access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal));
    }

    /// <summary>The reads the reader makes at its known call, without the load of the singleton it starts from.</summary>
    private static Access[] AtCall(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal) &&
                                                access.Source.StartLine == ReaderLine() && access.Operation is AccessOperation.Read or AccessOperation.AtomicRead &&
                                                access.Resource.AccessPath is not ["_state"])
           .ToArray();

    private static int ReaderLine() =>
        Source("", "").Split('\n').Select((line, index) => (line, index)).First(pair => pair.line.Contains("// reader-work", StringComparison.Ordinal)).index + 1;

    private static async Task<IReadOnlyList<Finding>> Findings(string read, string write)
    {
        var solution = FixtureSolution.Create(Packages, ("Case.cs", Source(read, write)));
        return (await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None)).Findings;
    }

    private static EngineRun Run(string read, string write) =>
        AnalyzeScope(FixtureSolution.Create(Packages, ("Case.cs", Source(read, write))), "scope:Fixture");

    /// <summary>A singleton <c>State</c> one worker hands to a known call while another changes it, with the container's own
    /// <c>Version</c> given to it.</summary>
    private static string Source(string read, string write) => Usings + $$"""
        using System.Collections.Generic;
        using System.Collections.Concurrent;
        using Microsoft.EntityFrameworkCore;

        public sealed class Item { public int Value; }
        public sealed class MyList : List<Item> { public int Counter; }
        public sealed record Data(int Value);
        public sealed class Tally { public int Counter { get => field; set => field = value; } }
        public sealed class Shop : DbContext { }

        public static class Holder
        {
            public static List<Item> Items = new();
            public static string[] Names = new string[4];
        }

        public sealed class State
        {
            public Item First = new();
            public Item Compared = new();
            public Item Replacement = new();
            public Item Handed = new();
            public Func<int, Item> Factory = key => new Item();
            public Item[][] Jagged;
            public List<List<Item>> Nested = new();
            public List<Item> Inner = new();
            public List<Item> Items = new();
            public string[] Names = new string[4];
            public List<Item> Emptied = new();
            public ConcurrentDictionary<int, Item> Map = new();
            public List<Item> Plain = new();
            public ConcurrentQueue<Item> Queue = new();
            public IEnumerable<Item> Mixed;
            public MyList Own = new();
            public Data Data = new(1);
            public Tally Tally = new();
            public Item Boxed = new();
            public System.Runtime.CompilerServices.StrongBox<Item> Box = new();
            public Version Version;

            public State(Version version)
            {
                Version = version;
                Jagged = new[] { new[] { First } };
                var inner = new List<Item>();
                inner.Add(First);
                Nested.Add(inner);
                Nested.Add(Inner);
                Emptied.Remove(First);
                Map.TryAdd(1, new Item());
                Map.TryUpdate(1, Replacement, Compared);
                Map.GetOrAdd(2, key => new Item());
                Map.GetOrAdd(3, (key, argument) => new Item(), Handed);
                Map.AddOrUpdate(4, (key, argument) => new Item(), (key, old, argument) => old, Handed);
                Map.GetOrAdd(5, Factory);
                Box.Value = Boxed;
                Plain.Add(new Item());
                Queue.Enqueue(new Item());
                Mixed = DateTime.Now.Ticks > 0 ? Plain : Queue;
                Own.Add(First);
            }
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            public Reader(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{read}} // reader-work
                return Task.CompletedTask;
            }

            private static void Concat(System.ReadOnlySpan<string> data) => string.Concat(data.Slice(1, 1));
        }

        public sealed class Writer : BackgroundService
        {
            private readonly State _state;
            private readonly Shop _shop = new();
            public Writer(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{write}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Version>(); services.AddSingleton<State>(); services.AddHostedService<Reader>(); services.AddHostedService<Writer>();");
}
