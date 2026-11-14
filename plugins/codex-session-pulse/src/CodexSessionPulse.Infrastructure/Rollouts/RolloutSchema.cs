namespace CodexSessionPulse.Infrastructure.Rollouts;

/// <summary>
/// The property names of one rollout format version: the adapter seam that branches on
/// <c>session_meta.cli_version</c> (spec §5.1).
/// </summary>
/// <remarks>
/// Every measured version, 0.75.0 to 0.167.0-alpha.3.2, writes snake_case (compatibility.md §7.3), so
/// <see cref="For"/> returns <see cref="SnakeCase"/> for all of them. A version that changes the names gets its
/// own instance here, chosen by its <c>cli_version</c>, with fixture tests.
/// </remarks>
/// <param name="InputTokens">The input-token counter.</param>
/// <param name="CachedInputTokens">The cached-input counter.</param>
/// <param name="CacheWriteInputTokens">The cache-write counter, optional.</param>
/// <param name="OutputTokens">The output counter.</param>
/// <param name="ReasoningOutputTokens">The reasoning-output counter, optional.</param>
/// <param name="TotalTokens">The total counter.</param>
/// <param name="LastTokenUsage">The per-request usage object inside <c>info</c>.</param>
/// <param name="TotalTokenUsage">The cumulative usage object inside <c>info</c>; used only to recognize duplicates.</param>
internal sealed record RolloutSchema(string InputTokens,
                                     string CachedInputTokens,
                                     string CacheWriteInputTokens,
                                     string OutputTokens,
                                     string ReasoningOutputTokens,
                                     string TotalTokens,
                                     string LastTokenUsage,
                                     string TotalTokenUsage)
{
    /// <summary>The snake_case names of every measured version.</summary>
    public static RolloutSchema SnakeCase { get; } = new("input_tokens", "cached_input_tokens", "cache_write_input_tokens",
                                                         "output_tokens", "reasoning_output_tokens", "total_tokens",
                                                         "last_token_usage", "total_token_usage");

    /// <summary>Chooses the schema for a CLI version.</summary>
    /// <param name="cliVersion">The <c>session_meta.cli_version</c>, or <see langword="null"/> when unknown.</param>
    /// <returns>The schema to read the file with.</returns>
    public static RolloutSchema For(string? cliVersion)
    {
        _ = cliVersion;
        return SnakeCase;
    }
}
