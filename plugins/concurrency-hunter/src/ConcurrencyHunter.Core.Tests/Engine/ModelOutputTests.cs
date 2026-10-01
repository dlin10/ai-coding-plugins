using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ModelOutputTests
{
    [Fact]
    public void Out_local_holds_what_the_model_assigns() => Assigned("Outputs.Lib.Pass(_state.Other, out var value); value.Hits = 1;");

    [Fact]
    public void Out_field_is_written_and_holds_the_output()
    {
        var run = Assigned("Outputs.Lib.Pass(_state.Other, out _state.Value); _state.Value.Hits = 1;");
        Assert.Contains(run.Of("Value"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void Out_cell_is_written_and_holds_the_output()
    {
        var run = Assigned("Outputs.Lib.Pass(_state.Other, out _state.Cells[0]); _state.Cells[0].Hits = 1;");
        Assert.Contains(run.Of("Cells"), access => access.Operation == AccessOperation.Write && access.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_local_with_an_output_holds_it() => Assigned("Item value = _state.Value; Outputs.Lib.Replace(_state.Other, ref value); value.Hits = 1;");

    [Fact]
    public void Ref_local_output_replaces_its_previous_points_to()
    {
        var run = Assigned("Item value = _state.Value; Outputs.Lib.Replace(_state.Other, ref value); value.Hits = 1;");
        Assert.DoesNotContain(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ForwardRef(ref value);")]
    [InlineData("ForwardOut(out value);")]
    public async Task Helper_parameter_output_reaches_the_callers_local(string call)
    {
        var work = "Item value = _state.Value; " + call + " value.Hits++;";
        var run = Run(work);
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.ReadModifyWrite && access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Of("Hits"), access => access.Operation == AccessOperation.ReadModifyWrite && access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
        using var repo = new CellModelRepository(Models());
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution(work, "_state.Other.Hits++;"), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        Assert.Contains(result.Findings, finding => finding.RuleId == "DCA1002" && finding.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_helper_outputs_to_one_parameter_reach_the_callers_local()
    {
        var run = Run("Item value = _state.Value; ForwardTwo(out value); value.Hits = 1;");
        var writes = run.Of("Hits").Where(access => access.Operation == AccessOperation.Write).ToArray();
        Assert.Contains(writes, access => access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
        Assert.Contains(writes, access => access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
    }

    [Fact]
    public void Helper_ref_parameter_assignment_without_a_model_reaches_the_callers_local()
    {
        var run = Assigned("Item value = _state.Value; ForwardAssigned(ref value); value.Hits = 1;");
        Assert.DoesNotContain(run.Of("Hits"), access => access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
    }

    [Fact]
    public void Ref_field_with_an_output_is_written_and_holds_it()
    {
        var run = Assigned("Outputs.Lib.Replace(_state.Other, ref _state.Value); _state.Value.Hits = 1;");
        Assert.Contains(run.Of("Value"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void Ref_through_a_reference_location_is_written_and_holds_the_output()
    {
        var run = Assigned("ref Item value = ref _state.Cells[0]; Outputs.Lib.Replace(_state.Other, ref value); value.Hits = 1;");
        Assert.Contains(run.Of("Cells"), access => access.Operation == AccessOperation.Write && access.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_argument_without_an_output_is_as_today()
    {
        var run = Run("Outputs.Lib.Unchanged(ref _state.Value); _state.Value.Hits = 1;");
        Assert.DoesNotContain(run.Of("Value"), access => access.Operation == AccessOperation.Write);
        Assert.Contains(run.Of("Hits"), access => access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
        Assert.Equal(0, run.Counter(CoverageCounters.UNPROVEN_REFERENCE));
        var opaque = Run("Outputs.Lib.Opaque(ref _state.Value);");
        Assert.Equal(1, opaque.Counter(CoverageCounters.UNPROVEN_REFERENCE));
    }

    [Fact]
    public void Two_outputs_to_one_variable_leave_it_holding_both()
    {
        foreach (var arguments in new[] { "_state.Value, _state.Other", "_state.Other, _state.Value" })
        {
            var run = Run($"Outputs.Lib.Two({arguments}, out Item value, out value); value.Hits = 1;");
            var writes = run.Of("Hits").Where(access => access.Operation == AccessOperation.Write).ToArray();
            Assert.Contains(writes, access => access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
            Assert.Contains(writes, access => access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Two_outputs_to_one_reference_location_hold_both_objects()
    {
        var run = Run("ref Item value = ref _state.Cells[0]; Outputs.Lib.Two(_state.Value, _state.Other, out value, out value); value.Hits = 1;");
        var writes = run.Of("Hits").Where(access => access.Operation == AccessOperation.Write).ToArray();
        Assert.Contains(writes, access => access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
        Assert.Contains(writes, access => access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
        Assert.Contains(run.Of("Cells"), access => access.Operation == AccessOperation.Write && access.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Output_is_filled_at_the_call_even_with_a_sequence_result()
    {
        var run = Run("Outputs.Lib.Lazy(_state.Produce(), _state.Other, _state, out var value); value.Hits = 1;",
            extra: Entry("Lazy", "\"outputs\":{\"value\":\"[elements(arg:items)]\"},\"result\":\"sequence(arg:other)\""));
        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_output_is_a_library_sequence_of_its_own()
    {
        var run = Run("var result = Outputs.Lib.Sequences(_state.Produce(), _state.ProduceOther(), _state, out var output); foreach (var x in output) { } foreach (var x in result) { }",
            extra: Entry("Sequences", "\"outputs\":{\"value\":\"sequence(elements(arg:items))\"},\"result\":\"sequence(elements(arg:other))\""));
        Assert.Equal(2, run.Execution.Heap.Heap.LibrarySequences.Count);
        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.Contains(run.Of("OtherCount"), access => access.Operation == AccessOperation.Write);
        var sequences = run.Execution.Heap.Heap.LibrarySequences.Values.ToArray();
        Assert.NotEqual(sequences[0].RegionId, sequences[1].RegionId);
    }

    [Fact]
    public void Sequence_output_enumerates_only_its_own_values()
    {
        var model = Entry("Sequences", "\"outputs\":{\"value\":\"sequence(elements(arg:items))\"},\"result\":\"sequence(arg:other)\"", "\"data\":[\"reads-deep\"]");
        const string call = "var result = Outputs.Lib.Sequences(_state.Produce(), _state.ProduceOther(), _state.Data, out var output); ";
        var output = Run(call + "foreach (var x in output) { }", extra: model);
        Assert.Contains(output.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(output.Of("DataHits"), access => access.Operation == AccessOperation.Read);
        var result = Run(call + "foreach (var x in result) { }", extra: model);
        Assert.DoesNotContain(result.Of("Count"), access => access.Operation == AccessOperation.Write);
        Assert.Contains(result.Of("DataHits"), access => access.Operation == AccessOperation.Read);
        var unused = Run(call, extra: model);
        Assert.Empty(unused.Of("Count"));
        Assert.Empty(unused.Of("DataHits"));
    }

    [Fact]
    public void Collection_output_is_a_new_collection_of_the_parameter_type()
    {
        var run = Run("Outputs.Lib.Collect(_state.Other, out var value); value[0].Hits = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
        Assert.Contains(run.Execution.Heap.Heap.Regions.Values, region => region.TypeKey == "System.Private.CoreLib:System.Collections.Generic.List<Fixture:Item>" ||
            region.TypeKey?.Contains("System.Collections.Generic.List<Fixture:Item>", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Output_display_continues_after_explicit_creations_in_source_order()
    {
        var run = Run("var explicitList = new List<Item>(); Outputs.Lib.Collect(_state.Other, out var first); Outputs.Lib.Collect(_state.Other, out var second); first[0].Hits = 1; second[0].Hits = 2;");
        var created = run.Execution.Heap.Heap.Regions.Values.Where(region => region.ModelCreationKey is not null).ToArray();
        Assert.Equal(new[] { "alloc:Worker.ExecuteAsync(CancellationToken)#System.Collections.Generic.List<Item>#2", "alloc:Worker.ExecuteAsync(CancellationToken)#System.Collections.Generic.List<Item>#3" },
                     created.Select(region => region.Display).Order(StringComparer.Ordinal));
        Assert.All(created, region => Assert.False(region.HasExactType));
    }

    [Theory]
    [InlineData("var result = new Item[0].ToList(); Outputs.Lib.Collect(_state.Other, out var output);", "System.Collections.Generic.List<Item>")]
    [InlineData("var result = new Item[0].ToDictionary(item => item); Outputs.Lib.Map(_state.Other, _state.Other, out var output);", "System.Collections.Generic.Dictionary<Item, Item>")]
    public void Output_display_does_not_count_earlier_collection_results(string work, string type)
    {
        var run = Run(work);
        var output = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.ModelCreationKey is not null);
        Assert.Equal("alloc:Worker.ExecuteAsync(CancellationToken)#" + type, output.Display);
    }

    [Fact]
    public void Output_objects_never_enter_the_calls_result()
    {
        var solution = Solution("Outputs.Lib.Arrays(_state.Other, out _state.First, out _state.Second)[0].Hits = 1;");
        var models = Models().Replace("\"result\":\"collection(arg:item)\",", "", StringComparison.Ordinal);
        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, models));
        Assert.Empty(run.Of("Hits"));
        Assert.Equal(2, run.Execution.Heap.Heap.Regions.Values.Count(region => region.ModelCreationKey is not null));
    }

    [Fact]
    public async Task Two_outputs_of_one_type_make_two_objects_apart_from_the_result()
    {
        const string work = "_state.Result = Outputs.Lib.Arrays(_state.Other, out _state.First, out _state.Second); _state.First[0] = _state.Other; _state.Second[0] = _state.Other;";
        var run = Run(work, "_ = _state.First[0]; _ = _state.Second[0];");
        var created = run.Execution.Heap.Heap.Regions.Values.Where(region => region.Display.StartsWith("alloc:Worker.ExecuteAsync(CancellationToken)#Item[]", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, created.Length);
        Assert.Equal(3, created.Select(region => region.Group).Distinct().Count());
        var outputs = created.Where(region => region.ModelCreationKey is not null).OrderBy(region => region.Display, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "alloc:Worker.ExecuteAsync(CancellationToken)#Item[]", "alloc:Worker.ExecuteAsync(CancellationToken)#Item[]#2" }, outputs.Select(region => region.Display));
        using var repo = new CellModelRepository(Models());
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution(work, "_ = _state.First[0]; _ = _state.Second[0];"), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        var findings = result.Findings.Where(finding => finding.Resource.Member.Name is "First" or "Second" && finding.Resource.Selector is not null).ToArray();
        Assert.Equal(2, findings.Length);
        Assert.Equal(2, findings.Select(finding => finding.Fingerprint).Distinct().Count());
    }

    [Fact]
    public async Task Opaque_result_assigned_through_an_output_to_a_shared_field_feeds_it()
    {
        var result = await Analyze("Outputs.Lib.Pass(Outputs.Lib.Unknown(), out _state.Unknown);");
        Assert.Contains(Assert.Single(result.Coverage).Gaps, gap => gap.Callee.Contains("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opaque_result_assigned_through_an_output_to_a_confined_field_does_not()
    {
        var result = await Analyze("var local = new State(); Outputs.Lib.Pass(Outputs.Lib.Unknown(), out local.Unknown);");
        Assert.Equal(0, Assert.Single(result.Coverage).Skips.GetValueOrDefault(CoverageCounters.MODEL_ENTRY_REJECTED));
        Assert.DoesNotContain(Assert.Single(result.Coverage).Gaps, gap => gap.Callee.Contains("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Output_on_a_parameter_that_is_neither_out_nor_ref_is_refused() =>
        Refused(Entry("Pass", "\"outputs\":{\"source\":\"[arg:source]\"}"), "out or ref");

    [Fact]
    public void Output_that_does_not_convert_to_the_parameter_type_is_refused() =>
        Refused(Entry("Wrong", "\"outputs\":{\"value\":\"[arg:source]\"}"), "does not convert");

    [Fact]
    public void Entries_differing_only_in_outputs_disagree() =>
        Refused(Entry("Two", "\"outputs\":{\"a\":\"[arg:first]\"}") + "," + Entry("Two", "\"outputs\":{\"a\":\"[arg:second]\"}"), "disagree");

    [Fact]
    public void Built_in_outputs_use_the_result_grammar()
    {
        var text = """{"schemaVersion":1,"assemblies":["Outputs"],"versions":{"minimum":"0.0.0.0","maximumExclusive":"1.0.0.0"},"models":[""" +
            Entry("Collect", "\"outputs\":{\"value\":\"collection(arg:item)\"}") + "]}";
        var model = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(text)).Members);
        Assert.Equal("collection(arg:item)", model.Outputs["value"].ToString());
    }

    [Fact]
    public void Dictionary_output_holds_keys_and_values_apart()
    {
        var run = Run("Outputs.Lib.Map(_state.Value, _state.Other, out var value); foreach (var pair in value) { pair.Key.Hits = 1; pair.Value.Hits = 2; }");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.EndsWith("#Old", StringComparison.Ordinal));
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
    }

    private static EngineRun Assigned(string work)
    {
        var run = Run(work);
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write && access.Resource.Region.EndsWith("#Added", StringComparison.Ordinal));
        return run;
    }

    private static EngineRun Run(string work, string read = "", string? extra = null)
    {
        var solution = Solution(work, read);
        return AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, Models(extra)));
    }

    private static async Task<AnalysisResult> Analyze(string work)
    {
        using var repo = new CellModelRepository(Models());
        return await PhaseOneAnalyzer.AnalyzeAsync(Solution(work), ROOT_DIRECTORY, repo.Root, CancellationToken.None);
    }

    private static void Refused(string entry, string reason)
    {
        using var repo = new CellModelRepository(Models(entry));
        var compilation = Solution("").Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var files = ProjectModelFiles.Read(repo.Root);
        var (_, rejected) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));
        Assert.Contains(rejected, rejection => rejection.Reason.Contains(reason, StringComparison.Ordinal));
    }

    private static string Models(string? extra = null) => """{"schemaVersion":1,"assemblies":["Outputs"],"models":[""" + string.Join(",", new[]
    {
        Entry("Pass", "\"outputs\":{\"value\":\"[arg:source]\"}"),
        Entry("Replace", "\"outputs\":{\"value\":\"[arg:source]\"}"),
        Entry("Two", "\"outputs\":{\"a\":\"[arg:first]\",\"b\":\"[arg:second]\"}"),
        Entry("Collect", "\"outputs\":{\"value\":\"collection(arg:item)\"}"),
        Entry("Arrays", "\"result\":\"collection(arg:item)\",\"outputs\":{\"a\":\"collection(arg:item)\",\"b\":\"collection(arg:item)\"}"),
        Entry("Map", "\"outputs\":{\"value\":\"dictionary(arg:key,arg:item)\"}"),
        Entry("Unchanged", "\"note\":\"no assignment\""), extra
    }.Where(entry => entry is not null)) + "]}";

    private static string Entry(string name, string properties, string effects = "") =>
        "{\"member\":\"" + DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Outputs.Lib")!.GetMembers(name).OfType<IMethodSymbol>().Single()) +
        "\",\"effects\":{" + effects + "}," + properties + "}";

    private static Solution Solution(string work, string read = "") => FixtureSolution.Create(
        new FixtureOptions { MetadataReferences = [Library] }, ("Case.cs", Usings + $$"""
        using System.Collections.Generic;
        using System.Linq;
        public class Item { public int Hits; }
        public sealed class Old : Item { }
        public sealed class Added : Item { }
        public sealed class Data { public int DataHits; }
        public sealed class State
        {
            public Item Value = new Old();
            public readonly Item Other = new Added();
            public readonly Item[] Cells = new Item[] { new Old() };
            public readonly Data Data = new Data();
            public Item[] First = null!, Second = null!, Result = null!;
            public object Unknown;
            public int Count, OtherCount;
            public IEnumerable<Item> Produce() { Count = 1; yield return Other; }
            public IEnumerable<Item> ProduceOther() { OtherCount = 1; yield return Value; }
        }
        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;
            private void ForwardRef(ref Item value) => Outputs.Lib.Replace(_state.Other, ref value);
            private void ForwardOut(out Item value) => Outputs.Lib.Pass(_state.Other, out value);
            private void ForwardTwo(out Item value) => Outputs.Lib.Two(_state.Value, _state.Other, out value, out value);
            private void ForwardAssigned(ref Item value) => value = _state.Other;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{work}} return Task.CompletedTask; }
        }
        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{read}} return Task.CompletedTask; }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();")));

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Outputs", [CSharpSyntaxTree.ParseText("""
        using System.Collections.Generic;
        namespace Outputs;
        public static class Lib
        {
            public static void Pass<T>(T source, out T value) { value = default!; }
            public static void Replace<T>(T source, ref T value) { }
            public static void Two<T>(T first, T second, out T a, out T b) { a = b = default!; }
            public static void Collect<T>(T item, out List<T> value) { value = null!; }
            public static T[] Arrays<T>(T item, out T[] a, out T[] b) { a = b = null!; return null!; }
            public static void Map<T>(T key, T item, out Dictionary<T, T> value) { value = null!; }
            public static IEnumerable<T> Lazy<T>(IEnumerable<T> items, T other, object data, out T value) { value = default!; return null!; }
            public static IEnumerable<T> Sequences<T>(IEnumerable<T> items, IEnumerable<T> other, object data, out IEnumerable<T> value) { value = null!; return null!; }
            public static void Unchanged<T>(ref T value) { }
            public static void Opaque<T>(ref T value) { }
            public static object Unknown() => null!;
            public static void Wrong(string source, out System.Version value) { value = null!; }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static MetadataReference Library => Reference.Value;
    private static readonly Lazy<MetadataReference> Reference = new(() =>
    {
        using var bytes = new MemoryStream();
        var emit = LibrarySource.Value.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });
}
