using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The argument write of a known call (R3): every field of the objects the argument may be, one level deep, written where
/// the call stands; a collection of them has its objects written and not itself.</summary>
public sealed class ArgumentWriteTests
{
    // ---- the pair an argument write makes ----

    [Fact]
    public async Task DbContext_Add_against_a_field_write_is_DCA1001_by_the_write()
    {
        var finding = Assert.Single(await Findings("_shop.Add(_state.Entity);", "_state.Entity.Stock = 1;"));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal(["Stock"], finding.Resource.AccessPath);
        AssertAdderWrites(finding);
    }

    [Fact]
    public async Task DbSet_Add_against_a_field_write_is_DCA1001_by_the_write()
    {
        var finding = Assert.Single(await Findings("_shop.Items.Add(_state.Entity);", "_state.Entity.Stock = 1;"));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal(["Stock"], finding.Resource.AccessPath);
        AssertAdderWrites(finding);
    }

    [Fact]
    public async Task Nested_object_of_the_entity_is_read_and_not_written()
    {
        var run = Run("_shop.Add(_state.Entity);", "");
        // The entity's own field that holds the object is written, and the writer reads it on the way: that is a pair of its own.
        var finding = Assert.Single(await Findings("_shop.Add(_state.Entity);", "_state.Entity.Detail.Note = 1;"),
                                    finding => finding.Resource.AccessPath is ["Note"]);

        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Note"] && access.Operation == AccessOperation.Read);
        Assert.DoesNotContain(AtCall(run), access => access.Resource.AccessPath is ["Note"] && access.Operation == AccessOperation.Write);
        Assert.Contains(new[] { finding.AccessA, finding.AccessB }, access => access.Symbol.StartsWith("Adder.", StringComparison.Ordinal) &&
                                                                               access.Operation == AccessOperation.Read);
    }

    [Fact]
    public void Written_field_is_not_also_read_at_the_call()
    {
        var run = Run("_shop.Add(_state.Entity);", "");

        var stock = Assert.Single(AtCall(run), access => access.Resource.AccessPath is ["Stock"]);
        Assert.Equal(AccessOperation.Write, stock.Operation);
    }

    // ---- collections of entities ----

    [Fact]
    public void AddRange_with_expanded_params_writes_each_element()
    {
        var run = Run("_shop.AddRange(_state.First, _state.Second);", "");

        Assert.Equal(2, AtCall(run).Count(access => access.Resource.AccessPath is ["Stock"] && access.Operation == AccessOperation.Write));
    }

    [Fact]
    public void AddRange_with_a_ready_array_writes_its_objects_and_not_its_cells()
    {
        var run = Run("_shop.AddRange(_state.Batch);", "");

        Assert.Equal(2, AtCall(run).Count(access => access.Resource.AccessPath is ["Stock"] && access.Operation == AccessOperation.Write));
        // Neither the cells nor the array object itself are written, however the write would name them.
        Assert.DoesNotContain(AtCall(run), access => access.Operation != AccessOperation.Read &&
                                                     (access.Resource.AccessPath[0] == "Batch" || access.Resource.Region.Contains("Item[]", StringComparison.Ordinal)));
        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Batch", "[?]"] && access.Operation == AccessOperation.Read);
    }

    [Fact]
    public void AddRange_with_a_sequence_writes_its_objects_and_not_its_structure()
    {
        var run = Run("_shop.Items.AddRange(_state.Pending);", "");

        Assert.Equal(2, AtCall(run).Count(access => access.Resource.AccessPath is ["Stock"] && access.Operation == AccessOperation.Write));
        Assert.DoesNotContain(AtCall(run), access => access.Operation != AccessOperation.Read &&
                                                     (access.Resource.AccessPath[0] == "Pending" || access.Resource.Region.Contains("List", StringComparison.Ordinal)));
        Assert.Contains(AtCall(run), access => access.Resource.AccessPath is ["Pending"] && access.Resource.CollectionId is not null);
    }

    [Fact]
    public async Task Entity_of_a_collection_type_of_its_own_is_written_itself()
    {
        var run = Run("_shop.Add(_state.Own);", "");
        var finding = Assert.Single(await Findings("_shop.Add(_state.Own);", "_state.Own.Counter = 1;"));

        // The parameter takes one entity: its own field is written, and the objects in it are not.
        Assert.Equal(["Counter"], finding.Resource.AccessPath);
        Assert.DoesNotContain(AtCall(run), access => access.Resource.AccessPath is ["Stock"] && access.Operation == AccessOperation.Write);
    }

