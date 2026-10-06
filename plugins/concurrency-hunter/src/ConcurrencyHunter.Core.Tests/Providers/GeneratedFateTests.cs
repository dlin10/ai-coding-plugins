using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The fates the model generator classifies on source fixture libraries shaped like the gold members (SPEC TD-034b, G-5), and
/// the reasons it classifies none in G-6's order.</summary>
public sealed class GeneratedFateTests
{
    private const string LAZY = """
        public static class Seq
        {
            public static IEnumerable<int> Iter(IEnumerable<int> s, Func<int, bool> p) { foreach (var x in s) if (p(x)) yield return x; }
        }
        """;

    private const string SEQUENCE_PREDICATE = "(System.Collections.Generic.IEnumerable{System.Int32},System.Func{System.Int32,System.Boolean})";

    private static readonly ClassifiedFate InvokeNow = new(FateClassifier.INVOKE_NOW, null);
    private static readonly ClassifiedFate Iterator = new(FateClassifier.ITERATOR, null);
    private static readonly ClassifiedFate HolderResult = new(FateClassifier.HOLDER, FateClassifier.RESULT);
    private static readonly ClassifiedFate HolderThis = new(FateClassifier.HOLDER, FateClassifier.THIS);
    private static readonly ClassifiedFate Unknown = new(FateClassifier.UNKNOWN_EXECUTION, null);
    private static readonly ClassifiedFate NotRun = new(FateClassifier.NOT_RUN, null);

    // ---- invoke-now ----

    [Fact]
    public void A_delegate_the_member_only_invokes_is_invoke_now()
    {
        Assert.Equal(InvokeNow, FateOf(Trace("public static class Api { public static void Run(Action a) { a(); } }", "M:Lib.Api.Run(System.Action)"), "a"));
    }

    [Fact]
    public void An_in_delegate_parameter_the_member_only_invokes_is_never_narrower_than_unknown_execution()
    {
        // The driver hands an in delegate its probe as a value (G-3), a temporary the engine cannot name: the invocation through the in
        // parameter is an unresolved dispatch with no receiver object, the probe never runs, and the open-world rule keeps
        // unknown-execution. A ref delegate, handed through its Out_ field, is followed (the test below).
        var trace = Trace("public static class Api { public static void Run(in Action a) { a(); } }", "M:Lib.Api.Run(System.Action@)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Empty(FiredIn(trace, "P_a_0_Call"));
        Assert.Contains(UnknownCalls.Of(trace.Run!.ScopeProgram!, trace.Run.Heap!),
                        call => call.Kind == SemanticGapKinds.UNRESOLVED_DISPATCH && call.Callee.StartsWith("System.Action.Invoke", StringComparison.Ordinal));
    }

    [Fact]
    public void A_ref_delegate_parameter_the_member_only_invokes_is_invoke_now_its_own_field_not_counting()
    {
        var trace = Trace("public static class Api { public static void Run(ref Action a) { a(); } }", "M:Lib.Api.Run(System.Action@)");

        Assert.Equal(InvokeNow, FateOf(trace, "a"));
        // The probe sits in its own Out_ field, a path start for every other parameter, but not a path that says the member kept it.
        Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Out_a_Call"));
    }

    [Fact]
    public void A_List_source_filled_by_setup_makes_no_write_in_the_calls_own_execution()
    {
        var trace = Trace("""
            public static class Api
            {
                public static bool All(IEnumerable<int> s, Func<int, bool> p) { foreach (var x in s) if (!p(x)) return false; return true; }
            }
            """, "M:Lib.Api.All" + SEQUENCE_PREDICATE);
        var list = Assert.Single(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_s_Call"));
        var reachability = new HeapReachability(trace.Run!.Heap!);
        var writes = trace.Run.Collection!.Accesses.Where(access => access.Operation is not (AccessOperation.Read or AccessOperation.AtomicRead) &&
                                                                    reachability.WrittenObjects(access).Contains(list))
                          .ToArray();

        Assert.Equal(InvokeNow, FateOf(trace, "p"));
        Assert.NotEmpty(writes);
        Assert.All(writes, write => Assert.True(trace.Run.Executions!.Executions.Single(execution => execution.Id == write.ExecutionId).TreeRootId ==
                                                DriverRootProvider.RootId(DriverSynthesizer.SETUP), write.ExecutionId));
    }

    [Fact]
    public void An_awaited_asynchronous_member_that_runs_the_delegate_before_its_first_await_is_invoke_now()
    {
        var trace = Trace("public static class Api { public static async Task RunAsync(Action a) { a(); await Task.Delay(1); } }",
                          "M:Lib.Api.RunAsync(System.Action)");

        Assert.Equal(InvokeNow, FateOf(trace, "a"));
    }

    [Fact]
    public void A_carried_probe_the_member_only_invokes_and_does_not_keep_is_invoke_now()
    {
        var trace = Trace("""
            public sealed class Parser { private readonly Func<int> _f; public Parser(Func<int> f) { _f = f; } public int Parse() => _f(); }
            public static class Api { public static int Use(Parser p) => p.Parse(); }
            """, "M:Lib.Api.Use(Lib.Parser)");

        // Setup's own construction links the probe to the parameter's Arg_ field: that path says nothing about the member.
        Assert.Equal(InvokeNow, FateOf(trace, "p"));
        Assert.Empty(trace.Answer.Generation.SetupWidened);
    }

    // ---- fired and kept, fired elsewhere ----

    [Fact]
    public void Fired_during_the_call_and_kept_only_through_the_result_object_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static Holder Make(Action a) { a(); return new Holder(a); } }", "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void Fired_during_the_call_and_in_a_Task_Run_it_starts_is_unknown_execution()
    {
        Assert.Equal(Unknown, FateOf(Trace("public static class Api { public static void Run(Action a) { a(); Task.Run(a); } }", "M:Lib.Api.Run(System.Action)"), "a"));
    }

    [Fact]
    public void Fired_only_in_a_Task_Run_the_call_starts_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static void Run(Action a) { Task.Run(a); } }", "M:Lib.Api.Run(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(FiredIn(trace, "P_a_0_Call"), execution => execution.StartsWith(DriverExecutions.Own(DriverSynthesizer.CALL) + ">spawn:", StringComparison.Ordinal));
    }

