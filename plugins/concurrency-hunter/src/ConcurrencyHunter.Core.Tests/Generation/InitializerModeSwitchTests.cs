using System.Text;
using System.Runtime.InteropServices;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class InitializerModeSwitchTests
{
    [Fact]
    public void A_member_whose_runs_fit_answers_with_inert_initializers()
    {
        var trace = Generate("public static class Api { public static void Run(Action a) => a(); }", "M:Lib.Api.Run(System.Action)");
        Assert.Equal("inert", trace.Answer.Generation.Mode);
        Assert.Single(trace.Answer.Generation.Attempts);
        Assert.NotNull(trace.Answer.Model);
    }

    [Fact]
    public void A_fate_run_past_the_bound_with_inert_initializers_is_generated_again_with_them_left_out()
    {
        var trace = Generate("public static class Api { static Api() { Huge.All(); } public static void Run(Action a) => a(); }" + Huge(),
                             "M:Lib.Api.Run(System.Action)");
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.Equal(2, trace.Answer.Generation.Attempts.Count);
        Assert.True(trace.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        Assert.True(trace.Answer.Generation.ReachedBodies < 1500);
        Assert.NotNull(trace.Answer.Model);
    }

    [Fact]
    public void A_confirmation_past_the_bound_with_inert_initializers_is_generated_again_with_them_left_out()
    {
        var trace = Holder("Tail.Touch();", "static Tail() { Huge.All(); }");
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.True(trace.Answer.Generation.Attempts[0].ConfirmationBodies > 1500);
        Assert.True(trace.Answer.Generation.Attempts[1].ConfirmationBodies < 1500);
        Assert.Equal(FateClassifier.HOLDER, FateOf(trace, "a").Fate);
        Assert.NotNull(trace.Answer.Model);
    }

    [Fact]
    public void Past_the_bound_in_both_modes_is_closure_bound()
    {
        var trace = Generate("public static class Api { public static void Run(Action a) { Huge.All(); a(); } }" + Huge(),
                             "M:Lib.Api.Run(System.Action)");
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.Equal(2, trace.Answer.Generation.Attempts.Count);
        Assert.Equal(GenerationReasons.CLOSURE_BOUND, trace.Answer.Reason);
        Assert.Equal($"the reachable set reached {trace.Run!.StoppedAtReachableBodies} bodies, over 1500", trace.Answer.Detail);
    }

    [Fact]
    public void A_confirmation_past_the_bound_in_both_modes_leaves_the_holder_unknown_execution()
    {
        var trace = Holder("Huge.All();", "");
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.All(trace.Answer.Generation.Attempts, attempt => Assert.True(attempt.ConfirmationBodies > 1500));
        Assert.Equal("unknown-execution", FateOf(trace, "a").Fate);
    }

    [Fact]
    public void CoreLib_is_generated_with_running_initializers_only()
    {
        var library = Compile(PRELUDE + "public static class Api { public static void Run(Action a) => a(); } }", "System.Private.CoreLib");
        var trace = ModelGenerator.Trace(new GenerationRequest("System.Private.CoreLib", "1.0", "M:Lib.Api.Run(System.Action)", null, null),
                                         library, CancellationToken.None);
        Assert.Equal("run", trace.Answer.Generation.Mode);
        Assert.Single(trace.Answer.Generation.Attempts);
        Assert.Equal(TypeInitializerTreatment.Run, trace.Run!.Reachable.TypeInitializersTreatment);
    }

    [Fact]
    public void The_test_override_off_runs_both_attempts()
    {
        var trace = Generate("public static class Api { public static void Run(Action a) => a(); }", "M:Lib.Api.Run(System.Action)",
                             TypeInitializerTreatment.Off);
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.Equal(new[] { "inert", "off" }, trace.Answer.Generation.Attempts.Select(attempt => attempt.Mode));
    }

    [Fact]
    public void Only_the_last_seeding_pass_and_the_confirmation_decide_the_switch()
    {
        var run = Generate("public static class Api { public static void Run(Action a) => a(); }", "M:Lib.Api.Run(System.Action)").Run!;
        foreach (var fateStopped in new[] { false, true })
        foreach (var confirmationStopped in new bool?[] { null, false, true })
        {
            var last = run with { StoppedAtReachableBodies = fateStopped ? 1501 : null };
            var confirmation = confirmationStopped is { } stopped ? run with { StoppedAtReachableBodies = stopped ? 1501 : null } : null;
            Assert.Equal(fateStopped || confirmationStopped == true, ModelGenerator.Overflowed(last, confirmation));
        }
    }

    [Fact]
    public void The_off_copy_is_compiled_once_per_library_and_released_with_it()
    {
        using var install = new GenerationInstall();
        const string NAME = "Fixture.OffCopy";
        var path = EmittedAssemblies.Write(Path.Combine(install.Root, NAME + ".dll"), NAME,
            "public sealed class Box { } public static class Api { private static readonly Box X = new(); public static void Run() { } }");
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var implementation = new ImplementationAssembly(NAME, path, null, "1.0.0", $"net{Environment.Version.Major}.0", runtime,
            EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(reference => Path.Combine(runtime, reference + ".dll")).ToArray(), [], "0.0.0.0", "", Guid.Empty);
        var ownership = new CompilationOwnership();
        LibraryCompilationResult library;
        using (ownership.Open())
            library = LibraryCompilation.Compile(implementation, CancellationToken.None);
        var checks = new GenerationChecks(CancellationToken.None);
        try
        {
            var starts = 0;
            var probe = new LibraryCompilationProbe { Started = () => Interlocked.Increment(ref starts) };
            var first = LibraryCompilation.RecipeCopy(library, checks, probe);
            Assert.Same(first, LibraryCompilation.RecipeCopy(LibraryCompilation.Compile(implementation, CancellationToken.None), checks, probe));
            Assert.Equal(1, starts);
            Assert.NotSame(library.Compilation, first.Compilation);
            Assert.Equal(library.Bodies, first.Bodies);
            Assert.Equal(library.ExternBodies, first.ExternBodies);
            Assert.Same(library.ExternMembers, first.ExternMembers);
            LibraryCompilation.Release(ownership.Created);
            LibraryCompilationResult again;
            using (ownership.Open())
                again = LibraryCompilation.Compile(implementation, CancellationToken.None);
            Assert.NotSame(library, again);
            Assert.NotSame(first, LibraryCompilation.RecipeCopy(again, checks, probe));
            Assert.Equal(2, starts);
        }
        finally
        {
            LibraryCompilation.Release(ownership.Created);
        }
    }

    private static GenerationTrace Holder(string trigger, string initializer) =>
        Generate("public static class Tail { " + initializer + " public static void Touch() { } } " +
                 "public sealed class Api { private Action _a; public Api(Action a) { _a = a; } public void Fire() { " + trigger + " _a(); } }" + Huge(),
                 "M:Lib.Api.#ctor(System.Action)");

    [Fact]
    public void A_mixed_generic_initializer_is_scanned_after_the_automatic_switch()
    {
        var trace = Generate("""
            public static class Registry<T>
            {
                static Registry() { _ = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite); }
                public static void Touch() { }
            }
            public static class Api
            {
                static Api() { Huge.All(); }
                public static void Run<T>(T value) { Registry<int>.Touch(); Registry<T>.Touch(); }
            }
            """ + Huge(), "M:Lib.Api.Run``1(``0)");
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.True(trace.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        Assert.Equal(ModelReasons.INCOMPLETE, trace.Answer.ModelReason);
        Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN &&
                                                   cause.Detail.Contains("Registry", StringComparison.Ordinal));
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("Registry<T>.Touch();", false)]
    [InlineData("Registry<int>.Touch();", true)]
    [InlineData("Registry<int>.Touch(); Registry<T>.Touch();", true)]
    [InlineData("Registry<List<T>>.Touch();", false)]
    [InlineData("Registry<List<int>>.Touch(); Registry<List<T>>.Touch();", true)]
    public void Initializer_scanning_uses_each_closed_key(string touches, bool scanned)
    {
        var trace = Generate("""
            public static class Registry<T>
            {
                static Registry() { _ = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite); }
                public static void Touch() { }
            }
            public static class Api { public static void Run<T>(T value) { TOUCHES } }
            """.Replace("TOUCHES", touches), "M:Lib.Api.Run``1(``0)", TypeInitializerTreatment.Off);
        Assert.Equal(scanned, trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN));
        if (scanned)
            Assert.Null(trace.Answer.Model);
        else
            Assert.NotNull(trace.Answer.Model);
    }

    [Fact]
    public void An_argument_delegate_does_not_resolve_a_static_handoff_after_the_automatic_switch()
    {
        var trace = Generate("""
            public interface ITarget { void Take(ref int value, Action callback); }
            public sealed class Target : ITarget { public void Take(ref int value, Action callback) { } }
            public static class State { public static int X; }
            public static class Api
            {
                public static extern ITarget Get();
                static Api() { _ = new Target(); Get().Take(ref State.X, () => { }); Huge.All(); }
                public static void Run(Action a) => a();
            }
            """ + Huge(), "M:Lib.Api.Run(System.Action)");
        Assert.True(trace.Answer.Generation.Mode == "off", System.Text.Json.JsonSerializer.Serialize(trace.Answer));
        Assert.True(trace.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
        Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE &&
                                                   cause.Detail.Contains("Lib.State", StringComparison.Ordinal));
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("() => { }", false)]
    [InlineData("Callback", false)]
    [InlineData("async () => { await Task.Yield(); }", false)]
    [InlineData("() => { }", true)]
    [InlineData("Callback", true)]
    [InlineData("async () => { await Task.Yield(); }", true)]
    public void Only_the_calls_own_targets_leave_virtual_handoffs_unresolved_and_async_arguments_may_start_work(string callback, bool resolved)
    {
        var source = """
            public interface ITarget { void Take(ref int value, Action callback); }
            public sealed class Target : ITarget { public void Take(ref int value, Action callback) { } }
            public static class State { public static int X; }
            public static class Api
            {
                public static extern ITarget Get();
                public static void Callback() { }
                static Api() { RECEIVER.Take(ref State.X, CALLBACK); }
                public static void Run(Action a) => a();
            }
            """.Replace("RECEIVER", resolved ? "((ITarget)new Target())" : "Get()").Replace("CALLBACK", callback);
        var trace = Generate(source, "M:Lib.Api.Run(System.Action)", TypeInitializerTreatment.Off);
        var running = Generate(source, "M:Lib.Api.Run(System.Action)", TypeInitializerTreatment.Run);
        // Known-wider (D-0025): Off treats both interface calls as unresolved; Run's visible implementation never invokes the callback.
        Assert.False(InitializerStartsWork(running));
        Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
        Assert.Equal(callback.StartsWith("async", StringComparison.Ordinal),
                     trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN));
        Assert.Null(trace.Answer.Model);
    }

    [Theory]
    [InlineData("static", "() => { }")]
    [InlineData("static", "Callback")]
    [InlineData("static", "async () => { await Task.Yield(); }")]
    [InlineData("static", "Async")]
    [InlineData("virtual", "() => { }")]
    [InlineData("virtual", "Callback")]
    [InlineData("virtual", "async () => { await Task.Yield(); }")]
    [InlineData("virtual", "Async")]
    [InlineData("constrained", "() => { }")]
    [InlineData("constrained", "Callback")]
    [InlineData("constrained", "async () => { await Task.Yield(); }")]
    [InlineData("constrained", "Async")]
    [InlineData("constructor", "() => { }")]
    [InlineData("constructor", "Callback")]
    [InlineData("constructor", "async () => { await Task.Yield(); }")]
    [InlineData("constructor", "Async")]
    [InlineData("delegate", "() => { }")]
    [InlineData("delegate", "Callback")]
    [InlineData("delegate", "async () => { await Task.Yield(); }")]
    [InlineData("delegate", "Async")]
    public void Argument_delegates_leave_each_unseen_call_kind_unresolved_and_async_arguments_may_start_work(string kind, string callback)
    {
        var declarations = kind switch
        {
            "static" => "public class Target { public static extern void Take(ref int x, Action callback); }",
            "virtual" => "public class Target { public virtual extern void Take(ref int x, Action callback); }",
            "constrained" => "public interface ITarget { void Take(ref int x, Action callback); } " +
                             "public struct Target : ITarget { public void Take(ref int x, Action callback) { } }",
            "constructor" => "public class Target { public extern Target(ref int x, Action callback); }",
            _ => "public delegate void Target(ref int x, Action callback);"
        };
        var call = kind switch
        {
            "static" => "Target.Take(ref State.X, CALLBACK);",
            "virtual" => "Get().Take(ref State.X, CALLBACK);",
            "constrained" => "void Send<T>(T receiver) where T : struct, ITarget { receiver.Take(ref State.X, CALLBACK); } Send(new Target());",
            "constructor" => "_ = new Target(ref State.X, CALLBACK);",
            _ => "Get()(ref State.X, CALLBACK);"
        };
        var source = declarations + """
            public static class State { public static int X; }
            public static class Api
            {
                public static extern Target Get();
                public static void Callback() { }
                public static async void Async() { await Task.Yield(); }
                static Api() { CALL }
                public static void Run(Action a) => a();
            }
            """.Replace("CALL", call.Replace("CALLBACK", callback));
        var trace = Generate(source, "M:Lib.Api.Run(System.Action)", TypeInitializerTreatment.Off);
        var running = Generate(source, "M:Lib.Api.Run(System.Action)", TypeInitializerTreatment.Run);
        // Known-wider (D-0025): async arguments of these extern/virtual/constrained/constructor/delegate calls may run unseen;
        // no Run twin observes a work start, including the visible constrained implementation whose body ignores the callback.
        Assert.False(InitializerStartsWork(running));
        Assert.Equal((callback == "Async" || callback.StartsWith("async", StringComparison.Ordinal)) ? ModelReasons.INCOMPLETE : ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
        Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
        Assert.Equal((callback == "Async" || callback.StartsWith("async", StringComparison.Ordinal)),
                     trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN));
        Assert.Null(trace.Answer.Model);
    }

    [Fact]
    public void A_static_only_the_confirmation_loads_gets_a_recipe_value()
    {
        var trace = Generate("""
            public sealed class Registry { public static readonly Registry Default = new Registry(); public Action Wrap(Action f) => f; }
            public sealed class Parser { private Action _f; public Parser(Action f) { _f = f; } public void Parse() => Registry.Default.Wrap(_f)(); }
            """, "M:Lib.Parser.#ctor(System.Action)", TypeInitializerTreatment.Off);
        Assert.NotNull(trace.Driver);
        Assert.Contains(trace.Driver.RecipeStatements, statement => statement.Operation.Syntax.ToString().Contains("Registry.Default = new"));
        Assert.Equal(2, trace.ConfirmationPasses);
        Assert.Equal(FateClassifier.HOLDER, FateOf(trace, "f").Fate);
        Assert.Equal(FateClassifier.RESULT, FateOf(trace, "f").Holder);
        Assert.NotNull(trace.Answer.Model);
    }

    [Fact]
    public void A_recipe_value_in_a_static_readonly_lock_is_one_object_per_process()
    {
        const string TYPES = "public sealed class Gate { } public static class Api { private static readonly Gate s_gate = new Gate(); private static int Count; " +
                             "public static void Run(Action a) { lock (s_gate) { Count++; a(); } } }";
        var off = Generate(TYPES, "M:Lib.Api.Run(System.Action)", TypeInitializerTreatment.Off);
        var run = Generate(TYPES, "M:Lib.Api.Run(System.Action)", TypeInitializerTreatment.Run);
        var gates = off.Run!.Heap!.Regions.Values.Where(region => region.TypeKey == ASSEMBLY + ":Lib.Gate").ToArray();
        Assert.NotEmpty(gates);
        Assert.All(gates, gate => Assert.True(off.Run.Executions!.IsSingleObject(gate.Identity)));
        Assert.Equal(run.Answer.Classified, off.Answer.Classified);
        Assert.NotEmpty(off.Run.Heap.Instances.Values.SelectMany(instance => instance.Summary.Locks));
        var offProtected = off.Run.Collection!.Accesses.Where(access => access.Resource.Member.Name == "Count" && access.HeldProtectionIds.Count != 0).ToArray();
        var runProtected = run.Run!.Collection!.Accesses.Where(access => access.Resource.Member.Name == "Count" && access.HeldProtectionIds.Count != 0).ToArray();
        Assert.NotEmpty(runProtected);
        Assert.Equal(runProtected.Length, offProtected.Length);
        Assert.All(offProtected, access => Assert.Contains(access.HeldProtectionIds, id => gates.Any(gate => id.Contains(gate.Identity, StringComparison.Ordinal))));
    }

    [Fact]
    public void A_store_through_an_unseeded_static_gives_incomplete()
    {
        var trace = Generate("public sealed class Value { private Value() { } } public sealed class Input { public Input() { _ = Api.X; } } " +
                             "public static class Api { public static Value X; public static void Run(Input input, Action a) { X = null; a(); } }",
                             "M:Lib.Api.Run(Lib.Input,System.Action)", TypeInitializerTreatment.Off);
        AssertUnseeded(trace);
    }

    [Fact]
    public void A_static_the_off_copy_cannot_write_is_unseeded()
    {
        var trace = Generate("""
            public sealed class Value { }
            public class Api { private static readonly Value Missing; public static Value Run() => Missing; }
            public class Derived : Api { public int Read() => Missing.Value; }
            public static class Missing { public static int Value => 1; }
            """, "M:Lib.Api.Run", TypeInitializerTreatment.Off);
        AssertUnseeded(trace);
        Assert.DoesNotContain(trace.Driver!.RecipeStatements, statement => statement.Operation.Syntax.ToString().Contains("Api.Missing ="));
    }

    [Fact]
    public void A_whole_struct_stored_through_a_ref_to_an_unseeded_static_gives_incomplete()
    {
        var trace = Generate("""
            public struct Pair { public object Value; }
            public sealed class Input { public Input() { _ = Api.X; } }
            public static class Api { public static Pair X; public static ref Pair Ref() => ref X;
                public static void Run(Input input, Action a) { ref var place = ref Ref(); place = default; a(); } }
            """, "M:Lib.Api.Run(Lib.Input,System.Action)", TypeInitializerTreatment.Off);
        AssertUnseeded(trace);
    }

    private static bool InitializerStartsWork(GenerationTrace trace) =>
        trace.Run!.Executions!.Executions.Any(execution => execution.Kind is ConcurrencyHunter.Execution.ExecutionKind.Spawn or
            ConcurrencyHunter.Execution.ExecutionKind.TimerCallback && execution.ParentId is { } parent &&
            trace.Run.Executions.Executions.Any(owner => owner.Id == parent && owner.Kind == ConcurrencyHunter.Execution.ExecutionKind.TypeInitializer));

    private static void AssertUnseeded(GenerationTrace trace)
    {
        Assert.Equal("off", trace.Answer.Generation.Mode);
        Assert.Null(trace.Answer.Reason);
        Assert.Equal(ModelReasons.INCOMPLETE, trace.Answer.ModelReason);
        Assert.Contains(trace.Answer.Causes, cause => cause.Code == ModelCauses.UNSEEDED_READ);
        Assert.Null(trace.Answer.Model);
    }

    private static GenerationTrace Generate(string types, string member, TypeInitializerTreatment? initializers = null) =>
        ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member, null, null), Compile(PRELUDE + types + "}"),
                             CancellationToken.None, initializers: initializers);

    private static string Huge()
    {
        var source = new StringBuilder("public static class Huge { public static void All() {");
        for (var i = 0; i < 1501; i++)
            source.Append($" M{i}();");
        source.Append(" }");
        for (var i = 0; i < 1501; i++)
            source.Append($" private static void M{i}() {{ }}");
        return source.Append(" }").ToString();
    }
}
