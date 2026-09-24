using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Enumeration reads every cell of what it enumerates (ADR 0010): a <c>foreach</c> over a shared array meets a write of
/// any of its cells as a read under an index nothing proves does.</summary>
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

    // ---- helpers ----

    private static void AssertTwoSides(Finding finding)
    {
        Assert.NotEqual(finding.AccessA.Symbol, finding.AccessB.Symbol);
        Assert.Contains(new[] { finding.AccessA, finding.AccessB }, access => access.Symbol.StartsWith("Reader.", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<Finding>> Findings(string read, string write)
    {
        var solution = FixtureSolution.Create(("Case.cs", Source(read, write)));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        return result.Findings;
    }

    /// <summary>A singleton <c>State</c> one worker enumerates while another changes a cell of it.</summary>
    private static string Source(string read, string write) => Usings + $$"""
        using System.Collections.Generic;

        public sealed class Item { public int Value; }

        public sealed class State
        {
            public Item[] C = new Item[2];
            public string[] Names = new string[4];
            public List<Item> Items = new() { new Item() };
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
