using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Unresolved calls the first phase 5b review found left out (R1-R4): what they are handed, what they see, and what their
/// unknown executions count for.</summary>
public sealed class UnresolvedCallRegressionTests
{
    // ---- what runs in an unknown execution (R3) ----

    [Fact]
    public void Root_body_handed_to_another_opaque_call_runs_in_an_unknown_execution()
    {
        var run = Run(controller: "public void Get() => _state.Count = 1; public void Post() => Opaque.Lib.Run(Get);");

        Assert.Contains(run.Of("Count"), access => KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee == "Opaque.Lib.Run(Action)");
    }

    [Fact]
    public void Delegate_passed_on_through_a_parameter_runs_its_whole_body()
    {
        var run = Run("Hand(() => _state.Mark());", members: "private static void Hand(Action work) => CancellationToken.None.Register(work);");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Delegate_read_from_a_singleton_field_runs_its_whole_body()
    {
        var run = Run("_state.Callback = () => _state.Mark(); CancellationToken.None.Register(_state.Callback);");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Delegate_in_a_params_array_runs_in_an_unknown_execution()
    {
        var run = Run("Opaque.Lib.Take((Action)(() => _state.Mark()), 1);");

        Assert.NotEmpty(run.Execution.Heap.Heap.DelegateHandoffs);
        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Object_captured_through_two_unknown_calls_stays_confined()
    {
        var run = Run(controller: "public int Get() { var local = new Item(); Opaque.Lib.Run(() => Opaque.Lib.Run(() => local.Value = 2)); return local.Value; }");

        Assert.Contains(run.Of("Value"), access => KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
        Assert.All(run.Of("Value"), access => Assert.Equal(OwnershipKind.ThreadConfined, access.Ownership));
        Assert.Empty(run.PairsOn("Value"));
    }

    // ---- what an unknown effect sees (R1) ----

    [Fact]
    public void Field_the_program_names_on_a_library_object_gets_the_unknown_effect()
    {
        var run = Run("System.Console.WriteLine(_state.Box);", other: "_ = _state.Box.Value;");

        Assert.Contains(run.Of("Value"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.NotEmpty(run.PairsOn("Value"));
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee == "System.Console.WriteLine(object)");
    }

    [Fact]
    public void Member_a_base_type_declares_sees_only_the_base_state_of_its_receiver()
    {
        var run = Run("_state.Touch();");

        Assert.Contains(run.Of("BaseValue"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.DoesNotContain(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Dispatch_to_a_receiver_whose_implementation_has_no_body_is_unresolved()
    {
        var run = Run("ISink sink = new BadSink(); sink.Put(_state);");

        var gap = Assert.Single(run.Collection.Coverage.Gaps, gap => gap.Callee == "ISink.Put(State)");
        Assert.Equal(SemanticGapKinds.UNRESOLVED_DISPATCH, gap.Kind);
        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    // ---- materiality (R4) ----

    [Fact]
    public void Gap_inside_an_unknown_call_counts_the_roots_that_handed_the_delegate()
    {
        var run = Run(controller: "public void Post() => CancellationToken.None.Register(() => System.Console.WriteLine(_state));");

        var gap = Assert.Single(run.Collection.Coverage.Gaps, gap => gap.Callee == "System.Console.WriteLine(object)");
        Assert.Equal(1, gap.Roots);
    }

    [Fact]
    public void Regions_of_an_unknown_call_include_what_its_own_unknown_effects_reach()
    {
        var run = Run("CancellationToken.None.Register(static () => System.Console.WriteLine(Global.Held));");

        var gap = Assert.Single(run.Collection.Coverage.Gaps, gap => gap.Callee == "System.Threading.CancellationToken.Register(Action)");
        Assert.Equal(3, gap.Regions);
    }

    // ---- second review ----

    [Fact]
    public void Receiver_object_of_a_dispatch_to_an_implementation_without_a_body_gets_the_unknown_effect()
    {
        var run = Run("((ITouch)_state.Bad).Touch();", other: "_ = _state.Bad.Value;");

        Assert.Contains(run.Of("Value"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.NotEmpty(run.PairsOn("Value"));
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee == "ITouch.Touch()" && gap.Kind == SemanticGapKinds.UNRESOLVED_DISPATCH);
    }

    [Fact]
    public void Object_seen_as_a_receiver_of_a_base_member_and_handed_as_an_argument_is_seen_whole()
    {
        var run = Run("_state.Pub.Poke(_state.Pub);", other: "_ = _state.Pub.Mark;");

        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee == "PokeBase.Poke(object)");
        Assert.Contains(run.Of("Mark"), access => access.Operation == AccessOperation.UnknownEffect);
    }

    [Fact]
    public void Constructor_handing_this_to_a_base_member_as_an_argument_publishes_the_shared_object()
    {
        var run = Run("", other: "_ = _state.Pub.Mark;");

        Assert.All(run.Collection.Accesses.Where(access => access.Symbol == "Pub..ctor()" && access.Resource.Member.Name == "Mark" &&
                                                           access.Operation == AccessOperation.Write),
                   access => Assert.False(access.IsConstructionLocal));
    }

    [Fact]
    public void Foreach_over_an_enumerable_of_the_run_whose_GetEnumerator_has_no_body_is_unresolved()
    {
        var run = Run("foreach (var item in _state.Bag) { }", other: "_ = _state.Bag.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region.Contains("Bag", StringComparison.Ordinal));
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Bag.GetEnumerator(", StringComparison.Ordinal));
    }

    [Fact]
    public void Delegate_whose_target_has_no_body_is_an_unresolved_call_of_that_target()
    {
        var run = Run("Action<State> run = Native.Touch; run(_state);", other: "_ = _state.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Kind == SemanticGapKinds.UNRESOLVED_DISPATCH);
    }

    [Fact]
    public void Delegate_handed_to_a_delegate_with_no_target_runs_its_whole_body()
    {
        var run = Run("_sink!(() => _state.Mark());", members: "private readonly Action<Action>? _sink = null;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Delegate_passed_on_as_an_object_runs_its_whole_body()
    {
        var run = Run("Hand((Action)(() => _state.Mark()));", members: "private static void Hand(object work) => System.Console.WriteLine(work);");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Delegates_in_an_array_passed_on_through_a_parameter_run_their_whole_body()
    {
        var run = Run("HandAll(new Action[] { () => _state.Mark() });", members: "private static void HandAll(Action[] work) => System.Console.WriteLine(work);");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.Write && KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
    }

    [Fact]
    public void Result_written_through_a_reference_into_a_singleton_field_is_a_gap()
    {
        var run = Run("ref var text = ref _state.Text; text = System.Environment.MachineName;");

        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee == "System.Environment.get_MachineName()");
    }

    // ---- third review ----

    [Fact]
    public void Object_of_the_run_handed_whole_is_seen_through_the_library_fields_it_inherits()
    {
        var run = Run("System.Console.WriteLine(_state.Boxed);", other: "_ = _state.Boxed.Value;");

        Assert.Contains(run.Of("Value"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.NotEmpty(run.PairsOn("Value"));
    }

    [Fact]
    public void Library_member_of_an_object_of_the_run_sees_the_library_state_and_not_its_own()
    {
        var run = Run("_state.Derived.Touch();", other: "_ = _state.Derived.Level; _ = _state.Derived.Own;");

        Assert.Contains(run.Of("Level"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.DoesNotContain(run.Of("Own"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee == "Opaque.Cell.Touch()");
    }

    [Fact]
    public void Library_object_is_seen_through_the_fields_its_library_base_declares()
    {
        var run = Run("System.Console.WriteLine(_state.Deep);", other: "_ = _state.Deep.Level;");

        Assert.Contains(run.Of("Level"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.NotEmpty(run.PairsOn("Level"));
    }

    [Fact]
    public void Library_field_the_program_names_only_through_a_reference_gets_the_unknown_effect()
    {
        var run = Run("System.Console.WriteLine(_state.Counter);", other: "ref var value = ref _state.Counter.Value; value++;");

        Assert.Contains(run.Of("Value"), access => access.Operation == AccessOperation.UnknownEffect);
        Assert.NotEmpty(run.PairsOn("Value"));
    }

    // ---- helpers ----

    private static ExecutionKind KindOf(EngineRun run, Access access) => run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    private static EngineRun Run(string work = "", string other = "", string controller = "public int Get() => 0;", string members = "") =>
        AnalyzeScope(FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] },
                                            ("Case.cs", Source(work, other, controller, members))),
                     "scope:Fixture");

    private static string Source(string work, string other, string controller, string members) => Usings + $$"""
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;

        public sealed class Item { public int Value; }
        public sealed class Child { public int Depth; }
        public sealed class Held { public int Level; public Child Child = new(); }
        public static class Global { public static readonly Held Held = new(); }

        public class Base
        {
            public int BaseValue;
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Touch();
        }

        public sealed class State : Base
        {
            public int Count;
            public string Text = "";
            public Action? Callback;
            public readonly StrongBox<int> Box = new();
            public readonly Bad Bad = new();
            public readonly Bag Bag = new();
            public readonly Pub Pub = new();
            public readonly Boxed Boxed = new();
            public readonly Derived Derived = new();
            public readonly Opaque.DeepCell Deep = new();
            public readonly StrongBox<long> Counter = new();
            public void Mark() => Count = 1;
        }

        public sealed class Boxed : StrongBox<int> { public int Own; }
        public sealed class Derived : Opaque.Cell { public int Own; }

        public interface ITouch { void Touch(); }
        public sealed class GoodTouch : ITouch { public int Touches; public void Touch() => Touches++; }
        public sealed class Bad : ITouch
        {
            public int Value;
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Touch();
        }

        public sealed class Bag
        {
            public int Count;
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern IEnumerator<int> GetEnumerator();
        }

        public class PokeBase
        {
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Poke(object value);
        }

        public sealed class Pub : PokeBase
        {
            public int Mark;
            public Pub() { Poke(this); Mark = 1; }
        }

        public static class Native
        {
            [MethodImpl(MethodImplOptions.InternalCall)]
            public static extern void Touch(State state);
        }

        public interface ISink { void Put(State state); }
        public sealed class GoodSink : ISink { public int Puts; public void Put(State state) => Puts++; }
        public sealed class BadSink : ISink
        {
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Put(State state);
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }

            {{members}}
        }

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }

        public sealed class PageController(State state) : ControllerBase
        {
            private readonly State _state = state;

            {{controller}}
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>(); " +
                      "GC.KeepAlive(new GoodSink()); GC.KeepAlive(new GoodTouch());");

    /// <summary>A library the run has no source of: every member is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Run(System.Action work) { }
                public static void Take(params object[] values) { }
            }

            public class Cell
            {
                public int Level;
                public void Touch() { }
            }

            public class DeepCell : Cell { }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
