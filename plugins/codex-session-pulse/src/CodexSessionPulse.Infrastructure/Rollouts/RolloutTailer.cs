using System.Runtime.CompilerServices;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>
/// The live rollout source: a <see cref="FileSystemWatcher"/> as the signal and polling by length every
/// <see cref="RolloutTailerOptions.PollInterval"/> to cover lost events (spec §5.2), over a <see cref="RolloutTail"/>.
/// </summary>
/// <remarks>
/// <para>Polls run on the thread pool, never on the thread that enumerates, so the overlay's dispatcher is not
/// blocked by file I/O. A burst of watcher notifications is coalesced: after a wake the loop waits
/// a short debounce before it polls.</para>
/// <para>Threads seen in a <c>session_meta</c> are raised through <see cref="ThreadObserved"/> before the usage events
/// of the same poll. A missing sessions folder is reported by <see cref="Status"/> and retried at every poll; the
/// watcher starts once the folder exists. Enumerate <see cref="WatchAsync"/> once at a time; disposing ends it.</para>
/// </remarks>
public sealed class RolloutTailer : ISessionEventSource
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly RolloutTailerOptions _options;
    private readonly RolloutTail _tail;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _disposed = new();
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

    /// <summary>Raised for each model request timed in a poll, after the poll's usage events.</summary>
    public event Action<RequestTiming>? RequestTimed;

    /// <summary>Gets whether the sessions folder was found at the last poll.</summary>
    public SourceStatus Status => _tail.Status;

    /// <summary>Gets the counters of skipped and recovered records.</summary>
    public RolloutDiagnostics Diagnostics => _tail.Diagnostics;

    /// <inheritdoc/>
    public async IAsyncEnumerable<TokenUsageSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        CancellationToken token = linked.Token;

        while (await PollAsync(token).ConfigureAwait(false) is { } result)
        {
            foreach (ThreadObservation observation in result.Observations)
            {
                ThreadObserved?.Invoke(observation);
            }

            foreach (TokenUsageSnapshot usage in result.Usages)
            {
                yield return usage;
            }

            // After the usages, so the request's thread exists by now.
            foreach (RequestTiming timing in result.Timings)
            {
                RequestTimed?.Invoke(timing);
            }

            if (!await WaitAsync(token).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Has the next poll track the newest rollout of a thread, so a thread opened in Codex is read even when discovery
    /// left it out (older than the newest <see cref="RolloutTailerOptions.MaxFiles"/>); see <see cref="RolloutTail.Follow"/>.
    /// </summary>
    /// <param name="threadId">The thread id, as a GUID.</param>
    public void Follow(string threadId)
    {
        _tail.Follow(threadId);
        Wake();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_watcherGate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        // Cancelling, not disposing the semaphore, ends a running enumeration cleanly.
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }

    private async Task<RolloutPollResult?> PollAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return await Task.Run(() =>
            {
                EnsureWatcher();
                return _tail.Poll();
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Waits for a watcher signal or the poll interval, then lets a burst of notifications settle.</summary>
    /// <param name="token">Ends the wait.</param>
    /// <returns><see langword="false"/> when the enumeration should end.</returns>
    private async Task<bool> WaitAsync(CancellationToken token)
    {
        try
        {
            if (await _signal.WaitAsync(_options.PollInterval, token).ConfigureAwait(false))
            {
                await Task.Delay(Debounce, token).ConfigureAwait(false);
                _signal.Wait(0, CancellationToken.None);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void EnsureWatcher()
    {
        lock (_watcherGate)
        {
            if (_watcher is not null || _disposed.IsCancellationRequested || !Directory.Exists(_options.Root))
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
        catch (SemaphoreFullException)
        {
            // Another notification signalled first.
        }
    }
}
