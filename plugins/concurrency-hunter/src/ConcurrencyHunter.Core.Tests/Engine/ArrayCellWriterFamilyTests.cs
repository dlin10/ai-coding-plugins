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

public sealed class ArrayCellWriterFamilyTests
{
    [Fact]
    public void Every_System_Array_member_that_writes_cells_is_modelled_or_excluded()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var referencePack = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", "packs", "Microsoft.NETCore.App.Ref",
            Path.GetFileName(runtime), "ref", "net10.0"));
        Assert.True(Directory.Exists(referencePack), $"No .NET 10 reference pack at {referencePack}.");
        var compilation = CSharpCompilation.Create("ArrayCensus", references: Directory.GetFiles(referencePack, "*.dll")
            .Select(path => MetadataReference.CreateFromFile(path)));
        var members = compilation.GetSpecialType(SpecialType.System_Array).GetMembers().OfType<IMethodSymbol>()
            .Where(method => method.DeclaredAccessibility == Accessibility.Public &&
                method.Name is "Clear" or "Copy" or "ConstrainedCopy" or "CopyTo" or "Fill" or "Resize" or "Reverse" or "SetValue" or "Sort" or "Initialize")
            .ToArray();
        Assert.Equal(42, members.Length);
        var excluded = members.Where(method => method.Name == "Initialize" || method.Name == "Sort" &&
            !method.Parameters.Any(parameter => parameter.Type is INamedTypeSymbol { Name: "Comparison" })).ToArray();
        Assert.Equal(17, excluded.Length);
        foreach (var method in members)
        {
            var models = LibraryModels.BuiltIn.Members.Where(model => model.Id == DocumentationCommentId.CreateDeclarationId(method)).ToArray();
            if (excluded.Contains(method)) Assert.Empty(models);
            else Assert.Single(models);
        }
    }

    [Fact]
    public void Sort_with_a_comparison_runs_it_now_on_the_cells_and_writes_them()
    {
        var run = Run("Array.Sort(_state.Source, (a, b) => { a.Hits = 1; b.Hits = 2; return 0; });");
        Assert.Contains(Cells(run), access => access.Resource.Member.Name == "Source" && access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(Cells(run), access => access.Operation == AccessOperation.Read);
        Assert.NotEmpty(run.Of("Hits"));
        Assert.All(run.Of("Hits"), access => Assert.Equal(ExecutionKind.Root, run.Execution.Analysis.Execution(access.ExecutionId).Kind));
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Copy_stores_the_source_elements_into_the_destination() =>
        AssertSourceStored("Array.Copy(_state.Source, _state.Target, 2);");

    [Fact]
    public void Fill_stores_its_value()
    {
        var run = Run("Array.Fill(_state.Target, _state.Source[0]); _state.Target[0].Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Resource.Region.EndsWith("#SourceItem", StringComparison.Ordinal));
        Assert.Contains(Cells(run), access => access.Resource.Member.Name == "Target" && access.Operation == AccessOperation.Write);
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void Resize_assigns_a_new_array_holding_the_old_elements()
    {
        var run = Run("var cells = _state.Source; Array.Resize(ref cells, 4); cells[0].Hits = 1; cells[0] = _state.Target[0];");
        Assert.Contains(run.Of("Hits"), access => access.Resource.Region.EndsWith("#SourceItem", StringComparison.Ordinal));
        Assert.DoesNotContain(Cells(run), access => access.Resource.Member.Name == "Source" && access.Operation == AccessOperation.Write);
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void SetValue_writes_a_cell_of_its_receiver()
    {
        var run = Run("((Array)_state.Target).SetValue(_state.Source[0], 0); _state.Target[0].Hits = 1;");
        Assert.Contains(Cells(run), access => access.Resource.Member.Name == "Target" && access.Operation == AccessOperation.Write);
        Assert.Contains(run.Of("Hits"), access => access.Resource.Region.EndsWith("#SourceItem", StringComparison.Ordinal));
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void CopyTo_stores_the_receivers_elements() => AssertSourceStored("_state.Source.CopyTo(_state.Target, 0);");

    [Fact]
    public void CopyTo_reads_the_receivers_cells()
    {
        var run = Run("_state.Source.CopyTo(_state.Target, 0);", "_state.Source[0] = new SourceItem();");
        Assert.Contains(Cells(run), access => access.Resource.Member.Name == "Source" && access.Operation == AccessOperation.Read);
        Assert.Contains(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "Source" && pair.Resource.Selector is not null);
    }

    [Fact]
    public void Interface_CopyTo_and_Clear_on_an_array_make_what_the_direct_call_makes()
    {
        foreach (var source in new[] { "Source", "Matrix" })
        {
            AssertSame($"((ICollection)_state.{source}).CopyTo(_state.Target, 0); _state.Target[0].Hits = 1;",
                $"_state.{source}.CopyTo(_state.Target, 0); _state.Target[0].Hits = 1;");
            AssertSame($"((IList)_state.{source}).Clear();", $"Array.Clear(_state.{source});");
        }
        AssertSame("((ICollection<Item>)_state.Source).CopyTo(_state.Target, 0); _state.Target[0].Hits = 1;",
            "_state.Source.CopyTo(_state.Target, 0); _state.Target[0].Hits = 1;");
    }

    [Theory]
    [InlineData("ICollection", "CopyTo", true)]
    [InlineData("ICollection", "CopyTo", false)]
    [InlineData("ICollection<Item>", "CopyTo", true)]
    [InlineData("ICollection<Item>", "CopyTo", false)]
    [InlineData("IList", "Clear", true)]
    [InlineData("IList", "Clear", false)]
    public void Interface_call_follows_a_project_model_of_the_direct_member(string through, string member, bool opaque)
    {
        var id = member == "Clear" ? "M:System.Array.Clear(System.Array)" : "M:System.Array.CopyTo(System.Array,System.Int32)";
        var entry = opaque ? "\"opaque\":true" : "\"effects\":{\"array\":[\"reads-deep\"]}";
        var models = $$"""{"schemaVersion":1,"assemblies":["System.Private.CoreLib"],"models":[{"member":"{{id}}",{{entry}}}]}""";
        var direct = member == "Clear" ? "Array.Clear(_state.Source);" : "_state.Source.CopyTo(_state.Target, 0);";
        var indirect = member == "Clear" ? $"(({through})_state.Source).Clear();" : $"(({through})_state.Source).CopyTo(_state.Target, 0);";
        AssertSame(indirect, direct, models, opaque);
    }

    [Theory]
    [InlineData("stoppingToken.IsCancellationRequested ? _state.Source : new Item[1, 1]", true)]
    [InlineData("stoppingToken.IsCancellationRequested ? new Item[1, 1] : _state.Source", true)]
    [InlineData("stoppingToken.IsCancellationRequested ? _state.Matrix : new Item[1]", true)]
    [InlineData("_state.Source", true)]
    [InlineData("stoppingToken.IsCancellationRequested ? new Item[1] : new Item[1, 1]", false)]
    public async Task Interface_projections_keep_every_receiver_for_opaque_result_stores(string receiver, bool shared)
    {
        const string models = """
            {"schemaVersion":1,"assemblies":["System.Private.CoreLib"],"models":[
              {"member":"M:System.Array.CopyTo(System.Array,System.Int32)","effects":{},"keeps":{"this":["arg:array"]}}
            ]}
            """;
        var solution = FixtureSolution.Create(new FixtureOptions(), ("Case.cs", Usings + $$"""
            using System.Collections;
            public class Item { }
            public class State
            {
                public readonly Item[] Source = new Item[1];
                public readonly Item[,] Matrix = new Item[1, 1];
            }
            public static class Opaque { public static extern Array GetArray(); }
            public class Worker(State state) : BackgroundService
            {
                private readonly State _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    ICollection source = {{receiver}};
                    source.CopyTo(Opaque.GetArray(), 0);
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();")));
        using var repo = new CellModelRepository(models);
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        Assert.Equal(0, Assert.Single(result.Coverage).Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.Equal(shared, Assert.Single(result.Coverage).Gaps.Any(gap => gap.Callee.Contains("GetArray", StringComparison.Ordinal)));
        Assert.DoesNotContain(Assert.Single(result.Coverage).Gaps, gap => gap.Callee.Contains("CopyTo", StringComparison.Ordinal));
    }

    private static void AssertSourceStored(string statement)
    {
        var run = Run(statement + " _state.Target[0].Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Resource.Region.EndsWith("#SourceItem", StringComparison.Ordinal));
        Assert.Contains(Cells(run), access => access.Resource.Member.Name == "Target" && access.Operation == AccessOperation.Write);
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    private static Access[] Cells(EngineRun run) => run.Collection.Accesses.Where(access => !access.IsConstructionLocal &&
        access.Symbol.StartsWith("Worker.", StringComparison.Ordinal) && access.Resource.Selector is not null).ToArray();

    private static void AssertSame(string indirect, string direct, string? models = null, bool opaque = false)
    {
        var expected = Run(direct, models: models);
        var actual = Run(indirect, models: models);
        static string[] Describe(EngineRun run) => run.Collection.Accesses.Where(access => !access.IsConstructionLocal)
            .Select(access => $"{access.Resource.Region}|{string.Join('.', access.Resource.AccessPath)}|{access.Operation}")
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Describe(expected), Describe(actual));
        Assert.Equal(opaque, actual.Collection.Coverage.Gaps.Count != 0);
        Assert.Equal(expected.Counter(CoverageCounters.OPAQUE_CALL), actual.Counter(CoverageCounters.OPAQUE_CALL));
        Assert.Equal(expected.Counter(CoverageCounters.KNOWN_CALL_BUILT_IN), actual.Counter(CoverageCounters.KNOWN_CALL_BUILT_IN));
        Assert.Equal(expected.Counter(CoverageCounters.KNOWN_CALL_PROJECT), actual.Counter(CoverageCounters.KNOWN_CALL_PROJECT));
        Assert.Equal(expected.Counter(CoverageCounters.OPAQUE_BY_PROJECT), actual.Counter(CoverageCounters.OPAQUE_BY_PROJECT));
        Assert.Equal(expected.Collection.Coverage.Gaps.Select(gap => gap.Callee).Order(StringComparer.Ordinal),
            actual.Collection.Coverage.Gaps.Select(gap => gap.Callee).Order(StringComparer.Ordinal));
    }

    private static EngineRun Run(string work, string read = "", string? models = null)
    {
        var solution = FixtureSolution.Create(new FixtureOptions(), ("Case.cs", Usings + $$"""
            using System.Collections;
            using System.Collections.Generic;
            public class Item { public int Hits; }
            public sealed class SourceItem : Item { }
            public sealed class TargetItem : Item { }
            public sealed class State
            {
                public readonly Item[] Source = new Item[] { new SourceItem(), new SourceItem() };
                public readonly Item[] Target = new Item[] { new TargetItem(), new TargetItem() };
                public readonly Item[,] Matrix = new Item[,] { { new SourceItem(), new SourceItem() } };
            }
            public sealed class Worker(State state) : BackgroundService
            {
                private readonly State _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{work}} return Task.CompletedTask; }
            }
            public sealed class Reader(State state) : BackgroundService
            {
                private readonly State _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{read}} return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();")));
        return AnalyzeScope(solution, "Fixture", models is null ? null : ModelCellFixture.Resolve(solution, models));
    }
}
