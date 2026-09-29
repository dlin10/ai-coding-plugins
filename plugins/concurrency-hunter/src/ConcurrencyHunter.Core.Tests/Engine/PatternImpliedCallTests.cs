using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class PatternImpliedCallTests
{
    [Fact]
    public async Task Positional_pattern_calls_Deconstruct()
    {
        var result = await Run("_ = new Shape() is (var first, var second);");
        Has(result, "Shape.Deconstruct");
    }

    [Fact]
    public async Task Positional_pattern_binds_the_Deconstruct_outputs()
    {
        var result = await Run("if (new Shape() is (var first, _)) first.Hits++;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]) &&
                                                  access.Resource.Region.Contains("Piece", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Positional_pattern_in_a_switch_statement_calls_Deconstruct()
    {
        var result = await Run("switch (new Shape()) { case (var first, _): first.Hits++; break; }");
        Has(result, "Shape.Deconstruct");
    }

    [Fact]
    public async Task Positional_pattern_through_ITuple_stays_unsupported()
    {
        var result = await Run("object value = (1, 2); _ = value is (var first, var second);");
        Assert.True(result.Coverage[0].Skips[CoverageCounters.UNSUPPORTED_OPERATION] > 0);
    }

    [Fact]
    public async Task List_pattern_on_a_source_type_calls_Count_and_the_indexer()
    {
        var result = await Run("_ = new Sequence() is [var item];");
        Has(result, "Sequence.get_Count");
        Has(result, "Sequence.get_Item");
    }

    [Fact]
    public async Task List_pattern_in_a_switch_expression_calls_the_indexer()
    {
        var result = await Run("_ = new Sequence() switch { [var item] => item.Hits, _ => 0 };");
        Has(result, "Sequence.get_Item");
    }

    [Fact]
    public async Task List_pattern_on_a_List_reads_the_structure_and_cells()
    {
        var result = await Run("var items = new System.Collections.Generic.List<Piece> { new Piece() }; if (items is [var item]) item.Hits++;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]) &&
                                                  access.Resource.Region.Contains("Piece", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_pattern_on_an_array_reads_its_length_and_cells()
    {
        var result = await Run("Piece[] items = new Piece[] { new Piece() }; if (items is [var item]) item.Hits++;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]) &&
                                                  access.Resource.Region.Contains("Piece", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_pattern_on_a_string_makes_no_access_and_no_gap()
    {
        var result = await Run("_ = \"abc\" is ['a', ..];");
        Assert.DoesNotContain(result.Accesses, access => access.Symbol.Contains("String", StringComparison.Ordinal));
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task List_pattern_designation_binds_the_tested_object()
    {
        var result = await Run("if (new Sequence() is [_, ..] list) { var count = list.Count; }");
        Has(result, "Sequence.get_Count");
        Assert.Equal(0, result.Coverage[0].Skips[CoverageCounters.UNSUPPORTED_OPERATION]);
    }

    [Fact]
    public async Task List_element_designation_binds_what_the_indexer_returned()
    {
        var result = await Run("if (new Sequence() is [var item]) item.Hits++;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]) &&
                                                  access.Resource.Region.Contains("Piece", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slice_pattern_calls_Slice_only_with_a_subpattern()
    {
        var result = await Run("_ = new Sequence() is [_, ..]; if (new Sequence() is [_, .. var rest]) _ = rest.Count;");
        Assert.Single(result.Accesses, access => access.Symbol.Contains("Sequence.Slice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slice_pattern_calls_the_range_indexer_when_the_compiler_binds_one()
    {
        var result = await Run("if (new RangeSequence() is [_, .. var rest]) _ = rest.Count;");
        Has(result, "RangeSequence.get_Item");
    }

    [Fact]
    public void Slice_pattern_passes_a_constructed_range_to_the_indexer()
    {
        var run = EngineFixture.Reach("""
            using System;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Routing;
            using Microsoft.Extensions.DependencyInjection;
            public sealed class Sequence
            {
                public int Count => 3;
                public int this[int index] => 0;
                public Sequence this[Range range] => this;
            }
            public sealed class CaseController : ControllerBase
            {
                public void Post() { if (new Sequence() is [_, .. var rest, _]) _ = rest.Count; }
            }
            """ + Startup());
        var body = Assert.Single(run.Result.Bodies.Values, body => body.OwnerSymbol.Contains("CaseController.Post", StringComparison.Ordinal));
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        var indexer = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains("Sequence.get_Item(Range)", StringComparison.Ordinal));
        var range = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Method.Contains("System.Range..ctor", StringComparison.Ordinal));
        Assert.Equal(range.ReceiverValue, Assert.Single(indexer.ArgumentValues));
        var indices = operations.OfType<IrCallOperation>().Where(call => call.Method.Contains("System.Index..ctor", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, indices.Length);
        Assert.Equal(indices.Select(call => call.ReceiverValue), range.ArgumentValues.Select(value => (int?)value));
        var start = indices[0].ArgumentValues[0];
        Assert.Equal("1", Assert.Single(body.Values, value => value.Id == start).Name);
        var end = Assert.Single(operations.OfType<IrComputeOperation>(), compute => compute.ResultValue == indices[1].ArgumentValues[0]);
        Assert.Equal("Add", end.Operator);
        Assert.Equal(start, end.OperandValues[0]);
        Assert.Contains(operations.OfType<IrComputeOperation>(), compute => compute.Operator == "Subtract" &&
                                                                          compute.ResultValue == end.OperandValues[1]);
    }

    private static Task<AnalysisResult> Run(string action) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + $$"""
            public static class Marks { public static int Calls; public static int Count; public static int Index; public static int Slice; public static int Range; }
            public sealed class Piece { public int Hits; }
            public sealed class Shape
            {
                private readonly Piece _first = new();
                private readonly Piece _second = new();
                public void Deconstruct(out Piece first, out Piece second)
                {
                    Marks.Calls++;
                    first = _first;
                    second = _second;
                }
            }
            public sealed class Sequence
            {
                private readonly Piece _item = new();
                public int Count { get { Marks.Count++; return 1; } }
                public Piece this[int index] { get { Marks.Index++; return _item; } }
                public Sequence Slice(int start, int length) { Marks.Slice++; return this; }
            }
            public sealed class RangeSequence
            {
                private readonly Piece _item = new();
                public int Count => 1;
                public Piece this[int index] => _item;
                public RangeSequence this[Range range] { get { Marks.Range++; return this; } }
            }
            public sealed class CaseController : ControllerBase { public void Post() { {{action}} } }
            """ + Startup())), ROOT_DIRECTORY, CancellationToken.None);

    private static void Has(AnalysisResult result, string symbol) =>
        Assert.Contains(result.Accesses, access => access.Symbol.Contains(symbol, StringComparison.Ordinal));
}
