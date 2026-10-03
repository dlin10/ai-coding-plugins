using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ConstructionSetTests
{
    [Fact]
    public void Two_construction_sets_reaching_one_helper_keep_both_alternatives()
    {
        var heap = Solve("""
            public abstract class Note { public int Value; }
            public static class Helper { public static void Touch(Note note) => note.Value = 1; }
            public sealed class Left : Note { public Left() { Helper.Touch(this); } }
            public sealed class Right : Note { public Right() { Helper.Touch(this); } }
            public class NotesController : ControllerBase
            {
                public void Post() { new Left(); new Right(); }
            }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 0));
        var scope = Scope(heap);
        var analysis = ExecutionModel.Build(scope, heap.Heap, CancellationToken.None);
        var helper = Assert.Single(heap.Instances("body:Fixture:M:Helper.Touch(Note)"));
        var stores = analysis.Accesses.Where(access => access.InstanceId == helper.Id && access.Access.Kind == SummaryAccessKind.Store).ToArray();
        Assert.Equal(2, stores.Select(access => access.RegionId).Distinct().Count());
        foreach (var region in stores.Select(access => access.RegionId).Distinct())
        {
            Assert.Contains(stores, access => access.RegionId == region && access.IsConstructionLocal);
            Assert.Contains(stores, access => access.RegionId == region && !access.IsConstructionLocal);
            Assert.DoesNotContain(region, analysis.PublishedObjects);
        }

        // Publication must still remove the local alternative, even when an interval contains the object.
        var published = Solve("""
            public static class Registry { public static Note? Current; }
            public sealed class Note
            {
                public int Value;
                public Note() { Registry.Current = this; Helper.Touch(this); }
            }
            public static class Helper { public static void Touch(Note note) => note.Value = 1; }
            public class NotesController : ControllerBase { public void Post() { new Note(); } }
            """ + Startup());
        var publishedAnalysis = ExecutionModel.Build(Scope(published), published.Heap, CancellationToken.None);
        var store = Assert.Single(publishedAnalysis.Accesses, access => access.Access.Field.Name == "Value");
        Assert.False(store.IsConstructionLocal);
        Assert.Contains(store.RegionId, publishedAnalysis.PublishedObjects);
    }

    [Fact]
    public void Constructor_chain_walks_each_node_a_bounded_number_of_times()
    {
        // Merged constructor contexts make each diamond meet at one instance, with different allocation intervals.
        var constructors = string.Join(Environment.NewLine, Enumerable.Range(0, 10).Select(index =>
            $"public sealed class C{index} {{ public C{index}() {{ " +
            (index == 9 ? "Helper.Touch();" : $"new C{index + 1}(); new C{index + 1}();") + " } }"));
        var heap = Solve(constructors + """
            public static class Helper { public static int Value; public static void Touch() => Value = 1; }
            public class ChainController : ControllerBase { public void Post() { new C0(); } }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 1));
        var scope = Scope(heap);
        var analysis = ExecutionModel.Build(scope, heap.Heap, CancellationToken.None);
        Assert.NotEmpty(analysis.WalkNodeVisits);
        Assert.Equal(analysis.WalkVisits, analysis.WalkNodeVisits.Values.Sum());
        Assert.All(analysis.WalkNodeVisits.Values, visits => Assert.Equal(1, visits));
        var constructed = heap.Heap.Constructions.SelectMany(construction => construction.ConstructorInstances
                                                                                       .Select(instance => (Instance: instance, construction.RegionId)))
                                   .GroupBy(item => item.Instance, StringComparer.Ordinal)
                                   .ToDictionary(group => group.Key, group => group.First().RegionId, StringComparer.Ordinal);
        var edges = heap.Heap.ExecutionEdges.GroupBy(edge => edge.CallerInstance, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var execution in analysis.WalkNodeVisits.GroupBy(pair => pair.Key.Execution, StringComparer.Ordinal))
        {
            var objects = (analysis.Entries.GetValueOrDefault(execution.Key) ?? []).Where(entry => entry.IntervalObject is not null)
                                 .Select(entry => entry.IntervalObject!).ToHashSet(StringComparer.Ordinal);
            foreach (var node in execution.Select(pair => pair.Key))
            {
                var instance = heap.Heap.Instances[node.Instance];
                foreach (var edge in edges.GetValueOrDefault(node.Instance) ?? [])
                {
                    if (analysis.Follow(instance, node.Segment, edge) is null)
                        continue;
                    if (edge.Reason == WholeProgram.CONSTRUCTION_REASON && constructed.TryGetValue(edge.CalleeInstance, out var region))
                        objects.Add(region);
                    else if (instance.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId) is { Kind: IrCallKind.Constructor } constructor)
                        objects.UnionWith(constructor.Receivers.SelectMany(value => heap.Heap.Resolve(instance.Id, value))
                                                     .Where(regionId => heap.Heap.Regions[regionId].Kind == HeapRegionKind.Allocation));
                }
            }
            Assert.All(execution, node => Assert.InRange(node.Value, 1, 2 * objects.Count + 1));
        }
    }

    private static ScopeProgram Scope(HeapRun heap) =>
        new(heap.Program.ScopeId, heap.Program.Input.Roots, heap.Program.Result, heap.Summaries, heap.Program.Input.Program,
            heap.Program.Input.DiIndex, heap.Program.Input.InjectionBindings) { MetadataSupertypes = heap.Program.MetadataSupertypes };
}