    [Fact]
    public void Fired_only_in_a_timer_callback_the_call_starts_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static void Run(Action a) { new System.Threading.Timer(_ => a(), null, 1, 1000); } }",
                          "M:Lib.Api.Run(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(FiredIn(trace, "P_a_0_Call"), execution => execution.StartsWith(DriverExecutions.Own(DriverSynthesizer.CALL) + ">timer:", StringComparison.Ordinal));
    }

    [Fact]
    public void Fired_only_in_an_unknown_execution_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static void Run(Action a) { Sink.Take(a); } }", "M:Lib.Api.Run(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(FiredIn(trace, "P_a_0_Call"), execution => execution.StartsWith("unknown-delegate-call:", StringComparison.Ordinal));
    }

    [Fact]
    public void Never_fired_and_not_kept_is_not_run()
    {
        var trace = Trace("public static class Api { public static void Run(Action a) { } }", "M:Lib.Api.Run(System.Action)");

        Assert.Equal(NotRun, FateOf(trace, "a"));
        Assert.Empty(FiredIn(trace, "P_a_0_Call"));
    }

    // ---- iterator ----

    [Fact]
    public void A_lazy_sequence_that_runs_the_delegate_when_enumerated_is_iterator()
    {
        var trace = Trace("public static class Api { public static IEnumerable<int> Where(IEnumerable<int> s, Func<int, bool> p) " +
                          "{ foreach (var x in s) if (p(x)) yield return x; } }", "M:Lib.Api.Where" + SEQUENCE_PREDICATE);

        Assert.Equal(Iterator, FateOf(trace, "p"));
        Assert.Equal([DriverExecutions.Own(DriverSynthesizer.ENUMERATE)], FiredIn(trace, "P_p_0_Enum"));
    }

    [Fact]
    public void A_sequence_whose_iterator_one_context_shares_between_V_Call_and_V_Enum_is_iterator()
    {
        // A static method's context is its call site: Seq.Iter, called from one place in Where, is one instance under both actions, so
        // V_Call's probe runs in own(V_Enum) too. Each action builds its own values and no static keeps the probe, so that firing is the
        // shared instance's, not the member's, and does not count.
        var trace = Trace(LAZY + "public static class Api { public static IEnumerable<int> Where(IEnumerable<int> s, Func<int, bool> p) => Seq.Iter(s, p); }",
                          "M:Lib.Api.Where" + SEQUENCE_PREDICATE);

        Assert.Equal(Iterator, FateOf(trace, "p"));
        Assert.Contains(DriverExecutions.Own(DriverSynthesizer.ENUMERATE), FiredIn(trace, "P_p_0_Call"));
    }

    [Fact]
    public void A_lazy_result_whose_escape_artefact_enumerates_it_is_still_iterator()
    {
        var trace = Trace("public static class Api { public static IEnumerable<int> Lazy(IEnumerable<int> s, Func<int, bool> p) " +
                          "{ foreach (var x in s) if (p(x)) yield return x; } }", "M:Lib.Api.Lazy" + SEQUENCE_PREDICATE);

        Assert.Equal(Iterator, FateOf(trace, "p"));
        // Storing the lazy result in Keep.R made it escape: the engine enumerates it in an unknown enumeration, the driver's doing.
        Assert.Contains(FiredIn(trace, "P_p_0_Call"), execution => execution.StartsWith("unknown-enumeration:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lazy_result_the_member_itself_hands_to_an_opaque_call_is_not_iterator()
    {
        var trace = Trace(LAZY + "public static class Api { public static IEnumerable<int> Leak(IEnumerable<int> s, Func<int, bool> p) " +
                          "{ var r = Seq.Iter(s, p); Sink.Take(r); return r; } }", "M:Lib.Api.Leak" + SEQUENCE_PREDICATE);

        Assert.Equal(Unknown, FateOf(trace, "p"));
        Assert.Contains(FiredIn(trace, "P_p_0_Call"), execution => execution.StartsWith("unknown-enumeration:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_delegate_fired_in_a_Task_Run_started_while_V_Enum_enumerates_is_not_iterator()
    {
        var trace = Trace("public static class Api { public static IEnumerable<int> Spawning(IEnumerable<int> s, Func<int, bool> p) " +
                          "{ foreach (var x in s) { Task.Run(() => p(x)); yield return x; } } }", "M:Lib.Api.Spawning" + SEQUENCE_PREDICATE);

        Assert.NotEqual(Iterator, FateOf(trace, "p"));
        Assert.All(FiredIn(trace, "P_p_0_Enum"), execution => Assert.NotEqual(DriverExecutions.Own(DriverSynthesizer.ENUMERATE), execution));
    }

    [Fact]
    public void A_member_returning_Task_of_a_sequence_that_runs_the_delegate_when_enumerated_is_never_narrower_than_holder_result()
    {
        // The engine carries no awaited value into V_Enum's foreach, so V_Enum never enumerates the sequence: neither iterator nor a
        // holder result can be shown, since how the sequence runs the delegate when enumerated was never seen.
        var trace = Trace(LAZY + "public static class Api { public static async Task<IEnumerable<int>> WhereAsync(IEnumerable<int> s, Func<int, bool> p) " +
                          "{ await Task.Delay(1); return Seq.Iter(s, p); } }", "M:Lib.Api.WhereAsync" + SEQUENCE_PREDICATE);

        Assert.Equal(Unknown, FateOf(trace, "p"));
        Assert.Null(FateClassifier.Refusal(trace.Driver!.Member, Iterator));
    }

    [Fact]
    public void A_member_returning_Task_of_a_sequence_whose_enumeration_starts_a_Task_Run_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static async Task<IEnumerable<int>> SpawningAsync(Action a) { await Task.Delay(1); return Spawning(a); } " +
                          "private static IEnumerable<int> Spawning(Action a) { for (var i = 0; i < 2; i++) { Task.Run(a); yield return i; } } }",
                          "M:Lib.Api.SpawningAsync(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_lazy_sequence_whose_enumeration_starts_a_Task_Run_that_runs_the_delegate_is_unknown_execution()
    {
        // V_Call's own probe fires nowhere but in the escape artefact, and the result keeps it; V_Enum's counterpart shows the
        // enumeration runs it in a spawned execution, which no fate covers.
        var trace = Trace("public static class Api { public static IEnumerable<int> Spawning(Action a) { for (var i = 0; i < 2; i++) { Task.Run(a); yield return i; } } }",
                          "M:Lib.Api.Spawning(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(FiredIn(trace, "P_a_0_Enum"), execution => execution.StartsWith(DriverExecutions.Own(DriverSynthesizer.ENUMERATE) + ">spawn:", StringComparison.Ordinal));
    }

    // ---- a probe in another action's root execution ----

    [Fact]
    public void An_eager_member_whose_helper_instance_V_Call_and_V_Enum_share_is_invoke_now()
    {
        // ToDictionary's shape: the delegate runs in a helper both actions' calls reach as one instance, so each action's probe is also
        // written in the other's root execution; each action builds its own values, and no static keeps the probe.
        var trace = Trace("""
            public static class Api
            {
                public static List<int> Run(IEnumerable<int> s, Func<int, int> f) => Helper.Fill(s, f);
            }
            internal static class Helper
            {
                public static List<int> Fill(IEnumerable<int> s, Func<int, int> f) { var list = new List<int>(); foreach (var x in s) list.Add(f(x)); return list; }
            }
            """, "M:Lib.Api.Run(System.Collections.Generic.IEnumerable{System.Int32},System.Func{System.Int32,System.Int32})");

        Assert.Equal(InvokeNow, FateOf(trace, "f"));
        Assert.Contains(DriverExecutions.Own(DriverSynthesizer.ENUMERATE), FiredIn(trace, "P_f_0_Call"));
        Assert.Contains(DriverExecutions.Own(DriverSynthesizer.CALL), FiredIn(trace, "P_f_0_Enum"));
    }

    [Fact]
    public void A_sequence_constructor_whose_helper_instance_V_Call_and_V_Enum_share_is_invoke_now()
    {
        // A constructor of an enumerable type: V_Enum constructs and enumerates its own object, and the constructor runs the delegate
        // through one helper instance both actions' constructions reach, so each action's probe is also written in the other's root.
        var trace = Trace("""
            public sealed class Runs : IEnumerable<int>
            {
                public Runs(Action a) { Engine.Run(a); }
                public IEnumerator<int> GetEnumerator() { yield break; }
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            internal static class Engine { public static void Run(Action a) => Core(a); private static void Core(Action a) => a(); }
            """, "M:Lib.Runs.#ctor(System.Action)");

        Assert.Equal(InvokeNow, FateOf(trace, "a"));
        Assert.Contains(DriverExecutions.Own(DriverSynthesizer.ENUMERATE), FiredIn(trace, "P_a_0_Call"));
    }

    [Fact]
    public void A_delegate_parked_in_a_library_static_that_the_next_call_runs_is_unknown_execution()
    {
        // Where the rule stops: the next call runs the delegate the last call parked, so V_Call's probe is written in V_Enum's root
        // execution for real. That root does not count, but the static keeps the probe, and kept rules out invoke-now.
        var trace = Trace("public static class Api { private static Action _last; " +
                          "public static List<int> Run(Action a) { _last?.Invoke(); _last = a; a(); return new List<int>(); } }",
                          "M:Lib.Api.Run(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(DriverExecutions.Own(DriverSynthesizer.ENUMERATE), FiredIn(trace, "P_a_0_Call"));
    }

    [Fact]
    public void A_shared_helper_that_runs_the_delegate_in_a_Task_Run_stays_unknown_execution()
    {
        // Where the rule stops: only another action's root execution is left out; a spawned child of any action still counts, so
        // V_Call's probe written in a Task.Run of V_Enum's tree is a firing elsewhere.
        var trace = Trace("""
            internal static class Engine { public static void Run(Action a) => Core(a); private static void Core(Action a) => Task.Run(a); }
            public static class Api { public static List<int> Run(Action a) { Engine.Run(a); return new List<int>(); } }
            """, "M:Lib.Api.Run(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(FiredIn(trace, "P_a_0_Call"),
                        execution => execution.StartsWith(DriverExecutions.Own(DriverSynthesizer.ENUMERATE) + ">spawn:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_member_that_is_not_async_returning_Task_of_T_that_keeps_the_delegate_is_unknown_execution()
    {
        // The heap does not say what Task.FromResult's task completes with: the result is unknown, and nothing is proven not kept.
        var task = Trace("public static class Api { public static Task<Holder> Make(Action a) { a(); return Task.FromResult(new Holder(a)); } }",
                         "M:Lib.Api.Make(System.Action)");
        var valueTask = Trace("public static class Api { public static ValueTask<Holder> Make(Action a) { a(); return new ValueTask<Holder>(new Holder(a)); } }",
                              "M:Lib.Api.Make(System.Action)");
        var receiver = Trace("public sealed class Bus { private Action _a; public Task<Holder> On(Action a) { _a = a; return Task.FromResult(new Holder(a)); } public void Fire() => _a(); }",
                             "M:Lib.Bus.On(System.Action)");

        Assert.Equal(Unknown, FateOf(task, "a"));
        Assert.Equal(Unknown, FateOf(valueTask, "a"));
        Assert.Equal(Unknown, FateOf(receiver, "a"));
    }

    [Fact]
    public void A_member_that_is_not_async_returning_Task_of_a_value_that_holds_nothing_is_still_invoke_now()
    {
        var trace = Trace("public static class Api { public static Task<int> Count(Action a) { a(); return Task.FromResult(1); } }", "M:Lib.Api.Count(System.Action)");

        Assert.Equal(InvokeNow, FateOf(trace, "a"));
    }

    // ---- the fate run's roots ----

    [Fact]
    public void Fate_run_roots_only_setup_call_and_enumeration()
    {
        // An enumerable holder with public triggers: the driver writes V_Setup, V_Call, V_Enum and a T_i for Fire and GetEnumerator, and
        // only the first three run.
        var trace = Trace("""
            public sealed class Bag : IEnumerable<int>
            {
                private readonly Action _a;
                public Bag(Action a) { _a = a; }
                public void Fire() { _a(); }
                public IEnumerator<int> GetEnumerator() { yield break; }
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }
            public static class Api { public static Bag Make(Action a) => new Bag(a); }
            """, "M:Lib.Api.Make(System.Action)");
        var triggers = trace.Driver!.Triggers;
        string[] roots = [DriverSynthesizer.SETUP, DriverSynthesizer.CALL, DriverSynthesizer.ENUMERATE];

        Assert.Contains(triggers, trigger => trigger.Member == "M:Lib.Bag.Fire");
        Assert.All(triggers, trigger => Assert.Contains($"static void {trigger.Action}()", trace.Driver.Source));
        Assert.Equal(roots.Select(DriverRootProvider.RootId).Order(StringComparer.Ordinal),
                     trace.Run!.Roots.Select(root => root.StableRootId).Order(StringComparer.Ordinal));
        Assert.All(triggers, trigger =>
        {
            Assert.DoesNotContain(trace.Run.Executions!.Executions, execution => execution.TreeRootId == DriverRootProvider.RootId(trigger.Action));
            Assert.Empty(FiredIn(trace, $"P_a_0_{trigger.Action}"));
        });
        Assert.Equal(HolderResult, FateOf(trace, "a"));
        // The holder's confirmation run roots the triggers too.
        Assert.All(triggers, trigger => Assert.Contains(trace.Confirmation!.Roots, root => root.StableRootId == DriverRootProvider.RootId(trigger.Action)));
    }

    // ---- a holder's confirmation run ----

    [Fact]
    public void A_result_holder_whose_trigger_runs_the_delegate_in_a_Task_Run_is_unknown_execution()
    {
        var trace = Trace("""
            public sealed class Spawner { private Action _a; public Spawner(Action a) { _a = a; } public void Fire() { Task.Run(_a); } }
            public static class Api { public static Spawner Make(Action a) => new Spawner(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(ConfirmationFiredIn(trace, "P_a_0_T0"),
                        execution => execution.StartsWith(DriverExecutions.Own("T0") + ">spawn:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_receiver_holder_whose_trigger_runs_the_delegate_in_a_Task_Run_is_unknown_execution()
    {
        var trace = Trace("public sealed class Bus { private Action _a; public void On(Action a) { _a = a; } public void Fire() { Task.Run(_a); } }",
                          "M:Lib.Bus.On(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_holder_whose_trigger_hands_the_delegate_to_an_opaque_call_is_unknown_execution()
    {
        var timer = Trace("public sealed class Bus { private Action _a; public void On(Action a) { _a = a; } " +
                          "public void Fire() { new System.Threading.Timer(_ => _a(), null, 1, 1000); } }", "M:Lib.Bus.On(System.Action)");
        var opaque = Trace("""
            public sealed class Leaky { private Action _a; public Leaky(Action a) { _a = a; } public void Fire() { Sink.Take(_a); } }
            public static class Api { public static Leaky Make(Action a) => new Leaky(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(timer, "a"));
        Assert.Equal(Unknown, FateOf(opaque, "a"));
        Assert.Contains(ConfirmationFiredIn(opaque, "P_a_0_T0"), execution => execution.StartsWith("unknown-delegate-call:", StringComparison.Ordinal));
    }

    [Fact]
    public void Holder_whose_triggers_run_the_delegate_in_place_stays_a_holder()
    {
        var result = Trace("public static class Api { public static Holder Make(Action a) => new Holder(a); }", "M:Lib.Api.Make(System.Action)");
        var receiver = Trace("public sealed class Bus { private Action _a; public void On(Action a) { _a = a; } public void Fire() => _a(); public void Twice() { _a(); _a(); } }",
                             "M:Lib.Bus.On(System.Action)");

        Assert.Equal(HolderResult, FateOf(result, "a"));
        Assert.Equal([DriverExecutions.Own("T0")], ConfirmationFiredIn(result, "P_a_0_T0"));
        Assert.Equal(HolderThis, FateOf(receiver, "a"));
        // Every member a caller could invoke on Bus has a trigger — On itself, Fire, Twice and object's three — and those that run the
        // delegate run it in place.
        Assert.True(receiver.Driver!.Covers(FateClassifier.THIS));
        Assert.Equal(["M:Lib.Bus.Fire", "M:Lib.Bus.On(System.Action)", "M:Lib.Bus.Twice", "M:System.Object.Equals(System.Object)",
                      "M:System.Object.GetHashCode", "M:System.Object.ToString"],
                     receiver.Driver.Triggers.Select(trigger => trigger.Member).Order(StringComparer.Ordinal));
        Assert.All(receiver.Driver.Triggers.Where(trigger => trigger.Member is "M:Lib.Bus.Fire" or "M:Lib.Bus.Twice"),
                   trigger => Assert.Equal([DriverExecutions.Own(trigger.Action)], ConfirmationFiredIn(receiver, $"P_a_0_{trigger.Action}")));
    }

    [Fact]
    public void A_result_typed_IDisposable_whose_library_Dispose_runs_the_delegate_in_a_Task_Run_is_unknown_execution()
    {
        // The result's static type is a framework interface: its Dispose is a member a caller can invoke, and the library's
        // implementation hands the delegate to a Task.Run.
        var trace = Trace("""
            public sealed class Worker : IDisposable { private Action _a; public Worker(Action a) { _a = a; } public void Dispose() { Task.Run(_a); } }
            public static class Api { public static IDisposable Start(Action a) => new Worker(a); }
            """, "M:Lib.Api.Start(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        var dispose = Assert.Single(trace.Driver!.Triggers, trigger => trigger.Member == "M:System.IDisposable.Dispose");
        Assert.Equal(FateClassifier.RESULT, dispose.Holder);
        Assert.Contains(ConfirmationFiredIn(trace, $"P_a_0_{dispose.Action}"),
                        execution => execution.StartsWith(DriverExecutions.Own(dispose.Action) + ">spawn:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_holder_member_the_driver_cannot_call_leaves_the_holder_unconfirmed()
    {
        // Scan takes a span the driver cannot build: no trigger calls it, so nothing shows it does not run the delegate elsewhere.
        var trace = Trace("""
            public sealed class Spanned { private Action _a; public Spanned(Action a) { _a = a; } public void Scan(ReadOnlySpan<int> s) { Task.Run(_a); } }
            public static class Api { public static Spanned Make(Action a) => new Spanned(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.False(trace.Driver!.Covers(FateClassifier.RESULT));
        Assert.Contains(trace.Driver.Uncovered[FateClassifier.RESULT], why => why.StartsWith("M:Lib.Spanned.Scan(System.ReadOnlySpan{System.Int32})", StringComparison.Ordinal));
        Assert.Null(trace.Confirmation);

        // A custom event accessor is called by `+=`, which the engine now lowers as a call of the accessor: it is a trigger, and the
        // trigger shows it runs the delegate in a Task.Run.
        var evented = Trace("""
            public sealed class Evented { private Action _a; public Evented(Action a) { _a = a; } public event Action Changed { add { Task.Run(_a); } remove { } } }
            public static class Api { public static Evented Make(Action a) => new Evented(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(evented, "a"));
        Assert.DoesNotContain(evented.Driver!.Uncovered[FateClassifier.RESULT], why => why.StartsWith("M:Lib.Evented.add_Changed(System.Action)", StringComparison.Ordinal));
        var add = Assert.Single(evented.Driver.Triggers, trigger => trigger.Member == "M:Lib.Evented.add_Changed(System.Action)");
        Assert.Contains(ConfirmationFiredIn(evented, $"P_a_0_{add.Action}"),
                        execution => execution.StartsWith(DriverExecutions.Own(add.Action) + ">spawn:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("public Action Get { get { Task.Run(_a); return null; } }", "getter")]
    [InlineData("public int this[int i] { get { Task.Run(_a); return i; } }", "indexer")]
    [InlineData("public void Later<T>(T value) { Task.Run(_a); }", "generic method")]
    [InlineData("public void Swap(ref int value, in int other, out int result) { result = value; Task.Run(_a); }", "ref, in and out parameters")]
    [InlineData("public override string ToString() { Task.Run(_a); return \"\"; }", "an override of object's")]
    public void A_holder_member_of_any_kind_that_runs_the_delegate_elsewhere_is_unknown_execution(string member, string kind)
    {
        var trace = Trace($$"""
            public sealed class Kept { private Action _a; public Kept(Action a) { _a = a; } {{member}} }
            public static class Api { public static Kept Make(Action a) => new Kept(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.True(trace.Driver!.Covers(FateClassifier.RESULT), $"{kind}: {string.Join("; ", trace.Driver.Uncovered[FateClassifier.RESULT])}");
        Assert.True(Unknown == FateOf(trace, "a"), kind);
    }

    [Fact]
    public void A_holder_member_inherited_from_another_type_or_interface_is_called_too()
    {
        // Fire is declared on the base class and Run on an interface the holder implements explicitly; both run the delegate elsewhere.
        var inherited = Trace("""
            public abstract class Base { protected Action A; public void Fire() { Task.Run(A); } }
            public sealed class Derived : Base { public Derived(Action a) { A = a; } }
            public static class Api { public static Derived Make(Action a) => new Derived(a); }
            """, "M:Lib.Api.Make(System.Action)");
        var explicitly = Trace("""
            public interface IRunner { void Run(); }
            public sealed class Hidden : IRunner { private Action _a; public Hidden(Action a) { _a = a; } void IRunner.Run() { Task.Run(_a); } }
            public static class Api { public static Hidden Make(Action a) => new Hidden(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(inherited, "a"));
        Assert.Contains(inherited.Driver!.Triggers, trigger => trigger.Member == "M:Lib.Base.Fire");
        Assert.Equal(Unknown, FateOf(explicitly, "a"));
        Assert.Contains(explicitly.Driver!.Triggers, trigger => trigger.Member == "M:Lib.IRunner.Run");
    }

    [Fact]
    public void A_receiver_holder_whose_own_member_runs_the_delegate_it_replaces_elsewhere_is_unknown_execution()
    {
        // Calling On again hands the delegate it held to a Task.Run: the member itself is one a caller can invoke on its holder.
        var trace = Trace("public sealed class Relay { private Action _a; public void On(Action a) { if (_a != null) Task.Run(_a); _a = a; } }",
                          "M:Lib.Relay.On(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(trace.Driver!.Triggers, trigger => trigger.Member == "M:Lib.Relay.On(System.Action)" && trigger.Holder == FateClassifier.THIS);
    }

    [Fact]
    public void A_holder_member_whose_call_does_not_compile_drops_its_trigger_not_the_driver()
    {
        // The cast to an interface obsolete as an error does not compile, and the driver's pragma does not lift an error: the trigger
        // goes, its member is uncovered, and the rest of the driver still classifies.
        var trace = Trace("""
            [Obsolete("gone", true)] public interface IOld { void Try(); }
            [Obsolete("old")] public sealed class Trial : IOld { private Action _a; public Trial(Action a) { _a = a; } void IOld.Try() { } public void Fire() { _a(); } }
            [Obsolete("old")] public static class Api { public static Trial Make(Action a) => new Trial(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Contains(trace.Driver!.Uncovered[FateClassifier.RESULT], why => why.StartsWith("M:Lib.IOld.Try: the driver's call does not compile", StringComparison.Ordinal));
        Assert.DoesNotContain(trace.Driver.Triggers, trigger => trigger.Member == "M:Lib.IOld.Try");
        Assert.Contains(trace.Driver.Triggers, trigger => trigger.Member == "M:Lib.Trial.Fire");
        Assert.Empty(trace.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void A_holder_with_more_members_than_the_triggers_is_unconfirmed()
    {
        var members = string.Concat(Enumerable.Range(0, 30).Select(index => $"public void M{index:00}() {{ }} "));
        var trace = Trace($$"""
            public sealed class Wide { private Action _a; public Wide(Action a) { _a = a; } {{members}} }
            public static class Api { public static Wide Make(Action a) => new Wide(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Equal(24, trace.Driver!.Triggers.Count(trigger => trigger.Holder == FateClassifier.RESULT));
        Assert.Contains(trace.Driver.Uncovered[FateClassifier.RESULT], why => why.Contains("past the driver's 24 triggers", StringComparison.Ordinal));
    }

    [Fact]
    public void A_receiver_holder_whose_members_the_result_holder_left_no_triggers_for_is_unconfirmed()
    {
        // The result's 30 members take all 24 triggers of the driver, so none is left for Keeper's own members: the delegate kept in
        // this stays unconfirmed, though Keeper alone would fit.
        var members = string.Concat(Enumerable.Range(0, 30).Select(index => $"public void M{index:00}() {{ }} "));
        var trace = Trace($$"""
            public sealed class Wide { public Wide() { } {{members}} }
            public sealed class Keeper { private Action _a; public Keeper() { } public Wide Hold(Action a) { _a = a; return new Wide(); } public void Fire() { _a(); } }
            """, "M:Lib.Keeper.Hold(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.Equal(24, trace.Driver!.Triggers.Count);
        Assert.All(trace.Driver.Triggers, trigger => Assert.Equal(FateClassifier.RESULT, trigger.Holder));
        Assert.False(trace.Driver.Covers(FateClassifier.THIS));
        Assert.Contains(trace.Driver.Uncovered[FateClassifier.THIS], why => why.StartsWith("M:Lib.Keeper.Fire: past the driver's 24 triggers", StringComparison.Ordinal));
    }

    [Fact]
    public void A_receiver_holder_the_result_holder_leaves_room_for_stays_a_holder()
    {
        // Where the cap stops: a small result leaves triggers for every member of the receiver, and the holder is confirmed.
        var trace = Trace("""
            public sealed class Narrow { public Narrow() { } public void N() { } }
            public sealed class Keeper { private Action _a; public Keeper() { } public Narrow Hold(Action a) { _a = a; return new Narrow(); } public void Fire() { _a(); } }
            """, "M:Lib.Keeper.Hold(System.Action)");

        Assert.Equal(HolderThis, FateOf(trace, "a"));
        Assert.True(trace.Driver!.Covers(FateClassifier.THIS));
    }

    [Fact]
    public void A_holder_whose_every_member_kind_is_covered_and_runs_nothing_elsewhere_stays_a_holder()
    {
        // A getter and a setter, an indexer, a generic method, ref/in/out parameters and an interface: all called, none runs the
        // delegate outside the action that called it. The field-like event's compiler-written accessors run no delegate and need none.
        var trace = Trace("""
            public interface IFire { void Fire(); }
            public sealed class Calm : IFire
            {
                private Action _a;
                public Calm(Action a) { _a = a; }
                public void Fire() { _a(); }
                public int Count { get { _a(); return 1; } set { } }
                public int this[int i] => i;
                public event Action Changed;
                public T Echo<T>(T value) => value;
                public void Swap(ref int value, in int other, out int result) { result = value; }
            }
            public static class Api { public static Calm Make(Action a) => new Calm(a); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(HolderResult, FateOf(trace, "a"));
        Assert.True(trace.Driver!.Covers(FateClassifier.RESULT), string.Join("; ", trace.Driver.Uncovered[FateClassifier.RESULT]));
        Assert.Contains(trace.Driver.Triggers, trigger => trigger.Member == "M:Lib.Calm.Echo``1(``0)");
        Assert.Contains(trace.Driver.Triggers, trigger => trigger.Member == "M:Lib.Calm.set_Count(System.Int32)");
        Assert.Contains(trace.Driver.Triggers, trigger => trigger.Member == "M:Lib.Calm.get_Item(System.Int32)");
        Assert.DoesNotContain(trace.Driver.Triggers, trigger => trigger.Member == "M:Lib.Calm.add_Changed(System.Action)");
        Assert.DoesNotContain(trace.Driver.Triggers, trigger => trigger.Member == "M:Lib.IFire.Fire");
    }

    [Fact]
    public void Holder_whose_confirmation_run_passes_the_bound_is_unknown_execution()
    {
        // The trigger reaches 1 600 bodies the fate run never sees: the fate run stays under the bound and shows a holder, the
        // confirmation run stops, and the holder is not confirmed.
        var fire = new StringBuilder("public void Fire() { _a();");
        var chain = new StringBuilder();
        for (var i = 0; i < 1600; i++)
        {
            fire.Append(" C").Append(i).Append("();");
            chain.Append("    static void C").Append(i).Append("() { }\n");
        }

        var trace = Trace($"public sealed class Heavy {{ private Action _a; public Heavy(Action a) {{ _a = a; }} {fire} }}\n{chain}}}\n" +
                          "public static class Api { public static Heavy Make(Action a) => new Heavy(a); }", "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.True(trace.Answer.Generation.ReachedBodies < 1500, trace.Answer.Generation.ReachedBodies.ToString());
        Assert.True(trace.Confirmation is { Stopped: true }, "the confirmation run was not stopped");
        Assert.True(trace.Confirmation.StoppedAtReachableBodies > 1500);
    }

    // ---- holder result ----

    [Fact]
    public void A_holder_result_whose_trigger_the_driver_never_called_is_unknown_execution()
    {
        // R7: no trigger ran the delegate the result keeps, so nothing shows the result runs it rather than only storing it.
        var trace = Trace("""
            public sealed class Quiet { private Action _a; public Quiet(Action a) { _a = a; } internal void Fire() => _a(); }
            public static class Api { public static Quiet Keep(Action a) => new Quiet(a); }
            """, "M:Lib.Api.Keep(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_constructor_that_keeps_the_delegate_in_the_object_it_creates_is_holder_result()
    {
        Assert.Equal(HolderResult, FateOf(Trace("", "M:Lib.Holder.#ctor(System.Action)"), "a"));
    }

    [Fact]
    public void A_member_returning_Task_of_T_whose_awaited_object_keeps_the_delegate_is_unknown_execution()
    {
        // The fate run finds the awaited object a holder, but R7 keeps it only when a trigger ran the delegate: a trigger's Fire on the
        // awaited object runs no body, since the heap carries no value through an await.
        var trace = Trace("public static class Api { public static async Task<Holder> MakeAsync(Action a) { await Task.Delay(1); return new Holder(a); } }",
                          "M:Lib.Api.MakeAsync(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_member_returning_ValueTask_of_T_whose_awaited_object_keeps_the_delegate_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static async ValueTask<Holder> MakeAsync(Action a) { await Task.Delay(1); return new Holder(a); } }",
                          "M:Lib.Api.MakeAsync(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_carried_probe_of_a_holding_parameter_kept_by_the_result_is_holder_result()
    {
        var trace = Trace("""
            public sealed class Parser { private readonly Func<int> _f; public Parser(Func<int> f) { _f = f; } public int Parse() => _f(); }
            public sealed class Codec { private readonly Parser _p; public Codec(Parser p) { _p = p; } public int Read() => _p.Parse(); }
            public static class Api { public static Codec For(int tag, Parser parser) => new Codec(parser); }
            """, "M:Lib.Api.For(System.Int32,Lib.Parser)");

        Assert.Equal(HolderResult, FateOf(trace, "parser"));
    }

    [Fact]
    public void A_result_holder_the_member_also_hands_to_an_opaque_call_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static Holder Make(Action a) { var h = new Holder(a); Sink.Take(h); return h; } }",
                          "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_holder_also_reachable_from_a_library_static_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static Holder Make(Action a) { var h = new Holder(a); Cache.Last = h; return h; } }",
                          "M:Lib.Api.Make(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_member_that_returns_an_object_a_static_initializer_made_has_no_result_object()
    {
        var trace = Trace("public static class Reg { private static readonly Box Shared = new Box(); public static Box Put(Action a) { Shared.A = a; return Shared; } }",
                          "M:Lib.Reg.Put(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        var run = trace.Run!;
        var reachability = new HeapReachability(run.Heap!);
        var executions = new DriverExecutions(trace.Driver!, run.Executions!, new HashSet<string>());
        var allocations = new Allocations(trace.Driver!, run.Heap!, run.Executions!, executions, reachability);
        Assert.Equal(AllocationKind.LibraryBefore, allocations.Of(Assert.Single(Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"))).Kind);
    }

    // ---- holder this ----

    [Fact]
    public void A_delegate_kept_in_the_receiver_is_holder_this()
    {
        var trace = Trace("public sealed class Bus { private Action _a; public void On(Action a) { _a = a; } public void Fire() => _a(); }",
                          "M:Lib.Bus.On(System.Action)");

        Assert.Equal(HolderThis, FateOf(trace, "a"));
    }

    [Fact]
    public void A_fluent_member_that_keeps_the_delegate_in_this_and_returns_this_is_holder_this()
    {
        var trace = Trace("public sealed class Bus { private Action _a; public Bus On(Action a) { _a = a; return this; } public void Fire() => _a(); }",
                          "M:Lib.Bus.On(System.Action)");

        Assert.Equal(HolderThis, FateOf(trace, "a"));
        Assert.Equal(Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Recv_Call"), Slot(trace, DriverSynthesizer.KEEP_TYPE, "R"));
    }

    [Fact]
    public void A_receiver_holder_handed_to_an_opaque_call_is_unknown_execution()
    {
        var trace = Trace("public sealed class Bus { private Action _a; public void On(Action a) { _a = a; Sink.Take(this); } public void Fire() => _a(); }",
                          "M:Lib.Bus.On(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_delegate_kept_by_both_the_result_and_the_receiver_is_unknown_execution()
    {
        var trace = Trace("public sealed class Bus { private Action _a; public Holder Both(Action a) { _a = a; return new Holder(a); } public void Fire() => _a(); }",
                          "M:Lib.Bus.Both(System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    // ---- kept elsewhere ----

    [Fact]
    public void A_static_member_that_returns_a_holder_argument_it_stored_the_delegate_in_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static Box Put(Box b, Action a) { b.A = a; return b; } }", "M:Lib.Api.Put(Lib.Box,System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_delegate_the_member_stores_into_an_argument_value_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static void Put(Box b, Action a) { b.A = a; } }", "M:Lib.Api.Put(Lib.Box,System.Action)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_delegate_parameter_the_member_stores_into_another_parameters_ref_slot_is_unknown_execution()
    {
        var trace = Trace("public static class Api { public static void Into(Action a, ref Action b) { a(); b = a; } }", "M:Lib.Api.Into(System.Action,System.Action@)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
    }

    [Fact]
    public void A_delegate_the_member_stores_into_an_out_variable_is_unknown_execution()
    {
        // The store goes through the out parameter into the driver's Out_ field; the heap's points-to does not carry it, the store
        // access does.
        var trace = Trace("public static class Api { public static void Keep(Action a, out Action kept) { kept = a; a(); } }",
                          "M:Lib.Api.Keep(System.Action,System.Action@)");

        Assert.Equal(Unknown, FateOf(trace, "a"));
        Assert.DoesNotContain("kept", trace.Answer.Classified!.Keys);
    }

    [Fact]
    public void A_carried_probe_the_member_stores_into_another_field_of_the_value_it_was_handed_is_unknown_execution()
    {
        var trace = Trace("""
            public sealed class Pair { public Func<int> F; public Func<int> Other; public Pair(Func<int> f) { F = f; } }
            public static class Api { public static void Swap(Pair p) { p.Other = p.F; } }
            """, "M:Lib.Api.Swap(Lib.Pair)");

        Assert.Equal(Unknown, FateOf(trace, "p"));
    }

    [Fact]
    public void A_parameter_carrying_two_probes_the_member_treats_differently_is_unknown_execution()
    {
        var trace = Trace("""
            public sealed class Pair { private Func<int> _f; private Func<int> _g; public Pair(Func<int> f, Func<int> g) { _f = f; _g = g; } public int F() => _f(); }
            public static class Api { public static int Mixed(Pair p) => p.F(); }
            """, "M:Lib.Api.Mixed(Lib.Pair)");

        Assert.Equal(Unknown, FateOf(trace, "p"));
        Assert.Equal(2, trace.Driver!.Parameters.Single().Probes.Count(probe => probe.Variant == DriverSynthesizer.CALL_VARIANT));
    }

    // ---- setup widens ----

    [Fact]
    public void A_carried_probe_its_carriers_constructor_fires_during_setup_is_unknown_execution_and_named()
    {
        var trace = Trace("""
            public sealed class Parser { private readonly Func<int> _f; public Parser(Func<int> f) { _f = f; f(); } public int Parse() => _f(); }
            public static class Api { public static int Use(Parser p) => p.Parse(); }
            """, "M:Lib.Api.Use(Lib.Parser)");

        Assert.Equal(Unknown, FateOf(trace, "p"));
        Assert.Equal(["p"], trace.Answer.Generation.SetupWidened);
    }

    [Fact]
    public void A_carried_probe_its_carriers_constructor_hands_to_an_opaque_call_during_setup_is_unknown_execution_and_named()
    {
        var trace = Trace("""
            public sealed class Parser { private readonly Func<int> _f; public Parser(Func<int> f) { _f = f; Sink.Take(f); } public int Parse() => _f(); }
            public static class Api { public static int Use(Parser p) => p.Parse(); }
            """, "M:Lib.Api.Use(Lib.Parser)");

        Assert.Equal(Unknown, FateOf(trace, "p"));
        Assert.Equal(["p"], trace.Answer.Generation.SetupWidened);
    }

    // ---- applicability ----

    [Fact]
    public void A_holder_this_on_a_static_member_is_refused_to_unknown_execution_with_the_refusal()
    {
        var library = Compile(PRELUDE + "public static class Api { public static void Run(Action a) { } }\n}\n");
        var member = (IMethodSymbol)DriverSynthesizer.FindMember(library.Compilation!, "M:Lib.Api.Run(System.Action)")!;

        var fate = FateClassifier.Applied(member, HolderThis, out var refusal);

        Assert.Equal(Unknown, fate);
        Assert.Equal("holder this needs an instance method that is not a constructor", refusal);
    }

    [Fact]
    public void An_iterator_on_a_member_that_returns_no_sequence_is_refused_to_unknown_execution_with_the_refusal()
    {
        var library = Compile(PRELUDE + "public static class Api { public static Holder Make(Action a) => new Holder(a); public static void Run(Action a) { } }\n}\n");
        var make = (IMethodSymbol)DriverSynthesizer.FindMember(library.Compilation!, "M:Lib.Api.Make(System.Action)")!;
        var run = (IMethodSymbol)DriverSynthesizer.FindMember(library.Compilation!, "M:Lib.Api.Run(System.Action)")!;

        Assert.Equal(Unknown, FateClassifier.Applied(make, Iterator, out var refusal));
        Assert.Equal("iterator needs a method whose result implements System.Collections.IEnumerable", refusal);
        Assert.Equal(Unknown, FateClassifier.Applied(run, HolderResult, out var resultRefusal));
        Assert.NotNull(resultRefusal);
        // What the rules allow passes as it is.
        Assert.Equal(HolderResult, FateClassifier.Applied(make, HolderResult, out var none));
        Assert.Null(none);
    }

    // ---- the built-in layer ----

    [Fact]
    public void A_member_of_the_implementation_assembly_made_extern_that_a_built_in_model_describes_stays_an_opaque_call()
    {
        const string ALL = "M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})";
        var trace = TraceOf("""
            [assembly: System.Reflection.AssemblyVersion("8.0.0.0")]
            namespace System.Linq
            {
                public static class Enumerable
                {
                    public static bool All<TSource>(System.Collections.Generic.IEnumerable<TSource> source, System.Func<TSource, bool> predicate) => Missing(source);
                }
            }

            namespace Lib
            {
                public static class Api
                {
                    public static bool Check(System.Collections.Generic.IEnumerable<int> s, System.Func<int, bool> p) => System.Linq.Enumerable.All(s, p);
                }
            }
            """, "M:Lib.Api.Check" + SEQUENCE_PREDICATE, "System.Linq");
        var all = (IMethodSymbol)DriverSynthesizer.FindMember(Compile("""
            [assembly: System.Reflection.AssemblyVersion("8.0.0.0")]
            namespace System.Linq { public static class Enumerable { public static extern bool All<TSource>(System.Collections.Generic.IEnumerable<TSource> source, System.Func<TSource, bool> predicate); } }
            """, "System.Linq").Compilation!, ALL)!;

        // The built-in layer describes All at this version, invoke-now; the generator leaves it out, so the extern All stays a call the
        // engine cannot follow and the predicate it is handed runs in an unknown execution.
        Assert.Equal(LibraryMatchKind.Known, LibraryModels.BuiltIn.Find(all)?.Kind);
        Assert.Equal(1, trace.Answer.Generation.ExternBodies);
        Assert.Equal(Unknown, FateOf(trace, "p"));
        Assert.Contains(FiredIn(trace, "P_p_0_Call"), execution => execution.StartsWith("unknown-delegate-call:", StringComparison.Ordinal));
    }

    // ---- reasons ----

    [Fact]
    public void An_own_body_that_does_not_compile_is_body_does_not_compile()
    {
        var trace = Trace("public static class Api { public static void Run(Action a) { a(Missing()); } }", "M:Lib.Api.Run(System.Action)");

        Assert.Null(trace.Answer.Classified);
        Assert.Equal(GenerationReasons.BODY_DOES_NOT_COMPILE, trace.Answer.Reason);
        Assert.Equal(1, trace.Answer.Generation.ExternBodies);
    }

    [Fact]
    public void The_closure_bound_stops_a_driver_that_reaches_too_many_bodies()
    {
        var trace = Trace(Chain(1501, "static void C{0}() {{ }}"), "M:Lib.Api.Run(System.Action)");

        Assert.Null(trace.Answer.Classified);
        Assert.Equal(GenerationReasons.CLOSURE_BOUND, trace.Answer.Reason);
        Assert.True(trace.Answer.Generation.ReachedBodies > 1500, trace.Answer.Generation.ReachedBodies.ToString());
        Assert.Contains("over 1500", trace.Answer.Detail);
    }

    [Fact]
    public void The_closure_bound_counts_bodies_no_method_of_its_own_holds()
    {
        // 800 methods each holding a lambda: under 1 500 methods, over 1 500 bodies once the nested ones count.
        var trace = Trace(Chain(800, "static void C{0}() {{ Action l = () => Cache.Last = null; l(); }}"), "M:Lib.Api.Run(System.Action)");

        Assert.Equal(GenerationReasons.CLOSURE_BOUND, trace.Answer.Reason);
        Assert.True(trace.Answer.Generation.ReachedBodies > 1500, trace.Answer.Generation.ReachedBodies.ToString());
    }

    [Fact]
    public void Closure_bound_stops_past_1500_bodies()
    {
        // The driver and Run reach a fixed number of bodies besides the chain, measured on a short chain: the chain that brings the
        // reachable set to exactly 1 500 bodies runs, one more method stops it.
        const string EMPTY = "static void C{0}() {{ }}";
        var others = Trace(Chain(10, EMPTY), "M:Lib.Api.Run(System.Action)").Answer.Generation.ReachedBodies - 10;

        var at = Trace(Chain(1500 - others, EMPTY), "M:Lib.Api.Run(System.Action)");
        var past = Trace(Chain(1501 - others, EMPTY), "M:Lib.Api.Run(System.Action)");

        Assert.Equal(InvokeNow, FateOf(at, "a"));
        Assert.Equal(1500, at.Answer.Generation.ReachedBodies);
        Assert.Equal(GenerationReasons.CLOSURE_BOUND, past.Answer.Reason);
        Assert.Equal(1501, past.Answer.Generation.ReachedBodies);
        Assert.Contains("1501 bodies, over 1500", past.Answer.Detail);
    }

    [Fact]
    public void Under_the_closure_bound_the_driver_is_classified()
    {
        var trace = Trace(Chain(100, "static void C{0}() {{ }}"), "M:Lib.Api.Run(System.Action)");

        Assert.Equal(InvokeNow, FateOf(trace, "a"));
        Assert.True(trace.Answer.Generation.ReachedBodies > 100);
    }

    [Fact]
    public void When_two_reasons_apply_the_first_of_G6_wins()
    {
        // The member's body does not compile, and it takes no delegate: not-a-candidate comes before body-does-not-compile.
        var candidate = Trace("public static class Api { public static int Add(int x) => Missing(x); }", "M:Lib.Api.Add(System.Int32)");
        // The library declares a type the engine claims, and has no such member: member-not-found comes before engine-recognized.
        var missing = Trace("public sealed class Mine { }\n}\nnamespace System.Threading.Tasks { public static class Parallel { public static void For() { } }",
                            "M:Lib.Api.Gone(System.Action)");

        Assert.Equal(GenerationReasons.NOT_A_CANDIDATE, candidate.Answer.Reason);
        Assert.Equal(1, candidate.Answer.Generation.ExternBodies);
        Assert.Equal(GenerationReasons.MEMBER_NOT_FOUND, missing.Answer.Reason);
        Assert.True(GenerationReasons.Ordered.ToList().IndexOf(GenerationReasons.MEMBER_NOT_FOUND) <
                    GenerationReasons.Ordered.ToList().IndexOf(GenerationReasons.ACCESSOR));
        Assert.True(GenerationReasons.Ordered.ToList().IndexOf(GenerationReasons.ACCESSOR) <
                    GenerationReasons.Ordered.ToList().IndexOf(GenerationReasons.ENGINE_RECOGNIZED));
        Assert.True(GenerationReasons.Ordered.ToList().IndexOf(GenerationReasons.NOT_A_CANDIDATE) <
                    GenerationReasons.Ordered.ToList().LastIndexOf(GenerationReasons.BODY_DOES_NOT_COMPILE));
    }

    [Fact]
    public void A_member_kind_the_generator_does_not_take_is_accessor_before_not_a_candidate()
    {
        var trace = Trace("public sealed class Bag { public int Count { get; set; } }", "P:Lib.Bag.Count");

        Assert.Equal(GenerationReasons.ACCESSOR, trace.Answer.Reason);
        Assert.Equal(0, trace.Answer.Generation.ReachedBodies);
    }

    [Fact]
    public void A_member_the_library_does_not_declare_is_member_not_found()
    {
        var trace = Trace("", "M:Lib.Nowhere.Run(System.Action)");

        Assert.Equal(GenerationReasons.MEMBER_NOT_FOUND, trace.Answer.Reason);
        Assert.Null(trace.Answer.Generation.Implementation);
    }

    /// <summary>The executions a probe's fired field was written in, in the holder's confirmation run.</summary>
    /// <param name="trace">The trace, with a confirmation run that was not stopped.</param>
    /// <param name="firedField">The probe's fired field.</param>
    private static IReadOnlyList<string> ConfirmationFiredIn(GenerationTrace trace, string firedField) =>
        FiredIn(new GenerationTrace(trace.Answer, trace.Driver, Assert.IsType<ScopeRun>(trace.Confirmation)), firedField);

    /// <summary>A library whose <c>Run(Action)</c> calls <paramref name="count"/> methods of the shape given.</summary>
    /// <param name="count">How many methods.</param>
    /// <param name="method">The method's declaration, <c>{0}</c> its number.</param>
    private static string Chain(int count, string method)
    {
        var text = new StringBuilder("public static class Api\n{\n    public static void Run(Action a)\n    {\n        a();\n");
        for (var i = 0; i < count; i++)
            text.Append("        C").Append(i).Append("();\n");
        text.Append("    }\n");
        for (var i = 0; i < count; i++)
            text.Append("    ").AppendFormat(method, i).Append('\n');
        return text.Append("}\n").ToString();
    }
}
