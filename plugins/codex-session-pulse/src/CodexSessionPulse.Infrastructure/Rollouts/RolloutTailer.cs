using System.Runtime.CompilerServices;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>
/// The live rollout source: a <see cref="FileSystemWatcher"/> as the signal and polling by length every
/// <see cref="RolloutTailerOptions.PollInterval"/> to cover lost events (spec §5.2), over a <see cref="RolloutTail"/>.
/// </summary>
/// <remarks>
/// Threads seen in a <c>session_meta</c> are raised through <see cref="ThreadObserved"/> before the usage events of
/// the same poll, on the enumerating thread. A missing sessions folder is reported by <see cref="Status"/> and
/// retried at every poll; the watcher starts once the folder exists.
/// </remarks>
public sealed class RolloutTailer : ISessionEventSource
{
    private readonly RolloutTailerOptions _options;
    private readonly RolloutTail _tail;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _watcherGate = new();
    private FileSystemWatcher? _watcher;

    /// <summary>Creates a source; nothing is read until <see cref="WatchAsync"/> is enumerated.</summary>
    /// <param name="options">The bounds.</param>
    /// <param name="clock">The clock that dates discovery.</param>
    public RolloutTailer(RolloutTailerOptions options, IClock clock)
    {
        _tail = new RolloutTail(options, clock);
        _options = options;
    }

    /// <summary>Raised for each thread first seen in a <c>session_meta</c>.</summary>
    public event Action<ThreadObservation>? ThreadObserved;

    /// <summary>Gets whether the sessions folder was found at the last poll.</summary>
    public SourceStatus Status => _tail.Status;

    /// <summary>Gets the counters of skipped and recovered records.</summary>
    public RolloutDiagnostics Diagnostics => _tail.Diagnostics;

    /// <inheritdoc/>
    public async IAsyncEnumerable<TokenUsageSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            EnsureWatcher();
            RolloutPollResult result = _tail.Poll();

            foreach (ThreadObservation observation in result.Observations)
            {
                ThreadObserved?.Invoke(observation);
            }

            foreach (TokenUsageSnapshot usage in result.Usages)
            {
                yield return usage;
            }

            try
            {
                await _signal.WaitAsync(_options.PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_watcherGate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        _signal.Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureWatcher()
    {
        lock (_watcherGate)
        {
            if (_watcher is not null || !Directory.Exists(_options.Root))
            {
                return;
            }

            FileSystemWatcher watcher = new(_options.Root, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _tail.MarkChanged(e.FullPath);
        Wake();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // An overflow or a removed root: list the folders again, and recreate the watcher if the root is gone.
        _tail.RequestRescan();

        lock (_watcherGate)
        {
            if (ReferenceEquals(sender, _watcher) && !Directory.Exists(_options.Root))
            {
                _watcher.Dispose();
                _watcher = null;
            }
        }

        Wake();
    }

    private void Wake()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException)
        {
            // Already signalled, or disposed while the watcher was still delivering.
        }
    }
}
