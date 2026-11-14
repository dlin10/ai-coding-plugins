using CodexSessionPulse.Core;

namespace CodexSessionPulse.Infrastructure.Desktop;

/// <summary>
/// Polls the log of the Codex Desktop process the overlay follows (<see cref="DesktopLogTail"/>) every
/// <see cref="PollInterval"/> on a thread-pool timer and raises <see cref="RouteChanged"/> when the page on screen
/// changes. One poll at a time, re-armed after it ends; a poll costs Codex nothing and this process one file length read.
/// </summary>
public sealed class DesktopRouteWatcher : IDisposable
{
    /// <summary>How often the log is read. Codex writes the route line 33–37 ms after a switch.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IClock _clock;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private DesktopLogTail? _tail;
    private string? _logsRoot;
    private int _processId;
    private bool _polling;
    private bool _disposed;
    private CodexRoute? _route;

    /// <summary>Creates a watcher that follows nothing yet.</summary>
    /// <param name="clock">The clock the log reader dates its file search by.</param>
    public DesktopRouteWatcher(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Raised when the page changes, on the timer's thread, and with <see langword="null"/> on the caller's thread when
    /// <see cref="Follow"/> moves to another process. It is raised under the watcher's lock, so handlers see the changes in
    /// order; they must not block and must not call back into the watcher.
    /// </summary>
    public event Action<CodexRoute?>? RouteChanged;

    /// <summary>Gets the latest route, or <see langword="null"/> when none is known.</summary>
    public CodexRoute? Current
    {
        get
        {
            lock (_gate)
            {
                return _route;
            }
        }
    }

    /// <summary>
    /// Follows the log of a Codex process, reading it at once. The same process again changes nothing; another one (Codex
    /// restarted) forgets the route until its log names one.
    /// </summary>
    /// <param name="logsRoot">Codex Desktop's <c>Logs</c> folder (<see cref="DesktopLogTail.LogsRoot"/>).</param>
    /// <param name="processId">The PID of Codex's main process.</param>
    public void Follow(string logsRoot, int processId)
    {
        lock (_gate)
        {
            if (_disposed || (_processId == processId && string.Equals(_logsRoot, logsRoot, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _logsRoot = logsRoot;
            _processId = processId;

            // A poll in flight still holds the old reader; it is dropped once that poll ends (see Poll).
            if (!_polling)
            {
                _tail?.Dispose();
            }

            _tail = new DesktopLogTail(logsRoot, processId, _clock);
            _timer.Change(0, Timeout.Infinite);

            if (_route is not null)
            {
                _route = null;
                RouteChanged?.Invoke(null);
            }
        }
    }

    /// <summary>Describes the watcher for diagnostics.</summary>
    /// <returns>The log reader's state, or <see langword="null"/> while no process is followed.</returns>
    public DesktopLogDiagnostics? GetDiagnostics()
    {
        lock (_gate)
        {
            return _polling ? null : _tail?.GetDiagnostics();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();

            if (!_polling)
            {
                _tail?.Dispose();
            }
        }
    }

    private void Poll()
    {
        DesktopLogTail tail;

        lock (_gate)
        {
            if (_disposed || _polling || _tail is null)
            {
                return;
            }

            _polling = true;
            tail = _tail;
        }

        bool changed = false;

        try
        {
            changed = tail.Poll();
        }
        catch (Exception)
        {
            // A thread-pool callback: an escaping exception would end the process. The next poll tries again.
        }

        CodexRoute? route = tail.Route;

        lock (_gate)
        {
            _polling = false;

            if (!ReferenceEquals(tail, _tail) || _disposed)
            {
                // Follow replaced the reader, or Dispose ran, while this poll read it.
                tail.Dispose();

                if (!_disposed)
                {
                    _timer.Change(0, Timeout.Infinite);
                }

                return;
            }

            _timer.Change(PollInterval, Timeout.InfiniteTimeSpan);

            if (changed)
            {
                // Raised under the lock, so a Follow's null and a poll's route reach the handler in order.
                _route = route;
                RouteChanged?.Invoke(route);
            }
        }
    }
}
