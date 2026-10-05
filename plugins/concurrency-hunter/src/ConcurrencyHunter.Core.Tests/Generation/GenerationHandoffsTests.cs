using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The handoffs, witness receipts and foreign accesses of a completed generator run.</summary>
public sealed class GenerationHandoffsTests
{
    [Fact]
    public void An_argument_of_an_unknown_call_is_handed()
    {
        var (trace, handoffs) = Run("public abstract class Node { public object Next; } public static class Api { public static void Send(Node n, Action a) => Sink.Take(n); }",
                                    "M:Lib.Api.Send(Lib.Node,System.Action)");

        Assert.All(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"), region => Assert.Contains(region, handoffs.HandedOutsideSetup));
    }

    [Fact]
    public void What_an_unknown_calls_argument_reaches_is_handed()
    {
        var (trace, handoffs) = Run("public abstract class Node { public object Next; } public static class Api { public static void Send(Node n, Action a) => Sink.Take(n); }",
                                    "M:Lib.Api.Send(Lib.Node,System.Action)");
        var root = Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"));
        var reached = new HeapReachability(trace.Run!.Heap!).From([root]);

        Assert.All(reached, region => Assert.Contains(region, handoffs.HandedOutsideSetup));
        Assert.True(reached.Count > 1);
    }

    [Fact]
    public void A_partly_resolved_calls_receiver_and_argument_are_handed()
    {
        var source = """
            public abstract class Node { public virtual void Ping(Node value) { } }
            public static class Maybe { public static extern Node Make(); }
            public static class Api
            {
                public static void Partial(Node n, Action a)
                {
                    var target = DateTime.Now.Ticks == 0 ? Maybe.Make() : n;
                    target.Ping(n);
                    a();
                }
            }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Partial(Lib.Node,System.Action)");

        Assert.NotEmpty(trace.Run!.Heap!.UnresolvedCallTargets);
        Assert.All(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"), region => Assert.Contains(region, handoffs.HandedOutsideSetup));
    }

    private const string SINKS = "public interface ISink { void Accept(object p); } public sealed class LibSink : ISink { public object Last; public void Accept(object p) { Last = p; } } " +
                                "public static class Extern { public static extern ISink Make(); } ";

    [Theory]
    [InlineData("Helper(Extern.Make(), p);")]
    [InlineData("Helper(DateTime.Now.Ticks == 0 ? Extern.Make() : new LibSink(), p);")]
    public void A_call_on_a_parameter_a_caller_binds_to_an_unknown_calls_result_hands_its_argument(string body)
    {
        var (trace, handoffs) = Run(SINKS + $"public static class Api {{ public static void Run(object p) {{ {body} }} static void Helper(ISink sink, object p) => sink.Accept(p); }}",
                                    "M:Lib.Api.Run(System.Object)");

        Assert.All(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_p_Call"), region => Assert.Contains(region, handoffs.HandedOutsideSetup));
    }

    [Theory]
    [InlineData("public static void Run(object p) => Helper(new LibSink(), p); static void Helper(ISink sink, object p) => sink.Accept(p);", "M:Lib.Api.Run(System.Object)")]
    [InlineData("public static void Run(ISink sink, object p) => sink.Accept(p);", "M:Lib.Api.Run(Lib.ISink,System.Object)")]
    public void A_call_on_a_parameter_the_heap_binds_to_named_objects_hands_nothing(string members, string id)
    {
        var (trace, handoffs) = Run(SINKS + $"public static class Api {{ {members} }}", id);

        Assert.All(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_p_Call"), region => Assert.DoesNotContain(region, handoffs.HandedOutsideSetup));
    }

    [Fact]
    public void An_unknown_delegate_handoff_includes_the_delegate_and_its_captures()
    {
        var source = """
            public abstract class Node { public object Next; }
            public static class Api { public static void Later(Node n, Action a) { Sink.Take(new Action(() => { _ = n.Next; a(); })); } }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Later(Lib.Node,System.Action)");
        var delegateRegion = Assert.Single(trace.Run!.Heap!.DelegateHandoffs).RegionId;

        Assert.Contains(delegateRegion, handoffs.HandedOutsideSetup);
        Assert.All(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"), region => Assert.Contains(region, handoffs.HandedOutsideSetup));
    }

    [Fact]
    public void A_delegate_handoff_does_not_include_the_calls_other_arguments()
    {
        var source = """
            public abstract class Node { public object Next; }
            public static class Api { public static void Later(Node other, Action a) { Sink.Take(new Action(() => a())); } }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Later(Lib.Node,System.Action)");

        Assert.DoesNotContain(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_other_Call")), handoffs.HandedOutsideSetup);
    }

    [Fact]
    public void A_library_body_touch_in_an_unknown_execution_is_foreign()
    {
        var source = """
            public abstract class Node { public object Next; }
            public static class Api { public static void Later(Node n, Action a) { Sink.Take(new Action(() => { _ = n.Next; })); } }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Later(Lib.Node,System.Action)");

        Assert.All(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"), region => Assert.Contains(region, handoffs.ForeignAccesses));
    }

    [Fact]
    public void The_escape_artefacts_enumeration_is_not_foreign()
    {
        var source = """
            public abstract class Node { public object Next; }
            public static class Api
            {
                public static IEnumerable<Node> Lazy(Node n, Func<Node, bool> keep)
                {
                    _ = n.Next;
                    if (keep(n)) yield return n;
                }
            }
            """;
        var (_, handoffs) = Run(source, "M:Lib.Api.Lazy(Lib.Node,System.Func{Lib.Node,System.Boolean})");

        Assert.Empty(handoffs.ForeignAccesses);
    }

    [Fact]
    public void A_setup_calls_handoff_is_in_setup_only()
    {
        var source = """
            public sealed class Carrier { public Carrier(Action a) { Sink.Take(this); } }
            public static class Api { public static void Run(Carrier c, Action done) => done(); }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Run(Lib.Carrier,System.Action)");
        var carrier = Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_c_Call"));

        Assert.Contains(carrier, handoffs.HandedInSetup);
        Assert.DoesNotContain(carrier, handoffs.HandedOutsideSetup);
    }

    [Fact]
    public void A_call_instance_used_in_setup_and_the_call_files_values_in_both_sets()
    {
        var source = """
            public static class Helper { public static void Send(object value) => Sink.Take(value); }
            public sealed class Carrier { public Carrier(Action a) { Helper.Send(this); } }
            public static class Api { public static void Run(Carrier c, Action done) { Helper.Send(c); done(); } }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Run(Lib.Carrier,System.Action)");
        var carrier = Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_c_Call"));

        Assert.Contains(carrier, handoffs.HandedInSetup);
        Assert.Contains(carrier, handoffs.HandedOutsideSetup);
        Assert.Contains(handoffs.HandoffsOutsideSetup.Values, regions => regions.Contains(carrier));
    }

    [Fact]
    public void A_witness_instance_used_in_setup_and_the_call_files_receipts_in_both_sets()
    {
        var source = """
            public abstract class Node
            {
                protected Node() { Ping(null); }
                public virtual void Ping(object value) { }
            }
            public static class Api { public static void Run(Node n, Action done) { n.Ping(done); } }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Run(Lib.Node,System.Action)");
        var node = Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"));

        Assert.Contains(node, handoffs.WitnessedInSetup);
        Assert.Contains(node, handoffs.WitnessedOutsideSetup);
    }

    [Fact]
    public void A_known_witness_call_records_its_delegate_input_and_receiver()
    {
        var source = """
            public abstract class Node { public virtual void Ping(Action value) { } }
            public static class Api { public static void Run(Node other, Action done) { other.Ping(done); } }
            """;
        var (trace, handoffs) = Run(source, "M:Lib.Api.Run(Lib.Node,System.Action)");
        var probe = trace.Driver!.Parameters.Single(parameter => parameter.Name == "done").Probes
                         .Single(candidate => candidate.Variant == DriverSynthesizer.CALL_VARIANT);
        var delegateRegion = trace.Run!.Heap!.Regions.Values.Single(region => region.SiteBodyId == Allocations.ProbeBody(probe)).Identity;

        Assert.Contains(delegateRegion, handoffs.WitnessedOutsideSetup);
        Assert.Contains(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_other_Call")), handoffs.WitnessedOutsideSetup);
    }

    [Fact]
    public void A_startup_delegate_is_handed_with_its_captures()
    {
        var trace = Trace("public abstract class Node { public object Next; } public static class Api { " +
                          "public static void Later(Node n, Action a) { Sink.Take(new Action(() => { _ = n.Next; a(); })); } }",
                          "M:Lib.Api.Later(Lib.Node,System.Action)");
        var heap = trace.Run!.Heap!;
        var handoff = Assert.Single(heap.DelegateHandoffs);
        var site = Assert.Single(handoff.Sites);
        var startup = new StartupDelegate(site.CallerInstance, site.OperationId, handoff.RegionId, Assert.Single(handoff.Callees));
        var handoffs = Build(trace, Copy(heap, delegateHandoffs: [], startupDelegates: [startup]));

        Assert.Contains(handoff.RegionId, handoffs.HandedOutsideSetup);
        Assert.Contains(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call")), handoffs.HandedOutsideSetup);
    }

    [Fact]
    public void A_handoff_made_only_in_the_escape_artefact_is_in_no_set()
    {
        var trace = Trace("public abstract class Node { public object Next; } public static class Api { " +
                          "public static IEnumerable<Node> Lazy(Node n, Action done) { done(); yield return n; } }",
                          "M:Lib.Api.Lazy(Lib.Node,System.Action)");
        var heap = trace.Run!.Heap!;
        var (_, executions) = Context(trace, heap);
        var instance = trace.Run.Executions!.InstanceExecutions.First(pair => pair.Value.Count != 0 && pair.Value.All(executions.IsArtefact)).Key;
        var region = Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"));
        var artificial = new DelegateHandoff(region, [(instance, 0)], []);
        var handoffs = Build(trace, Copy(heap, delegateHandoffs: [artificial]));

        Assert.DoesNotContain(region, handoffs.HandedInSetup);
        Assert.DoesNotContain(region, handoffs.HandedOutsideSetup);
    }

    [Fact]
    public void A_known_unknown_execution_handoff_includes_its_input_but_not_the_calls_other_argument()
    {
        var trace = Trace("public abstract class Node { public object Next; } public static class Api { " +
                          "public static void Run(Node input, Node other, Action<Node> callback) => callback(input); }",
                          "M:Lib.Api.Run(Lib.Node,Lib.Node,System.Action{Lib.Node})");
        var heap = trace.Run!.Heap!;
        var probe = trace.Driver!.Parameters.Single(parameter => parameter.Name == "callback").Probes
                         .Single(candidate => candidate.Variant == DriverSynthesizer.CALL_VARIANT);
        var region = heap.Regions.Values.Single(candidate => candidate.SiteBodyId == Allocations.ProbeBody(probe)).Identity;
        var callee = heap.Instances.Values.Single(instance => instance.BodyId.StartsWith(Allocations.ProbeBody(probe) + "#", StringComparison.Ordinal));
        var edge = heap.ExecutionEdges.Single(candidate => candidate.CalleeInstance == callee.Id);
        var handoff = new DelegateHandoff(region, [(edge.CallerInstance, edge.OperationId)], [callee.Id]);
        var handoffs = Build(trace, Copy(heap, delegateHandoffs: [handoff]));

        Assert.Contains(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_input_Call")), handoffs.HandedOutsideSetup);
        Assert.DoesNotContain(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_other_Call")), handoffs.HandedOutsideSetup);
    }

    private static (GenerationTrace Trace, GenerationHandoffs Handoffs) Run(string types, string member)
    {
        var trace = Trace(types, member);
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return (trace, Build(trace, trace.Run.Heap!));
    }

    private static GenerationHandoffs Build(GenerationTrace trace, HeapSolution heap)
    {
        var (reachability, executions) = Context(trace, heap);
        return new GenerationHandoffs(trace.Driver!, trace.Run! with { Heap = heap }, executions, reachability);
    }

    private static (HeapReachability Reachability, DriverExecutions Executions) Context(GenerationTrace trace, HeapSolution heap)
    {
        var reachability = new HeapReachability(heap);
        var result = reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        return (reachability,
                new DriverExecutions(trace.Driver!, trace.Run!.Executions!, reachability.From(result is null ? [] : reachability.Targets(result))));
    }

    private static HeapSolution Copy(HeapSolution heap, IReadOnlyList<DelegateHandoff>? delegateHandoffs = null,
                                     IReadOnlyList<StartupDelegate>? startupDelegates = null) =>
        new(heap.Regions, heap.Instances, heap.Edges, heap.TypeInitializers, heap.Constructions, heap.Counters, heap.ReachableBodies,
            heap.LoweredNotReached, heap.NoReceiverObjects, heap.Resolve, heap.PointsTo, heap.Cell, heap.RootInstances, heap.StaticRegionOf,
            heap.FieldsOf, heap.DelegateCaptures)
        {
            ExecutionEdges = heap.ExecutionEdges,
            UnresolvedCallTargets = heap.UnresolvedCallTargets,
            DelegateHandoffs = delegateHandoffs ?? heap.DelegateHandoffs,
            StartupDelegates = startupDelegates ?? heap.StartupDelegates
        };
}
