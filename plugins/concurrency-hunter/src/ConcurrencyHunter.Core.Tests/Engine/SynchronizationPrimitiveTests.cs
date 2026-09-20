using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The synchronization primitives of TD-080 and TD-083: which entry holds what, where it starts holding it, what an
/// exceptional path leaves held, and which primitive survives a suspension point.</summary>
public sealed class SynchronizationPrimitiveTests
{
    private const string Shared = """
        public sealed class Guarded
        {
            public readonly object Gate = new();
            public readonly object Other = new();
            public readonly Lock Slim = new();
            public readonly Mutex Mtx = new();
            public readonly Mutex Second = new();
            public readonly SemaphoreSlim One = new(1, 1);
            public readonly SemaphoreSlim Two = new(2, 2);
            public readonly SemaphoreSlim Loose = new(1, 2);
            public readonly SemaphoreSlim Unknown = new(Limit, Limit);
            public readonly ReaderWriterLockSlim Rw = new();
            public int Value;
            private static int Limit => 3;
        }

        """;

    private const string Registrations =
        "services.AddSingleton<Guarded>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();";

    [Fact]
    public async Task Monitor_held_by_both_sides_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT, await Verdict("lock (_guarded.Gate) { _guarded.Value = 1; }",
                                                              "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_on_two_objects_is_different_identity() =>
        Assert.Equal(PairProtection.DIFFERENT_IDENTITY, await Verdict("lock (_guarded.Gate) { _guarded.Value = 1; }",
                                                                      "lock (_guarded.Other) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Lock_scope_held_by_both_sides_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT, await Verdict("using (_guarded.Slim.EnterScope()) { _guarded.Value = 1; }",
                                                              "using (_guarded.Slim.EnterScope()) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Lock_left_before_the_write_protects_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED, await Verdict("using (_guarded.Slim.EnterScope()) { } _guarded.Value = 1;",
                                                               "using (_guarded.Slim.EnterScope()) { } _guarded.Value = 2;"));

    [Fact]
    public async Task Mutex_held_by_both_sides_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("_guarded.Mtx.WaitOne(); try { _guarded.Value = 1; } finally { _guarded.Mtx.ReleaseMutex(); }",
                                   "_guarded.Mtx.WaitOne(); try { _guarded.Value = 2; } finally { _guarded.Mtx.ReleaseMutex(); }"));

