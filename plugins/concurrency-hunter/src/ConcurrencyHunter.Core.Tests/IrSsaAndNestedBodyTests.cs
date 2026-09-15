using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class IrSsaAndNestedBodyTests
{
    private const string Helpers = """
        static void Work() { }
        static void Use(object? value) { }
        static bool Flag() => true;
        """;

    [Fact]
    public async Task If_else_join_has_a_phi_over_both_branches()
    {
        var body = (await Lower($$"""
            class C
            {
                void M(bool condition)
                {
                    int value;
                    if (condition) value = 1; else value = 2;
                    Use(value);
                }
                {{Helpers}}
            }
            """)).Body;

        var (block, phi) = Assert.Single(Phis(body), pair => Value(body, pair.Phi.TargetValue).Name == "value");
        Assert.Equal(2, phi.Inputs.Count);
        Assert.All(phi.Inputs, input => Assert.Equal(IrEdgeKind.Explicit, input.Predecessor.EdgeKind));
        Assert.Equal([1, 2], phi.Inputs.Select(input => Value(body, input.Value).SsaVersion).Order());
        Assert.Equal(block.FlowPredecessors, phi.Inputs.Select(input => input.Predecessor));
    }

    [Fact]
    public async Task Loop_header_has_a_phi_over_entry_and_back_edge()
    {
        var body = (await Lower($$"""
            class C
            {
                void M()
                {
                    var index = 0;
                    while (index < 10) index = index + 1;
                    Use(index);
                }
                {{Helpers}}
            }
            """)).Body;

        var (block, phi) = Assert.Single(Phis(body), pair => Value(body, pair.Phi.TargetValue).Name == "index");
        Assert.Equal(2, phi.Inputs.Count);
        Assert.Contains(phi.Inputs, input => input.Predecessor.BlockOrdinal < block.Ordinal);
        Assert.Contains(phi.Inputs, input => input.Predecessor.BlockOrdinal > block.Ordinal);
        Assert.Equal(2, phi.Inputs.Select(input => input.Value).Distinct().Count());
    }

    [Fact]
    public async Task Local_assigned_in_finally_reaches_the_code_after_it()
    {
        var body = (await Lower($$"""
            class C
            {
                static readonly object GateA = new();
                static readonly object GateB = new();
                void M()
                {
                    var gate = GateA;
                    try { Work(); } finally { gate = GateB; }
                    lock (gate) { Work(); }
                }
                {{Helpers}}
            }
            """)).Body;

        var acquire = Assert.Single(Operations<IrAcquireOperation>(body));
        var load = Assert.IsType<IrLoadFieldOperation>(Definition(body, Resolve(body, acquire.LockValue)));
        Assert.Equal("GateB", load.Field.Name);
        var finallyRegion = body.Regions.Where(region => region.Kind == IrRegionKind.Finally)
                                .MinBy(region => region.FirstBlockOrdinal)!;
        var finallyEntry = body.Blocks[finallyRegion.FirstBlockOrdinal];
        Assert.Contains(finallyEntry.FlowPredecessors, predecessor => predecessor.EdgeKind == IrEdgeKind.FinallyEntry);
        Assert.Contains(finallyEntry.FlowPredecessors, predecessor => predecessor.EdgeKind == IrEdgeKind.Exceptional);
        Assert.Contains(body.Blocks.SelectMany(block => block.FlowPredecessors), predecessor =>
            predecessor.BlockOrdinal == finallyRegion.LastBlockOrdinal && predecessor.EdgeKind == IrEdgeKind.FinallyExit);
    }

    [Fact]
    public async Task Catch_entry_phi_inputs_follow_flow_predecessors()
    {
        var body = (await Lower($$"""
            class C
            {
                static readonly object GateA = new();
                static readonly object GateB = new();
                void M()
                {
                    var gate = GateA;
                    try
                    {
                        if (Flag()) gate = GateB;
                        Work();
                    }
                    catch
                    {
                        Use(gate);
                    }
                }
                {{Helpers}}
            }
            """)).Body;

        var catchRegion = Assert.Single(body.Regions, region => region.Kind == IrRegionKind.Catch);
        var catchEntry = body.Blocks[catchRegion.FirstBlockOrdinal];
        var phi = Assert.Single(catchEntry.Operations.OfType<IrPhiOperation>(),
            operation => Value(body, operation.TargetValue).Name == "gate");
        Assert.True(catchEntry.FlowPredecessors.Count >= 3);
        Assert.All(catchEntry.FlowPredecessors, predecessor => Assert.Equal(IrEdgeKind.Exceptional, predecessor.EdgeKind));
        Assert.Equal(catchEntry.FlowPredecessors, phi.Inputs.Select(input => input.Predecessor));
        Assert.Contains(phi.Inputs, input => Value(body, input.Value) is { Kind: IrValueKind.Constant, Name: "exceptional" });
        Assert.Contains(phi.Inputs, input => Value(body, input.Value) is { Kind: IrValueKind.Local, SsaVersion: 1 });
    }

    [Fact]
    public async Task Handler_sees_exceptional_when_the_protected_block_defines_the_variable()
    {
        var lowered = await Lower($$"""
            class C
            {
                static readonly object SharedGate = new();
                void M(object other)
                {
                    var gate = other;
                    try { Work(); gate = SharedGate; }
                    catch { lock (gate) { Work(); } }
                }
                {{Helpers}}
            }
            """);
        var body = lowered.Body;

        var acquire = Assert.Single(Operations<IrAcquireOperation>(body));
        var lockValue = Resolve(body, acquire.LockValue);
        var reaching = Definition(body, lockValue) is IrPhiOperation phi
            ? phi.Inputs.Select(input => Resolve(body, input.Value)).ToArray()
            : [lockValue];
        Assert.Contains(reaching, value => Value(body, value) is { Kind: IrValueKind.Constant, Name: "exceptional" });
        Assert.DoesNotContain(reaching, value => Definition(body, value) is IrLoadFieldOperation);
        var catchRegion = Assert.Single(body.Regions, region => region.Kind == IrRegionKind.Catch);
        var lockFinally = Assert.Single(body.Regions, region => region.Kind == IrRegionKind.Finally);
        Assert.All(body.Blocks[lockFinally.FirstBlockOrdinal].FlowPredecessors, predecessor =>
            Assert.InRange(predecessor.BlockOrdinal, catchRegion.FirstBlockOrdinal, catchRegion.LastBlockOrdinal));
        Assert.Empty(IrValidator.Validate(body));
        Assert.Contains("name=\"exceptional\"", IrPrinter.Print(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_named_local_functions_in_sibling_blocks_have_distinct_ids_that_survive_a_line_shift()
    {
        const string Source = """
            class C
            {
                void M(bool condition)
                {
                    if (condition) { F(); void F() { } }
                    else { F(); void F() { } }
                }
            }
            """;
        var lowered = await Lower(Source);
        var shifted = await Lower("\n\n\n" + Source.Replace("{ F();", "{\n F();\n", StringComparison.Ordinal));

        var ids = lowered.NestedBodies.Select(nested => nested.BodyId).ToArray();
        Assert.Equal([lowered.Body.BodyId + "#local:F~1", lowered.Body.BodyId + "#local:F~2"], ids);
        Assert.Equal(ids, shifted.NestedBodies.Select(nested => nested.BodyId));
        Assert.Equal(ids, Operations<IrCallOperation>(lowered.Body).Select(call => call.Method));
        Assert.All(Operations<IrCallOperation>(lowered.Body), call => Assert.Equal(IrCallKind.LocalFunction, call.CallKind));
    }

    [Fact]
    public async Task Definition_used_in_its_own_block_has_no_phi()
    {
        var body = (await Lower($$"""
            class C
            {
                void M(bool condition)
                {
                    var value = 1;
                    if (condition) Work();
                    Use(value);
                }
                {{Helpers}}
            }
            """)).Body;

        Assert.DoesNotContain(Phis(body), pair => Value(body, pair.Phi.TargetValue).Name == "value");
        Assert.Single(body.Values, value => value is { Name: "value", Kind: IrValueKind.Local });
    }

    [Fact]
    public async Task Phi_input_without_a_reaching_definition_is_the_default_constant()
    {
        var body = (await Lower($$"""
            class C
            {
                void M(bool condition)
                {
                    int value;
                    if (condition) { value = 1; Use(value); }
                    Work();
                }
                {{Helpers}}
            }
            """)).Body;

        var defaultValue = Assert.Single(body.Values, value => value is { Kind: IrValueKind.Constant, Name: "default" });
        var (_, phi) = Assert.Single(Phis(body), pair => Value(body, pair.Phi.TargetValue).Name == "value");
        Assert.Contains(phi.Inputs, input => input.Value == defaultValue.Id);
    }

    [Fact]
    public async Task Reassigned_parameter_gets_a_new_version_and_the_receiver_is_not_versioned()
    {
        var body = (await Lower($$"""
            class C
            {
                int Field;
                void M(int count)
                {
                    Use(count);
                    count = 2;
                    Field = count;
                }
                {{Helpers}}
            }
            """)).Body;

        var versions = body.Values.Where(value => value is { Name: "count", Kind: IrValueKind.Parameter }).ToArray();
        Assert.Equal([0, 1], versions.Select(value => value.SsaVersion));
        var store = Assert.Single(Operations<IrStoreFieldOperation>(body));
        Assert.Equal(versions[1].Id, store.Value);
        var receiver = Assert.Single(body.Values, value => value.Kind == IrValueKind.Receiver);
        Assert.Equal(0, receiver.SsaVersion);
        Assert.Equal(receiver.Id, store.ReceiverValue);
    }

    [Fact]
    public async Task Coalesce_assignment_on_a_field_is_one_read_modify_write()
    {
        var body = (await Lower("""
            class C
            {
                object? Value;
                static object? Other;
                void M() { Value ??= (Other = new object()); }
            }
            """)).Body;

        var load = Assert.Single(Operations<IrLoadFieldOperation>(body));
        var stores = Operations<IrStoreFieldOperation>(body);
        var store = Assert.Single(stores, operation => operation.Field.Name == "Value");
        Assert.Equal("Value", load.Field.Name);
        Assert.Equal(load.Id, store.ReadModifyWriteOf);
        Assert.Equal("coalesce-assignment", store.Provenance.Transformation);
        var other = Assert.Single(stores, operation => operation.Field.Name == "Other");
        Assert.Null(other.ReadModifyWriteOf);
    }

    [Fact]
    public async Task Lock_statement_lowers_to_acquire_and_release_in_try_finally()
    {
        var body = (await Lower($$"""
            class C
            {
                static readonly object Gate = new();
                static readonly object Inner = new();
                void M()
                {
                    lock (Gate)
                    {
                        System.Threading.Monitor.Enter(Inner);
                        System.Threading.Monitor.Exit(Inner);
                    }
                }
                {{Helpers}}
            }
            """)).Body;

        var acquires = Operations<IrAcquireOperation>(body);
        var releases = Operations<IrReleaseOperation>(body);
        Assert.Equal(["lock-statement", "monitor-call"], acquires.Select(acquire => acquire.Provenance.Transformation).Order());
        Assert.Equal(["lock-statement", "monitor-call"], releases.Select(release => release.Provenance.Transformation).Order());
        var lockRelease = Assert.Single(releases, release => release.Provenance.Transformation == "lock-statement");
        Assert.Equal(IrRegionKind.Finally, RegionOf(body, lockRelease).Kind);
        var lockAcquire = Assert.Single(acquires, acquire => acquire.Provenance.Transformation == "lock-statement");
        Assert.Equal(IrRegionKind.Try, RegionOf(body, lockAcquire).Kind);
        Assert.Equal(Resolve(body, lockAcquire.LockValue), Resolve(body, lockRelease.LockValue));
        Assert.DoesNotContain(Operations<IrCallOperation>(body), call => call.Method.StartsWith("System.Threading.Monitor.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_monitor_calls_lower_to_acquire_and_release_in_try_finally()
    {
        var body = (await Lower($$"""
            using System.Threading;
            class C
            {
                static readonly object Gate = new();
                void M()
                {
                    var taken = false;
                    try
                    {
                        Monitor.Enter(Gate, ref taken);
                        Work();
                    }
                    finally
                    {
                        if (taken) Monitor.Exit(Gate);
                    }
                }
                {{Helpers}}
            }
            """)).Body;

        var acquire = Assert.Single(Operations<IrAcquireOperation>(body));
        var release = Assert.Single(Operations<IrReleaseOperation>(body));
        Assert.Equal("monitor-call", acquire.Provenance.Transformation);
        Assert.Equal("monitor-call", release.Provenance.Transformation);
        Assert.Equal(IrSynchronizationPrimitive.Monitor, acquire.Primitive);
        Assert.Equal(IrLockMode.Exclusive, acquire.Mode);
        Assert.Equal(IrRegionKind.Try, RegionOf(body, acquire).Kind);
        Assert.Equal(IrRegionKind.Finally, RegionOf(body, release).Kind);
    }

    [Fact]
    public async Task Monitor_try_enter_stays_a_call()
    {
        var body = (await Lower("""
            using System.Threading;
            class C
            {
                static readonly object Gate = new();
                void M()
                {
                    var taken = false;
                    Monitor.TryEnter(Gate, ref taken);
                    Monitor.TryEnter(Gate);
                }
            }
            """)).Body;

        Assert.Empty(Operations<IrAcquireOperation>(body));
        Assert.Equal(2, Operations<IrCallOperation>(body).Count(call => call.Method.StartsWith("System.Threading.Monitor.TryEnter(", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task System_threading_lock_stays_calls()
    {
        var body = (await Lower($$"""
            using System.Threading;
            class C
            {
                static readonly Lock Gate = new();
                void M()
                {
                    lock (Gate) { Work(); }
                    Gate.Enter();
                    Gate.Exit();
                }
                {{Helpers}}
            }
            """)).Body;

        Assert.Empty(Operations<IrAcquireOperation>(body));
        Assert.Empty(Operations<IrReleaseOperation>(body));
        var methods = Operations<IrCallOperation>(body).Select(call => call.Method).ToArray();
        Assert.Contains("System.Threading.Lock.Enter()", methods);
        Assert.Contains("System.Threading.Lock.Exit()", methods);
    }

    [Fact]
    public async Task Lambda_body_has_its_own_receiver_parameters_and_captures()
    {
        var lowered = await Lower("""
            class C
            {
                int Field;
                void M(int count)
                {
                    var local = 1;
                    System.Action<int> action = extra => { Field = local + count + extra; };
                    action(2);
                }
            }
            """);

        var nested = Assert.Single(lowered.NestedBodies);
        Assert.Equal(lowered.Body.BodyId + "#lambda1", nested.BodyId);
        Assert.Equal(IrBodyKind.Lambda, nested.Kind);
        Assert.Equal("C.M(int)", nested.OwnerSymbol);
        var create = Assert.Single(Operations<IrCreateDelegateOperation>(lowered.Body));
        Assert.Equal(nested.BodyId, create.TargetBodyId);
        var receiver = Assert.Single(nested.Values, value => value.Kind == IrValueKind.Receiver);
        Assert.Equal(receiver.Id, Assert.Single(Operations<IrStoreFieldOperation>(nested)).ReceiverValue);
        Assert.Contains(nested.Values, value => value is { Name: "extra", Kind: IrValueKind.Parameter, SsaVersion: 0 });
        var captures = Operations<IrCaptureOperation>(nested);
        Assert.Equal(["count", "local"], captures.Select(capture => Value(nested, capture.Value).Name).Order());
        Assert.All(captures, capture => Assert.Equal(nested.BodyId, capture.TargetBodyId));
        Assert.Empty(Operations<IrCaptureOperation>(lowered.Body));
    }

    [Fact]
    public async Task Lambda_in_a_static_method_has_no_receiver()
    {
        var lowered = await Lower("""
            class C
            {
                static void M()
                {
                    System.Action action = () => { };
                    System.Func<int> local = F;
                    int F() => 1;
                }
            }
            """);

        Assert.Equal(2, lowered.NestedBodies.Count);
        Assert.All(lowered.NestedBodies, nested =>
            Assert.DoesNotContain(nested.Values, value => value.Kind == IrValueKind.Receiver));
    }

    [Fact]
    public async Task Local_function_body_has_receiver_and_captures_and_is_called_by_its_id()
    {
        var lowered = await Lower("""
            class C
            {
                int Field;
                void M(int count)
                {
                    var local = 1;
                    Store();
                    void Store() { Field = local + count; }
                }
            }
            """);

        var nested = Assert.Single(lowered.NestedBodies);
        Assert.Equal(lowered.Body.BodyId + "#local:Store", nested.BodyId);
        Assert.Equal(IrBodyKind.LocalFunction, nested.Kind);
        Assert.Equal("C.M(int)", nested.OwnerSymbol);
        var call = Assert.Single(Operations<IrCallOperation>(lowered.Body));
        Assert.Equal(nested.BodyId, call.Method);
        Assert.Single(nested.Values, value => value.Kind == IrValueKind.Receiver);
        Assert.Equal(["count", "local"], Operations<IrCaptureOperation>(nested).Select(capture => Value(nested, capture.Value).Name).Order());
    }

    [Fact]
    public async Task Lambda_inside_a_local_function_composes_ids_and_captures_the_local_function_local()
    {
        var lowered = await Lower("""
            class C
            {
                void M()
                {
                    Outer();
                    void Outer()
                    {
                        var value = 1;
                        System.Action first = () => { };
                        System.Action second = () => { Outer(); System.GC.KeepAlive(value); };
                    }
                }
            }
            """);

        var rootId = lowered.Body.BodyId;
        Assert.Equal([rootId + "#local:Outer", rootId + "#local:Outer#lambda1", rootId + "#local:Outer#lambda2"],
            lowered.NestedBodies.Select(nested => nested.BodyId));
        var second = lowered.NestedBodies[2];
        Assert.Equal("C.M()", second.OwnerSymbol);
        Assert.Equal(["value"], Operations<IrCaptureOperation>(second).Select(capture => Value(second, capture.Value).Name));
        Assert.Contains(Operations<IrCallOperation>(second), call => call.Method == rootId + "#local:Outer");
    }

    [Fact]
    public async Task Method_group_delegates_name_the_method_or_the_local_function_body()
    {
        var lowered = await Lower($$"""
            class C
            {
                void Instance() { }
                void M()
                {
                    System.Action first = Instance;
                    System.Action second = Work;
                    System.Action third = Local;
                    void Local() { }
                }
                {{Helpers}}
            }
            """);
        var body = lowered.Body;

        var creates = Operations<IrCreateDelegateOperation>(body);
        Assert.Equal(3, creates.Length);
        Assert.Equal("C.Instance()", creates[0].TargetMethod);
        Assert.Null(creates[0].TargetBodyId);
        Assert.Equal(IrValueKind.Receiver, Value(body, Assert.IsType<int>(creates[0].ReceiverValue)).Kind);
        Assert.Equal("C.Work()", creates[1].TargetMethod);
        Assert.Null(creates[1].ReceiverValue);
        Assert.Equal(body.BodyId + "#local:Local", creates[2].TargetBodyId);
        Assert.Null(creates[2].TargetMethod);
        Assert.Single(lowered.NestedBodies);
    }

    [Fact]
    public async Task Printing_the_same_snippet_twice_gives_identical_text()
    {
        const string Source = """
            class C
            {
                static readonly object Gate = new();
                object? Value;
                void M(bool condition)
                {
                    var gate = Gate;
                    try { if (condition) gate = new object(); Value ??= gate; }
                    catch { lock (gate) { Value = null; } }
                    finally { System.Threading.Monitor.Enter(Gate); System.Threading.Monitor.Exit(Gate); }
                    System.Action action = () => { Value = gate; Local(); };
                    void Local() { System.Action inner = () => System.GC.KeepAlive(gate); }
                }
            }
            """;

        var first = Print(await Lower(Source));
        var second = Print(await Lower(Source));

        Assert.Equal(first, second);
        Assert.Contains("#local:Local#lambda1", first, StringComparison.Ordinal);
    }

    private static async Task<IrLoweredMethod> Lower(string source)
    {
        var solution = FixtureSolution.Create(("Case.cs", source));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var method = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(method, compilation, @"C:\fixture", CancellationToken.None);
    }

    private static string Print(IrLoweredMethod lowered) =>
        string.Concat(new[] { lowered.Body }.Concat(lowered.NestedBodies).Select(IrPrinter.Print));

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        body.Blocks.SelectMany(block => block.Operations).OfType<T>().ToArray();

    private static IEnumerable<(IrBlock Block, IrPhiOperation Phi)> Phis(IrBody body) =>
        body.Blocks.SelectMany(block => block.Operations.OfType<IrPhiOperation>().Select(phi => (block, phi)));

    private static IrValue Value(IrBody body, int id) => body.Values.Single(value => value.Id == id);

    private static IrOperation? Definition(IrBody body, int value) =>
        body.Blocks.SelectMany(block => block.Operations).FirstOrDefault(operation => operation.DefinedValues.Contains(value));

    private static int Resolve(IrBody body, int value)
    {
        while (Definition(body, value) is IrAssignOperation assign)
            value = assign.SourceValue;
        return value;
    }

    private static IrRegion RegionOf(IrBody body, IrOperation operation)
    {
        var block = body.Blocks.Single(candidate => candidate.Operations.Contains(operation));
        return body.Regions.Single(region => region.Id == block.Region);
    }
}