    [Fact]
    public async Task Sequence_of_a_type_of_its_own_is_not_written_itself()
    {
        var run = Run("_shop.AddRange(_state.Source);", "");

        // The parameter takes entities: the sequence handing them over is no entity, however it is declared.
        Assert.DoesNotContain(AtCall(run), access => access.Resource.AccessPath is ["Counter"] && access.Operation == AccessOperation.Write);
        Assert.Empty(await Findings("_shop.AddRange(_state.Source);", "_ = _state.Source.Counter;"));
    }

    [Fact]
    public async Task Sequence_of_a_type_of_its_own_writes_the_objects_its_collections_hold()
    {
        // The sequence enumerates the list its field holds, which holds the first item.
        var finding = Assert.Single(await Findings("_shop.AddRange(_state.Source);", "_ = _state.First.Stock;"));

        Assert.Equal(["Stock"], finding.Resource.AccessPath);
        AssertAdderWrites(finding);
    }

    [Theory]
    [InlineData("_state.Deep")]
    [InlineData("_state.Boxed")]
    [InlineData("_state.Pair")]
    public async Task Sequence_of_a_type_of_its_own_writes_every_object_of_its_element_type_it_reaches(string sequence)
    {
        // Two levels down, behind a library object, or in a field its iterator yields: the enumerator may hand any of them out.
        var ordinary = Assert.Single(await Findings("_state.First.Stock = 0;", "_ = _state.First.Stock;"));
        var written = Assert.Single(await Findings($"_shop.AddRange({sequence});", "_ = _state.First.Stock;"));

        Assert.Equal(ordinary.Resource.Identity, written.Resource.Identity);
        AssertAdderWrites(written);
    }

    [Fact]
    public async Task Sequence_of_a_type_of_its_own_writes_no_object_of_another_type_it_reaches()
    {
        // The holder on the way and the objects of an entity are no entities of the sequence: both are read, neither is written.
        Assert.Empty(await Findings("_shop.AddRange(_state.Deep);", "_ = _state.Deep.Middle.Counter;"));
        Assert.DoesNotContain(await Findings("_shop.AddRange(_state.Deep);", "_ = _state.First.Detail.Note;"),
                              finding => finding.Resource.AccessPath is ["Note"]);
    }

    // ---- the other members ----

    [Theory]
    [InlineData("_shop.Update(_state.Entity);")]
    [InlineData("_shop.Attach(_state.Entity);")]
    [InlineData("_shop.Remove(_state.Entity);")]
    [InlineData("_shop.Items.Update(_state.Entity);")]
    public async Task Update_Attach_and_Remove_write_the_entity(string call)
    {
        var finding = Assert.Single(await Findings(call, "_state.Entity.Stock = 1;"));

        AssertAdderWrites(finding);
    }

    [Fact]
    public async Task Entry_writes_the_entity()
    {
        var finding = Assert.Single(await Findings("_shop.Entry(_state.Entity);", "_state.Entity.Stock = 1;"));

        AssertAdderWrites(finding);
    }

    [Fact]
    public void SaveChanges_Find_and_ToListAsync_make_no_access()
    {
        const string Items = "var items = _shop.Items; ";
        var run = Run(Items + "_shop.SaveChanges(); _shop.Find<Item>(1); _ = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(items);", "");
        var without = Run(Items, "");

        // Only the loads of the context and its set stand on the line; the three calls add nothing.
        Assert.DoesNotContain(AtCall(run), access => access.Resource.AccessPath is not ["Items"]);
        Assert.Equal(3, run.Counter(CoverageCounters.KNOWN_CALL) - without.Counter(CoverageCounters.KNOWN_CALL));
    }

    // ---- protection and types from metadata ----

    [Fact]
    public async Task Write_under_the_callers_lock_is_protected()
    {
        Assert.Empty(await Findings("lock (_state) { _shop.Add(_state.Entity); }", "lock (_state) { _state.Entity.Stock = 1; }"));
    }

