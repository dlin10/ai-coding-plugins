using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class NewResultTests
{
    [Fact]
    public void New_result_is_an_object_created_at_the_call()
    {
        var run = Run("var p = JsonSerializer.Deserialize<Profile>(\"{}\"); p.Changes = 1;");
        var root = Assert.Single(Created(run, "Profile"));
        Assert.Contains("alloc:Worker.ExecuteAsync(CancellationToken)#Profile", root.Display);
        Assert.Contains(run.Of("Changes"), access => access.Resource.RegionId == root.Identity);
    }

    [Fact]
    public void Array_root_holds_new_elements() => HasWrite("var p = JsonSerializer.Deserialize<Profile[]>(\"[]\"); p[0].Changes = 1;", "Changes");

    [Fact]
    public void List_root_holds_new_elements() => HasWrite("var p = JsonSerializer.Deserialize<List<Profile>>(\"[]\"); p[0].Changes = 1;", "Changes");

    [Fact]
    public void Dictionary_root_holds_new_keys_and_values()
    {
        var run = Run("var p = JsonSerializer.Deserialize<Dictionary<Key, Profile>>(\"{}\"); foreach (var k in p.Keys) k.Hits = 1; foreach (var v in p.Values) v.Changes = 1;");
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.Contains(run.Of("Changes"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void Source_field_of_a_new_object_holds_a_new_object() => HasWrite("var p = JsonSerializer.Deserialize<Fields>(\"{}\"); p.First.Hits = 1;", "Hits");

    [Fact]
    public void Property_backing_field_of_a_new_object_holds_a_new_object() => HasWrite("var p = JsonSerializer.Deserialize<Profile>(\"{}\"); p.Home.Hits = 1;", "Hits");

    [Fact]
    public void Field_of_a_generic_base_holds_an_object_of_the_substituted_type() => HasWrite("var p = JsonSerializer.Deserialize<Derived>(\"{}\"); p.Base.Hits = 1;", "Hits");

    [Fact]
    public void Two_fields_of_one_type_hold_two_objects()
    {
        var run = Run("var p = JsonSerializer.Deserialize<Fields>(\"{}\"); p.First.Hits = 1; p.Second.Hits = 2;");
        var objects = Created(run, "Address");
        Assert.Equal(2, objects.Length);
        Assert.Equal(2, run.Of("Hits").Select(access => access.Resource.RegionId).Distinct().Count());
        Assert.Equal(2, objects.Select(region => region.Group).Distinct().Count());
    }

    [Fact]
    public async Task Two_fields_of_one_type_make_two_fingerprints()
    {
        var result = await Full("_state.Fields = JsonSerializer.Deserialize<Fields>(\"{}\");", "_state.Fields.First.Hits++; _state.Fields.Second.Hits++;");
        var findings = result.Findings.Where(finding => finding.RuleId == "DCA1002" && finding.Resource.Member.Name == "Hits").ToArray();
        Assert.Equal(2, findings.Length);
        Assert.Equal(2, findings.Select(finding => finding.Fingerprint).Distinct().Count());
    }

    [Fact]
    public async Task Graph_stops_at_the_summary_depth()
    {
        var solution = Solution("_ = JsonSerializer.Deserialize<Node>(\"{}\");");
        var shallow = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, new AnalysisLimits(1, 8, 16), CancellationToken.None);
        var deeper = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, new AnalysisLimits(2, 8, 16), CancellationToken.None);
        Assert.Equal(Assert.Single(shallow.ScopeSizes).Regions + 1, Assert.Single(deeper.ScopeSizes).Regions);
    }

    [Fact]
    public void Collection_field_holds_a_new_collection_of_new_elements()
    {
        var run = Run("var p = JsonSerializer.Deserialize<Collections>(\"{}\"); p.Items[0].Hits = 1; p.Array[0].Hits = 2; foreach (var k in p.Map.Keys) k.Hits = 3; foreach (var v in p.Map.Values) v.Hits = 4;");
        Assert.Equal(4, run.Of("Hits").Count(access => access.Operation == AccessOperation.Write));
        Assert.Equal(3, run.Execution.Heap.Heap.Regions.Values.Count(region => region.ModelCreationKey is not null && region.TypeKey?.Contains("<Fixture:", StringComparison.Ordinal) == true || region.ModelCreationKey is not null && region.TypeKey == "Fixture:Address[]"));
    }

    [Fact]
    public void Interface_or_struct_field_holds_nothing()
    {
        var run = Run("var p = JsonSerializer.Deserialize<Boundaries>(\"{}\"); p.Interface.Touch(); p.Struct.Value.Hits = 1; p.Abstract.Hits = 2; p.Metadata.ToString();");
        Assert.Empty(Created(run, "Address"));
        Assert.Empty(run.Of("Hits"));
        Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.ModelCreationKey is not null);
    }

    [Fact]
    public void Making_the_graph_is_no_access()
    {
        var run = Run("_ = JsonSerializer.Deserialize<Profile>(\"{}\");");
        Assert.Equal(2, run.Execution.Heap.Heap.Regions.Values.Count(region => region.ModelCreationKey is not null));
        Assert.DoesNotContain(run.Collection.Accesses, access => !access.IsConstructionLocal && access.Symbol.StartsWith("Worker.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_objects_of_two_invocations_pair_only_once_published()
    {
        var local = await Full("", "var p = JsonSerializer.Deserialize<Profile>(\"{}\"); p.Home.Hits++;");
        Assert.DoesNotContain(local.Findings, finding => finding.Resource.Member.Name == "Hits");
        var shared = await Full("_state.Profile = JsonSerializer.Deserialize<Profile>(\"{}\");", "_state.Profile.Home.Hits++;");
        Assert.Contains(shared.Findings, finding => finding.Resource.Member.Name == "Hits" && finding.RuleId == "DCA1002");
    }

    [Fact]
    public async Task New_graph_objects_are_not_of_exact_type()
    {
        var result = await Full("_state.Base = JsonSerializer.Deserialize<Base>(\"{}\");", "if (_state.Base is Extended p) p.Extra = 1;");
        Assert.Contains(result.Findings, finding => finding.Resource.Member.Name == "Extra");
        var run = Run("_ = JsonSerializer.Deserialize<Profile>(\"{}\");");
        Assert.NotEmpty(Created(run, "Profile"));
        Assert.All(run.Execution.Heap.Heap.Regions.Values.Where(region => region.ModelCreationKey is not null), region => Assert.False(region.HasExactType));
    }

    [Fact]
    public void New_output_is_a_new_graph_of_the_parameter_type()
    {
        var solution = Solution("NewGraph.Lib.Make<Profile>(out var p); p.Home.Hits = 1;");
        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, OutputModels));
        Assert.Single(Created(run, "Profile"));
        Assert.Single(Created(run, "Address"));
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.Write);
        Assert.All(run.Execution.Heap.Heap.Regions.Values.Where(region => region.ModelCreationKey is not null), region => Assert.Contains("output0", region.ModelCreationKey!));
    }

    [Fact]
    public void Display_ordinal_follows_the_explicit_creations_of_the_method()
    {
        var run = Run("var explicitAddress = new Address(); var p = JsonSerializer.Deserialize<Profile>(\"{}\"); p.Home.Hits = 1;");
        Assert.Equal("alloc:Worker.ExecuteAsync(CancellationToken)#Address#2", Assert.Single(Created(run, "Address")).Display);
    }

    [Theory]
    [InlineData("_ = new Address[0].ToList(); _ = JsonSerializer.Deserialize<List<Address>>(\"[]\");", "System.Collections.Generic.List<Address>")]
    [InlineData("_ = new Address[0].ToDictionary(address => address); _ = JsonSerializer.Deserialize<Dictionary<Address, Address>>(\"{}\");", "System.Collections.Generic.Dictionary<Address, Address>")]
    public void Display_ordinal_does_not_count_earlier_collection_results(string work, string type)
    {
        var run = Run(work);
        var root = Assert.Single(run.Execution.Heap.Heap.Regions.Values, region => region.ModelCreationKey is not null && region.ModelCreationKey.Contains("|root|", StringComparison.Ordinal));
        Assert.Equal("alloc:Worker.ExecuteAsync(CancellationToken)#" + type, root.Display);
    }

    [Fact]
    public void Display_ordinals_follow_the_walk_across_two_graphs_in_one_method()
    {
        var run = Run("var first = JsonSerializer.Deserialize<Walk>(\"{}\"); var second = JsonSerializer.Deserialize<Walk>(\"{}\");");
        var roots = Created(run, "Walk").OrderBy(region => region.Display, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, roots.Length);
        for (var graph = 0; graph < roots.Length; graph++)
        {
            var prefix = roots[graph].ModelCreationKey!.Split("|root|")[0];
            var addresses = Created(run, "Address").Where(region => region.ModelCreationKey!.StartsWith(prefix + "|", StringComparison.Ordinal))
                .OrderBy(Ordinal).ToArray();
            Assert.Equal(5, addresses.Length);
            Assert.Equal(Enumerable.Range(1 + graph * 5, 5), addresses.Select(Ordinal));
            Assert.Contains("Ordinary", addresses[0].ModelCreationKey!);
            Assert.Contains("Property", addresses[1].ModelCreationKey!);
            Assert.Contains("Base", addresses[2].ModelCreationKey!);
            Assert.EndsWith(PathValue.KEYS + "|Fixture:Address", addresses[3].ModelCreationKey);
            Assert.EndsWith(PathValue.ELEMENT + "|Fixture:Address", addresses[4].ModelCreationKey);
        }
    }

    [Fact]
    public void Object_interface_and_abstract_roots_have_no_graph()
    {
        var run = Run("_ = JsonSerializer.Deserialize<object>(\"{}\"); _ = JsonSerializer.Deserialize<IEnumerable<Profile>>(\"[]\"); _ = JsonSerializer.Deserialize<Abstract>(\"{}\");");
        Assert.Equal(3, run.Execution.Heap.Heap.Regions.Values.Count(region => region.ModelCreationKey is not null));
        Assert.Empty(Created(run, "Profile"));
    }

    [Fact]
    public void Result_without_a_form_still_links_to_nothing()
    {
        var solution = Solution("var p = NewGraph.Lib.None<Profile>(); p.Home.Hits = 1;");
        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, OutputModels));
        Assert.Empty(run.Of("Hits"));
        Assert.Empty(Created(run, "Profile"));
    }

    [Fact]
    public void Source_collection_base_has_substituted_new_elements() =>
        HasWrite("var p = JsonSerializer.Deserialize<AddressList>(\"[]\"); p[0].Hits = 1;", "Hits");

    private static EngineRun Run(string work, string read = "") => AnalyzeScope(Solution(work, read), "scope:Fixture");
    private static int Ordinal(HeapRegion region) => int.TryParse(region.Display.Split('#').Last(), out var ordinal) ? ordinal : 1;
    private static void HasWrite(string work, string member) => Assert.Contains(Run(work).Of(member), access => access.Operation == AccessOperation.Write);
    private static HeapRegion[] Created(EngineRun run, string type) => run.Execution.Heap.Heap.Regions.Values
        .Where(region => region.ModelCreationKey is not null && region.TypeKey == "Fixture:" + type).ToArray();
    private static Task<AnalysisResult> Full(string work, string read) => PhaseOneAnalyzer.AnalyzeAsync(Solution(work, read), ROOT_DIRECTORY, CancellationToken.None);

    private const string OutputModels = """
        {"schemaVersion":1,"assemblies":["NewGraph"],"models":[
          {"member":"M:NewGraph.Lib.Make``1(``0@)","effects":{},"outputs":{"value":"new"}},
          {"member":"M:NewGraph.Lib.None``1","effects":{}}
        ]}
        """;

    private static Solution Solution(string work, string read = "") => FixtureSolution.Create(
        new FixtureOptions { MetadataReferences = [Library] }, ("Case.cs", Usings + $$"""
        using System.Collections.Generic;
        using System.Linq;
        using System.Text.Json;
        public class Address { public int Hits; }
        public class Key { public int Hits; }
        public class Profile { public int Changes; public Address Home { get; set; } }
        public class Fields { public Address First; public Address Second; }
        public class GenericBase<T> { public T Base; }
        public class Derived : GenericBase<Address> { }
        public class Node { public int Hits; public Node Next; }
        public class Collections { public List<Address> Items; public Address[] Array; public Dictionary<Key, Address> Map; }
        public interface I { void Touch(); }
        public abstract class Abstract { public int Hits; }
        public struct Structure { public Address Value; }
        public class Boundaries { public I Interface; public Structure Struct; public Abstract Abstract; public System.Version Metadata; public KeyValuePair<Key, Address> Pair; }
        public class AddressList : List<Address> { }
        public class Base { public int Hits; }
        public class Extended : Base { public int Extra; }
        public class Walk : GenericBase<Address> { public Dictionary<Address, Address> Map; public Address Ordinary; public Address Property { get; set; } }
        public class State { public Profile Profile; public Fields Fields; public Node Node; public Base Base; }
        public class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{work}} return Task.CompletedTask; }
        }
        [ApiController] public class Reader(State state) : ControllerBase
        {
            private readonly State _state = state;
            [HttpPost] public void Post() { {{read}} }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();")));

    private static MetadataReference Library => Reference.Value;
    private static readonly Lazy<MetadataReference> Reference = new(() =>
    {
        var compilation = CSharpCompilation.Create("NewGraph", [CSharpSyntaxTree.ParseText("""
            namespace NewGraph;
            public static class Lib
            {
                public static void Make<T>(out T value) { value = default!; }
                public static T None<T>() => default!;
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        Assert.True(compilation.Emit(bytes).Success);
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });
}
