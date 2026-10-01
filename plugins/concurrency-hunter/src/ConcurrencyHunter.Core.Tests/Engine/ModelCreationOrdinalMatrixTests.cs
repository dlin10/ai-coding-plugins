using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ModelCreationOrdinalMatrixTests
{
    [Theory]
    [InlineData("method", "Address", 3)]
    [InlineData("method", "Other", 2)]
    [InlineData("type", "Address", 3)]
    [InlineData("type", "Other", 2)]
    public async Task Explicit_sites_are_counted_after_generic_substitution(string kind, string secondType, int ordinal)
    {
        var work = kind == "method" ? $"Make<Address,{secondType}>();" : $"Generic<Address,{secondType}>.Make();";
        var solution = Solution(work, """
            static void Make<T,U>()
            {
                _ = new List<T>(); _ = new List<U>();
                var value = JsonSerializer.Deserialize<List<T>>("[]"); value[0] = default;
            }
            """, """
            public static class Generic<T,U>
            {
                public static void Make()
                {
                    _ = new List<T>(); _ = new List<U>();
                    var value = JsonSerializer.Deserialize<List<T>>("[]"); value[0] = default;
                }
            }
            """);
        var run = AnalyzeScope(solution, "Fixture");
        var root = Assert.Single(Created(run, "System.Collections.Generic.List<Fixture:Address>"));
        Assert.EndsWith($"#System.Collections.Generic.List<Address>#{ordinal}", root.Display);
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        Assert.Single(result.ScopeSizes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_initializers_at_equal_positions_are_ordered_by_file_then_position(bool reverseDocuments)
    {
        var files = new[]
        {
            ("A.cs", "using System.Text.Json; public partial class State { public Address A = JsonSerializer.Deserialize<Address>(\"{}\"); }"),
            ("B.cs", "using System.Text.Json; public partial class State { public Address B = JsonSerializer.Deserialize<Address>(\"{}\"); }")
        };
        var documents = files.Append(("Main.cs", Source("_state.A.Hits = 1; _state.B.Hits = 2;")));
        var solution = FixtureSolution.Create((reverseDocuments ? documents.Reverse() : documents).ToArray());
        var run = AnalyzeScope(solution, "Fixture");
        var state = run.Execution.Heap.Region("di:State@Singleton");
        Assert.Equal(new[] { "alloc:State..ctor()#Address" }, run.Execution.Heap.Targets(state, "A"));
        Assert.Equal(new[] { "alloc:State..ctor()#Address#2" }, run.Execution.Heap.Targets(state, "B"));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        Assert.Equal(new[] { "alloc:State..ctor()#Address", "alloc:State..ctor()#Address#2" }, result.Accesses
            .Where(access => access.Resource.Member.Name == "Hits").Select(access => access.Resource.Region).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("lambda")]
    [InlineData("local")]
    public async Task Calls_on_one_line_and_nested_bodies_follow_the_ordinary_members_source_order(string route)
    {
        const string first = "JsonSerializer.Deserialize<Address>(\"{}\").Hits = 1;";
        var work = route switch
        {
            "ordinary" => first + first,
            "lambda" => "Action a = () => { " + first + " }; a(); " + first,
            "local" => "void A() { " + first + " } A(); " + first,
            _ => throw new InvalidOperationException(route)
        };
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution(work), ROOT_DIRECTORY, CancellationToken.None);
        Assert.Equal(new[] { "alloc:Worker.ExecuteAsync(CancellationToken)#Address", "alloc:Worker.ExecuteAsync(CancellationToken)#Address#2" },
            result.Accesses.Where(access => access.Resource.Member.Name == "Hits").Select(access => access.Resource.Region).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Action untouched = () => { _ = new Address(); }; _ = new Address();", 3)]
    [InlineData("Action a = () => { _ = new Address(); }; a(); _ = new Address();", 3)]
    [InlineData("void A() { _ = new Address(); } A(); _ = new Address();", 3)]
    public async Task Explicit_sites_in_nested_bodies_count_in_their_ordinary_member(string explicitSites, int ordinal)
    {
        var result = await PhaseOneAnalyzer.AnalyzeAsync(Solution(explicitSites + "JsonSerializer.Deserialize<Address>(\"{}\").Hits = 1;"), ROOT_DIRECTORY, CancellationToken.None);
        Assert.Contains(result.Accesses, access => access.Resource.Region == "alloc:Worker.ExecuteAsync(CancellationToken)#Address#" + ordinal);
    }

    [Theory]
    [InlineData("_ = new Address[0].Select(item => item);", "List", "System.Collections.Generic.List<Address>")]
    [InlineData("_ = new Address[0].ToList();", "List", "System.Collections.Generic.List<Address>")]
    [InlineData("_ = new Address[0].ToDictionary(item => item);", "Map", "System.Collections.Generic.Dictionary<Address, Address>")]
    public async Task Legacy_results_and_sequences_do_not_count_as_new_graph_objects(string legacy, string kind, string displayType)
    {
        var createdType = kind == "List" ? "List<Address>" : "Dictionary<Address,Address>";
        var solution = Solution(legacy + $"_ = JsonSerializer.Deserialize<{createdType}>(\"[]\");");
        var run = AnalyzeScope(solution, "Fixture");
        var root = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.ModelCreationKey is not null && region.ModelCreationKey.Contains("|root|", StringComparison.Ordinal));
        Assert.Equal("alloc:Worker.ExecuteAsync(CancellationToken)#" + displayType, root.Display);
        Assert.Single((await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None)).ScopeSizes);
    }

    [Fact]
    public async Task Sequence_outputs_do_not_count_as_new_graph_objects()
    {
        var solution = Solution("var r = Ordinals.Lib.Sequences(new Address(), out var a, out var b); _ = JsonSerializer.Deserialize<IEnumerable<Address>>(\"[]\");");
        var run = AnalyzeScope(solution, "Fixture", ModelCellFixture.Resolve(solution, Models));
        var root = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.ModelCreationKey?.StartsWith("model|", StringComparison.Ordinal) == true);
        Assert.Equal("alloc:Worker.ExecuteAsync(CancellationToken)#System.Collections.Generic.IEnumerable<Address>", root.Display);
        Assert.Equal(3, run.Execution.Heap.Heap.LibrarySequences.Count);
        using var repo = new CellModelRepository(Models);
        Assert.Single((await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, repo.Root, CancellationToken.None)).ScopeSizes);
    }

    [Theory]
    [InlineData("new", "Address", 2)]
    [InlineData("collection", "System.Collections.Generic.List<Fixture:Address>", 2)]
    [InlineData("dictionary", "System.Collections.Generic.Dictionary<Fixture:Address,Fixture:Address>", 2)]
    public async Task Outputs_are_counted_in_parameter_order_after_the_result(string form, string type, int explicitSites)
    {
        var work = form switch
        {
            "new" => "_ = new Address(); _ = new Address(); var r = Ordinals.Lib.Objects<Address>(out var a, out var b); r.Hits = 1; a.Hits = 2; b.Hits = 3;",
            "collection" => "_ = new List<Address>(); _ = new List<Address>(); var r = Ordinals.Lib.Lists(new Address(), out var a, out var b); a[0] = null; b[0] = null;",
            "dictionary" => "_ = new Dictionary<Address,Address>(); _ = new Dictionary<Address,Address>(); var r = Ordinals.Lib.Maps(new Address(), out var a, out var b); a.Clear(); b.Clear();",
            _ => throw new InvalidOperationException(form)
        };
        var solution = Solution(work);
        var run = AnalyzeScope(solution, "Fixture", ModelCellFixture.Resolve(solution, Models));
        var objects = Created(run, type).OrderBy(region => region.SiteOperationId).ThenBy(region => region.ModelCreationKey, StringComparer.Ordinal).ToArray();
        // Only 'new' results count; legacy collection/dictionary results retain their old display.
        var destinations = form == "new" ? new[] { "result", "output0", "output1" } : new[] { "output1", "output2" };
        foreach (var (destination, index) in destinations.Select((destination, index) => (destination, index)))
        {
            var created = Assert.Single(objects, region => region.ModelCreationKey!.Contains("|" + destination + "|root|", StringComparison.Ordinal));
            Assert.Equal(1 + explicitSites + index, Ordinal(created));
        }
        using var repo = new CellModelRepository(Models);
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        if (form == "new")
            Assert.All(objects, region => Assert.Contains(result.Accesses, access => access.Resource.Region == region.Display));
        else
            Assert.Single(result.ScopeSizes);
    }

    [Fact]
    public async Task Breadth_first_graph_order_continues_across_result_outputs_and_the_next_call()
    {
        const string work = """
            _ = new Address();
            var r = Ordinals.Lib.Graphs<Graph>(out var a, out var b);
            var s = Ordinals.Lib.Graphs<Graph>(out var c, out var d);
            r.First.Hits = 1; r.Second.Hits = 1; r.Map.Keys.First().Hits = 1; r.Map.Values.First().Hits = 1;
            a.First.Hits = 1; a.Second.Hits = 1; a.Map.Keys.First().Hits = 1; a.Map.Values.First().Hits = 1;
            b.First.Hits = 1; b.Second.Hits = 1; b.Map.Keys.First().Hits = 1; b.Map.Values.First().Hits = 1;
            s.First.Hits = 1; c.First.Hits = 1; d.First.Hits = 1;
            """;
        var solution = Solution(work);
        var run = AnalyzeScope(solution, "Fixture", ModelCellFixture.Resolve(solution, Models));
        var addresses = Created(run, "Address");
        Assert.Equal(Enumerable.Range(2, 24), addresses.Select(Ordinal).Order());
        var roots = Created(run, "Graph").OrderBy(Ordinal).ToArray();
        for (var graph = 0; graph < 6; graph++)
        {
            var prefix = roots[graph].ModelCreationKey!.Split("|root|")[0];
            var nodes = addresses.Where(region => region.ModelCreationKey!.StartsWith(prefix + "|", StringComparison.Ordinal)).OrderBy(Ordinal).ToArray();
            Assert.Equal(Enumerable.Range(2 + graph * 4, 4), nodes.Select(Ordinal));
            Assert.Contains("First", nodes[0].ModelCreationKey!);
            Assert.Contains("Second", nodes[1].ModelCreationKey!);
            Assert.EndsWith(PathValue.KEYS + "|Fixture:Address", nodes[2].ModelCreationKey);
            Assert.EndsWith(PathValue.ELEMENT + "|Fixture:Address", nodes[3].ModelCreationKey);
        }
        using var repo = new CellModelRepository(Models);
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, repo.Root, CancellationToken.None);
        Assert.Contains(result.Accesses, access => access.Resource.Region.EndsWith("#Address#22", StringComparison.Ordinal));
    }

    private static HeapRegion[] Created(EngineRun run, string type) => run.Execution.Heap.Heap.Regions.Values
        .Where(region => region.ModelCreationKey is not null && region.TypeKey?.Replace(" ", "", StringComparison.Ordinal).EndsWith(":" + type, StringComparison.Ordinal) == true).ToArray();

    private static int Ordinal(HeapRegion region) => int.TryParse(region.Display.Split('#').Last(), out var ordinal) ? ordinal : 1;

    private static Solution Solution(string work, string methods = "", string extra = "") => FixtureSolution.Create(
        new FixtureOptions { MetadataReferences = [Library.Value] }, ("Main.cs", Source(work, methods, extra)));

    private static string Source(string work, string methods = "", string extra = "") => Usings + $$"""
        using System.Collections.Generic;
        using System.Linq;
        using System.Text.Json;
        public class Address { public int Hits; }
        public class Other { public int Hits; }
        public class Graph { public Dictionary<Address,Address> Map; public Address First; public Address Second { get; set; } }
        public partial class State { }
        {{extra}}
        public class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{work}} return Task.CompletedTask; }
            {{methods}}
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();");

    private const string Models = """
        {"schemaVersion":1,"assemblies":["Ordinals"],"models":[
          {"member":"M:Ordinals.Lib.Objects``1(``0@,``0@)","effects":{},"result":"new","outputs":{"a":"new","b":"new"}},
          {"member":"M:Ordinals.Lib.Graphs``1(``0@,``0@)","effects":{},"result":"new","outputs":{"a":"new","b":"new"}},
          {"member":"M:Ordinals.Lib.Lists``1(``0,System.Collections.Generic.List{``0}@,System.Collections.Generic.List{``0}@)","effects":{},"result":"collection(arg:item)","outputs":{"a":"collection(arg:item)","b":"collection(arg:item)"}},
          {"member":"M:Ordinals.Lib.Maps``1(``0,System.Collections.Generic.Dictionary{``0,``0}@,System.Collections.Generic.Dictionary{``0,``0}@)","effects":{},"result":"dictionary(arg:item,arg:item)","outputs":{"a":"dictionary(arg:item,arg:item)","b":"dictionary(arg:item,arg:item)"}},
          {"member":"M:Ordinals.Lib.Sequences``1(``0,System.Collections.Generic.IEnumerable{``0}@,System.Collections.Generic.IEnumerable{``0}@)","effects":{},"result":"sequence(arg:item)","outputs":{"a":"sequence(arg:item)","b":"sequence(arg:item)"}}
        ]}
        """;

    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        var compilation = CSharpCompilation.Create("Ordinals", [CSharpSyntaxTree.ParseText("""
            using System.Collections.Generic;
            namespace Ordinals;
            public static class Lib
            {
                public static T Objects<T>(out T a, out T b) { a = b = default; return default; }
                public static T Graphs<T>(out T a, out T b) { a = b = default; return default; }
                public static List<T> Lists<T>(T item, out List<T> a, out List<T> b) { a = b = null; return null; }
                public static Dictionary<T,T> Maps<T>(T item, out Dictionary<T,T> a, out Dictionary<T,T> b) { a = b = null; return null; }
                public static IEnumerable<T> Sequences<T>(T item, out IEnumerable<T> a, out IEnumerable<T> b) { a = b = null; return null; }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        Assert.True(compilation.Emit(bytes).Success);
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });
}