    [Fact]
    public void Entity_of_a_type_from_metadata_is_one_wildcard_write()
    {
        var run = Run("_shop.Add(_state.Builder);", "");

        var write = Assert.Single(AtCall(run), access => access.Operation == AccessOperation.Write);
        Assert.True(write.Resource.IsWildcard);
        Assert.Contains("StringBuilder", write.Resource.Region, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static readonly FixtureOptions Packages = new() { MetadataReferences = LibraryPackages.BesideStubs };

    private static void AssertAdderWrites(Finding finding)
    {
        Assert.NotEqual(finding.AccessA.Symbol, finding.AccessB.Symbol);
        Assert.Contains(new[] { finding.AccessA, finding.AccessB },
                        access => access.Symbol.StartsWith("Adder.", StringComparison.Ordinal) && access.Operation == AccessOperation.Write);
    }

    /// <summary>What the adder does on the line of its call, without the loads that name the argument.</summary>
    private static Access[] AtCall(EngineRun run) =>
        run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Adder.", StringComparison.Ordinal) &&
                                                access.Source.StartLine == AdderLine() &&
                                                access.Resource.AccessPath is not (["_state"] or ["_shop"] or ["Entity"] or ["First"] or ["Second"] or ["Builder"]))
           .ToArray();

    private static int AdderLine() =>
        Source("", "").Split('\n').Select((line, index) => (line, index)).First(pair => pair.line.Contains("// adder-work", StringComparison.Ordinal)).index + 1;

    private static async Task<IReadOnlyList<Finding>> Findings(string add, string write)
    {
        var solution = FixtureSolution.Create(Packages, ("Case.cs", Source(add, write)));
        return (await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None)).Findings;
    }

    private static EngineRun Run(string add, string write) =>
        AnalyzeScope(FixtureSolution.Create(Packages, ("Case.cs", Source(add, write))), "scope:Fixture");

    /// <summary>A singleton <c>State</c> holding entities one worker hands to its own context while another changes them.</summary>
    private static string Source(string add, string write) => Usings + $$"""
        using System.Collections.Generic;
        using Microsoft.EntityFrameworkCore;

        public sealed class Detail { public int Note; }
        public sealed class Item { public int Stock; public Detail Detail = new(); }
        public sealed class ItemList : List<Item> { public int Counter; }

        public sealed class ItemSource : IEnumerable<Item>
        {
            public int Counter;
            public List<Item> Items = new();
            public IEnumerator<Item> GetEnumerator() => Items.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class Middle { public int Counter; public List<Item> Items = new(); }

        public sealed class DeepSource : IEnumerable<Item>
        {
            public Middle Middle = new();
            public IEnumerator<Item> GetEnumerator() => Middle.Items.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class BoxedSource : IEnumerable<Item>
        {
            public System.Runtime.CompilerServices.StrongBox<List<Item>> Box = new();
            public IEnumerator<Item> GetEnumerator() => Box.Value!.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class PairSource : IEnumerable<Item>
        {
            public Item? A;
            public IEnumerator<Item> GetEnumerator() { yield return A!; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class Shop : DbContext
        {
            public DbSet<Item> Items { get; set; } = null!;
        }

        public sealed class State
        {
            public Item Entity = new();
            public Item First = new();
            public Item Second = new();
            public Item[] Batch;
            public List<Item> Pending = new();
            public System.Text.StringBuilder Builder = new();
            public ItemList Own = new();
            public ItemSource Source = new();
            public DeepSource Deep = new();
            public BoxedSource Boxed = new();
            public PairSource Pair = new();

            public State()
            {
                Batch = new[] { First, Second };
                Pending.Add(First);
                Pending.Add(Second);
                Own.Add(First);
                Source.Items.Add(First);
                Deep.Middle.Items.Add(First);
                var boxed = new List<Item>();
                boxed.Add(First);
                Boxed.Box.Value = boxed;
                Pair.A = First;
            }
        }

        public sealed class Adder : BackgroundService
        {
            private readonly State _state;
            private readonly Shop _shop = new();
            public Adder(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{add}} // adder-work
                return Task.CompletedTask;
            }
        }

        public sealed class Writer : BackgroundService
        {
            private readonly State _state;
            public Writer(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{write}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Adder>(); services.AddHostedService<Writer>();");
}
