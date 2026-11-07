namespace CodexSessionPulse.Core;

/// <summary>
/// Turns usage events, rollout observations and hook signals into the one <see cref="CacheSnapshot"/> the overlay
/// shows: per-thread state in a <see cref="ThreadRegistry"/>, the choice in an <see cref="ActiveThreadResolver"/>,
/// the result in <see cref="Snapshots"/>. Thread-safe: every call is serialized and publishes the new snapshot.
/// </summary>
/// <remarks>
/// Hook callers (#252) pass the thread id resolved from <c>transcript_path</c>, never the hook <c>session_id</c>,
/// which a fork or subagent shares. Speed is always <see langword="null"/> in P0. A shown thread with no activity for
/// <see cref="StaleAfter"/> is <see cref="CacheState.Stale"/>; <see cref="Prune"/> drops threads idle for
/// <see cref="PruneAfter"/>, so a thread whose <c>SessionEnd</c> was missed does not live on.
/// </remarks>
public sealed class PulseEngine
{
    /// <summary>
    /// The idle time after which the shown thread is <see cref="CacheState.Stale"/>, or the TTL estimate when that is longer,
    /// so Stale never hides an estimate that is still running.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    /// <summary>The idle time after which <see cref="Prune"/> drops a thread that is not on display.</summary>
    public static readonly TimeSpan PruneAfter = TimeSpan.FromHours(24);

    /// <summary>The most threads <see cref="Prune"/> keeps.</summary>
    public const int MaxLiveThreads = 64;

    private readonly Lock _gate = new();
    private readonly CacheTrackerOptions _options;
    private readonly IClock _clock;
    private readonly ThreadRegistry _registry;
    private readonly ActiveThreadResolver _resolver = new();
    private int _codexWindows;
    private SourceStatus _sourceStatus = SourceStatus.Available;
    private string? _sourceDiagnostics;

    /// <summary>Creates an engine with no threads.</summary>
    /// <param name="options">The TTL and threshold settings.</param>
    /// <param name="clock">The clock every snapshot is computed at.</param>
    public PulseEngine(CacheTrackerOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
        _registry = new ThreadRegistry(options);
    }

    /// <summary>Gets the published snapshots; a subscriber gets the current one first.</summary>
    public SnapshotPublisher<CacheSnapshot> Snapshots { get; } = new();

    /// <summary>Folds a usage event into its thread.</summary>
    /// <param name="usage">The usage event.</param>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot Apply(TokenUsageSnapshot usage)
    {
        lock (_gate)
        {
            _registry.Apply(usage);
            return PublishLocked();
        }
    }

    /// <summary>Registers a thread seen in a rollout's <c>session_meta</c>.</summary>
    /// <param name="observation">The observed thread.</param>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot Observe(ThreadObservation observation)
    {
        lock (_gate)
        {
            _registry.Observe(observation);
            return PublishLocked();
        }
    }

