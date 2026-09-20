using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Effects on a value, lifted to the call site (ADR 0009): a wrapper whose releaser frees a modelled primitive proves
/// protection on that primitive's region and on nothing else, and a callee that waits on a handle it was handed orders its
/// caller.</summary>
public sealed class LiftedEffectTests
{
    private const string Primitives = """
        public sealed class AsyncLock
        {
            private readonly SemaphoreSlim _semaphore;
            public AsyncLock(SemaphoreSlim semaphore) => _semaphore = semaphore;
            public Releaser Lock() { _semaphore.Wait(); return new Releaser(_semaphore); }
            public async Task<Releaser> LockAsync() { await _semaphore.WaitAsync(); return new Releaser(_semaphore); }
            public async Task<Releaser> TryLockAsync() { await _semaphore.WaitAsync(TimeSpan.FromSeconds(1)); return new Releaser(_semaphore); }
        }

        public sealed class Releaser : IDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
            public void Dispose() => _semaphore.Release();
        }

        public sealed class LeakyLock
        {
            private readonly SemaphoreSlim _semaphore;
            public LeakyLock(SemaphoreSlim semaphore) => _semaphore = semaphore;
            public LeakyReleaser Lock() { _semaphore.Wait(); return new LeakyReleaser(); }
        }

        public sealed class LeakyReleaser : IDisposable
        {
            public void Dispose() { }
        }

        public sealed class MutexLock
        {
            private readonly Mutex _mutex;
            public MutexLock(Mutex mutex) => _mutex = mutex;
            public MutexReleaser Lock() { _mutex.WaitOne(); return new MutexReleaser(_mutex); }
        }

        public sealed class MutexReleaser : IDisposable
        {
            private readonly Mutex _mutex;
            public MutexReleaser(Mutex mutex) => _mutex = mutex;
            public void Dispose() => _mutex.ReleaseMutex();
        }

        public sealed class ConditionalLock
        {
            private readonly SemaphoreSlim _semaphore;
            public ConditionalLock(SemaphoreSlim semaphore) => _semaphore = semaphore;
            public ConditionalReleaser Lock() { _semaphore.Wait(); return new ConditionalReleaser(_semaphore); }
        }

        public sealed class ConditionalReleaser : IDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            public ConditionalReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
            public void Dispose() { if (_semaphore.CurrentCount == 0) _semaphore.Release(); }
        }

        public interface IGuard { IDisposable Enter(); }

        public sealed class RealGuard : IGuard
        {
            private readonly SemaphoreSlim _semaphore;
            public RealGuard(SemaphoreSlim semaphore) => _semaphore = semaphore;
            public IDisposable Enter() { _semaphore.Wait(); return new Releaser(_semaphore); }
        }

        public sealed class OpenGuard : IGuard
        {
            public IDisposable Enter() => new LeakyReleaser();
        }

        public sealed class CustomGate
        {
            public void Acquire() { }
            public void Free() { }
        }

        public sealed class CustomLock
        {
            private readonly CustomGate _gate;
            public CustomLock(CustomGate gate) => _gate = gate;
            public CustomReleaser Lock() { _gate.Acquire(); return new CustomReleaser(_gate); }
        }

        public sealed class CustomReleaser : IDisposable
        {
            private readonly CustomGate _gate;
            public CustomReleaser(CustomGate gate) => _gate = gate;
            public void Dispose() => _gate.Free();
        }

        public sealed class Holder
        {
            private static int Limit => 4;

            public readonly SemaphoreSlim One = new(1, 1);
            public readonly SemaphoreSlim Other = new(1, 1);
            public readonly SemaphoreSlim Unproven = new(Limit, Limit);
            public readonly Mutex Mtx = new();
            public readonly CustomGate Gate = new();
            public readonly AsyncLock Lock;
            public readonly AsyncLock Same;
            public readonly AsyncLock Over;
            public readonly AsyncLock Unknown;
            public readonly LeakyLock Leaky;
            public readonly MutexLock Owned;
            public readonly CustomLock Custom;
            public readonly ConditionalLock Conditional;
            public readonly IGuard Guard;
            public int Value;

            public Holder()
            {
                Lock = new AsyncLock(One);
                Same = new AsyncLock(One);
                Over = new AsyncLock(Other);
                Unknown = new AsyncLock(Unproven);
                Leaky = new LeakyLock(One);
                Owned = new MutexLock(Mtx);
                Custom = new CustomLock(Gate);
                Conditional = new ConditionalLock(One);
                Guard = One.CurrentCount == 1 ? new RealGuard(One) : (IGuard)new OpenGuard();
            }
        }

        """;

