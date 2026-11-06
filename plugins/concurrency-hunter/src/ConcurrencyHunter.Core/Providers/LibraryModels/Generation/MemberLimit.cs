using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>A member's limit on the thread that creates it (SPEC TD-034c, ADR 0033): cancels the member's token when that thread has spent
/// the limit in CPU time, or when <see cref="BACKSTOP"/> limits have passed on the clock, whichever comes first. Only the system clock reads
/// the thread's own CPU time; any other clock stands for it too, so that a test moves both with one clock.</summary>
internal sealed class MemberLimit : IDisposable
{
    /// <summary>The clock's backstop, in member limits: it ends a member that waits without spending CPU time.</summary>
    internal const int BACKSTOP = 4;

    /// <summary>The shortest wait before the CPU time is read again.</summary>
    private static readonly TimeSpan RECHECK = TimeSpan.FromMilliseconds(50);

    private readonly CancellationTokenSource _member;
    private readonly TimeSpan _limit;
    private readonly Func<TimeSpan> _spent;
    private readonly object _gate = new();
    private readonly ITimer _backstop;
    private ITimer? _check;
    private bool _reached;
    private bool _disposed;

    /// <summary>The limit of the member the calling thread generates.</summary>
    /// <param name="member">The member's token source, cancelled when the limit is reached.</param>
    /// <param name="limit">The member limit.</param>
    /// <param name="time">The clock: the system clock, or a test's clock that stands for the thread's CPU time as well.</param>
    internal MemberLimit(CancellationTokenSource member, TimeSpan limit, TimeProvider time)
        : this(member, limit, time, time == TimeProvider.System ? ThreadCpuTime() : ElapsedOn(time))
    {
    }

    /// <summary>A member's limit over a given reading of the CPU time spent.</summary>
    /// <param name="member">The member's token source, cancelled when the limit is reached.</param>
    /// <param name="limit">The member limit.</param>
    /// <param name="time">The clock the checks and the backstop wait on.</param>
    /// <param name="spent">The CPU time the member has spent so far; read on the clock's timer threads.</param>
    internal MemberLimit(CancellationTokenSource member, TimeSpan limit, TimeProvider time, Func<TimeSpan> spent)
    {
        _member = member;
        _limit = limit;
        _spent = spent;
        _backstop = time.CreateTimer(_ => Reach(), null, limit * BACKSTOP, Timeout.InfiniteTimeSpan);
        // A thread spends no more CPU time than passes on the clock, so the limit is reached no sooner than one limit from now, and after
        // each reading no sooner than what is left of it.
        _check = time.CreateTimer(_ => Check(), null, limit, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether the limit cancelled the member's token.</summary>
    internal bool Reached
    {
        get
        {
            lock (_gate)
                return _reached;
        }
    }

    /// <summary>Stops both timers; a member that ended first is never cancelled after this.</summary>
    // bounded: disposes the two timers, not this limit again.
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _check?.Dispose();
        _backstop.Dispose();
    }

    private void Check()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var left = _limit - _spent();
            if (left > TimeSpan.Zero)
            {
                _check?.Change(left > RECHECK ? left : RECHECK, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        Reach();
    }

    private void Reach()
    {
        lock (_gate)
        {
            if (_disposed || _reached)
                return;
            _reached = true;
        }

        try
        {
            _member.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The CPU time the calling thread spends from now on, readable from any thread, through the process's view of the thread.</summary>
    private static Func<TimeSpan> ThreadCpuTime()
    {
        var id = (int)GetCurrentThreadId();
        using var process = Process.GetCurrentProcess();
        var thread = process.Threads.Cast<ProcessThread>().Single(thread => thread.Id == id);
        var start = thread.TotalProcessorTime;
        return () => thread.TotalProcessorTime - start;
    }

    /// <summary>The operating system's id of the calling thread, by which <see cref="Process.Threads"/> names it: the analyzer's one native
    /// import (SPEC TD-034b), since .NET gives a managed thread's system id to no public member.</summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>The time that passes on a clock from now on.</summary>
    /// <param name="time">The clock.</param>
    private static Func<TimeSpan> ElapsedOn(TimeProvider time)
    {
        var start = time.GetTimestamp();
        return () => time.GetElapsedTime(start);
    }
}
