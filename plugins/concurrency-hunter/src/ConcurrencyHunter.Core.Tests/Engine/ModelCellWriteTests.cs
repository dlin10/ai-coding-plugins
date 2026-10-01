using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ModelCellWriteTests
{
    [Fact]
    public void Writes_cells_writes_every_cell_of_the_array_argument()
    {
        var run = Run("Cells.Lib.Rewrite(_state.Numbers);");
        var write = Assert.Single(AtCall(run), access => access.Resource.Selector is not null);
        Assert.Equal(AccessOperation.Write, write.Operation);
        Assert.Equal("[?]", write.Resource.Selector!.Text);
        Assert.Equal(0, run.Counter(CoverageCounters.ELEMENT_OPERATION) - Run("").Counter(CoverageCounters.ELEMENT_OPERATION));
    }

    [Fact]
    public void Cell_write_pairs_with_a_cell_read_of_the_same_array()
    {
        var run = Run("Cells.Lib.Rewrite(_state.Numbers);", "_ = _state.Numbers[7];");
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Selector is not null &&
            (pair.First.Operation == AccessOperation.Write || pair.Second.Operation == AccessOperation.Write));
    }

    [Fact]
    public void Cell_write_hides_the_same_calls_read_of_those_cells()
    {
        var run = Run("Cells.Lib.Merge(_state.Cells, _state.Others);");
        var cells = AtCall(run).Where(access => access.Resource.Selector is not null).ToArray();
        Assert.Contains(cells, access => access.Resource.Member.Name == "Cells" && access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(cells, access => access.Resource.Member.Name == "Cells" && access.Operation == AccessOperation.Read);
        Assert.Contains(cells, access => access.Resource.Member.Name == "Others" && access.Operation == AccessOperation.Read);
    }

    [Fact]
    public void Cell_write_is_never_atomic()
    {
        var run = Run("Cells.Lib.Rewrite(_state.Numbers);");
        var writes = AtCall(run).Where(access => access.Resource.Selector is not null).ToArray();
        Assert.NotEmpty(writes);
        Assert.All(writes, access => Assert.Equal(AccessOperation.Write, access.Operation));
    }

    [Fact]
    public void Cell_write_keeps_what_the_cells_hold()
    {
        var run = Run("Cells.Lib.Rewrite(_state.Cells); _state.Cells[0].Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
    }

    [Fact]
    public void Stores_puts_its_values_into_the_cells()
    {
        var run = Run("Cells.Lib.Fill(_state.Cells, new Added()); _state.Cells[0].Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.Contains("#Added", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Object_stored_into_a_shared_array_is_escaped()
    {
        using var repo = new CellModelRepository(ModelCellFixture.Models);
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution("Cells.Lib.Fill(_state.Cells, new Added()); _state.Cells[0].Hits = 1;",
            "_ = _state.Cells[0].Hits;"), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        Assert.Contains(result.Findings, finding => finding.Resource.Region.EndsWith("#Added", StringComparison.Ordinal) && finding.Resource.AccessPath.SequenceEqual(["Hits"]));
    }

    [Fact]
    public void Writes_cells_on_this_writes_the_receivers_cells()
    {
        var model = """{"schemaVersion":1,"assemblies":["System.Private.CoreLib"],"models":[{"member":"M:System.Array.SetValue(System.Object,System.Int32)","effects":{"this":["writes-cells"]}}]}""";
        var run = Run("((Array)_state.Numbers).SetValue(1, 0);", models: model);
        Assert.Contains(AtCall(run), access => access.Resource.Selector is not null && access.Operation == AccessOperation.Write);
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Theory]
    [InlineData("Cells.Lib.Rewrite(_state.Numbers);")]
    [InlineData("var array = _state.Numbers; Cells.Lib.Rewrite(array);")]
    [InlineData("Rewrite(_state.Numbers);")]
    [InlineData("Cells.Lib.Rewrite(Get());")]
    [InlineData("var box = new List<int[]> { _state.Numbers }; Cells.Lib.Rewrite(box[0]);")]
    [InlineData("Action action = () => Cells.Lib.Rewrite(_state.Numbers); action();")]
    public void Cell_write_reaches_an_array_through_every_route(string work)
    {
        var run = Run(work);
        Assert.Contains(AtCall(run), access => access.Resource.Selector is not null && access.Operation == AccessOperation.Write && access.Resource.Member.Name == "Numbers");
    }

    [Fact]
    public void Stores_act_at_the_call_even_with_a_sequence_result()
    {
        var run = Run("_ = Cells.Lib.Lazy(_state.Cells, _state.Produce(), _state.Other); _state.Cells[0].Hits = 1;");
        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && run.Execution.Analysis.Execution(access.ExecutionId).Kind == ExecutionKind.Root);
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.Contains("#Added", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opaque_result_stored_into_a_shared_array_feeds_it()
    {
        var result = await AnalyzeResult("Cells.Lib.Fill(_state.Objects, Cells.Lib.Unknown());");
        Assert.Contains(Assert.Single(result.Coverage).Gaps, gap => gap.Callee.Contains("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opaque_result_stored_into_a_confined_array_does_not()
    {
        var result = await AnalyzeResult("var array = new object[1]; Cells.Lib.Fill(array, Cells.Lib.Unknown());");
        var coverage = Assert.Single(result.Coverage);
        Assert.Equal(0, coverage.Skips[CoverageCounters.MODEL_ENTRY_REJECTED]);
        Assert.DoesNotContain(coverage.Gaps, gap => gap.Callee.Contains("Unknown", StringComparison.Ordinal));
    }

    private static IEnumerable<Access> AtCall(EngineRun run) => run.Collection.Accesses.Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal));

    private static EngineRun Run(string work, string read = "", string? models = null)
    {
        var solution = Solution(work, read);
        return AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, models ?? ModelCellFixture.Models));
    }

    private static async Task<AnalysisResult> AnalyzeResult(string work)
    {
        using var repo = new CellModelRepository(ModelCellFixture.Models);
        return await PhaseOneAnalyzer.AnalyzeAsync(Solution(work), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
    }

    private static Solution Solution(string work, string read = "") => FixtureSolution.Create(
        new FixtureOptions { MetadataReferences = [ModelCellFixture.Library] }, ("Case.cs", Usings + $$"""
        using System.Collections.Generic;
        public class Item { public int Hits; }
        public sealed class Old : Item { }
        public sealed class Added : Item { }
        public sealed class State
        {
            public readonly int[] Numbers = new int[8];
            public readonly Item[] Cells = new Item[] { new Old() };
            public readonly Item[] Others = new Item[] { new Added() };
            public readonly object[] Objects = new object[1];
            public readonly Item Other = new Added();
            public int Count;
            public IEnumerable<Item> Produce() { Count = 1; yield return Other; }
        }
        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;
            private int[] Get() => _state.Numbers;
            private void Rewrite(int[] array) => Cells.Lib.Rewrite(array);
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{work}} return Task.CompletedTask; }
        }
        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{read}} return Task.CompletedTask; }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();")));
}

internal static class ModelCellFixture
{
    internal const string Models = """
        {"schemaVersion":1,"assemblies":["Cells"],"models":[
          {"member":"M:Cells.Lib.Rewrite``1(``0[])","effects":{"array":["writes-cells"]}},
          {"member":"M:Cells.Lib.Fill``1(``0[],``0)","effects":{"array":["writes-cells"]},"stores":{"array":["arg:value"]}},
          {"member":"M:Cells.Lib.Merge``1(``0[],``0[])","effects":{"array":["writes-cells"]},"stores":{"array":["elements(arg:array)","elements(arg:other)"]}},
          {"member":"M:Cells.Lib.Lazy``1(``0[],System.Collections.Generic.IEnumerable{``0},``0)","effects":{"array":["writes-cells"]},"stores":{"array":["elements(arg:source)"]},"result":"sequence(arg:other)"}
        ]}
        """;

    internal static MetadataReference Library => Reference.Value;
    private static readonly Lazy<MetadataReference> Reference = new(() =>
    {
        var compilation = CSharpCompilation.Create("Cells", [CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            namespace Cells;
            public static class Lib
            {
                public static void Rewrite<T>(T[] array) { }
                public static void Fill<T>(T[] array, T value) { }
                public static void Merge<T>(T[] array, T[] other) { }
                public static IEnumerable<T> Lazy<T>(T[] array, IEnumerable<T> source, T other) => null!;
                public static object Unknown() => null!;
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });

    internal static LibraryModels Resolve(Solution solution, string text)
    {
        using var repo = new CellModelRepository(text);
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var files = ProjectModelFiles.Read(repo.Root);
        var (models, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));
        Assert.Empty(rejections);
        return models;
    }
}

internal sealed class CellModelRepository : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("ch-cells-").FullName;

    internal CellModelRepository(string text)
    {
        var directory = Path.Combine(Root, ".concurrency-hunter", "models");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cells.json"), text, new UTF8Encoding(false));
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
