namespace CodexSessionPulse.Core;

/// <summary>
/// The independent state of one thread in a <see cref="ThreadRegistry"/>: its own <see cref="CacheTracker"/>,
/// kind, model, source and last activity. Mutated by the registry; read it only under the same serialization
/// (<see cref="PulseEngine"/>'s lock), since its values are not updated atomically.
/// </summary>
public sealed class ThreadPulseState
{
    internal ThreadPulseState(string threadId, CacheTracker tracker, long generation)
    {
        ThreadId = threadId;
        Tracker = tracker;
        Generation = generation;
    }

    /// <summary>Gets the thread id; the registry key.</summary>
    public string ThreadId { get; }

    /// <summary>Gets the cache state machine of this thread only.</summary>
    public CacheTracker Tracker { get; }

    /// <summary>Gets the lifecycle generation: a fresh state after an end gets a higher one.</summary>
    public long Generation { get; }

    /// <summary>Gets the kind; the first determined kind wins over <see cref="ThreadKind.Unknown"/>.</summary>
    public ThreadKind Kind { get; internal set; } = ThreadKind.Unknown;

    /// <summary>Gets the parent thread of a fork or subagent, when known.</summary>
    public string? ParentThreadId { get; internal set; }

    /// <summary>Gets the root session id, when known; kept apart from the key.</summary>
    public string? RootSessionId { get; internal set; }

    /// <summary>Gets the model of the last request, when known.</summary>
    public string? Model { get; internal set; }

    /// <summary>Gets the rollout file of the last observation, when known.</summary>
    public string? SourcePath { get; internal set; }

    /// <summary>Gets the CLI version of the last observation, when known.</summary>
    public string? CliVersion { get; internal set; }

    /// <summary>Gets the time of the latest observed activity (usage, session meta or session start), in UTC.</summary>
    public DateTimeOffset LastActivityUtc { get; internal set; }
}
