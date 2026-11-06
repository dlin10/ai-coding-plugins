namespace CodexSessionPulse.Core;

/// <summary>One observed token-usage event of a thread.</summary>
/// <param name="ObservedAtUtc">When the event was observed, in UTC.</param>
/// <param name="ThreadId">The id of the thread the usage belongs to.</param>
/// <param name="RootSessionId">The id of the root session, when known.</param>
/// <param name="TurnId">The id of the turn, when known.</param>
/// <param name="Model">The model name, when known.</param>
/// <param name="InputTokens">The number of input tokens of the request.</param>
/// <param name="CachedInputTokens">The number of input tokens served from the cache.</param>
/// <param name="CacheWriteInputTokens">The number of input tokens written to the cache.</param>
/// <param name="OutputTokens">The number of output tokens.</param>
/// <param name="ReasoningOutputTokens">The number of reasoning output tokens, when reported.</param>
/// <param name="SourcePath">The path of the rollout file the event came from.</param>
/// <param name="EventSequence">The position of the event in its source, used for ordering.</param>
public readonly record struct TokenUsageSnapshot(
    DateTimeOffset ObservedAtUtc,
    string ThreadId,
    string? RootSessionId,
    string? TurnId,
    string? Model,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long? ReasoningOutputTokens,
    string SourcePath,
    long EventSequence);
