using System.Runtime.InteropServices;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>One scope's stage deadlines, available-commit guard and sampled working-set peak.</summary>
internal sealed class BenchmarkWatchdog : IDisposable
{
    private const ulong MINIMUM_COMMIT = 2UL * 1024 * 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<ulong> _availableCommit;
    private readonly Func<long> _workingSet;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _sync = new();
    private readonly Timer? _timer;
    private DateTimeOffset? _deadline;
    private string? _reason;
    private long _peakWorkingSet;

    internal BenchmarkWatchdog(Func<DateTimeOffset> clock, Func<ulong> availableCommit, Func<long> workingSet, bool startTimer = true)
    {
        _clock = clock;
        _availableCommit = availableCommit;
        _workingSet = workingSet;
        if (startTimer)
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    internal CancellationToken Token => _cancellation.Token;
    internal string? Reason { get { lock (_sync) return _reason; } }
    internal double PeakWorkingSetMb { get { lock (_sync) return _peakWorkingSet / (1024.0 * 1024); } }

    internal void Start(ConcurrencyHunter.Analysis.ScopeStep step)
    {
        lock (_sync)
            _deadline = step is ConcurrencyHunter.Analysis.ScopeStep.SummariesAndFixpoint or ConcurrencyHunter.Analysis.ScopeStep.Executions
                ? _clock() + Budget : null;
    }

    internal void Tick()
    {
        lock (_sync)
        {
            _peakWorkingSet = Math.Max(_peakWorkingSet, _workingSet());
            if (_reason is not null)
                return;
            _reason = _availableCommit() < MINIMUM_COMMIT ? "memory"
                : _deadline is { } deadline && _clock() >= deadline ? "timeout"
                : null;
            if (_reason is not null)
                _cancellation.Cancel();
        }
    }

    internal static ulong AvailableCommit()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return status.AvailablePageFile;
    }

    public void Dispose()
    {
        // Wait for an in-flight sample before disposing the cancellation source or reading the final peak.
        if (_timer is not null)
        {
            using var finished = new ManualResetEvent(false);
            if (_timer.Dispose(finished))
                finished.WaitOne();
        }

        _cancellation.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }
}