    private const string Registrations =
        "services.AddSingleton<Holder>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();";

    [Fact]
    public async Task A_wrapper_over_a_semaphore_of_capacity_one_proves_protection() =>
        Assert.Equal(PairProtection.SUFFICIENT, await Verdict("using (_holder.Lock.Lock()) { _holder.Value = 1; }",
                                                              "using (_holder.Lock.Lock()) { _holder.Value = 2; }"));

    /// <summary>ADR 0009: a capacity nothing proves gives no protection at all, not a weaker one.</summary>
    [Fact]
    public async Task A_wrapper_over_a_semaphore_of_an_unproven_capacity_proves_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED, await Verdict("using (_holder.Unknown.Lock()) { _holder.Value = 1; }",
                                                               "using (_holder.Unknown.Lock()) { _holder.Value = 2; }"));

    /// <summary>Identity is the primitive's region, never the wrapper's, so two wrappers over one semaphore do exclude.</summary>
    [Fact]
    public async Task Two_wrappers_over_one_semaphore_protect_each_other() =>
        Assert.Equal(PairProtection.SUFFICIENT, await Verdict("using (_holder.Lock.Lock()) { _holder.Value = 1; }",
                                                              "using (_holder.Same.Lock()) { _holder.Value = 2; }"));

    [Fact]
    public async Task Wrappers_over_two_semaphores_are_different_identity() =>
        Assert.Equal(PairProtection.DIFFERENT_IDENTITY, await Verdict("using (_holder.Lock.Lock()) { _holder.Value = 1; }",
                                                                      "using (_holder.Over.Lock()) { _holder.Value = 2; }"));

    /// <summary>A releaser that frees nothing leaves the scope unproven, so the entry it handed out is not protection.</summary>
    [Fact]
    public async Task A_releaser_that_releases_nothing_proves_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED, await Verdict("using (_holder.Leaky.Lock()) { _holder.Value = 1; }",
                                                               "using (_holder.Leaky.Lock()) { _holder.Value = 2; }"));

    /// <summary>A must-effect is what every callee does: a call that may run an implementation which acquires nothing carries
    /// nothing out of itself, whatever the implementation beside it acquires (ADR 0009).</summary>
    [Fact]
    public async Task A_call_one_of_whose_implementations_acquires_nothing_proves_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED, await Verdict("using (_holder.Guard.Enter()) { _holder.Value = 1; }",
                                                               "using (_holder.Guard.Enter()) { _holder.Value = 2; }"));

    /// <summary>The exit a call carries back has to be a release on every path out of the callee, not a release standing somewhere
    /// in it: a `Dispose` that branches over its release leaves the scope open on the other branch (R3, ADR 0009).</summary>
    [Fact]
    public async Task A_releaser_that_releases_on_one_branch_proves_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED, await Verdict("using (_holder.Conditional.Lock()) { _holder.Value = 1; }",
                                                               "using (_holder.Conditional.Lock()) { _holder.Value = 2; }"));

    [Fact]
    public async Task A_wrapper_over_a_type_the_analysis_does_not_model_proves_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED, await Verdict("using (_holder.Custom.Lock()) { _holder.Value = 1; }",
                                                               "using (_holder.Custom.Lock()) { _holder.Value = 2; }"));

    /// <summary>The wrapper takes none of R3 away: a mutex belongs to the thread that took it, wrapper or no wrapper.</summary>
    [Fact]
    public async Task A_wrapper_over_a_mutex_held_over_an_await_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("using (_holder.Owned.Lock()) { await Task.Yield(); _holder.Value = 1; }",
                                   "using (_holder.Owned.Lock()) { _holder.Value = 2; }", isAsync: true));

    [Fact]
    public async Task A_wrapper_over_a_semaphore_held_over_an_await_stays_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("using (_holder.Lock.Lock()) { await Task.Yield(); _holder.Value = 1; }",
                                   "using (_holder.Lock.Lock()) { _holder.Value = 2; }", isAsync: true));

    /// <summary>A measured limit, pinned here so that lifting it fails loudly: the heap does not follow an async method's result
    /// to the object it returns, so the disposal of a scope handed out that way names no callee at all, the exit is not proven,
    /// and the entry it carried is therefore not protection. The same wrapper taken synchronously does prove it.</summary>
    [Fact]
    public async Task An_awaited_wrapper_proves_nothing_until_an_async_result_is_followed() =>
        Assert.Equal(PairProtection.UNPROTECTED,
                     await Verdict("using (await _holder.Lock.LockAsync()) { _holder.Value = 1; }",
                                   "using (await _holder.Lock.LockAsync()) { _holder.Value = 2; }", isAsync: true));

    /// <summary>The entry inside the wrapper is as conditional as anywhere: a timed wait whose result nobody read proves no
    /// entry, so the wrapper hands out a scope over nothing (TD-083).</summary>
    [Fact]
    public async Task A_wrapper_whose_timed_wait_is_unchecked_proves_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED,
                     await Verdict("using (await _holder.Lock.TryLockAsync()) { _holder.Value = 1; }",
                                   "using (await _holder.Lock.TryLockAsync()) { _holder.Value = 2; }", isAsync: true));

    /// <summary>`using` is not what proves anything: the same disposal written by hand proves the same.</summary>
    [Fact]
    public async Task A_hand_written_try_finally_proves_what_using_proves() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("var releaser = _holder.Lock.Lock(); try { _holder.Value = 1; } finally { releaser.Dispose(); }",
                                   "using (_holder.Lock.Lock()) { _holder.Value = 2; }"));

    [Fact]
    public async Task A_disposal_that_is_not_on_every_path_proves_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED,
                     await Verdict("var releaser = _holder.Lock.Lock(); _holder.Value = 1; releaser.Dispose();",
                                   "var releaser = _holder.Lock.Lock(); _holder.Value = 2; releaser.Dispose();"));

    /// <summary>R4: the handle is the caller's, and the callee waits for it on every path, so the call is that wait.</summary>
    [Fact]
    public async Task A_join_on_a_parameter_orders_the_caller()
    {
        var result = await Analyze("""
            public sealed class JoinWorker : BackgroundService
            {
                private int _value;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var task = Task.Run(() => { _value = 1; });
                    Await(task);
                    _value = 2;
                    return Task.CompletedTask;
                }
                private static void Await(Task handle) => handle.Wait();
            }
            """ + Startup("services.AddHostedService<JoinWorker>();"));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task A_join_on_a_parameter_passed_inline_orders_the_caller()
    {
        var result = await Analyze("""
            public sealed class InlineJoinWorker : BackgroundService
            {
                private int _value;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    Await(Task.Run(() => { _value = 1; }));
                    _value = 2;
                    return Task.CompletedTask;
                }
                private static void Await(Task handle) => handle.Wait();
            }
            """ + Startup("services.AddHostedService<InlineJoinWorker>();"));

        Assert.Empty(result.Findings);
    }

    /// <summary>A callee that waits on one branch does not wait on every path, so its caller keeps its race.</summary>
    [Fact]
    public async Task A_join_on_a_parameter_inside_a_condition_does_not_order_the_caller()
    {
        var result = await Analyze("""
            public sealed class MaybeJoinWorker : BackgroundService
            {
                private int _value;
                public bool Flag { get; set; }
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var task = Task.Run(() => { _value = 1; });
                    Await(task, Flag);
                    _value = 2;
                    return Task.CompletedTask;
                }
                private static void Await(Task handle, bool wait) { if (wait) handle.Wait(); }
            }
            """ + Startup("services.AddHostedService<MaybeJoinWorker>();"));

        Assert.NotEmpty(result.Findings);
    }

    /// <summary>A handle the analysis cannot name is not proven, however faithfully the callee waits for it.</summary>
    [Fact]
    public async Task A_join_on_a_parameter_of_an_unproven_handle_does_not_order_the_caller()
    {
        var result = await Analyze("""
            public sealed class OpaqueJoinWorker : BackgroundService
            {
                private int _value;
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var task = Task.Run(() => { _value = 1; });
                    Await(Choose(task));
                    _value = 2;
                    return Task.CompletedTask;
                }
                private static Task Choose(Task handle) => Task.WhenAny(handle);
                private static void Await(Task handle) => handle.Wait();
            }
            """ + Startup("services.AddHostedService<OpaqueJoinWorker>();"));

        Assert.NotEmpty(result.Findings);
    }

    /// <summary>The phase 3 shape, where the handles live in the receiver's fields, keeps the verdicts it was written for: the
    /// callee that waits on every path orders, the one that waits on a branch does not.</summary>
    [Fact]
    public async Task A_callee_that_joins_on_fields_keeps_its_verdicts()
    {
        var result = await Analyze("""
            public sealed class FieldJoinWorker : BackgroundService
            {
                private Task? _always;
                private Task? _maybe;
                private int _ordered;
                private int _raced;
                public bool Flag { get; set; }

                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    _always = Task.Run(() => { _ordered = 1; });
                    _maybe = Task.Run(() => { _raced = 1; });
                    Run();
                    _ordered = 2;
                    _raced = 2;
                    return Task.CompletedTask;
                }

                private void Run()
                {
                    _always!.Wait();
                    if (Flag)
                        _maybe!.Wait();
                }
            }
            """ + Startup("services.AddHostedService<FieldJoinWorker>();"));

        Assert.DoesNotContain(result.Findings, finding => finding.Resource.AccessPath.SequenceEqual(["_ordered"]));
        Assert.Contains(result.Findings, finding => finding.Resource.AccessPath.SequenceEqual(["_raced"]));
    }

    private static string Worker(string name, string body, bool isAsync) => $$"""
        public sealed class {{name}}Worker : BackgroundService
        {
            private readonly Holder _holder;
            public {{name}}Worker(Holder holder) => _holder = holder;
            protected override {{(isAsync ? "async Task" : "Task")}} ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                {{(isAsync ? "" : "return Task.CompletedTask;")}}
            }
        }
        """;

    /// <summary>The verdict of the one pair the two workers make on the guarded field; no finding means the protection was
    /// sufficient and took the candidate away.</summary>
    private static async Task<string> Verdict(string first, string second, bool isAsync = false)
    {
        var result = await Analyze(Primitives + Worker("First", first, isAsync) + Worker("Second", second, isAsync) +
                                   Startup(Registrations));
        var onValue = result.Findings.Where(finding => finding.Resource.AccessPath.SequenceEqual(["Value"])).ToArray();
        if (onValue.Length != 0)
            return Assert.Single(onValue).ProtectionResult;

        Assert.True(result.Pairs.Suppressed > 0, "The two writes made no pair at all.");
        return PairProtection.SUFFICIENT;
    }

    private static async Task<AnalysisResult> Analyze(string source) =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY,
                                            ProviderRegistry.BuiltIn, CancellationToken.None);
}
