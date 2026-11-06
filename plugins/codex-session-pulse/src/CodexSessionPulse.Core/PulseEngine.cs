namespace CodexSessionPulse.Core;

/// <summary>
/// Turns usage events, rollout observations and hook signals into the one <see cref="CacheSnapshot"/> the overlay
/// shows: per-thread state in a <see cref="ThreadRegistry"/>, the choice in an <see cref="ActiveThreadResolver"/>,
/// the result in <see cref="Snapshots"/>. Thread-safe: every call is serialized and publishes the new snapshot.
/// </summary>
/// <remarks>
/// Hook callers (#252) pass the thread id resolved from <c>transcript_path</c>, never the hook <c>session_id</c>,
/// which a fork or subagent shares. Speed is always <see langword="null"/> in P0. <see cref="CacheState.Stale"/> is
/// not produced yet: the spec gives it no threshold.
/// </remarks>
public sealed class PulseEngine
{
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
            _registry.End(threadId, atUtc);
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

        return tracked with
        {
            State = failed ? CacheState.Error : tracked.State,
            ThreadId = thread.ThreadId,
            Kind = thread.Kind,
            Selection = resolved.Selection,
            Source = new SourceInfo(_sourceStatus, thread.SourcePath, thread.CliVersion),
            Diagnostics = failed ? _sourceDiagnostics : null,
            Model = tracked.Model ?? thread.Model,
        };
    }
}
