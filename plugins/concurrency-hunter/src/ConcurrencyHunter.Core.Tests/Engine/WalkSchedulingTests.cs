using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class WalkSchedulingTests
{
    [Fact]
    public void Each_node_of_an_acyclic_execution_is_walked_once()
    {
        var heap = Solve("""
            public sealed class Note { public int Value; public Note() { Helper.Touch(this); } }
            public static class Helper { public static void Touch(Note note) => note.Value = 1; }
            public class NotesController : ControllerBase { public void Post() { new Note(); new Note(); } }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 0));
        var analysis = Build(heap);
        Assert.NotEmpty(analysis.WalkNodeVisits);
        Assert.Equal(analysis.WalkNodeVisits.Count, analysis.WalkVisits);
        Assert.All(analysis.WalkNodeVisits.Values, visits => Assert.Equal(1, visits));
        AssertEquivalent(heap, analysis);
    }

    [Fact]
    public void Node_with_many_predecessors_is_walked_once_per_change_not_once_per_edge()
    {
        var constructors = string.Join(Environment.NewLine, Enumerable.Range(0, 30).Select(index =>
            $"public sealed class C{index} : Note {{ public C{index}() {{ Helper.Touch(this); }} }}"));
        var calls = string.Join(" ", Enumerable.Range(0, 30).Select(index => $"new C{index}();"));
        var heap = Solve(constructors + $$"""
            public abstract class Note { public int Value; }
            public static class Helper { public static void Touch(Note note) => note.Value = 1; }
            public class NotesController : ControllerBase { public void Post() { {{calls}} } }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 0));
        var analysis = Build(heap);
        var helper = Assert.Single(heap.Instances("body:Fixture:M:Helper.Touch(Note)"));
        Assert.True(heap.Heap.ExecutionEdges.Count(edge => edge.CalleeInstance == helper.Id) >= 30);
        Assert.Equal(1, Assert.Single(analysis.WalkNodeVisits, pair => pair.Key.Instance == helper.Id).Value);
        var stores = analysis.Accesses.Where(access => access.InstanceId == helper.Id).ToArray();
        Assert.Equal(30, stores.Select(access => access.RegionId).Distinct().Count());
        Assert.Contains(stores, access => access.IsConstructionLocal);
        Assert.Contains(stores, access => !access.IsConstructionLocal);
        AssertEquivalent(heap, analysis);
    }

    [Fact]
    public void Executions_reaching_a_shared_node_together_arrive_in_one_visit()
    {
        // More roots than one word of bits holds: the walk takes every root's entry before it passes executions on, so the helper is
        // walked once for all of them, not once per root.
        var actions = string.Join(" ", Enumerable.Range(0, 65).Select(index => $"public void A{index}() {{ Helper.Touch(this); }}"));
        var heap = Solve($$"""
            public static class Helper { public static void Touch(NotesController notes) => notes.Value = 1; }
            public class NotesController : ControllerBase { public int Value; {{actions}} }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 0));
        var analysis = Build(heap);
        var helper = Assert.Single(heap.Instances("body:Fixture:M:Helper.Touch(NotesController)"));
        Assert.Equal(1, Assert.Single(analysis.WalkNodeVisits, pair => pair.Key.Instance == helper.Id).Value);
        Assert.Equal(65, analysis.Accesses.Where(access => access.InstanceId == helper.Id).SelectMany(access => access.Executions).Distinct().Count());
        AssertEquivalent(heap, analysis);
    }

    [Fact]
    public void Cycle_is_walked_until_its_sets_settle()
    {
        var heap = Solve("""
            public sealed class Note { public int Value; public Note() { Helper.First(this); } }
            public sealed class Link { public Link(Note note) { Helper.First(note); } }
            public static class Helper
            {
                public static void First(Note note) { Second(note); }
                public static void Second(Note note) { note.Value = 1; new Link(note); }
            }
            public class NotesController : ControllerBase { public void Post() { new Note(); } }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 0));
        var analysis = Build(heap);
        var first = Assert.Single(heap.Instances("body:Fixture:M:Helper.First(Note)"));
        // The sets that settle are the executions at each node: the one execution arrives at each node of the cycle once, and the
        // objects under construction are worked out per object afterwards.
        Assert.Contains(analysis.WalkNodeVisits, pair => pair.Key.Instance == first.Id);
        Assert.Equal(analysis.WalkVisits, analysis.WalkNodeVisits.Values.Sum());
        Assert.All(analysis.WalkNodeVisits.Values, visits => Assert.Equal(1, visits));
        AssertEquivalent(heap, analysis);
    }

    [Fact]
    public void Later_entry_reaches_known_nodes_through_their_stored_successors()
    {
        var heap = Solve("""
            public static class Helper
            {
                public static void Touch(NotesController note) { Deep.Touch(note); }
            }
            public static class Deep { public static void Touch(NotesController note) => note.Value = 1; }
            public class NotesController : ControllerBase
            {
                public int Value;
                public NotesController() { Helper.Touch(this); }
                public void Post() { Helper.Touch(this); }
            }
            """ + Startup(), new AnalysisLimits(MaxContextsPerMethod: 0));
        var analysis = Build(heap);
        var helper = Assert.Single(heap.Instances("body:Fixture:M:Helper.Touch(NotesController)"));
        var deep = Assert.Single(heap.Instances("body:Fixture:M:Deep.Touch(NotesController)"));
        // Both entries belong to the root's execution, which arrives at each node once; the second entry's object under construction
        // is told apart per object, not by walking the nodes again.
        Assert.Equal(1, Assert.Single(analysis.WalkNodeVisits, pair => pair.Key.Instance == helper.Id).Value);
        Assert.Equal(1, Assert.Single(analysis.WalkNodeVisits, pair => pair.Key.Instance == deep.Id).Value);
        var stores = analysis.Accesses.Where(access => access.InstanceId == deep.Id).ToArray();
        Assert.Contains(stores, access => access.IsConstructionLocal);
        Assert.Contains(stores, access => !access.IsConstructionLocal);
        AssertEquivalent(heap, analysis);
    }

    private static ScopeProgram Scope(HeapRun heap) =>
        new(heap.Program.ScopeId, heap.Program.Input.Roots, heap.Program.Result, heap.Summaries, heap.Program.Input.Program,
            heap.Program.Input.DiIndex, heap.Program.Input.InjectionBindings) { MetadataSupertypes = heap.Program.MetadataSupertypes };

    private static ExecutionAnalysis Build(HeapRun heap) => ExecutionModel.Build(Scope(heap), heap.Heap, CancellationToken.None);

    private static void AssertEquivalent(HeapRun heap, ExecutionAnalysis analysis)
    {
        var scope = Scope(heap);
        var expected = ExecutionObservation.Capture(scope, heap.Heap, analysis);
        foreach (var mode in new ExecutionWalkOrder[] { new(Reverse: true), new(Seed: 17) })
        {
            var permuted = ExecutionModel.Build(scope, heap.Heap, CancellationToken.None, mode);
            var actual = ExecutionObservation.Capture(scope, heap.Heap, permuted);
            Assert.Equal(expected.Properties, actual.Properties);
            Assert.Equal(expected.Queries, actual.Queries);
            Assert.Equal(expected.Accesses, actual.Accesses);
            Assert.Equal(expected.Findings, actual.Findings);
        }
    }
}
