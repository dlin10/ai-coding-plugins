using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class DeconstructionCallTests
{
    [Fact]
    public async Task Deconstruction_assignment_calls_a_source_Deconstruct() =>
        Has(await Run("var (value, stamp) = new Shape();"), "Shape.Deconstruct");

    [Fact]
    public async Task Deconstruction_into_existing_variables_calls_a_source_Deconstruct() =>
        Has(await Run("int value = 0, stamp = 0; (value, stamp) = new Shape();"), "Shape.Deconstruct");

    [Fact]
    public async Task Nested_deconstruction_calls_each_source_Deconstruct()
    {
        var result = await Run("var ((value, stamp), other) = new Outer();");
        Has(result, "Outer.Deconstruct");
        Has(result, "Shape.Deconstruct");
    }

    [Fact]
    public async Task Foreach_deconstruction_calls_a_source_Deconstruct() =>
        Has(await Run("foreach (var (value, stamp) in new Shape[] { new Shape() }) _ = value;"), "Shape.Deconstruct");

    [Fact]
    public async Task Source_extension_Deconstruct_is_called() =>
        Has(await Run("var (value, stamp) = new ExtensionShape();"), "Extensions.Deconstruct");

    [Fact]
    public async Task Tuple_deconstruction_keeps_the_element_split()
    {
        var result = await Run("var (first, second) = (new Piece(), new Piece()); first.Hits++;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]) &&
                                                  access.Resource.Region.Contains("Piece", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeyValuePair_deconstruction_keeps_key_and_value_storage()
    {
        var result = await Run("var pair = new System.Collections.Generic.KeyValuePair<Piece, Piece>(new Piece(), new Piece()); var (key, value) = pair; key.Hits++;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]) &&
                                                  access.Resource.Region.Contains("Piece", StringComparison.Ordinal));
    }

    private static Task<AnalysisResult> Run(string action) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + $$"""
            public static class Marks { public static int Calls; public static int Outer; public static int Extension; }
            public sealed class Piece { public int Hits; }
            public sealed class Shape
            {
                public int Value;
                public void Deconstruct(out int value, out int stamp)
                {
                    Marks.Calls++;
                    value = Value;
                    stamp = 0;
                }
            }
            public sealed class Outer
            {
                private readonly Shape _inner = new();
                public void Deconstruct(out Shape inner, out int other)
                {
                    Marks.Outer++;
                    inner = _inner;
                    other = 0;
                }
            }
            public sealed class ExtensionShape { public int Value; }
            public static class Extensions
            {
                public static void Deconstruct(this ExtensionShape shape, out int value, out int stamp)
                {
                    Marks.Extension++;
                    value = shape.Value;
                    stamp = 0;
                }
            }
            public sealed class CaseController : ControllerBase { public void Post() { {{action}} } }
            """ + Startup())), ROOT_DIRECTORY, CancellationToken.None);

    private static void Has(AnalysisResult result, string symbol) =>
        Assert.Contains(result.Accesses, access => access.Symbol.Contains(symbol, StringComparison.Ordinal));
}
