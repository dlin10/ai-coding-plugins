using System.Collections.Concurrent;

namespace CodexSessionPulse.Core;

/// <summary>
/// Every known thread with its own independent state (spec §4.3, §5.3), keyed by thread id only: forks that
/// share a root session are never merged, and a subagent never replaces a main thread's state.
/// </summary>
/// <remarks>
/// Not thread-safe as a whole: the caller serializes every read and write, as <see cref="PulseEngine"/> does under
/// its lock. The concurrent dictionary only keeps enumeration valid while a thread is added or removed.
/// An ended thread leaves a bounded tombstone: discovery of its old JSONL cannot bring it back, while a new
/// <c>SessionStart</c>/resume or activity newer than the end clears the tombstone.
/// </remarks>
public sealed class ThreadRegistry
{
    private const int MaxTombstones = 256;

    private readonly ConcurrentDictionary<string, ThreadPulseState> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _tombstones = new(StringComparer.Ordinal);
    private readonly Queue<Tombstone> _tombstoneOrder = new();
    private readonly CacheTrackerOptions _options;
    private long _generation;

    /// <summary>Creates an empty registry.</summary>
    /// <param name="options">The settings each thread's tracker gets.</param>
    public ThreadRegistry(CacheTrackerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets the live threads.</summary>
    public ICollection<ThreadPulseState> Threads => _threads.Values;

    /// <summary>Finds a live thread.</summary>
    /// <param name="threadId">The thread id.</param>
    /// <param name="state">The thread's state when found.</param>
    /// <returns>Whether the thread is live.</returns>
    public bool TryGet(string threadId, out ThreadPulseState state)
    {
        return _threads.TryGetValue(threadId, out state!);
    }

    /// <summary>Gets whether the thread was ended and is held off by a tombstone.</summary>
    /// <param name="threadId">The thread id.</param>
    /// <returns>Whether a tombstone exists.</returns>
    public bool IsEnded(string threadId)
    {
        return _tombstones.ContainsKey(threadId);
    }

    /// <summary>Folds a usage event into its own thread's state.</summary>
    /// <param name="usage">The usage event.</param>
    /// <returns>The thread's state, or <see langword="null"/> when a tombstone holds off this old event.</returns>
    public ThreadPulseState? Apply(TokenUsageSnapshot usage)
    {
        if (!PassesTombstone(usage.ThreadId, usage.ObservedAtUtc))
        {
            return null;
        }

        ThreadPulseState state = GetOrAdd(usage.ThreadId);
        Describe(state, usage.Kind, usage.ParentThreadId, usage.RootSessionId, usage.SourcePath, usage.CliVersion);
        state.Model = usage.Model ?? state.Model;
        Touch(state, usage.ObservedAtUtc);
        state.Tracker.Apply(usage);

        return state;
    }

    /// <summary>Registers a thread seen in a rollout's <c>session_meta</c>.</summary>
    /// <param name="observation">The observed thread.</param>
    /// <returns>The thread's state, or <see langword="null"/> when a tombstone holds off this old observation.</returns>
    public ThreadPulseState? Observe(ThreadObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!PassesTombstone(observation.ThreadId, observation.ObservedAtUtc))
        {
            return null;
        }

        ThreadPulseState state = GetOrAdd(observation.ThreadId);
        Describe(state, observation.Kind, observation.ParentThreadId, observation.RootSessionId,
                 observation.SourcePath, observation.CliVersion);
        Touch(state, observation.ObservedAtUtc);

        return state;
    }

    /// <summary>
    /// Handles a hook that shows the thread is in use (<c>SessionStart</c>, resume, user activity): clears a tombstone
    /// older than the hook and makes sure the thread is live.
    /// </summary>
    /// <param name="threadId">The thread id.</param>
    /// <param name="atUtc">When the hook fired, in UTC.</param>
    /// <returns>The thread's state, or <see langword="null"/> when the hook is not newer than the thread's end.</returns>
    public ThreadPulseState? Start(string threadId, DateTimeOffset atUtc)
    {
        if (!PassesTombstone(threadId, atUtc))
        {
            return null;
        }

        ThreadPulseState state = GetOrAdd(threadId);
        Touch(state, atUtc);

        return state;
    }

    /// <summary>
    /// Handles <c>SessionEnd</c> for exactly one thread: removes its state and leaves a tombstone. Idempotent; an
    /// end older than the thread's latest activity is stale and ignored. Other threads, including forks with the
    /// same root session and the thread's subagents, are kept.
    /// </summary>
    /// <param name="threadId">The thread id.</param>
    /// <param name="atUtc">When the session ended, in UTC.</param>
    /// <returns>Whether a live state was removed.</returns>
    public bool End(string threadId, DateTimeOffset atUtc)
    {
        if (_threads.TryGetValue(threadId, out ThreadPulseState? state) && state.LastActivityUtc > atUtc)
        {
            return false;
        }

        bool removed = _threads.TryRemove(threadId, out _);
        AddTombstone(threadId, atUtc);

        return removed;
    }

    private static void Describe(ThreadPulseState state,
                                 ThreadKind kind,
                                 string? parentThreadId,
                                 string? rootSessionId,
                                 string? sourcePath,
                                 string? cliVersion)
    {
        if (state.Kind == ThreadKind.Unknown)
        {
            state.Kind = kind;
        }

        state.ParentThreadId ??= parentThreadId;
        state.RootSessionId ??= rootSessionId;
        state.SourcePath = sourcePath ?? state.SourcePath;
        state.CliVersion = cliVersion ?? state.CliVersion;
    }

    private static void Touch(ThreadPulseState state, DateTimeOffset atUtc)
    {
        if (atUtc > state.LastActivityUtc)
        {
            state.LastActivityUtc = atUtc;
        }
    }

    private ThreadPulseState GetOrAdd(string threadId)
    {
        return _threads.GetOrAdd(threadId, id => new ThreadPulseState(id, new CacheTracker(_options), ++_generation));
    }

    private bool PassesTombstone(string threadId, DateTimeOffset atUtc)
    {
        if (!_tombstones.TryGetValue(threadId, out DateTimeOffset endedAtUtc))
        {
            return true;
        }

        if (atUtc <= endedAtUtc)
        {
            return false;
        }

        _tombstones.Remove(threadId);

        return true;
    }

    private void AddTombstone(string threadId, DateTimeOffset atUtc)
    {
        if (_tombstones.TryGetValue(threadId, out DateTimeOffset existing) && existing >= atUtc)
        {
            return;
        }

        _tombstones[threadId] = atUtc;
        _tombstoneOrder.Enqueue(new Tombstone(threadId, atUtc));

        while (_tombstoneOrder.Count > MaxTombstones)
        {
            Tombstone oldest = _tombstoneOrder.Dequeue();

            // A later end or a removed-and-re-added tombstone has its own, newer entry in the queue.
            if (_tombstones.TryGetValue(oldest.ThreadId, out DateTimeOffset current) && current == oldest.EndedAtUtc)
            {
                _tombstones.Remove(oldest.ThreadId);
            }
        }
    }

    private readonly record struct Tombstone(string ThreadId, DateTimeOffset EndedAtUtc);
}