    /// <summary>Handles a user-activity hook (<c>UserPromptSubmit</c>).</summary>
    /// <param name="threadId">The thread resolved from the hook's <c>transcript_path</c>.</param>
    /// <param name="atUtc">When the hook fired, in UTC.</param>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot OnUserActivity(string threadId, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        lock (_gate)
        {
            if (_registry.Start(threadId, atUtc) is not null)
            {
                _resolver.OnUserActivity(threadId, atUtc);
            }

            return PublishLocked();
        }
    }

    /// <summary>Handles a <c>SessionStart</c> hook (startup, resume, fork, compact).</summary>
    /// <param name="threadId">The thread resolved from the hook's <c>transcript_path</c>.</param>
    /// <param name="atUtc">When the hook fired, in UTC.</param>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot OnSessionStart(string threadId, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        lock (_gate)
        {
            if (_registry.Start(threadId, atUtc) is not null)
            {
                _resolver.OnSessionStart(threadId, atUtc);
            }

            return PublishLocked();
        }
    }

    /// <summary>Handles a <c>SessionEnd</c> hook for exactly one thread; idempotent, a stale end is ignored.</summary>
    /// <param name="threadId">The thread resolved from the hook's <c>transcript_path</c>.</param>
    /// <param name="atUtc">When the hook fired, in UTC.</param>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot OnSessionEnd(string threadId, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        lock (_gate)
        {
            if (_registry.End(threadId, atUtc))
            {
                _resolver.Forget(threadId);
            }

            return PublishLocked();
        }
    }

    /// <summary>Records how many Codex windows exist; two or more cap the confidence at inferred.</summary>
    /// <param name="count">The <see cref="AnchorSnapshot.CodexWindows"/> value.</param>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot OnCodexWindows(int count)
    {
        lock (_gate)
        {
            _codexWindows = count;
            return PublishLocked();
        }
    }

    /// <summary>Records the state of the rollout source.</summary>
    /// <param name="status">The source status; <see cref="SourceStatus.Failed"/> yields the Error state.</param>
    /// <param name="diagnostics">A <see cref="DiagnosticCodes"/> value, or <see langword="null"/>.</param>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot OnSourceStatus(SourceStatus status, string? diagnostics)
    {
        lock (_gate)
        {
            _sourceStatus = status;
            _sourceDiagnostics = diagnostics;
            return PublishLocked();
        }
    }

    /// <summary>
    /// Drops the threads idle for longer than <see cref="PruneAfter"/> and the oldest beyond <see cref="MaxLiveThreads"/>,
    /// keeping the most recent one and the one on display; each leaves a tombstone (see <see cref="ThreadRegistry.Prune"/>).
    /// </summary>
    /// <returns>The number of dropped threads.</returns>
    public int Prune()
    {
        lock (_gate)
        {
            string? shown = _resolver.Resolve(_registry, _codexWindows)?.Thread.ThreadId;
            IReadOnlyList<string> dropped = _registry.Prune(_clock.UtcNow, PruneAfter, MaxLiveThreads, shown);

            foreach (string threadId in dropped)
            {
                _resolver.Forget(threadId);
            }

            if (dropped.Count > 0)
            {
                PublishLocked();
            }

            return dropped.Count;
        }
    }

    /// <summary>Gets whether a thread was ended or pruned and is held off by a tombstone.</summary>
    /// <param name="threadId">The thread id.</param>
    /// <returns>Whether a tombstone exists.</returns>
    public bool IsEnded(string threadId)
    {
        lock (_gate)
        {
            return _registry.IsEnded(threadId);
        }
    }

    /// <summary>Recomputes the snapshot at the clock's current time without a new observation.</summary>
    /// <returns>The published snapshot.</returns>
    public CacheSnapshot Tick()
    {
        lock (_gate)
        {
            return PublishLocked();
        }
    }

    private CacheSnapshot PublishLocked()
    {
        CacheSnapshot snapshot = ComputeLocked(_clock.UtcNow);
        Snapshots.Publish(snapshot);
        return snapshot;
    }

    private CacheSnapshot ComputeLocked(DateTimeOffset nowUtc)
    {
        ResolvedThread? resolved = _resolver.Resolve(_registry, _codexWindows);

        if (resolved is null)
        {
            CacheState empty = _sourceStatus == SourceStatus.Failed ? CacheState.Error : CacheState.NoSession;

            return new CacheSnapshot(nowUtc, empty, null, ThreadKind.Unknown,
                                     new ThreadSelection(SelectionConfidence.Unknown, SelectionReasons.NoThread),
                                     null, null, null, null, new CacheTtl(_options.EstimatedTtl, null, TtlBasis.Unknown),
                                     null, new SourceInfo(_sourceStatus, null, null), _sourceDiagnostics, null, null);
        }

        ThreadPulseState thread = resolved.Thread;
        CacheSnapshot tracked = thread.Tracker.Tick(nowUtc);
        bool failed = _sourceStatus == SourceStatus.Failed;

        bool stale = nowUtc - thread.LastActivityUtc >= (StaleAfter > _options.EstimatedTtl ? StaleAfter : _options.EstimatedTtl);

        return tracked with
        {
            State = failed ? CacheState.Error : stale ? CacheState.Stale : tracked.State,
            ThreadId = thread.ThreadId,
            Kind = thread.Kind,
            Selection = resolved.Selection,
            Source = new SourceInfo(_sourceStatus, thread.SourcePath, thread.CliVersion),
            Diagnostics = failed ? _sourceDiagnostics : null,
            Model = tracked.Model ?? thread.Model,
        };
    }
}
