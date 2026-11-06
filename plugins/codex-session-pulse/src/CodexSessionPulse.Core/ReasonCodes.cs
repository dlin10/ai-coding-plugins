namespace CodexSessionPulse.Core;

/// <summary>Machine-readable codes for <see cref="ThreadSelection.Reason"/>; Core never emits free text.</summary>
public static class SelectionReasons
{
    /// <summary>The thread was named by the latest user-activity hook (<c>UserPromptSubmit</c>).</summary>
    public const string UserActivityHook = "hook-user-activity";

    /// <summary>The thread was named by the latest <c>SessionStart</c> hook (startup, resume, fork or compact).</summary>
    public const string SessionStartHook = "hook-session-start";

    /// <summary>No live hook signal: the thread with the latest JSONL activity was taken.</summary>
    public const string LatestJsonlActivity = "jsonl-latest-activity";

    /// <summary>There is no thread to show.</summary>
    public const string NoThread = "no-thread";
}

/// <summary>
/// Machine-readable diagnostic codes: the values of <see cref="CacheSnapshot.Diagnostics"/> and the keys of the
/// reader's counters. They never carry conversation text, paths or identifiers.
/// </summary>
public static class DiagnosticCodes
{
    /// <summary>The rollout root does not exist.</summary>
    public const string SessionRootNotFound = "session-root-not-found";

    /// <summary>The watcher or the reader failed; the snapshot is in the Error state.</summary>
    public const string SourceFailed = "source-failed";

    /// <summary>A rollout line was not valid JSON.</summary>
    public const string ParseFailed = "parse-failed";

    /// <summary>A rollout line exceeded the line limit and was skipped without being buffered.</summary>
    public const string LineTooLong = "line-too-long";

    /// <summary>A <c>token_count</c> had <c>info: null</c>; it was skipped, not read as zero.</summary>
    public const string InfoNull = "info-null";

    /// <summary>A <c>token_count</c> had no <c>info.last_token_usage</c> or no required counter; it was skipped.</summary>
    public const string UsageIncomplete = "usage-incomplete";

    /// <summary>A <c>token_count</c> had negative counters or more cached than input tokens; it was skipped.</summary>
    public const string UsageInvalid = "usage-invalid";

    /// <summary>An all-zero <c>last_token_usage</c> after a compaction; it is not a request and not a miss.</summary>
    public const string AllZeroUsage = "all-zero-usage";

    /// <summary>A <c>token_count</c> repeated the previous one (same last and total usage) and was dropped.</summary>
    public const string DuplicateUsage = "duplicate-usage";

    /// <summary>A record was read again at the same path and offset (truncation, re-creation, re-discovery) and was dropped.</summary>
    public const string ReplayedRecord = "replayed-record";

    /// <summary>A <c>token_count</c> inside a copied parent prefix; the parent's own rollout carries it.</summary>
    public const string InheritedPrefix = "inherited-prefix";

    /// <summary>A rollout file could not be opened or read; the reader retries on the next poll.</summary>
    public const string ReadFailed = "read-failed";

    /// <summary>A rollout shrank or was replaced; its cursor was rebuilt.</summary>
    public const string FileReset = "file-reset";
}
