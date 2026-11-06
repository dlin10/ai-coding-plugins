namespace CodexSessionPulse.Core;

/// <summary>
/// The immutable snapshot the overlay renders (spec §4.3). It carries state, confidence, diagnostics,
/// timestamps and numbers; Core never formats them into strings.
/// </summary>
/// <param name="AtUtc">When the snapshot was computed, in UTC.</param>
/// <param name="State">The display state.</param>
/// <param name="ThreadId">The selected thread, or <see langword="null"/> when there is none.</param>
/// <param name="Kind">The kind of the selected thread.</param>
/// <param name="Selection">Why this thread was selected and how sure that is.</param>
/// <param name="LastRequestAt">When the last request was observed, or <see langword="null"/> when none was.</param>
/// <param name="CachedInput">Input tokens served from the cache by the last request, when known.</param>
/// <param name="Input">Input tokens of the last request, when known.</param>
/// <param name="CacheRatio">The share of cached input in total input, from 0 to 1, when known.</param>
/// <param name="Ttl">The TTL estimate.</param>
/// <param name="Speed">Output tokens per second; always <see langword="null"/> in P0.</param>
/// <param name="Source">The data source behind the snapshot.</param>
public sealed record CacheSnapshot(
    DateTimeOffset AtUtc,
    CacheState State,
    string? ThreadId,
    ThreadKind Kind,
    ThreadSelection Selection,
    DateTimeOffset? LastRequestAt,
    long? CachedInput,
    long? Input,
    double? CacheRatio,
    CacheTtl Ttl,
    double? Speed,
    SourceInfo Source);

/// <summary>Why a thread was selected and how sure that is.</summary>
/// <param name="Confidence">How sure the selection is.</param>
/// <param name="Reason">A machine-readable reason code for diagnostics, when there is one.</param>
public sealed record ThreadSelection(SelectionConfidence Confidence, string? Reason);

/// <summary>A TTL estimate.</summary>
/// <param name="Estimate">The estimated total TTL, or <see langword="null"/> when none can be made.</param>
/// <param name="Basis">What the estimate is based on.</param>
public sealed record CacheTtl(TimeSpan? Estimate, TtlBasis Basis);

/// <summary>The data source behind a snapshot.</summary>
/// <param name="Status">Whether the source is readable.</param>
/// <param name="Path">The path of the rollout file, when there is one.</param>
/// <param name="CliVersion">The Codex CLI version recorded in the source, when known.</param>
public sealed record SourceInfo(SourceStatus Status, string? Path, string? CliVersion);