    [Fact]
    public async Task Mutex_released_before_the_write_protects_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED,
                     await Verdict("_guarded.Mtx.WaitOne(); _guarded.Mtx.ReleaseMutex(); _guarded.Value = 1;",
                                   "_guarded.Mtx.WaitOne(); _guarded.Mtx.ReleaseMutex(); _guarded.Value = 2;"));

    [Fact]
    public async Task Semaphore_of_capacity_one_held_by_both_sides_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("_guarded.One.Wait(); try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "_guarded.One.Wait(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }"));

    [Fact]
    public async Task Semaphore_of_two_different_objects_is_different_identity() =>
        Assert.Equal(PairProtection.DIFFERENT_IDENTITY,
                     await Verdict("_guarded.One.Wait(); try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "_guarded.Two.Wait(); try { _guarded.Value = 2; } finally { _guarded.Two.Release(); }"));

    [Fact]
    public async Task Reader_writer_lock_write_held_by_both_sides_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("_guarded.Rw.EnterWriteLock(); try { _guarded.Value = 1; } finally { _guarded.Rw.ExitWriteLock(); }",
                                   "_guarded.Rw.EnterWriteLock(); try { _guarded.Value = 2; } finally { _guarded.Rw.ExitWriteLock(); }"));

    [Fact]
    public async Task Reader_writer_lock_exited_before_the_write_protects_nothing() =>
        Assert.Equal(PairProtection.UNPROTECTED,
                     await Verdict("_guarded.Rw.EnterWriteLock(); _guarded.Rw.ExitWriteLock(); _guarded.Value = 1;",
                                   "_guarded.Rw.EnterWriteLock(); _guarded.Rw.ExitWriteLock(); _guarded.Value = 2;"));

    /// <summary>The standard pattern: the flag exists for the release in `finally`, and after a normal return it is always true,
    /// so no branch over it may be demanded (TD-083).</summary>
    [Fact]
    public async Task Monitor_enter_with_a_taken_flag_in_the_standard_pattern_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("var taken = false; try { Monitor.Enter(_guarded.Gate, ref taken); _guarded.Value = 1; } " +
                                   "finally { if (taken) Monitor.Exit(_guarded.Gate); }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_enter_with_a_taken_flag_and_no_branch_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("var taken = false; Monitor.Enter(_guarded.Gate, ref taken); _guarded.Value = 1; Monitor.Exit(_guarded.Gate);",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_try_enter_checked_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("if (Monitor.TryEnter(_guarded.Gate)) { try { _guarded.Value = 1; } finally { Monitor.Exit(_guarded.Gate); } }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_try_enter_written_outside_its_branch_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("if (Monitor.TryEnter(_guarded.Gate)) { Monitor.Exit(_guarded.Gate); } _guarded.Value = 1;",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_try_enter_with_an_ignored_result_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("Monitor.TryEnter(_guarded.Gate); _guarded.Value = 1; Monitor.Exit(_guarded.Gate);",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_try_enter_with_a_taken_flag_is_sufficient_on_its_true_branch() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("var taken = false; Monitor.TryEnter(_guarded.Gate, ref taken); " +
                                   "if (taken) { try { _guarded.Value = 1; } finally { Monitor.Exit(_guarded.Gate); } }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_try_enter_with_a_taken_flag_holds_nothing_on_its_false_branch() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("var taken = false; Monitor.TryEnter(_guarded.Gate, ref taken); if (!taken) { _guarded.Value = 1; }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Monitor_try_enter_with_an_unchecked_taken_flag_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("var taken = false; Monitor.TryEnter(_guarded.Gate, ref taken); _guarded.Value = 1;",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }"));

    [Fact]
    public async Task Semaphore_wait_with_a_timeout_checked_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("if (_guarded.One.Wait(TimeSpan.FromSeconds(1))) { try { _guarded.Value = 1; } finally { _guarded.One.Release(); } }",
                                   "_guarded.One.Wait(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }"));

    [Fact]
    public async Task Semaphore_wait_with_a_timeout_unchecked_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.One.Wait(TimeSpan.FromSeconds(1)); try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "_guarded.One.Wait(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }"));

    [Fact]
    public async Task Mutex_wait_one_with_a_timeout_checked_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("if (_guarded.Mtx.WaitOne(TimeSpan.FromSeconds(1))) { try { _guarded.Value = 1; } finally { _guarded.Mtx.ReleaseMutex(); } }",
                                   "_guarded.Mtx.WaitOne(); try { _guarded.Value = 2; } finally { _guarded.Mtx.ReleaseMutex(); }"));

    [Fact]
    public async Task Mutex_wait_one_with_a_timeout_unchecked_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.Mtx.WaitOne(TimeSpan.FromSeconds(1)); try { _guarded.Value = 1; } finally { _guarded.Mtx.ReleaseMutex(); }",
                                   "_guarded.Mtx.WaitOne(); try { _guarded.Value = 2; } finally { _guarded.Mtx.ReleaseMutex(); }"));

    [Fact]
    public async Task Awaited_wait_async_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("await _guarded.One.WaitAsync(); try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "await _guarded.One.WaitAsync(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                   isAsync: true));

    /// <summary>The entry is taken where the result is actually awaited, not only where `WaitAsync` is written: a task put in a
    /// local and awaited later is the same task, and what it means travels with it into that local (R3).</summary>
    [Fact]
    public async Task A_wait_async_awaited_through_a_local_is_still_the_entry() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("var pending = _guarded.One.WaitAsync(); await pending; " +
                                   "try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "await _guarded.One.WaitAsync(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                   isAsync: true));

    [Fact]
    public async Task Awaited_wait_async_with_a_timeout_checked_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("if (await _guarded.One.WaitAsync(TimeSpan.FromSeconds(1))) { try { _guarded.Value = 1; } finally { _guarded.One.Release(); } }",
                                   "await _guarded.One.WaitAsync(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                   isAsync: true));

    [Fact]
    public async Task Awaited_wait_async_with_a_timeout_holds_nothing_on_its_false_branch() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("if (!await _guarded.One.WaitAsync(TimeSpan.FromSeconds(1))) { _guarded.Value = 1; }",
                                   "await _guarded.One.WaitAsync(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                   isAsync: true));

    [Fact]
    public async Task Awaited_wait_async_with_a_timeout_and_an_ignored_result_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("await _guarded.One.WaitAsync(TimeSpan.FromSeconds(1)); try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "await _guarded.One.WaitAsync(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                   isAsync: true));

    /// <summary>A wait nobody awaits has not been waited for: the semaphore is not held.</summary>
    [Fact]
    public async Task Unawaited_wait_async_holds_nothing() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_ = _guarded.One.WaitAsync(); _guarded.Value = 1;",
                                   "await _guarded.One.WaitAsync(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                   isAsync: true));

    /// <summary>A wait that ends in cancellation took nothing, so the write in the handler is unprotected and the release there is
    /// unpaired.</summary>
    [Fact]
    public async Task A_release_on_the_cancellation_path_holds_nothing()
    {
        var verdict = await Verdict("try { await _guarded.One.WaitAsync(_token); } catch (OperationCanceledException) " +
                                    "{ _guarded.Value = 1; _guarded.One.Release(); }",
                                    "await _guarded.One.WaitAsync(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                    isAsync: true);

        Assert.Equal(PairProtection.PARTIAL, verdict);
    }

    /// <summary>An abandoned mutex is still owned by the thread that waited on it, so its handler holds it (TD-083).</summary>
    [Fact]
    public async Task An_abandoned_mutex_is_held_in_its_handler() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("try { _guarded.Mtx.WaitOne(); _guarded.Mtx.ReleaseMutex(); } " +
                                   "catch (AbandonedMutexException) { _guarded.Value = 1; _guarded.Mtx.ReleaseMutex(); }",
                                   "_guarded.Mtx.WaitOne(); try { _guarded.Value = 2; } finally { _guarded.Mtx.ReleaseMutex(); }"));

    /// <summary>The handler owns the one mutex whose wait threw, and any wait of the guarded region may be that one, so it holds
    /// only what every one of those paths holds. A mutex a later wait would have taken is not among them: the wait that threw came
    /// first and the later one never ran (TD-083).</summary>
    [Fact]
    public async Task An_abandoned_mutex_handler_does_not_hold_a_mutex_a_later_wait_would_take() =>
        Assert.Equal(PairProtection.DIFFERENT_IDENTITY,
                     await Verdict("try { _guarded.Mtx.WaitOne(); _guarded.Second.WaitOne(); } " +
                                   "catch (AbandonedMutexException) { _guarded.Value = 1; }",
                                   "_guarded.Second.WaitOne(); try { _guarded.Value = 2; } finally { _guarded.Second.ReleaseMutex(); }"));

    /// <summary>The `lock` statement over a `System.Threading.Lock` is that lock's own section, and a monitor taken on the same
    /// object is the other mechanism of TD-083: one object, two mechanisms, neither excluding the other. The compiler rewrites
    /// this statement into nothing the graph carries, so the section is read from the statement itself (R3).</summary>
    [Fact]
    public async Task A_lock_statement_over_a_lock_against_a_monitor_on_one_object_is_incompatible_mode() =>
        Assert.Equal(PairProtection.INCOMPATIBLE_MODE,
                     await Verdict("lock (_guarded.Slim) { _guarded.Value = 1; }",
                                   "lock ((object)_guarded.Slim) { _guarded.Value = 2; }"));

    [Fact]
    public async Task A_lock_statement_over_a_lock_held_by_both_sides_is_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("lock (_guarded.Slim) { _guarded.Value = 1; }",
                                   "lock (_guarded.Slim) { _guarded.Value = 2; }"));

    /// <summary>A body that branches is the same section: the statement's own span says which blocks it covers, and the exit
    /// stands on every edge that leaves them. A body with an `if` in it is the ordinary case, not the exception (R3).</summary>
    [Fact]
    public async Task A_lock_statement_whose_body_branches_still_holds_the_lock() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("lock (_guarded.Slim) { if (stoppingToken.IsCancellationRequested) _guarded.Value = 1; else _guarded.Value = 2; }",
                                   "lock (_guarded.Slim) { _guarded.Value = 3; }"));

    /// <summary>And the exit is on the way out, not at the end of a block: a write after a branching section is outside it.</summary>
    [Fact]
    public async Task A_write_after_a_lock_statement_whose_body_branches_is_not_held() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("lock (_guarded.Slim) { if (stoppingToken.IsCancellationRequested) { } } _guarded.Value = 1;",
                                   "lock (_guarded.Slim) { _guarded.Value = 2; }"));

    /// <summary>The write inside the statement's body is an access like any other: the compiler leaves it in the graph, and
    /// leaving the section behind leaves it unprotected.</summary>
    [Fact]
    public async Task A_write_after_a_lock_statement_over_a_lock_is_not_held() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("lock (_guarded.Slim) { } _guarded.Value = 1;",
                                   "lock (_guarded.Slim) { _guarded.Value = 2; }"));

    /// <summary>One object, two mechanisms: a scope of `System.Threading.Lock` and a monitor on the same object are independent,
    /// so neither excludes the other (TD-083).</summary>
    [Fact]
    public async Task A_lock_scope_against_a_monitor_on_one_object_is_incompatible_mode() =>
        Assert.Equal(PairProtection.INCOMPATIBLE_MODE,
                     await Verdict("using (_guarded.Slim.EnterScope()) { _guarded.Value = 1; }",
                                   "lock ((object)_guarded.Slim) { _guarded.Value = 2; }"));

    /// <summary>Two mechanisms on one object nest legally, and the access holds both: the holding the two sides share decides the
    /// verdict, and the one only this side took takes nothing away (TD-083).</summary>
    [Fact]
    public async Task One_object_held_by_two_mechanisms_keeps_the_holding_the_sides_share() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("using (_guarded.Slim.EnterScope()) { lock ((object)_guarded.Slim) { _guarded.Value = 1; } }",
                                   "using (_guarded.Slim.EnterScope()) { _guarded.Value = 2; }"));

    [Fact]
    public async Task A_mutex_held_over_an_await_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.Mtx.WaitOne(); try { await Task.Yield(); _guarded.Value = 1; } finally { _guarded.Mtx.ReleaseMutex(); }",
                                   "_guarded.Mtx.WaitOne(); try { _guarded.Value = 2; } finally { _guarded.Mtx.ReleaseMutex(); }",
                                   isAsync: true));

    [Fact]
    public async Task A_monitor_held_over_an_await_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("Monitor.Enter(_guarded.Gate); try { await Task.Yield(); _guarded.Value = 1; } finally { Monitor.Exit(_guarded.Gate); }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }",
                                   isAsync: true));

    [Fact]
    public async Task A_reader_writer_lock_held_over_an_await_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.Rw.EnterWriteLock(); try { await Task.Yield(); _guarded.Value = 1; } finally { _guarded.Rw.ExitWriteLock(); }",
                                   "_guarded.Rw.EnterWriteLock(); try { _guarded.Value = 2; } finally { _guarded.Rw.ExitWriteLock(); }",
                                   isAsync: true));

    /// <summary>An iterator stops at a `yield return` and is resumed by whoever enumerates it, which may be another thread. Any
    /// suspension breaks a primitive owned by the thread that took it, not only an `await` (R3, TD-083).</summary>
    [Fact]
    public async Task A_monitor_held_over_a_yield_return_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("foreach (var item in Walk()) { }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }",
                                   extra: """
                                       private System.Collections.Generic.IEnumerable<int> Walk()
                                       {
                                           Monitor.Enter(_guarded.Gate);
                                           try
                                           {
                                               yield return 1;
                                               _guarded.Value = 1;
                                           }
                                           finally
                                           {
                                               Monitor.Exit(_guarded.Gate);
                                           }
                                       }
                                       """));

    /// <summary>A semaphore belongs to no thread, so a continuation on another thread still holds it.</summary>
    [Fact]
    public async Task A_semaphore_held_over_an_await_stays_sufficient() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("_guarded.One.Wait(); try { await Task.Yield(); _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "_guarded.One.Wait(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }",
                                   isAsync: true));

    [Fact]
    public async Task A_semaphore_of_a_constant_capacity_of_one_excludes() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("_guarded.One.Wait(); try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "_guarded.One.Wait(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }"));

    [Fact]
    public async Task A_semaphore_of_a_constant_capacity_of_two_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.Two.Wait(); try { _guarded.Value = 1; } finally { _guarded.Two.Release(); }",
                                   "_guarded.Two.Wait(); try { _guarded.Value = 2; } finally { _guarded.Two.Release(); }"));

    [Fact]
    public async Task A_semaphore_of_a_capacity_from_a_parameter_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.Unknown.Wait(); try { _guarded.Value = 1; } finally { _guarded.Unknown.Release(); }",
                                   "_guarded.Unknown.Wait(); try { _guarded.Value = 2; } finally { _guarded.Unknown.Release(); }"));

    /// <summary>A release outside `finally` is skipped when the guarded code throws, so the pair is not a proven mutex (TD-083).</summary>
    [Fact]
    public async Task A_semaphore_released_outside_finally_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.One.Wait(); _guarded.Value = 1; _guarded.One.Release();",
                                   "_guarded.One.Wait(); _guarded.Value = 2; _guarded.One.Release();"));

    /// <summary>Standing inside `finally` is not what makes a release unconditional: a `finally` that branches over it leaves the
    /// semaphore held on the other branch, so the release is proven over the control flow of the `finally` or not at all (R3).</summary>
    [Fact]
    public async Task A_semaphore_released_on_one_branch_of_finally_is_partial() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.One.Wait(); try { _guarded.Value = 1; } " +
                                   "finally { if (stoppingToken.IsCancellationRequested) _guarded.One.Release(); }",
                                   "_guarded.One.Wait(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }"));

    [Fact]
    public async Task Two_read_locks_do_not_exclude_each_other() =>
        Assert.Equal(PairProtection.INCOMPATIBLE_MODE, await ReaderWriterVerdict("Read", "Read"));

    [Fact]
    public async Task A_read_lock_and_an_upgradeable_read_lock_do_not_exclude_each_other() =>
        Assert.Equal(PairProtection.INCOMPATIBLE_MODE, await ReaderWriterVerdict("Read", "UpgradeableRead"));

    [Fact]
    public async Task A_read_lock_and_a_write_lock_exclude_each_other() =>
        Assert.Equal(PairProtection.SUFFICIENT, await ReaderWriterVerdict("Read", "Write"));

    [Fact]
    public async Task Two_write_locks_exclude_each_other() =>
        Assert.Equal(PairProtection.SUFFICIENT, await ReaderWriterVerdict("Write", "Write"));

    [Fact]
    public async Task A_write_lock_and_an_upgradeable_read_lock_exclude_each_other() =>
        Assert.Equal(PairProtection.SUFFICIENT, await ReaderWriterVerdict("Write", "UpgradeableRead"));

    [Fact]
    public async Task Two_upgradeable_read_locks_exclude_each_other() =>
        Assert.Equal(PairProtection.SUFFICIENT, await ReaderWriterVerdict("UpgradeableRead", "UpgradeableRead"));

    /// <summary>One lock taken again in another mode is held in the new mode until that acquisition is left: an upgradeable reader
    /// that enters the write lock excludes a reader, and keeping the outer mode would call that write section compatible with
    /// somebody else's read (TD-083).</summary>
    [Fact]
    public async Task A_write_lock_taken_inside_an_upgradeable_read_lock_excludes_a_reader() =>
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("_guarded.Rw.EnterUpgradeableReadLock(); _guarded.Rw.EnterWriteLock(); " +
                                   "try { _guarded.Value = 1; } " +
                                   "finally { _guarded.Rw.ExitWriteLock(); _guarded.Rw.ExitUpgradeableReadLock(); }",
                                   "_guarded.Rw.EnterReadLock(); try { _guarded.Value = 2; } finally { _guarded.Rw.ExitReadLock(); }"));

    /// <summary>`SpinLock` is not modelled, so both writers under it hold nothing (TD-086).</summary>
    [Fact]
    public async Task A_spin_lock_is_no_protection()
    {
        var result = await Analyze(Shared + """
            public sealed class Spinning
            {
                private SpinLock _gate = new(enableThreadOwnerTracking: false);
                private readonly Guarded _guarded;
                public Spinning(Guarded guarded) => _guarded = guarded;
                public void Set(int value)
                {
                    var taken = false;
                    try { _gate.Enter(ref taken); _guarded.Value = value; } finally { if (taken) _gate.Exit(); }
                }
            }
            """ + Worker("First", "_spinning.Set(1);", holder: "Spinning") +
            Worker("Second", "_spinning.Set(2);", holder: "Spinning") +
            Startup("services.AddSingleton<Guarded>(); services.AddSingleton<Spinning>(); services.AddHostedService<FirstWorker>();" +
                    "services.AddHostedService<SecondWorker>();"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal(PairProtection.UNPROTECTED, finding.ProtectionResult);
        Assert.Empty(finding.AccessA.HeldProtection);
    }

    /// <summary>A scope a call leaves open is protection where nothing breaks it, and the suspension of the call that opens it
    /// breaks it: the two features hold together, so an awaited wrapper leaves the caller's section excluding nobody (TD-083,
    /// ADR 0009).</summary>
    [Fact]
    public async Task A_scope_opened_by_a_call_is_protection_until_the_call_suspends()
    {
        Assert.Equal(PairProtection.SUFFICIENT,
                     await Verdict("Enter(); try { _guarded.Value = 1; } finally { Monitor.Exit(_guarded.Gate); }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }",
                                   extra: "private void Enter() { Monitor.Enter(_guarded.Gate); }"));
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("await EnterAsync(); try { _guarded.Value = 1; } finally { Monitor.Exit(_guarded.Gate); }",
                                   "lock (_guarded.Gate) { _guarded.Value = 2; }", isAsync: true,
                                   extra: "private async Task EnterAsync() { Monitor.Enter(_guarded.Gate); await Task.Yield(); }"));
    }

    /// <summary>An exit that gives back more permits than the wait took leaves the semaphore open to a second holder, so two such
    /// sections do not exclude each other however paired the wait and the release look (R3).</summary>
    [Fact]
    public async Task A_semaphore_given_back_two_permits_is_no_mutex() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.Loose.Wait(); try { _guarded.Value = 1; } finally { _guarded.Loose.Release(2); }",
                                   "_guarded.Loose.Wait(); try { _guarded.Value = 2; } finally { _guarded.Loose.Release(2); }"));

    /// <summary>A release that pairs with no wait of the body gives back a permit nobody took, and the sections after it overlap
    /// exactly as an over-release makes them (R3).</summary>
    [Fact]
    public async Task A_semaphore_given_back_without_a_wait_is_no_mutex() =>
        Assert.Equal(PairProtection.PARTIAL,
                     await Verdict("_guarded.One.Release(); _guarded.One.Wait(); try { _guarded.Value = 1; } finally { _guarded.One.Release(); }",
                                   "_guarded.One.Wait(); try { _guarded.Value = 2; } finally { _guarded.One.Release(); }"));

    /// <summary>What a body holds on entry is what every call site of it holds, weakened to what is true on all of them: a helper
    /// called once under the write lock and once under the read lock holds the read lock, and keeping the first call site's
    /// holding would call its write section exclusive of somebody else's read (TD-083).</summary>
    [Fact]
    public async Task A_helper_called_under_two_modes_holds_the_weaker_one()
    {
        var result = await Analyze(Shared +
            Worker("First",
                   "_guarded.Rw.EnterWriteLock(); try { Store(1); } finally { _guarded.Rw.ExitWriteLock(); } " +
                   "_guarded.Rw.EnterReadLock(); try { Store(2); } finally { _guarded.Rw.ExitReadLock(); }",
                   extra: "private void Store(int value) => _guarded.Value = value;") +
            Worker("Second", "_guarded.Rw.EnterReadLock(); try { _guarded.Value = 3; } finally { _guarded.Rw.ExitReadLock(); }") +
            Startup(Registrations));

        Assert.Contains(result.Findings, finding => finding.ProtectionResult == PairProtection.INCOMPATIBLE_MODE);
    }

    private static async Task<string> ReaderWriterVerdict(string first, string second) =>
        await Verdict($"_guarded.Rw.Enter{first}Lock(); try {{ _guarded.Value = 1; }} finally {{ _guarded.Rw.Exit{first}Lock(); }}",
                      $"_guarded.Rw.Enter{second}Lock(); try {{ _guarded.Value = 2; }} finally {{ _guarded.Rw.Exit{second}Lock(); }}");

    /// <summary>The verdict of the one pair the two workers make; no finding means the protection was sufficient and took the
    /// candidate away.</summary>
    private static async Task<string> Verdict(string first, string second, bool isAsync = false, string extra = "")
    {
        var result = await Analyze(Shared + Worker("First", first, isAsync, extra: extra) + Worker("Second", second, isAsync) +
                                   Startup(Registrations));
        if (result.Findings.Count != 0)
            return Assert.Single(result.Findings).ProtectionResult;

        Assert.True(result.Pairs.Suppressed > 0, "The two writes made no pair at all.");
        return PairProtection.SUFFICIENT;
    }

    /// <summary>One hosted service holding the guarded object, running <paramref name="body"/>.</summary>
    private static string Worker(string name, string body, bool isAsync = false, string holder = "Guarded", string extra = "") => $$"""
        public sealed class {{name}}Worker : BackgroundService
        {
            private readonly {{holder}} {{(holder == "Guarded" ? "_guarded" : "_spinning")}};
            private readonly CancellationToken _token;
            public {{name}}Worker({{holder}} value) => {{(holder == "Guarded" ? "_guarded" : "_spinning")}} = value;
            protected override {{(isAsync ? "async Task" : "Task")}} ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                {{(isAsync ? "" : "return Task.CompletedTask;")}}
            }

            {{extra}}
        }
        """;

    private static async Task<AnalysisResult> Analyze(string source) =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + source)), ROOT_DIRECTORY,
                                            ProviderRegistry.BuiltIn, CancellationToken.None);
}
