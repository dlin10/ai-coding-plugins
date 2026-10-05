using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Who made an object of a driver run and when (SPEC TD-034b, G-0), in the order the rule checks: the member's object, the
/// driver, the library during an action, in a child of it, before, unknown.</summary>
public sealed class AllocationsTests
{
    private const string BOX = "Fixture.Library:Lib.Box";

    [Fact]
    public void A_constructor_members_created_object_is_the_members_object_though_the_driver_wrote_its_new()
    {
        var (trace, allocations) = Run("", "M:Lib.Holder.#ctor(System.Action)");
        var created = Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"));

        Assert.StartsWith($"body:{DriverSynthesizer.ASSEMBLY}:", trace.Run!.Heap!.Regions[created].SiteBodyId);
        Assert.Equal(new Allocation(AllocationKind.MemberObject, DriverRole.None, null), allocations.Of(created));
    }

    [Fact]
    public void An_argument_value_a_probe_object_a_probe_delegate_and_a_probe_lambdas_return_are_the_drivers()
    {
        var (trace, allocations) = Run("public static class Api { public static object Take(object o, IEnumerable<int> s, Func<object> f) => f(); }",
                                       "M:Lib.Api.Take(System.Object,System.Collections.Generic.IEnumerable{System.Int32},System.Func{System.Object})");
        var heap = trace.Run!.Heap!;
        var factory = Allocations.ProbeBody(trace.Driver!.Parameters.Single(parameter => parameter.Name == "f").Probes
                                                 .Single(probe => probe.Variant == DriverSynthesizer.CALL_VARIANT));
        var probe = heap.Regions.Values.First(region => region.Kind == HeapRegionKind.Delegate && region.SiteBodyId == factory);
        var returned = heap.Regions.Values.First(region => region.SiteBodyId?.StartsWith(factory + "#", StringComparison.Ordinal) == true);

        Assert.Equal(new Allocation(AllocationKind.Driver, DriverRole.ProbeObject, null),
                     allocations.Of(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_o_Call").First()));
        Assert.Equal(new Allocation(AllocationKind.Driver, DriverRole.ArgumentValue, null),
                     allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_s_Call"))));
        Assert.Equal(new Allocation(AllocationKind.Driver, DriverRole.ProbeDelegate, null), allocations.Of(probe.Identity));
        Assert.Equal(new Allocation(AllocationKind.Driver, DriverRole.ProbeLambdaReturn, null), allocations.Of(returned.Identity));
        Assert.False(allocations.CreatedByCall(probe.Identity));
        Assert.False(allocations.CreatedByCall(returned.Identity));
    }

    [Fact]
    public void An_intermediate_and_an_Out_initial_value_are_the_drivers()
    {
        var (trace, allocations) = Run("""
            public sealed class Codec { private readonly Box _b; public Codec(Box b) { _b = b; } }
            public static class Api { public static void Use(Codec c, ref Box b, Action a) { a(); } }
            """, "M:Lib.Api.Use(Lib.Codec,Lib.Box@,System.Action)");

        Assert.Equal(new Allocation(AllocationKind.Driver, DriverRole.Intermediate, null),
                     allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "K0"))));
        Assert.Equal(new Allocation(AllocationKind.Driver, DriverRole.OutInitialValue, null),
                     allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Out_b_Call"))));
    }

    [Fact]
    public void An_object_a_library_factory_called_by_setup_made_is_the_drivers()
    {
        var (trace, allocations) = Run("""
            public sealed class Made { private Made() { } public static Made Create() => new Made(); }
            public static class Api { public static void Use(Made m, Action a) { a(); } }
            """, "M:Lib.Api.Use(Lib.Made,System.Action)");
        var made = Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_m_Call"));

        Assert.StartsWith($"body:{ASSEMBLY}:", trace.Run!.Heap!.Regions[made].SiteBodyId);
        Assert.Equal(new Allocation(AllocationKind.Driver, DriverRole.Setup, null), allocations.Of(made));
    }

    [Fact]
    public void An_object_the_library_made_while_V_Call_ran_is_the_librarys_during_V_Call()
    {
        var (trace, allocations) = Run("public static class Api { public static Holder Make(Action a) => new Holder(a); }", "M:Lib.Api.Make(System.Action)");

        Assert.Equal(new Allocation(AllocationKind.LibraryDuring, DriverRole.None, DriverSynthesizer.CALL),
                     allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"))));
    }

    [Fact]
    public void An_object_the_library_made_in_a_Task_Run_the_call_started_is_the_librarys_in_a_child()
    {
        var (trace, allocations) = Run("public static class Api { public static void Bg(Action a) { a(); Task.Run(() => { Cache.Last = new Box(); }); } }",
                                       "M:Lib.Api.Bg(System.Action)");
        var box = trace.Run!.Heap!.Regions.Values.Single(region => region.TypeKey == BOX);

        Assert.Equal(new Allocation(AllocationKind.LibraryInChild, DriverRole.None, DriverSynthesizer.CALL), allocations.Of(box.Identity));
    }

    [Fact]
    public void An_object_a_static_initializer_made_is_the_librarys_before()
    {
        var (trace, allocations) = Run("public static class Reg { private static readonly Box Shared = new Box(); public static Box Put(Action a) { Shared.A = a; return Shared; } }",
                                       "M:Lib.Reg.Put(System.Action)");

        Assert.Equal(new Allocation(AllocationKind.LibraryBefore, DriverRole.None, null),
                     allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"))));
    }

    [Fact]
    public void An_object_made_in_an_unknown_execution_is_unknown()
    {
        var (trace, allocations) = Run("public static class Api { public static void Later(Action a) { a(); Sink.Take(new Action(() => { Cache.Last = new Box(); })); } }",
                                       "M:Lib.Api.Later(System.Action)");
        var box = trace.Run!.Heap!.Regions.Values.Single(region => region.TypeKey == BOX);

        Assert.Equal(new Allocation(AllocationKind.Unknown, DriverRole.None, null), allocations.Of(box.Identity));
        Assert.Equal(new Allocation(AllocationKind.Unknown, DriverRole.None, null), allocations.Of("no-such-region"));
    }

    [Fact]
    public void A_region_whose_allocation_instance_also_ran_in_setup_was_not_created_by_the_call()
    {
        var (trace, allocations) = Run("""
            public static class Factory { public static Box Make() => new Box(); }
            public sealed class Carrier { public Carrier(Box value) { } }
            public static class Api { public static void Run(Carrier carrier, Action done) { _ = Factory.Make(); done(); } }
            """, "M:Lib.Api.Run(Lib.Carrier,System.Action)");
        var regions = trace.Run!.Heap!.Regions.Values.Where(region => region.TypeKey == BOX).ToArray();

        Assert.Contains(regions, region => !allocations.CreatedByCall(region.Identity));
    }

    [Fact]
    public void A_trigger_actions_fresh_region_is_created_by_the_call_in_the_confirmation_run()
    {
        var trace = Trace("""
            public sealed class Made { }
            public sealed class Result
            {
                private readonly Action _done;
                public Result(Action done) { _done = done; }
                public void Fire() { Cache.Last = new Made(); _done(); }
            }
            public static class Api { public static Result Make(Action done) => new Result(done); }
            """, "M:Lib.Api.Make(System.Action)");
        var confirmation = Assert.IsType<ConcurrencyHunter.Analysis.ScopeRun>(trace.Confirmation);
        Assert.False(confirmation.Stopped);
        var reachability = new HeapReachability(confirmation.Heap!);
        var result = reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        var executions = new DriverExecutions(trace.Driver!, confirmation.Executions!,
            reachability.From(result is null ? [] : reachability.Targets(result)));
        var allocations = new Allocations(trace.Driver!, confirmation.Heap!, confirmation.Executions!, executions, reachability);
        var made = confirmation.Heap!.Regions.Values.Single(region => region.TypeKey == $"{ASSEMBLY}:Lib.Made");

        Assert.True(allocations.CreatedByCall(made.Identity));
    }

    [Fact]
    public void A_generic_probe_class_created_by_a_probe_lambda_has_the_return_role()
    {
        var (trace, allocations) = Run("public abstract class Node { public object Value; } " +
                                       "public static class Api { public static T Run<T>(Func<T> make) where T : Node => make(); }",
                                       "M:Lib.Api.Run``1(System.Func{``0})");

        Assert.Equal(DriverRole.ProbeLambdaReturn, allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"))).Role);
    }

    [Fact]
    public void A_generic_probe_class_created_by_a_witness_has_the_witness_role()
    {
        var (trace, allocations) = Run("public abstract class Node { public object Value; } public interface IUser<T> { T Make(); } " +
                                       "public static class Api { public static T Run<T>(IUser<T> user) where T : Node => user.Make(); }",
                                       "M:Lib.Api.Run``1(Lib.IUser{``0})");

        Assert.Equal(DriverRole.Witness, allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"))).Role);
    }

    [Fact]
    public void A_delegate_seed_has_the_seed_role_and_its_return_has_the_witness_role()
    {
        var (trace, allocations) = Run("public sealed class Options { public Func<object> Make; } " +
                                       "public static class Api { public static object Run(Options options) => options.Make(); }",
                                       "M:Lib.Api.Run(Lib.Options)");
        var seeds = trace.Run!.Heap!.Regions.Values.Where(region => region.Kind == HeapRegionKind.Delegate &&
            trace.Driver!.SeedFactories.Any(factory => region.SiteBodyId == $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{factory}")).ToArray();

        Assert.NotEmpty(seeds);
        Assert.All(seeds, seed => Assert.Equal(DriverRole.Seed, allocations.Of(seed.Identity).Role));
        Assert.Equal(DriverRole.Witness, allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"))).Role);
    }

    [Theory]
    [InlineData("List<object>")]
    [InlineData("Dictionary<string, object>")]
    public void A_collection_a_witness_returned_through_a_container_helper_has_the_witness_role(string type)
    {
        var (trace, allocations) = Run($"public interface IUser {{ {type} Make(); }} " +
                                       $"public static class Api {{ public static {type} Run(IUser user) => user.Make(); }}",
                                       "M:Lib.Api.Run(Lib.IUser)");
        var result = Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"));

        Assert.StartsWith($"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.SeedContainer_", trace.Run!.Heap!.Regions[result].SiteBodyId);
        Assert.Equal(DriverRole.Witness, allocations.Of(result).Role);
    }

    [Fact]
    public void A_container_helper_setup_called_keeps_the_argument_value_role()
    {
        var (trace, allocations) = Run("public sealed class Options { public List<List<object>> Items; } " +
                                       "public static class Api { public static object Run(Options options) => options.Items[0][0]; }",
                                       "M:Lib.Api.Run(Lib.Options)");
        var helpers = trace.Run!.Heap!.Regions.Values.Where(region => region.SiteBodyId?.StartsWith(
            $"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.SeedContainer_", StringComparison.Ordinal) == true &&
            region.TypeKey?.StartsWith($"{DriverSynthesizer.ASSEMBLY}:", StringComparison.Ordinal) == false).ToArray();

        Assert.NotEmpty(helpers);
        Assert.All(helpers, region => Assert.Equal(DriverRole.ArgumentValue, allocations.Of(region.Identity).Role));
    }

    private static (GenerationTrace Trace, Allocations Allocations) Run(string types, string memberId)
    {
        var trace = Trace(types, memberId);
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        var run = trace.Run;
        var reachability = new HeapReachability(run.Heap!);
        var executions = new DriverExecutions(trace.Driver!, run.Executions!, new HashSet<string>(StringComparer.Ordinal));
        return (trace, new Allocations(trace.Driver!, run.Heap!, run.Executions!, executions, reachability));
    }
}
