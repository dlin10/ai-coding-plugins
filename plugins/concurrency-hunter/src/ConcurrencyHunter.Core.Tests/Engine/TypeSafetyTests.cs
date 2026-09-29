using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class TypeSafetyTests
{
    [Fact]
    public void Definition_of_a_key_drops_its_arguments()
    {
        Assert.Equal("A:System.Collections.Generic.List`1", TypeSafety.DefinitionOf("A:System.Collections.Generic.List<A:System.Int32>"));
        Assert.Equal("A:System.Collections.Generic.Dictionary`2",
                     TypeSafety.DefinitionOf("A:System.Collections.Generic.Dictionary<A:System.String, A:System.Collections.Generic.List<A:System.Int32>>"));
        Assert.Equal("A:Outer`1.Inner`1", TypeSafety.DefinitionOf("A:Outer<A:System.Int32>.Inner<A:System.String>"));
        Assert.Equal("A:Box`2", TypeSafety.DefinitionOf("A:Box<A:System.Int32[,], A:System.String>"));
        Assert.Equal("[]", TypeSafety.DefinitionOf("A:System.Int32[]"));
        Assert.Equal("[,]", TypeSafety.DefinitionOf("A:System.Int32[,]"));
        Assert.Equal("Fixture:Tag", TypeSafety.DefinitionOf("Fixture:Tag"));
    }

    [Fact]
    public async Task Array_rank_inside_a_generic_argument_does_not_admit_a_three_parameter_member()
    {
        var result = await AnalyzeFixture("""
            public sealed class Box<T, U> { }
            public sealed class Box<T, U, V> { public void Touch() => Marks.Hits++; }
            """, "((Box<int, string, int>)(object)new Box<int[,], string>()).Touch();");

        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task Call_skips_a_receiver_that_cannot_be_the_declaring_type()
    {
        var result = await Analyze("((Batch)(object)new Tag()).Apply(() => Marks.Hits++);");
        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
    }

    [Fact]
    public async Task Call_whose_every_receiver_is_skipped_is_dead()
    {
        var result = await Analyze("((Batch)(object)new Tag()).Apply(() => Marks.Hits++);");
        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
        Assert.Empty(result.Coverage[0].Gaps);
        Assert.Equal(0, result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT]);
    }

    [Fact]
    public async Task Call_through_a_pattern_local_skips_objects_of_other_types() =>
        AssertNoHits(await Analyze("object entry = new Tag(); if (entry is Batch batch) batch.Apply(() => Marks.Hits++);"));

    [Fact]
    public async Task Call_through_an_as_value_skips_objects_of_other_types() =>
        AssertNoHits(await Analyze("object entry = new Tag(); var batch = entry as Batch; if (batch != null) batch.Apply(() => Marks.Hits++);"));

    [Fact]
    public async Task Non_virtual_call_whose_every_receiver_is_skipped_is_dead()
    {
        var source = Fixture("((Batch)(object)new Tag()).Apply(() => Marks.Hits++);");
        AssertNoHits(await AnalyzeSource(source));
        Assert.DoesNotContain(Solve(source).Heap.Instances.Values,
                              instance => instance.BodyId.Contains("Batch.Apply", StringComparison.Ordinal) && instance.IsReceiverless);
    }

    [Fact]
    public async Task Parameter_receiver_bound_to_known_objects_can_be_dead() =>
        AssertNoHits(await AnalyzeFixture("public static class Helper { public static void Go(object value) => ((Batch)value).Apply(() => Marks.Hits++); }",
                                          "Helper.Go(new Tag());"));

    [Fact]
    public async Task Dead_call_hands_no_delegate_off() =>
        AssertNoHits(await Analyze("((Batch)(object)new Tag()).Apply(() => Marks.Hits++);"));

    [Fact]
    public async Task Dead_call_counts_no_receiver_object()
    {
        var result = await Analyze("((Batch)(object)new Tag()).Apply(() => Marks.Hits++);");
        Assert.Equal(0, result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT]);
    }

    [Fact]
    public async Task Receiver_without_regions_stays_unresolved()
    {
        var result = await Analyze("Batch? batch = null; batch!.Apply(() => Marks.Hits++);");
        Assert.True(result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT] > 0);
    }

    [Fact]
    public async Task Receiver_with_an_opaque_call_origin_is_not_dead()
    {
        var result = await Analyze("object value = new Tag(); if (Environment.GetEnvironmentVariable(\"X\") != null) value = Activator.CreateInstance(typeof(Tag))!; ((Batch)value).Apply(() => Marks.Hits++);");
        Assert.True(result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT] > 0);
    }

    [Fact]
    public async Task Field_access_on_an_object_of_the_declaring_type_is_kept()
    {
        var result = await AnalyzeFixture("public class Base { public int Count; public int Value { get; set; } } public sealed class Derived : Base { }",
                                          "var value = new Derived(); value.Count++; value.Value++; ");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Count"]));
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]));
    }

    [Fact]
    public async Task Field_access_on_a_generic_type_keeps_its_own_objects()
    {
        var result = await AnalyzeFixture("public sealed class Box<T> { public int Value; public void Touch() => Value++; }",
                                          "var box = new Box<int>(); box.Value++; box.Touch();");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Value"]));
    }

    [Fact]
    public async Task Field_access_skips_an_object_that_cannot_have_the_field()
    {
        var result = await Analyze("((Slot)(object)new Tag()).Count++;");
        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Count"]));
    }

    [Fact]
    public async Task Field_access_through_a_recursive_or_list_designation_skips_other_types() =>
        Assert.DoesNotContain((await Analyze("object entry = new Tag(); if (entry is Slot { } slot) slot.Count++; ")).Accesses,
                              access => access.Resource.AccessPath.SequenceEqual(["Count"]));

    [Fact]
    public async Task Symbolic_receiver_is_kept()
    {
        var result = await AnalyzeSource(Usings + "public sealed class Batch { public void Apply() { } } public sealed class CaseController : ControllerBase { public void Post(Batch batch) => batch.Apply(); }" + Startup(""));
        Assert.True(result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT] > 0);
    }

    [Fact]
    public async Task Di_object_is_kept()
    {
        var result = await AnalyzeFixture("", "((Batch)(object)_tag).Apply(() => Marks.Hits++);", "services.AddSingleton<Tag>();", "private readonly Tag _tag; public CaseController(Tag tag) => _tag = tag;");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
    }

    [Fact]
    public async Task Library_method_result_is_kept()
    {
        var result = await Analyze("((Batch)(object)new List<int> { 1 }.ToList()).Apply(() => Marks.Hits++);");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
    }

    [Fact]
    public async Task Metadata_object_is_skipped_for_a_source_member() =>
        AssertNoHits(await Analyze("((Batch)(object)new List<int>()).Apply(() => Marks.Hits++);"));

    [Fact]
    public void Metadata_object_without_recorded_supertypes_is_kept()
    {
        var source = Fixture("((Batch)(object)new List<int>()).Apply(() => Marks.Hits++);");
        var run = Reach(source) with { MetadataSupertypes = new Dictionary<string, IReadOnlySet<string>>() };
        Assert.Contains(Solve(run).Heap.Instances.Values,
                        instance => instance.BodyId.Contains("Batch.Apply", StringComparison.Ordinal));
    }

    [Fact]
    public void Metadata_object_is_kept_for_an_interface_it_implements()
    {
        var run = Reach(Fixture("IList<int> list = new List<int> { 1 }; var count = list.Count;"));
        var region = Assert.Single(Solve(run).Heap.Regions.Values, candidate => candidate.HasExactType && candidate.TypeKey!.Contains("List", StringComparison.Ordinal));
        var declaring = Assert.Single(run.MetadataSupertypes[TypeSafety.DefinitionOf(region.TypeKey!)],
                                      key => key.EndsWith("IList`1", StringComparison.Ordinal));
        Assert.False(new TypeSafety(run.Input.Program, run.MetadataSupertypes)
                     .CannotBe(region, declaring));
    }

    [Fact]
    public void Variant_interface_member_keeps_the_object()
    {
        var run = Reach(Fixture("IEnumerable<object> items = new List<string>(); var iterator = items.GetEnumerator();"));
        var region = Assert.Single(Solve(run).Heap.Regions.Values, region => region.HasExactType && region.TypeKey!.Contains("List", StringComparison.Ordinal));
        var safety = new TypeSafety(run.Input.Program, run.MetadataSupertypes);
        Assert.False(safety.CannotBe(region, "System.Private.CoreLib:System.Collections.Generic.IEnumerable<System.Private.CoreLib:System.Object>"));
    }

    [Fact]
    public void Member_of_object_keeps_every_region()
    {
        var run = Reach(Fixture("var tag = new Tag();"));
        var region = Assert.Single(Solve(run).Heap.Regions.Values, candidate => candidate.HasExactType && candidate.TypeKey == "Fixture:Tag");
        Assert.False(new TypeSafety(run.Input.Program, run.MetadataSupertypes).CannotBe(region, "System.Private.CoreLib:System.Object"));
        Assert.False(new TypeSafety(run.Input.Program, run.MetadataSupertypes).CannotBe(region, "System.Runtime:object"));
    }

    [Fact]
    public async Task Region_arriving_in_a_later_pass_makes_the_call_run()
    {
        var result = await AnalyzeFixture("public static class Factory { public static Batch Create() => new Batch(); }",
                                          "Factory.Create().Apply(() => Marks.Hits++);");
        Assert.Contains(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
    }

    [Fact]
    public async Task Substituted_metadata_type_finds_its_supertypes() =>
        AssertNoHits(await AnalyzeFixture("public static class Factory { public static void Go<T>() => ((Batch)(object)new List<T>()).Apply(() => Marks.Hits++); }",
                                          "Factory.Go<int>();"));

    [Fact]
    public async Task Array_is_skipped_for_a_source_member_and_kept_for_its_interfaces()
    {
        AssertNoHits(await Analyze("((Batch)(object)new int[3]).Apply(() => Marks.Hits++);"));
        var result = await Analyze("IList<int> array = new int[3]; var count = array.Count;");
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public void Every_created_metadata_region_has_recorded_supertypes()
    {
        var run = Reach(Fixture("new List<int>(); new Dictionary<string, Tag>(); _ = new int[3]; new System.Text.StringBuilder(); Factory.Go<int>();",
                                "public static class Factory { public static void Go<T>() { new List<T>(); } }"));
        var heap = Solve(run).Heap;
        Assert.All(heap.Regions.Values.Where(region => region.HasExactType && run.Input.Program.Type(region.TypeKey!) is null),
                   region => Assert.Contains(TypeSafety.DefinitionOf(region.TypeKey!), run.MetadataSupertypes.Keys));
    }

    [Fact]
    public async Task Demo_created_metadata_regions_all_have_recorded_supertypes()
    {
        await DemoWorkspace.EnsureRestoredAsync();
        var path = RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx");
        using var loaded = await new Common.Roslyn.MsBuildSolutionLoader().LoadAsync(path);
        var run = ReachScope(loaded.Solution, "Demo.Web");
        var heap = Solve(run).Heap;
        Assert.All(heap.Regions.Values.Where(region => region.HasExactType && run.Input.Program.Type(region.TypeKey!) is null),
                   region => Assert.Contains(TypeSafety.DefinitionOf(region.TypeKey!), run.MetadataSupertypes.Keys));
    }

    private static void AssertNoHits(AnalysisResult result)
    {
        Assert.DoesNotContain(result.Accesses, access => access.Resource.AccessPath.SequenceEqual(["Hits"]));
        Assert.Empty(result.Coverage[0].Gaps);
    }

    private static Task<AnalysisResult> AnalyzeFixture(string extra, string action, string registration = "", string members = "") =>
        AnalyzeSource(Fixture(action, extra, registration, members));

    private static Task<AnalysisResult> AnalyzeSource(string source) =>
        PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", source)), ROOT_DIRECTORY, CancellationToken.None);

    private static string Fixture(string action, string extra = "", string registration = "", string members = "") =>
        "using System.Collections.Generic; using System.Linq; " + Usings + $$"""
        public sealed class Tag { }
        public sealed class Slot { public int Count; }
        public class Batch { public void Apply(Action work) => work(); }
        public static class Marks { public static int Hits; }
        {{extra}}
        public sealed class CaseController : ControllerBase
        {
            {{members}}
            public void Post() { {{action}} }
        }
        """ + Startup(registration);

    private static Task<AnalysisResult> Analyze(string action) => AnalyzeSource(Fixture(action));
}
