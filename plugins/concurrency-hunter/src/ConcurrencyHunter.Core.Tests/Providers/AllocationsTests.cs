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
