namespace CodexSessionPulse.Core;

/// <summary>The display state of the cache for one thread (spec §3.2).</summary>
public enum CacheState
{
    /// <summary>No rollout JSONL was found.</summary>
    NoSession,

    /// <summary>A session exists but no <c>token_count</c> event has been observed yet.</summary>
    WaitingForUsage,

    /// <summary>The last observation confirms a cache hit of sufficient size.</summary>
    Warm,

    /// <summary>The remaining time is at most 25% of the estimated TTL.</summary>
    ExpiringSoon,

    /// <summary>The TTL estimate has passed without a new observation; a miss is not claimed.</summary>
    EstimatedExpired,

    /// <summary>A new request confirmed a strong drop in cache reuse against a comparable prefix.</summary>
    ObservedMiss,

    /// <summary>The data has not been updated for a long time, or the file is unavailable.</summary>
    Stale,

    /// <summary>The watcher or the parser failed.</summary>
    Error,
}

/// <summary>The kind of a Codex thread.</summary>
public enum ThreadKind
{
    /// <summary>A main (root) thread.</summary>
    Main,

    /// <summary>A forked thread.</summary>
    Fork,

    /// <summary>A subagent thread.</summary>
    Subagent,
}

/// <summary>How sure the engine is that a thread is the one the user is looking at.</summary>
public enum SelectionConfidence
{
    /// <summary>The selection is confirmed by a direct signal.</summary>
    Confirmed,

    /// <summary>The selection is inferred from indirect signals.</summary>
    Inferred,

    /// <summary>The selection is not known.</summary>
    Unknown,
}

/// <summary>What a TTL estimate is based on.</summary>
public enum TtlBasis
{
    /// <summary>There is no basis for an estimate.</summary>
    Unknown,

    /// <summary>A built-in assumption, not an observation.</summary>
    Assumed,

    /// <summary>Derived from observed cache behaviour.</summary>
    Observed,
}

/// <summary>The availability of the data source behind a snapshot.</summary>
public enum SourceStatus
{
    /// <summary>The source is readable.</summary>
    Available,

    /// <summary>The source was not found.</summary>
    NotFound,

    /// <summary>The source exists but cannot be read or parsed.</summary>
    Failed,
}
