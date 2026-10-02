using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The driver's executions (SPEC TD-034b, G-0): own, tree, setup, the escape artefact with its tree, elsewhere.</summary>
public sealed class DriverExecutionsTests
{
    private const string SPAWNING = "public static class Api { public static Holder Make(Action a) { Task.Run(() => Cache.Last = null); return new Holder(a); } }";

    private const string LAZY_SPAWNING = "public static class Api { public static IEnumerable<int> Lazy(IEnumerable<int> s, Func<int, bool> p) " +
                                         "{ foreach (var x in s) { Task.Run(() => p(x)); yield return x; } } }";

    [Fact]
    public void Own_is_the_actions_root_execution()
    {
        var (trace, executions) = Run(SPAWNING, "M:Lib.Api.Make(System.Action)");
        var own = DriverExecutions.Own(DriverSynthesizer.CALL);

        Assert.Contains(trace.Run!.Executions!.Executions, execution => execution.Id == own && execution.Kind == ExecutionKind.Root);
        Assert.Equal(new DriverExecution(DriverExecutionRole.Own, DriverSynthesizer.CALL), executions.Of(own));
        Assert.True(DriverExecutions.IsOwn(own, DriverSynthesizer.CALL));
        Assert.False(DriverExecutions.IsOwn(own, DriverSynthesizer.SETUP));
    }

    [Fact]
    public void A_spawn_the_action_starts_is_in_its_tree_but_not_its_own()
    {
        var (trace, executions) = Run(SPAWNING, "M:Lib.Api.Make(System.Action)");
        var child = trace.Run!.Executions!.Executions.Single(execution => execution.Kind == ExecutionKind.Spawn &&
                                                                           execution.ParentId == DriverExecutions.Own(DriverSynthesizer.CALL));

        Assert.Equal(new DriverExecution(DriverExecutionRole.Child, DriverSynthesizer.CALL), executions.Of(child.Id));
        Assert.True(executions.InTree(child.Id, DriverSynthesizer.CALL));
        Assert.False(DriverExecutions.IsOwn(child.Id, DriverSynthesizer.CALL));
    }

    [Fact]
    public void Setup_is_the_tree_of_V_Setup()
    {
        var (_, executions) = Run(SPAWNING, "M:Lib.Api.Make(System.Action)");
        var setup = DriverExecutions.Own(DriverSynthesizer.SETUP);

        Assert.Equal(new DriverExecution(DriverExecutionRole.Setup, DriverSynthesizer.SETUP), executions.Of(setup));
        Assert.True(executions.InSetup(setup));
        Assert.False(executions.InSetup(DriverExecutions.Own(DriverSynthesizer.CALL)));
    }

    [Fact]
    public void The_unknown_enumeration_of_the_stored_result_is_the_escape_artefact()
    {
        var (trace, executions) = Run(LAZY_SPAWNING, "M:Lib.Api.Lazy(System.Collections.Generic.IEnumerable{System.Int32},System.Func{System.Int32,System.Boolean})");
        var artefact = trace.Run!.Executions!.Executions.Single(execution => execution.Kind == ExecutionKind.UnknownEnumeration);

        Assert.Contains(artefact.Subject!, Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"));
        Assert.Equal(new DriverExecution(DriverExecutionRole.Artefact, null), executions.Of(artefact.Id));
        Assert.True(executions.IsArtefact(artefact.Id));
    }

    [Fact]
    public void A_child_of_the_escape_artefact_is_the_artefact_too()
    {
        var (trace, executions) = Run(LAZY_SPAWNING, "M:Lib.Api.Lazy(System.Collections.Generic.IEnumerable{System.Int32},System.Func{System.Int32,System.Boolean})");
        var artefact = trace.Run!.Executions!.Executions.Single(execution => execution.Kind == ExecutionKind.UnknownEnumeration);
        var child = trace.Run.Executions.Executions.Single(execution => execution.Kind == ExecutionKind.Spawn && execution.TreeRootId == artefact.Id);

        Assert.Equal(new DriverExecution(DriverExecutionRole.Artefact, null), executions.Of(child.Id));
        Assert.True(executions.IsArtefact(child.Id));
    }

    [Fact]
    public void An_unknown_execution_is_elsewhere()
    {
        var (trace, executions) = Run("public static class Api { public static void Run(Action a) { Sink.Take(a); } }", "M:Lib.Api.Run(System.Action)");
        var unknown = trace.Run!.Executions!.Executions.Single(execution => execution.Kind == ExecutionKind.UnknownDelegateCall);

        Assert.Equal(new DriverExecution(DriverExecutionRole.Elsewhere, null), executions.Of(unknown.Id));
        Assert.False(executions.InSetup(unknown.Id));
        Assert.False(executions.IsArtefact(unknown.Id));
    }

    [Fact]
    public void An_unknown_enumeration_of_an_object_the_result_does_not_reach_is_elsewhere()
    {
        var (trace, executions) = Run("""
            public static class Api
            {
                public static int Leak(IEnumerable<int> s, Func<int, bool> p) { Sink.Take(Iter(s, p)); return 0; }
                private static IEnumerable<int> Iter(IEnumerable<int> s, Func<int, bool> p) { foreach (var x in s) if (p(x)) yield return x; }
            }
            """, "M:Lib.Api.Leak(System.Collections.Generic.IEnumerable{System.Int32},System.Func{System.Int32,System.Boolean})");
        var enumeration = trace.Run!.Executions!.Executions.Single(execution => execution.Kind == ExecutionKind.UnknownEnumeration);

        Assert.Equal(new DriverExecution(DriverExecutionRole.Elsewhere, null), executions.Of(enumeration.Id));
    }

    private static (GenerationTrace Trace, DriverExecutions Executions) Run(string types, string memberId)
    {
        var trace = Trace(types, memberId);
        Assert.True(trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        var reachability = new HeapReachability(trace.Run.Heap!);
        var result = reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        var fromResult = reachability.From(result is null ? [] : reachability.Targets(result));
        return (trace, new DriverExecutions(trace.Driver!, trace.Run.Executions!, fromResult));
    }
}
