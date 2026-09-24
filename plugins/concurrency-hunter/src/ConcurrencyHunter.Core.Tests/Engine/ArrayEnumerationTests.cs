using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Enumeration reads every cell of what it enumerates (ADR 0010): a <c>foreach</c> over a shared array meets a write of
/// any of its cells as a read under an index nothing proves does, whatever static type the array is enumerated through, and its
/// loop variable is each object those cells hold.</summary>
public sealed class ArrayEnumerationTests
{
    [Fact]
    public async Task Foreach_over_an_array_pairs_with_a_cell_write_as_an_unproven_index_does()
    {
        var enumerated = Assert.Single(await Findings("foreach (var x in _state.C) { }", "_state.C[1] = new Item();"));
        var indexed = Assert.Single(await Findings("_ = _state.C[System.DateTime.Now.Second];", "_state.C[1] = new Item();"));

        Assert.Equal("DCA1001", enumerated.RuleId);
        Assert.Equal(indexed.Resource.Identity, enumerated.Resource.Identity);
        AssertTwoSides(enumerated);
    }

    [Fact]
    public async Task Foreach_over_a_string_array_pairs_with_a_cell_write()
    {
        var finding = Assert.Single(await Findings("foreach (var s in _state.Names) { }", "_state.Names[0] = \"x\";"));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal("Names", finding.Resource.AccessPath[0]);
        Assert.NotNull(finding.Resource.Selector);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Foreach_over_a_List_pairs_with_an_indexer_set_on_the_cell_as_well_as_the_structure()
    {
        var findings = await Findings("foreach (var x in _state.Items) { }", "_state.Items[0] = new Item();");

        Assert.Contains(findings, finding => finding.Resource.AccessPath[0] == "Items" && finding.Resource.Selector is not null);
        Assert.Contains(findings, finding => finding.Resource.AccessPath[0] == "Items" && finding.Resource.Selector is null);
        Assert.All(findings, AssertTwoSides);
    }

    [Fact]
    public async Task Foreach_over_an_array_against_a_read_of_a_cell_is_no_finding()
    {
        Assert.Empty(await Findings("foreach (var x in _state.C) { }", "_ = _state.C[1];"));
    }

    // ---- an array enumerated through an interface or a slice ----

    [Theory]
    [InlineData("foreach (var x in _state.Seq) { }")]
    [InlineData("foreach (var x in _state.ReadOnly) { }")]
    [InlineData("foreach (var x in _state.C.AsSpan()) { }")]
    [InlineData("System.ReadOnlySpan<Item> span = _state.C; foreach (var x in span) { }")]
    public async Task Foreach_over_an_array_behind_an_interface_or_a_span_pairs_with_a_cell_write(string read)
    {
        var finding = Assert.Single(await Findings(read, "_state.C[1] = new Item();"));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.NotNull(finding.Resource.CollectionId);
        Assert.NotNull(finding.Resource.Selector);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Foreach_under_the_lock_the_writer_takes_is_protected()
    {
        Assert.Empty(await Findings("lock (_state) { foreach (var x in _state.Seq) { } }", "lock (_state) { _state.C[1] = new Item(); }"));
    }

    /// <summary>A value two branches meet in names no field it came from; the arrays it may be are read where a field holds them.</summary>
    [Fact]
    public async Task Foreach_over_an_array_either_branch_may_give_pairs_with_a_cell_write()
    {
        var finding = Assert.Single(await Findings("var items = System.DateTime.Now.Second > 30 ? _state.C : _state.D; foreach (var x in items) { }",
                                                   "_state.C[1] = new Item();"));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.NotNull(finding.Resource.Selector);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Foreach_over_an_interface_holding_a_List_conflicts_with_Add_on_the_structure()
    {
        var finding = Assert.Single(await Findings("foreach (var x in _state.ItemsSeq) { }", "_state.Items.Add(new Item());"),
                                    finding => finding.Resource.Selector is null);

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.NotNull(finding.Resource.CollectionId);
        AssertTwoSides(finding);
    }

    [Fact]
    public async Task Foreach_over_an_interface_holding_a_ConcurrentDictionary_reads_it_atomically_and_meets_no_TryAdd()
    {
        var result = await Analyze("foreach (var pair in _state.MapSeq) { }", "_state.Map.TryAdd(2, new Item());");

        Assert.DoesNotContain(result.Findings, finding => finding.Resource.CollectionId is not null);
        Assert.Contains(result.Accesses, access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal) &&
                                                   access.Resource.CollectionId is not null && access.Operation == AccessOperation.AtomicRead);
    }

    // ---- the loop variable ----

    [Theory]
    [InlineData("foreach (var x in _state.C) { _ = x.Value; }", "_state.First.Value = 1;")]
    [InlineData("foreach (var x in _state.C) { x.Value = 1; }", "_ = _state.First.Value;")]
    [InlineData("foreach (var x in _state.Seq) { x.Value = 1; }", "_ = _state.First.Value;")]
    [InlineData("foreach (var x in _state.C.AsSpan()) { x.Value = 1; }", "_ = _state.First.Value;")]
    [InlineData("System.ReadOnlySpan<Item> span = _state.C; foreach (var x in span) { x.Value = 1; }", "_ = _state.First.Value;")]
    public async Task Loop_variable_is_each_object_the_enumerated_array_holds(string read, string write)
    {
        var finding = Assert.Single(await Findings(read, write));

        Assert.Equal("DCA1001", finding.RuleId);
        Assert.Equal(["Value"], finding.Resource.AccessPath);
        AssertTwoSides(finding);
    }

    // ---- helpers ----

    private static void AssertTwoSides(Finding finding)
    {
        Assert.NotEqual(finding.AccessA.Symbol, finding.AccessB.Symbol);
        Assert.Contains(new[] { finding.AccessA, finding.AccessB }, access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<Finding>> Findings(string read, string write) => (await Analyze(read, write)).Findings;

    private static Task<AnalysisResult> Analyze(string read, string write) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Source(read, write))), ROOT_DIRECTORY, CancellationToken.None);

    /// <summary>A singleton <c>State</c> one worker enumerates while another changes a cell of it or an object in one. The
    /// interface fields hold the collections of the fields above them.</summary>
    private static string Source(string read, string write) => Usings + $$"""
        using System.Collections.Concurrent;
        using System.Collections.Generic;

        public sealed class Item { public int Value; }

        public sealed class State
        {
            public Item First = new();
            public Item[] C = new Item[2];
            public Item[] D = new Item[2];
            public string[] Names = new string[4];
            public List<Item> Items = new() { new Item() };
            public ConcurrentDictionary<int, Item> Map = new();
            public IEnumerable<Item> Seq;
            public IReadOnlyList<Item> ReadOnly;
            public IEnumerable<Item> ItemsSeq;
            public IEnumerable<KeyValuePair<int, Item>> MapSeq;

            public State()
            {
                C[0] = First;
                Seq = C;
                ReadOnly = C;
                ItemsSeq = Items;
                MapSeq = Map;
            }
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            public Reader(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{read}}
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
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Reader>(); services.AddHostedService<Writer>();");
}
