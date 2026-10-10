using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Work the engine starts itself — a spawn's work, a timer's callback, a delegate handed to an opaque call — that resolves to a
/// member without a body: an unresolved call of that member in the execution running the work, with the unknown effect on what its
/// receiver reaches and on what the work is handed (question 145).</summary>
public sealed class BodylessWorkTests
{
    [Fact]
    public void Task_run_of_a_method_group_without_a_body_touches_its_receiver_in_the_spawn()
    {
        var run = Run("Task.Run(_state.Touch);", other: "_ = _state.BaseValue;");

        Assert.Contains(run.Of("BaseValue"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.DoesNotContain(run.Of("BaseValue"), access => access.Operation.IsUnknownEffect() && KindOf(run, access) != ExecutionKind.Spawn);
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Kind == SemanticGapKinds.UNRESOLVED_DISPATCH && gap.Callee.Contains("Touch"));
        Assert.NotEmpty(run.PairsOn("BaseValue"));
    }

    [Fact]
    public void Object_the_caller_creates_and_reads_after_the_spawn_is_shared_with_the_work()
    {
        var run = Run("var local = new State(); Task.Run(local.Touch); _ = local.BaseValue;");

        Assert.Contains(run.Of("BaseValue"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.NotEmpty(run.PairsOn("BaseValue"));
    }

    [Fact]
    public void Queued_static_method_group_without_a_body_touches_its_state()
    {
        var run = Run("ThreadPool.QueueUserWorkItem(Native.Touch, _state, false);", other: "_ = _state.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.NotEmpty(run.PairsOn("Count"));
    }

    [Fact]
    public void Work_item_whose_execute_has_no_body_touches_the_item()
    {
        var run = Run("ThreadPool.UnsafeQueueUserWorkItem(_state.Job, false);", other: "_ = _state.Job.Done;");

        Assert.Contains(run.Of("Done"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.NotEmpty(run.PairsOn("Done"));
    }

    [Fact]
    public void Thread_started_on_a_method_without_a_body_touches_its_receiver()
    {
        var run = Run("new Thread(_state.Touch).Start();", other: "_ = _state.BaseValue;");

        Assert.Contains(run.Of("BaseValue"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
    }

    [Fact]
    public void Parallel_body_without_a_body_touches_the_local_value_its_local_init_returns()
    {
        var run = Run("Parallel.For(0, 4, () => _state, Native.Step, local => { });", other: "_ = _state.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
    }

    [Fact]
    public void Timer_callback_without_a_body_touches_its_state()
    {
        var run = Run("var timer = new Timer(Native.Tick, _state, 0, 1000);", other: "_ = _state.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.TimerCallback);
        Assert.NotEmpty(run.PairsOn("Count"));
    }

    [Fact]
    public void Method_group_without_a_body_handed_to_an_opaque_call_touches_its_receiver_in_an_unknown_execution()
    {
        var run = Run("Opaque.Lib.Run(_state.Touch);", other: "_ = _state.BaseValue;");

        Assert.Contains(run.Of("BaseValue"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.UnknownDelegateCall);
        Assert.NotEmpty(run.PairsOn("BaseValue"));
    }

    [Fact]
    public void Lock_held_where_the_work_starts_does_not_protect_the_work()
    {
        var run = Run("lock (_state.Gate) { Task.Run(_state.Touch); }", other: "lock (_state.Gate) { _ = _state.BaseValue; }");

        Assert.Contains(run.Of("BaseValue"), access => access.Operation == AccessOperation.UnknownEffect && access.HeldProtection.Count == 0);
        Assert.NotEmpty(run.PairsOn("BaseValue"));
    }

    [Fact]
    public void Continuation_without_a_body_touches_its_state()
    {
        var run = Run("Task.CompletedTask.ContinueWith(Native.After, _state);", other: "_ = _state.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
    }

    [Fact]
    public void Parallel_local_finally_without_a_body_touches_what_local_init_and_the_body_return()
    {
        var run = Run("Parallel.For(0, 4, () => _state, (index, loop, local) => local, Native.Finally);", other: "_ = _state.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
    }

    [Fact]
    public void Work_item_of_two_types_runs_the_one_with_a_body_and_touches_the_other()
    {
        var run = Run("IThreadPoolWorkItem item = _state.Count > 0 ? _state.Job : _state.GoodJob; ThreadPool.UnsafeQueueUserWorkItem(item, false);",
                      other: "_ = _state.Job.Done; _ = _state.GoodJob.Runs;");

        Assert.Contains(run.Of("Runs"), access => access.Operation == AccessOperation.ReadModifyWrite && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.Contains(run.Of("Done"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
    }

    [Fact]
    public void Library_method_group_on_a_library_object_touches_the_array_it_is_handed()
    {
        var run = Run("ThreadPool.QueueUserWorkItem(Opaque.Noise.Shared.Fill, _state.Bytes, false);", other: "_ = _state.Bytes[0];");

        Assert.Contains(run.Execution.Heap.Heap.UnresolvedWork, work => work.Site.Callee.Contains("Fill"));
        Assert.Contains(run.Collection.Accesses, access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.NotEmpty(run.Pairs.Pairs);
    }

    [Fact]
    public void Synchronous_call_of_a_library_method_group_touches_what_it_is_handed()
    {
        var run = Run("Action<byte[]> fill = Opaque.Noise.Shared.Fill; fill(_state.Bytes);", other: "_ = _state.Bytes[0];");

        Assert.Contains(run.Collection.Accesses, access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Root);
        Assert.NotEmpty(run.Pairs.Pairs);
    }

    [Fact]
    public void Lambda_work_calling_a_member_without_a_body_keeps_its_effect_in_the_spawn()
    {
        var run = Run("Task.Run(() => Native.Touch(_state));", other: "_ = _state.Count;");

        Assert.Contains(run.Of("Count"), access => access.Operation == AccessOperation.UnknownEffect && KindOf(run, access) == ExecutionKind.Spawn);
    }

    // ---- helpers ----

    private static ExecutionKind KindOf(EngineRun run, Access access) => run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    private static EngineRun Run(string work = "", string other = "", string controller = "public int Get() => 0;") =>
        AnalyzeScope(FixtureSolution.Create(new FixtureOptions { MetadataReferences = [OpaqueLibrary.Value] },
                                            ("Case.cs", Source(work, other, controller))),
                     "scope:Fixture");

    private static string Source(string work, string other, string controller) => Usings + $$"""
        using System.Runtime.CompilerServices;

        public class Base
        {
            public int BaseValue;
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Touch();
        }

        public sealed class State : Base
        {
            public int Count;
            public readonly byte[] Bytes = new byte[16];
            public readonly object Gate = new();
            public readonly Job Job = new();
            public readonly GoodJob GoodJob = new();
        }

        public sealed class Job : IThreadPoolWorkItem
        {
            public int Done;
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Execute();
        }

        public sealed class GoodJob : IThreadPoolWorkItem
        {
            public int Runs;
            public void Execute() => Runs++;
        }

        public static class Native
        {
            [MethodImpl(MethodImplOptions.InternalCall)]
            public static extern void Touch(State state);
            [MethodImpl(MethodImplOptions.InternalCall)]
            public static extern void Tick(object? state);
            [MethodImpl(MethodImplOptions.InternalCall)]
            public static extern State Step(int index, ParallelLoopState loop, State local);
            [MethodImpl(MethodImplOptions.InternalCall)]
            public static extern void After(Task antecedent, object? state);
            [MethodImpl(MethodImplOptions.InternalCall)]
            public static extern void Finally(State local);
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
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
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>();");

    /// <summary>A library the run has no source of: <c>Lib.Run</c> is an opaque call the library table does not describe.</summary>
    private static readonly Lazy<MetadataReference> OpaqueLibrary = new(() =>
    {
        var compilation = CSharpCompilation.Create("Opaque", [CSharpSyntaxTree.ParseText("""
            namespace Opaque;
            public static class Lib
            {
                public static void Run(System.Action work) { }
            }

            public class Noise
            {
                public static Noise Shared { get; } = new();
                public virtual void Fill(byte[] data) { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
