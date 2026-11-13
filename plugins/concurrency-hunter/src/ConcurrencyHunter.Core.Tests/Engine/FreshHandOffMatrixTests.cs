using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class FreshHandOffMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private enum Call
    {
        SyncStatic, SyncInstance, SyncVirtual, DirectDelegate, AwaitedAsync, AwaitedConfigureAwait,
        AwaitedForwarder, ForwarderCreator, ForwarderChain, ForwarderDropped, UnawaitedAsync, LaterAwait,
        BlockingWait, TaskRun, ModelDelegate, IteratorCall
    }

    private enum Channel { Argument, Receiver }
    private enum Effect
    {
        Write, Read, ReadModifyWrite, ReadModifyWriteMixed, UnknownEffect, UnknownOnReceiver, UnknownWithHolder,
        FieldOfField, HeldArrayWrite, HeldArrayReadModifyWrite, HeldListAdd, HeldMapCompound
    }
    private enum Origin
    {
        One, TwoFresh, FreshAndLoaded, Merged, RootInjected, Recursive, RecursiveNew,
        MixedAwaited, MixedForwarder, MixedModel
    }
    private enum Depth { One, Two }

    private sealed record Cell(Call Call, Channel Channel, Effect Access, Origin Origin = Origin.One, Depth Depth = Depth.One)
    {
        public string Name => $"C_{Call}_{Channel}_{Access}_{Origin}_{Depth}";
    }

    private EngineRun Normal => cache.Get("normal", () => Run(Cells()));
    private EngineRun Merged => cache.Get("merged", () => Run(Solve(Normal.Execution.Heap.Program, new AnalysisLimits(MaxContextsPerMethod: 0))));
    private EngineRun RunOf(Cell cell) => cell.Origin == Origin.Merged ? Merged : Normal;

    private static Cell[] Cells()
    {
        var cells = new List<Cell>();
        foreach (var call in Enum.GetValues<Call>())
            foreach (var channel in Enum.GetValues<Channel>())
                if (channel != Channel.Receiver || call is not (Call.SyncStatic or Call.ModelDelegate))
                    foreach (var access in new[] { Effect.Write, Effect.Read, Effect.ReadModifyWrite, Effect.UnknownEffect, Effect.FieldOfField })
                        cells.Add(new(call, channel, access));
        foreach (var call in new[] { Call.SyncInstance, Call.AwaitedAsync, Call.ForwarderChain })
            foreach (var channel in Enum.GetValues<Channel>())
            {
                foreach (var access in new[] { Effect.ReadModifyWriteMixed, Effect.UnknownOnReceiver, Effect.UnknownWithHolder,
                                               Effect.HeldArrayWrite, Effect.HeldArrayReadModifyWrite, Effect.HeldListAdd, Effect.HeldMapCompound })
                    cells.Add(new(call, channel, access));
                foreach (var origin in new[] { Origin.FreshAndLoaded, Origin.Merged, Origin.RootInjected, Origin.Recursive })
                    cells.Add(new(call, channel, Effect.Write, origin));
                if (channel == Channel.Argument)
                    foreach (var origin in new[] { Origin.TwoFresh, Origin.RecursiveNew })
                        cells.Add(new(call, channel, Effect.Write, origin));
            }
        foreach (var call in new[] { Call.SyncStatic, Call.SyncInstance, Call.SyncVirtual, Call.DirectDelegate, Call.AwaitedAsync,
                                    Call.AwaitedConfigureAwait, Call.AwaitedForwarder, Call.ForwarderChain, Call.UnawaitedAsync, Call.TaskRun })
            cells.Add(new(call, Channel.Argument, Effect.Write, Depth: Depth.Two));
        cells.Add(new(Call.AwaitedAsync, Channel.Argument, Effect.Write, Origin.MixedAwaited));
        cells.Add(new(Call.AwaitedForwarder, Channel.Argument, Effect.Write, Origin.MixedForwarder));
        cells.Add(new(Call.SyncInstance, Channel.Argument, Effect.Write, Origin.MixedModel));
        return cells.ToArray();
    }

    private static bool Expected(Cell cell)
    {
        if (cell.Origin is not (Origin.One or Origin.TwoFresh or Origin.RecursiveNew))
            return false; // R7: a loaded, injected, merged, circular or mixed incoming binding is not fresh.
        if (cell.Access is not (Effect.Write or Effect.Read or Effect.ReadModifyWrite or Effect.UnknownEffect or Effect.UnknownOnReceiver))
            return false; // R7: a folded nonfresh load, another start or another object's storage is not fresh.
        if (cell is { Call: Call.DirectDelegate, Channel: Channel.Receiver })
            return false; // R7: a delegate's bound receiver comes through the delegate, not the call's fresh receiver.
        return cell.Call switch
        {
            Call.SyncStatic or Call.SyncInstance or Call.SyncVirtual or Call.DirectDelegate or Call.AwaitedAsync or
            Call.AwaitedConfigureAwait or Call.AwaitedForwarder or Call.ForwarderCreator or Call.ForwarderChain => true, // R6: kinds 1–3.
            _ => false // R7: dropped tasks, later/blocking waits, spawns, models and enumeration do not hand freshness down.
        };
    }

    [Fact]
    public void There_are_235_cells_covering_every_value_of_every_axis()
    {
        var cells = Cells();
        Assert.Equal(235, cells.Length);
        Assert.Equal(235, cells.Select(cell => cell.Name).Distinct().Count());
        Assert.Equal(Enum.GetValues<Call>(), cells.Select(cell => cell.Call).Distinct().Order());
        Assert.Equal(Enum.GetValues<Channel>(), cells.Select(cell => cell.Channel).Distinct().Order());
        Assert.Equal(Enum.GetValues<Effect>(), cells.Select(cell => cell.Access).Distinct().Order());
        Assert.Equal(Enum.GetValues<Origin>(), cells.Select(cell => cell.Origin).Distinct().Order());
        Assert.Equal(Enum.GetValues<Depth>(), cells.Select(cell => cell.Depth).Distinct().Order());
    }

    [Fact]
    public void Every_cell_is_exact_and_the_wider_list_is_empty()
    {
        var failures = new List<string>();
        foreach (var cell in Cells())
        {
            try { Check(cell, RunOf(cell)); }
            catch (Exception error) { failures.Add($"{cell.Name}: {error.Message}"); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void No_cell_is_fresh_where_its_expectation_pairs()
    {
        var failures = new List<string>();
        foreach (var cell in Cells().Where(cell => !Expected(cell)))
        {
            try
            {
                var run = RunOf(cell);
                Preconditions(cell, run);
                var targets = Targets(cell, run);
                Assert.NotEmpty(targets);
                Assert.All(targets, access => Assert.False(access.IsFresh, Describe(access)));
            }
            catch (Exception error) { failures.Add($"{cell.Name}: {error.Message}"); }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Edges_a_model_a_held_delegate_a_consumption_or_a_comparer_adds_are_not_direct()
    {
        CheckEdges(Analyze("""
            using System.Collections.Generic;
            public sealed class Hook { public Hook() { } public void Mark() { } }
            public abstract class Base { public abstract void Mark(Hook h); }
            public sealed class Marker : Base { public Marker() { } public override void Mark(Hook h) { } }
            public class HooksController : ControllerBase
            {
                private static void Direct(Hook h) { }
                public void Post()
                {
                    var h = new Hook(); Direct(h); Base marker = new Marker(); marker.Mark(h);
                    Action<Hook> mark = x => x.Mark(); mark(h);
                }
            }
            """ + Startup()), direct: true, "Hook.Mark", "Marker.Mark", "HooksController.Direct", "Hook..ctor", "Marker..ctor");
        CheckEdges(Analyze("""
            using System.Collections.Generic;
            public sealed class Hook { public void Mark() { } }
            public class HooksController : ControllerBase
            {
                private static void Mark(Hook h) { h.Mark(); }
                public void Post() { new List<Hook> { new Hook() }.ForEach(Mark); }
            }
            """ + Startup()), direct: false, "HooksController.Mark");
        CheckEdges(AwaitedRunsFixture.Run("var holder = Runs.Lib.Keep(() => WorkAsync()); holder.Trigger();", http: true),
                   direct: false, "Worker.()");
        CheckEdges(Analyze("""
            using System.Linq;
            public class HooksController : ControllerBase
            {
                private static int Mark(int n) => n;
                public void Post() { _ = new[] { 1 }.Select(Mark).ToArray(); }
            }
            """ + Startup()), direct: false, "HooksController.Mark");
        var modeled = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", Usings + """
            public sealed class Hook { public override int GetHashCode() => 1; }
            public class HooksController : ControllerBase
            {
                public void Post() { Opaque.Lib.Touch(new Hook()); }
            }
            """ + Startup()));
        CheckEdges(AnalyzeScope(modeled, "scope:Fixture", ModelCellFixture.Resolve(modeled, """
            {"schemaVersion":1,"assemblies":["Opaque"],"models":[
              {"member":"M:Opaque.Lib.Touch(System.Object)","effects":{},"calls":[{"member":"GetHashCode","on":"arg:value"}]}
            ]}
            """)), direct: false, "Hook.GetHashCode");
        var comparer = TypeInitializerTreatmentFixture.Run("""
            using System;
            using System.Runtime.CompilerServices;
            public static class Selection
            {
                public static object Make<T>() => System.Collections.Generic.ComparerHelpers.CreateDefaultEqualityComparer(typeof(T));
            }
            namespace System.Collections.Generic
            {
                public static class ComparerHelpers
                {
                    [MethodImpl(MethodImplOptions.InternalCall)] public static extern object CreateDefaultEqualityComparer(Type type);
                }
                public abstract class EqualityComparer<T> { public abstract bool Equals(T x, T y); }
                public sealed class ObjectEqualityComparer<T> : EqualityComparer<T>
                {
                    public ObjectEqualityComparer() { }
                    public override bool Equals(T x, T y) => true;
                }
            }
            """, "_ = Selection.Make<Driver.D>();", TypeInitializerTreatment.Run, comparer: true);
        Assert.False(comparer.Stopped);
        var comparerHeap = comparer.Heap!;
        var constructor = Assert.Single(comparerHeap.Instances.Values, instance => instance.BodyId.Contains("ObjectEqualityComparer`1.#ctor", StringComparison.Ordinal));
        var synthesized = comparerHeap.Edges.Where(edge => edge.CalleeInstance == constructor.Id).ToArray();
        Assert.NotEmpty(synthesized);
        Assert.All(synthesized, edge => Assert.DoesNotContain((edge.CallerInstance, edge.OperationId, edge.CalleeInstance), comparerHeap.TransferEdges));
    }

    [Fact]
    public void Freshness_and_pairs_do_not_depend_on_walk_order_or_the_candidate_index()
    {
        foreach (var run in new[] { Normal, Merged })
        {
            var scope = Scope(run);
            var heap = run.Execution.Heap.Heap;
            var expected = FreshSlots(new HandedFreshness(scope, heap, run.Execution.Analysis), heap);
            Assert.Equal(expected, FreshSlots(new HandedFreshness(scope, heap, run.Execution.Analysis, reverseOrder: true), heap));
            Assert.Equal(expected, FreshSlots(new HandedFreshness(scope, heap, run.Execution.Analysis, orderSeed: 17), heap));
            var pairs = PairKeys(run.Pairs);
            Assert.Equal(pairs, PairKeys(ReferencePair(run.Collection.Accesses, run.Execution.Analysis, heap)));
            var accesses = AccessKeys(run.Collection);
            foreach (var order in new ExecutionWalkOrder[] { new(Reverse: true), new(Seed: 17), new(Seed: 93), new(Seed: 106) })
            {
                var analysis = ExecutionModel.Build(scope, heap, CancellationToken.None, order);
                var collection = InterproceduralAccesses.Collect(new InterproceduralInput(scope, heap, analysis), CancellationToken.None);
                Assert.Equal(accesses, AccessKeys(collection));
                var indexed = InterproceduralPairing.Pair(collection.Accesses, analysis, heap);
                Assert.Equal(pairs, PairKeys(indexed));
                Assert.Equal(pairs, PairKeys(ReferencePair(collection.Accesses, analysis, heap)));
            }
        }
    }

    private static string[] FreshSlots(HandedFreshness owner, HeapSolution heap) => heap.Instances.Values.SelectMany(instance =>
        instance.Parameters.Keys.Select(ordinal => new HandedSlot(ordinal)).Prepend(new HandedSlot(null))
            .Where(slot => owner.IsFresh(instance.Id, slot)).Select(slot => $"{instance.Id}|{slot.ParameterOrdinal}"))
        .Order(StringComparer.Ordinal).ToArray();

    private static ScopeProgram Scope(EngineRun run)
    {
        var heap = run.Execution.Heap;
        var program = heap.Program;
        return new ScopeProgram(program.ScopeId, program.Input.Roots, program.Result, heap.Summaries, program.Input.Program,
                                program.Input.DiIndex, program.Input.InjectionBindings) { MetadataSupertypes = program.MetadataSupertypes };
    }

    private static string Describe(Access access) =>
        $"{access.InstanceId}|{access.OperationId}|{access.ExecutionId}|{access.Resource.Identity}|{access.Operation}";

    private static string[] AccessKeys(InterproceduralCollection collection) => collection.Accesses
        .Select(access => $"{Describe(access)}|fresh={access.IsFresh}").Order(StringComparer.Ordinal).ToArray();

    private static string PairKey(AccessPair pair) =>
        string.Join(" <> ", new[] { Describe(pair.First), Describe(pair.Second) }.Order(StringComparer.Ordinal)) + "|" + pair.Resource.Identity;

    private static string[] PairKeys(PairAnalysis pairs) => pairs.Pairs.Select(PairKey).Order(StringComparer.Ordinal).ToArray();

    private static void CheckEdges(EngineRun run, bool direct, params string[] symbols)
    {
        var heap = run.Execution.Heap.Heap;
        foreach (var symbol in symbols)
        {
            var edges = heap.Edges.Where(edge => run.Execution.Heap.Program.Result.Bodies[heap.Instances[edge.CalleeInstance].BodyId].MethodSymbol.Contains(symbol, StringComparison.Ordinal)).ToArray();
            Assert.True(edges.Length != 0, $"No edge to {symbol}: " + string.Join(", ", heap.Edges.Select(edge => run.Execution.Heap.Program.Result.Bodies[heap.Instances[edge.CalleeInstance].BodyId].MethodSymbol)));
            Assert.All(edges, edge => Assert.Equal(direct, heap.TransferEdges.Contains((edge.CallerInstance, edge.OperationId, edge.CalleeInstance))));
        }
        if (direct)
            Assert.Contains(heap.Edges, edge => heap.Instances[edge.CallerInstance].Summary.Calls.Any(call =>
                call.OperationId == edge.OperationId && call.Kind == IrCallKind.Delegate) &&
                heap.TransferEdges.Contains((edge.CallerInstance, edge.OperationId, edge.CalleeInstance)));
    }

    private static bool InCell(Cell cell, Access access) => access.BodyId.StartsWith($"body:Fixture:M:{cell.Name}.", StringComparison.Ordinal);

    private static Access[] Targets(Cell cell, EngineRun run) => run.Collection.Accesses.Where(access => InCell(cell, access) &&
        !access.IsConstructionLocal && (cell.Access switch
        {
            Effect.HeldArrayWrite or Effect.HeldArrayReadModifyWrite => access.Resource.Selector is not null && access.Resource.Member.Name == "Cells",
            Effect.HeldListAdd => access.Resource.CollectionId is not null && access.Symbol.Contains(".Mark", StringComparison.Ordinal),
            Effect.HeldMapCompound => access.Resource.CollectionId is not null && access.Symbol.Contains(".Mark", StringComparison.Ordinal),
            _ => access.Resource.Member.Name == "Hits" && !run.Execution.Heap.Heap.RootInstances.Values.Contains(access.InstanceId) &&
                 !access.Symbol.Contains(".CreateAndForward(", StringComparison.Ordinal)
        })).ToArray();

    private static void Check(Cell cell, EngineRun run)
    {
        Preconditions(cell, run);
        var targets = Targets(cell, run);
        Assert.NotEmpty(targets);
        Assert.All(targets, access => Assert.True(Expected(cell) == access.IsFresh, $"fresh={access.IsFresh}: {Describe(access)}"));
        Assert.All(targets, access => Assert.True(access.Ownership == (cell.Origin == Origin.RootInjected ? OwnershipKind.Shared :
                                                                      cell.Origin == Origin.Merged ? OwnershipKind.Unknown : OwnershipKind.Escaped),
                                                $"ownership={access.Ownership}: {Describe(access)}"));
        var relevant = run.Pairs.Pairs.Where(pair => targets.Contains(pair.First) || targets.Contains(pair.Second)).ToArray();
        if (Expected(cell))
        {
            Assert.Empty(relevant);
            return;
        }
        Assert.NotEmpty(relevant);
        var expectedPairs = new List<string>();
        var writes = cell.Access == Effect.Read
            ? run.Collection.Accesses.Where(access => InCell(cell, access) && access.Resource.Member.Name == "Hits" &&
                access.Operation == AccessOperation.Write && !targets.Contains(access)).ToArray() : [];
        if (cell.Access == Effect.Read)
        {
            Assert.NotEmpty(writes);
            foreach (var read in targets)
                foreach (var write in writes.Where(write => write.Resource.Identity == read.Resource.Identity))
                    expectedPairs.Add(PairKey(new AccessPair(read, write, PairProtection.UNPROTECTED)));
        }
        else
        {
            for (var first = 0; first < targets.Length; first++)
                for (var second = first; second < targets.Length; second++)
                    if (targets[first].Resource.Identity == targets[second].Resource.Identity &&
                        (targets[first].Operation.Conflicts() || targets[second].Operation.Conflicts()))
                        expectedPairs.Add(PairKey(new AccessPair(targets[first], targets[second], PairProtection.UNPROTECTED)));
        }
        Assert.Equal(expectedPairs.Order(StringComparer.Ordinal), relevant.Select(PairKey).Order(StringComparer.Ordinal));
    }

    private static void Preconditions(Cell cell, EngineRun run)
    {
        var heap = run.Execution.Heap.Heap;
        var prefix = $"{cell.Name}.";
        string Symbol(MethodInstance instance) => run.Execution.Heap.Program.Result.Bodies[instance.BodyId].MethodSymbol;
        var helpers = heap.Instances.Values.Where(instance => Symbol(instance).StartsWith(prefix, StringComparison.Ordinal) &&
            (Symbol(instance).Contains(".Mark(", StringComparison.Ordinal) || Symbol(instance).Contains(".MarkAsync(", StringComparison.Ordinal) ||
             Symbol(instance).Contains(".Iterate(", StringComparison.Ordinal) ||
             cell.Call == Call.DirectDelegate && instance.BodyId.Contains("#lambda", StringComparison.Ordinal))).ToArray();
        Assert.NotEmpty(helpers);
        IReadOnlySet<string> Slot(MethodInstance instance) => cell.Channel == Channel.Argument ? instance.Parameters[0] : instance.Receivers;
        CallEdge[] Incoming(MethodInstance instance) => heap.Edges.Where(edge => edge.CalleeInstance == instance.Id)
            .DistinctBy(edge => (edge.CallerInstance, edge.OperationId, edge.CalleeInstance)).ToArray();
        CheckCall(cell, run, helpers);
        if (cell.Access is Effect.UnknownEffect or Effect.UnknownOnReceiver)
        {
            var unknown = UnknownCalls.Of(Scope(run), heap);
            var targets = Targets(cell, run);
            foreach (var helper in helpers)
            {
                var hook = Assert.Single(Slot(helper));
                var terminal = Assert.Single(unknown, call => call.Instance.Id == helper.Id &&
                    targets.Any(access => access.InstanceId == helper.Id && access.OperationId == call.OperationId));
                var receivers = terminal.Receivers.SelectMany(value => heap.Resolve(helper.Id, value)).ToHashSet(StringComparer.Ordinal);
                var arguments = terminal.Arguments.SelectMany(argument => argument.Values)
                    .SelectMany(value => heap.Resolve(helper.Id, value)).ToHashSet(StringComparer.Ordinal);
                if (cell.Access == Effect.UnknownOnReceiver)
                {
                    Assert.Contains(hook, receivers);
                    Assert.DoesNotContain(hook, arguments);
                    var opaque = Assert.Single(helper.Summary.OpaqueCalls, call => call.OperationId == terminal.OperationId);
                    var slot = new HandedSlot(cell.Channel == Channel.Argument ? 0 : null);
                    Assert.False(opaque.ReceiverIsFresh);
                    Assert.Equal(slot, opaque.ReceiverHandedSlot);
                    Assert.False(terminal.ReceiverIsFresh);
                    Assert.Equal(slot, terminal.ReceiverHandedSlot);
                }
                else
                {
                    Assert.Contains(hook, arguments);
                    Assert.DoesNotContain(hook, receivers);
                }
            }
        }
        if (cell.Depth == Depth.Two)
        {
            var middle = Assert.Single(heap.Instances.Values, instance => run.Execution.Heap.Program.Result.Bodies[instance.BodyId].MethodSymbol.StartsWith(prefix, StringComparison.Ordinal) &&
                run.Execution.Heap.Program.Result.Bodies[instance.BodyId].MethodSymbol.Contains(".Middle(", StringComparison.Ordinal));
            var first = Assert.Single(Incoming(middle));
            Assert.True(run.Execution.Heap.Program.Result.Bodies[middle.BodyId].IsAsync);
            Assert.True(heap.Instances[first.CallerInstance].Summary.Calls.Single(call => call.OperationId == first.OperationId).IsAwaitedImmediately);
            Assert.True(heap.TransferEdges.Contains((first.CallerInstance, first.OperationId, first.CalleeInstance)));
            if (cell.Call == Call.TaskRun)
                Assert.Contains(heap.Spawns, spawn => spawn.CallerInstance == middle.Id);
            else if (cell.Call == Call.DirectDelegate)
                Assert.Contains(heap.Edges, edge => edge.CallerInstance == middle.Id &&
                    middle.Summary.Calls.Any(call => call.OperationId == edge.OperationId && call.Kind == IrCallKind.Delegate));
            else
                Assert.Contains(heap.Edges, edge => edge.CallerInstance == middle.Id &&
                    (helpers.Any(helper => helper.Id == edge.CalleeInstance) ||
                     run.Execution.Heap.Program.Result.Bodies[heap.Instances[edge.CalleeInstance].BodyId].MethodSymbol.Contains(".Forward(", StringComparison.Ordinal)));
        }
        switch (cell.Origin)
        {
            case Origin.One:
                break;
            case Origin.TwoFresh:
            {
                var first = cell.Call == Call.ForwarderChain
                    ? Assert.Single(heap.Instances.Values, instance => run.Execution.Heap.Program.Result.Bodies[instance.BodyId].MethodSymbol.StartsWith(prefix, StringComparison.Ordinal) &&
                        run.Execution.Heap.Program.Result.Bodies[instance.BodyId].MethodSymbol.Contains(".Forward(", StringComparison.Ordinal)) : Assert.Single(helpers);
                Assert.False(first.IsMerged);
                var incoming = Incoming(first);
                Assert.Equal(2, incoming.Length);
                Assert.Equal(2, incoming.Select(edge => edge.CallerInstance).Distinct().Count());
                Assert.Equal(2, Slot(first).Count);
                Assert.All(incoming, edge => Assert.True(heap.Instances[edge.CallerInstance].Summary.Calls
                    .Single(call => call.OperationId == edge.OperationId).Arguments.Single(arg => arg.ParameterOrdinal == 0).IsFresh));
                break;
            }
            case Origin.FreshAndLoaded:
                Assert.Contains(heap.Instances.Values.Where(instance => Symbol(instance).StartsWith(prefix, StringComparison.Ordinal)), helper => Incoming(helper).Any(edge =>
                {
                    if (!Symbol(heap.Instances[edge.CallerInstance]).Contains(".Other(", StringComparison.Ordinal))
                        return false;
                    var call = heap.Instances[edge.CallerInstance].Summary.Calls.Single(call => call.OperationId == edge.OperationId);
                    return cell.Channel == Channel.Argument ? call.Arguments.Any(arg => arg.ParameterOrdinal == 0 && !arg.IsFresh && arg.HandedSlot is null)
                        : !call.ReceiverIsFresh && call.ReceiverHandedSlot is null;
                }));
                Assert.Contains(Targets(cell, run), access => access.PathRoot.Symbol.Contains("Other", StringComparison.Ordinal));
                break;
            case Origin.Merged:
                Assert.All(helpers, helper => Assert.True(helper.IsMerged));
                break;
            case Origin.RootInjected:
            {
                var root = Assert.Single(heap.RootInstances.Values.Select(id => heap.Instances[id]), instance => Symbol(instance).StartsWith(prefix, StringComparison.Ordinal));
                var injected = root.Parameters[0];
                Assert.Single(injected);
                Assert.All(injected, region => Assert.Equal(HeapRegionKind.Di, heap.Regions[region].Kind));
                Assert.All(helpers, helper => Assert.True(Slot(helper).SetEquals(injected)));
                break;
            }
            case Origin.Recursive:
            case Origin.RecursiveNew:
                Assert.Contains(helpers, helper => Incoming(helper).Any(edge => edge.CallerInstance == helper.Id));
                foreach (var helper in helpers)
                    foreach (var edge in Incoming(helper).Where(edge => edge.CallerInstance == helper.Id))
                    {
                        var call = helper.Summary.Calls.Single(call => call.OperationId == edge.OperationId);
                        if (cell.Channel == Channel.Argument)
                        {
                            var argument = call.Arguments.Single(arg => arg.ParameterOrdinal == 0);
                            Assert.Equal(cell.Origin == Origin.RecursiveNew, argument.IsFresh);
                            Assert.Equal(cell.Origin == Origin.Recursive ? new HandedSlot(0) : null, argument.HandedSlot);
                            var bound = argument.Values.SelectMany(value => heap.Resolve(helper.Id, value)).ToHashSet(StringComparer.Ordinal);
                            if (cell.Origin == Origin.Recursive)
                                Assert.True(bound.SetEquals(Slot(helper)));
                            else
                            {
                                Assert.Single(bound);
                                Assert.All(bound, region => Assert.Equal(helper.BodyId, heap.Regions[region].SiteBodyId));
                            }
                        }
                        else
                            Assert.Equal(new HandedSlot(null), call.ReceiverHandedSlot);
                    }
                break;
            case Origin.MixedAwaited:
            case Origin.MixedModel:
            case Origin.MixedForwarder:
            {
                var helper = Assert.Single(helpers);
                Assert.False(helper.IsMerged);
                var first = cell.Origin == Origin.MixedForwarder
                    ? Assert.Single(heap.Instances.Values, instance => run.Execution.Heap.Program.Result.Bodies[instance.BodyId].MethodSymbol.StartsWith(prefix, StringComparison.Ordinal) &&
                        run.Execution.Heap.Program.Result.Bodies[instance.BodyId].MethodSymbol.Contains(".Forward(", StringComparison.Ordinal)) : helper;
                Assert.False(first.IsMerged);
                var incoming = Incoming(first);
                Assert.Equal(2, incoming.Length);
                Assert.Equal(2, first.Parameters[0].Count);
                foreach (var edge in incoming)
                    Assert.Single(first.Parameters[0], region => heap.Regions[region].Kind == HeapRegionKind.Allocation &&
                        heap.Regions[region].SiteBodyId == heap.Instances[edge.CallerInstance].BodyId);
                if (cell.Origin == Origin.MixedModel)
                {
                    Assert.Single(incoming, edge => heap.TransferEdges.Contains((edge.CallerInstance, edge.OperationId, edge.CalleeInstance)));
                    Assert.All(first.Parameters[0], region => Assert.Equal(HeapRegionKind.Allocation, heap.Regions[region].Kind));
                }
                else
                {
                    var calls = incoming.Select(edge => heap.Instances[edge.CallerInstance].Summary.Calls.Single(call => call.OperationId == edge.OperationId)).ToArray();
                    Assert.All(calls, call => Assert.True(call.Arguments.Single(arg => arg.ParameterOrdinal == 0).IsFresh));
                    Assert.Single(calls, call => call.IsAwaitedImmediately);
                }
                if (cell.Origin == Origin.MixedForwarder)
                {
                    var edge = Assert.Single(Incoming(helper));
                    Assert.Equal(first.Id, edge.CallerInstance);
                    Assert.Equal(new HandedSlot(0), first.Summary.Calls.Single(call => call.OperationId == edge.OperationId)
                        .Arguments.Single(arg => arg.ParameterOrdinal == 0).HandedSlot);
                }
                break;
            }
        }
    }

    private static void CheckCall(Cell cell, EngineRun run, MethodInstance[] helpers)
    {
        var heap = run.Execution.Heap.Heap;
        var bodies = run.Execution.Heap.Program.Result.Bodies;
        var caller = Assert.Single(heap.Instances.Values, instance => bodies[instance.BodyId].MethodSymbol ==
            $"{cell.Name}.HooksController.{(cell.Depth == Depth.Two ? "Middle(Hook)" : cell.Origin == Origin.RootInjected ? "Post(Hook)" : "Post()")}");
        var outgoing = heap.Edges.Where(edge => edge.CallerInstance == caller.Id).ToArray();
        if (cell.Call == Call.TaskRun)
        {
            Assert.Contains(heap.Spawns, spawn => spawn.CallerInstance == caller.Id);
            return;
        }
        if (cell.Call == Call.ModelDelegate)
        {
            Assert.Contains(outgoing, edge => helpers.Any(helper => helper.Id == edge.CalleeInstance) &&
                !heap.TransferEdges.Contains((edge.CallerInstance, edge.OperationId, edge.CalleeInstance)));
            return;
        }
        if (cell.Call == Call.IteratorCall)
        {
            Assert.All(helpers, helper => Assert.True(bodies[helper.BodyId].IsIterator));
            Assert.Contains(heap.IteratorObjects, iterator => helpers.Any(helper => helper.Id == iterator.Creation.CalleeInstance));
            return;
        }
        var target = cell.Call is Call.AwaitedForwarder or Call.ForwarderChain or Call.ForwarderDropped ? ".Forward("
            : cell.Call == Call.ForwarderCreator ? ".CreateAndForward(" : null;
        var edge = Assert.Single(outgoing, edge => target is null ? helpers.Any(helper => helper.Id == edge.CalleeInstance)
            : bodies[heap.Instances[edge.CalleeInstance].BodyId].MethodSymbol.Contains(target, StringComparison.Ordinal));
        Assert.Contains((edge.CallerInstance, edge.OperationId, edge.CalleeInstance), heap.TransferEdges);
        var call = caller.Summary.Calls.Single(call => call.OperationId == edge.OperationId);
        Assert.Equal(cell.Call is Call.AwaitedAsync or Call.AwaitedConfigureAwait or Call.AwaitedForwarder or Call.ForwarderCreator or Call.ForwarderChain,
                     call.IsAwaitedImmediately);
        if (cell.Call is Call.SyncStatic or Call.SyncVirtual or Call.DirectDelegate)
            Assert.Equal(cell.Call == Call.SyncStatic ? IrCallKind.Static : cell.Call == Call.SyncVirtual ? IrCallKind.Virtual : IrCallKind.Delegate, call.Kind);
        if (cell.Call is Call.AwaitedForwarder or Call.ForwarderCreator or Call.ForwarderChain or Call.ForwarderDropped)
        {
            var forwarders = heap.Instances.Values.Where(instance => instance.BodyId.StartsWith($"body:Fixture:M:{cell.Name}.", StringComparison.Ordinal) &&
                (bodies[instance.BodyId].MethodSymbol.Contains(".Forward(", StringComparison.Ordinal) ||
                 bodies[instance.BodyId].MethodSymbol.Contains(".Tail(", StringComparison.Ordinal) ||
                 bodies[instance.BodyId].MethodSymbol.Contains(".CreateAndForward(", StringComparison.Ordinal))).ToArray();
            Assert.NotEmpty(forwarders);
            Assert.All(forwarders, forwarder =>
            {
                Assert.NotEmpty(forwarder.Summary.Returns);
                var returned = Assert.Single(forwarder.Summary.Returns.SelectMany(value => value.SourceCalls).Distinct());
                Assert.All(forwarder.Summary.Returns, value => Assert.Equal(new[] { returned }, value.SourceCalls));
                Assert.Contains(heap.Edges, step => step.CallerInstance == forwarder.Id && step.OperationId == returned);
                Assert.All(run.Execution.Analysis.InstanceExecutions[forwarder.Id], execution =>
                    Assert.NotNull(run.Execution.Analysis.ForwardedTask(execution, forwarder.Id)));
            });
        }
    }

    private static EngineRun Run(IEnumerable<Cell> cells)
    {
        var selected = cells.ToArray();
        var source = Usings + "using System.Collections.Generic;\n" + string.Join("\n", selected.Select(Source)) + Startup(string.Join("\n", selected.Select(cell =>
            $"services.AddSingleton<{cell.Name}.Repository>(); services.AddSingleton<{cell.Name}.Marker>(); services.AddSingleton<{cell.Name}.Hook>();")));
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] }, ("Case.cs", source));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return Run(Solve(ReachScope(solution, "scope:Fixture")));
    }

    private static EngineRun Run(HeapRun heap)
    {
        var execution = Execute(heap);
        var collection = Collect(execution);
        return new EngineRun(execution, collection, InterproceduralPairing.Pair(collection.Accesses, execution.Analysis, execution.Heap.Heap));
    }

    private static string Source(Cell cell)
    {
        var receiver = cell.Channel == Channel.Receiver;
        var h = receiver ? "this" : "h";
        var repository = receiver ? "repository" : "_repository";
        var access = cell.Access switch
        {
            Effect.Write => $"{h}.Hits = 1;",
            Effect.Read => $"_ = {h}.Hits;",
            Effect.ReadModifyWrite => $"{h}.Hits++;",
            Effect.ReadModifyWriteMixed => $"{h}.Hits = {repository}.Latest.Hits + 1;",
            Effect.UnknownEffect => $"Opaque.Lib.Touch({h});",
            Effect.UnknownOnReceiver => $"{h}.Touch();",
            Effect.UnknownWithHolder => $"Opaque.Lib.Touch({h}, {repository});",
            Effect.FieldOfField => $"{h}.Next!.Hits = 1;",
            Effect.HeldArrayWrite => $"{h}.Cells[0] = 1;",
            Effect.HeldArrayReadModifyWrite => $"{h}.Cells[0]++;",
            Effect.HeldListAdd => $"{h}.Items.Add(1);",
            Effect.HeldMapCompound => $"if (!{h}.Map.ContainsKey(1)) {h}.Map[1] = 1;",
            _ => throw new ArgumentOutOfRangeException(nameof(cell))
        };
        var needsRepository = receiver && (cell.Access is Effect.ReadModifyWriteMixed or Effect.UnknownWithHolder || cell.Origin == Origin.Recursive);
        var parameters = receiver ? needsRepository ? "Repository repository" : "" : "Hook h";
        var argument = receiver ? needsRepository ? "repository" : "" : "h";
        var next = cell.Origin == Origin.RecursiveNew ? "fresh" : argument;
        var recursiveCreate = cell.Origin == Origin.RecursiveNew ? "var fresh = new Hook(); _repository.Items.Add(fresh); " : "";
        var recurse = cell.Origin is Origin.Recursive or Origin.RecursiveNew
            ? cell.Call == Call.SyncInstance ? $"if ({repository}.Flag) {{ {recursiveCreate} Mark({next}); }}" : $"if ({repository}.Flag) {{ {recursiveCreate} await MarkAsync({next}); }}" : "";
        var helpers = $$"""
            public {{(cell.Call == Call.SyncVirtual ? "override " : "")}}void Mark({{parameters}}) { {{access}} {{(cell.Call == Call.SyncInstance ? recurse : "")}} }
            public async Task MarkAsync({{parameters}}) { await Task.Yield(); {{access}} {{(cell.Call != Call.SyncInstance ? recurse : "")}} }
            public Task Forward({{parameters}}) => {{(cell.Call == Call.ForwarderChain ? "Tail" : "MarkAsync")}}({{argument}});
            public Task Tail({{parameters}}) => MarkAsync({{argument}});
            public IEnumerable<int> Iterate({{parameters}}) { {{access}} yield return 1; }
            """;
        var fields = cell.Access switch
        {
            Effect.FieldOfField => "public Hook? Next;",
            Effect.HeldArrayWrite or Effect.HeldArrayReadModifyWrite => "public int[] Cells = null!;",
            Effect.HeldListAdd => "public List<int> Items = null!;",
            Effect.HeldMapCompound => "public Dictionary<int, int> Map = null!;",
            _ => ""
        };
        var initial = cell.Access switch
        {
            Effect.FieldOfField => "new Hook { Next = new Hook() }",
            Effect.HeldArrayWrite or Effect.HeldArrayReadModifyWrite => "new Hook { Cells = _repository.SharedCells }",
            Effect.HeldListAdd => "new Hook { Items = _repository.SharedItems }",
            Effect.HeldMapCompound => "new Hook { Map = _repository.SharedMap }",
            _ => "new Hook()"
        };
        var create = $"var hook = {initial}; _repository.Items.Add(hook); " +
            (cell.Access == Effect.Read ? "hook.Hits = 0; " : "") +
            (cell.Origin == Origin.FreshAndLoaded || cell.Access == Effect.ReadModifyWriteMixed ? "_repository.Latest = hook; " : "");
        var helperTarget = receiver ? "hook" : cell.Call is Call.SyncStatic or Call.TaskRun ? "StaticMarker" : "_marker";
        var callArgument = receiver ? needsRepository ? "_repository" : "" : "hook";
        var mark = $"{helperTarget}.Mark({callArgument})";
        var markAsync = $"{helperTarget}.MarkAsync({callArgument})";
        var forward = $"{helperTarget}.Forward({callArgument})";
        var invocation = cell.Call switch
        {
            Call.SyncStatic or Call.SyncInstance or Call.SyncVirtual => mark + ";",
            Call.DirectDelegate => receiver ? "Action mark = hook.Mark; mark();"
                : $"Action<Hook> mark = h => {{ {access} }}; mark(hook);",
            Call.AwaitedAsync => $"await {markAsync};",
            Call.AwaitedConfigureAwait => $"await {markAsync}.ConfigureAwait(false);",
            Call.AwaitedForwarder or Call.ForwarderChain => $"await {forward};",
            Call.ForwarderCreator => "await CreateAndForward();",
            Call.ForwarderDropped => $"_ = {forward};",
            Call.UnawaitedAsync => $"_ = {markAsync};",
            Call.LaterAwait => $"var t = {markAsync}; await Task.Yield(); await t;",
            Call.BlockingWait => $"{markAsync}.Wait();",
            Call.TaskRun => $"await Task.Run(() => {mark});",
            Call.ModelDelegate => "new List<Hook> { hook }.ForEach(_marker.Mark);",
            Call.IteratorCall => $"foreach (var item in {helperTarget}.Iterate({callArgument})) {{ }}",
            _ => throw new ArgumentOutOfRangeException(nameof(cell))
        };
        string Action(string name, bool other)
        {
            var setup = other && cell.Origin == Origin.FreshAndLoaded ? "var hook = _repository.Latest; " :
                cell.Origin == Origin.RootInjected ? "var fresh = new Hook(); _repository.Items.Add(fresh); var hook = shared; " : create;
            var invoke = other && cell.Origin == Origin.MixedAwaited ? $"_ = {markAsync};" :
                other && cell.Origin == Origin.MixedForwarder ? $"_ = {forward};" :
                other && cell.Origin == Origin.MixedModel ? "new List<Hook> { hook }.ForEach(_marker.Mark);" : invocation;
            if (cell.Call == Call.ForwarderCreator)
                setup = "";
            if (cell.Depth == Depth.Two)
                invoke = "await Middle(hook);";
            return $"public async Task {name}({(cell.Origin == Origin.RootInjected ? "[FromServices] Hook shared" : "")}) {{ {setup} {invoke} }}";
        }
        var otherAction = cell.Origin is Origin.TwoFresh or Origin.FreshAndLoaded or Origin.MixedAwaited or Origin.MixedForwarder or Origin.MixedModel
            ? Action("Other", true) : "";
        return $$"""
            namespace {{cell.Name}}
            {
                {{(receiver && cell.Call == Call.SyncVirtual ? $"public abstract class HookBase {{ public abstract void Mark({parameters}); }}" : "")}}
                public sealed class Hook {{(receiver && cell.Call == Call.SyncVirtual ? ": HookBase" : "")}}
                {
                    public int Hits;
                    {{(cell.Access == Effect.UnknownOnReceiver ? "public extern void Touch(); " : "")}}{{fields}}
                    {{(receiver ? helpers : "")}}
                }
                public sealed class Repository
                {
                    public readonly List<Hook> Items = new();
                    public Hook Latest = new Hook();
                    public bool Flag;
                    public int[] SharedCells = new int[1];
                    public List<int> SharedItems = new();
                    public Dictionary<int, int> SharedMap = new();
                }
                public abstract class MarkerBase { public abstract void Mark(Hook h); }
                public sealed class Marker(Repository repository) {{(!receiver && cell.Call == Call.SyncVirtual ? ": MarkerBase" : "")}}
                {
                    private readonly Repository _repository = repository;
                    {{(!receiver ? helpers : "")}}
                }
                {{(!receiver && cell.Call is Call.SyncStatic or Call.TaskRun ? $"public static class StaticMarker {{ public static void Mark(Hook h) {{ {access} }} }}" : "")}}
                public class HooksController(Repository repository, Marker marker) : ControllerBase
                {
                    private readonly Repository _repository = repository;
                    private readonly {{(!receiver && cell.Call == Call.SyncVirtual ? "MarkerBase" : "Marker")}} _marker = marker;
                    {{Action("Post", false)}}
                    {{otherAction}}
                    {{(cell.Depth == Depth.Two ? $"private async Task Middle(Hook hook) {{ {invocation} }}" : "")}}
                    {{(cell.Call == Call.ForwarderCreator ? $"private Task CreateAndForward() {{ {create} return {markAsync}; }}" : "")}}
                }
            }
            """;
    }

    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib { public static void Touch(object value) { } public static void Touch(object value, object holder) { } }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
