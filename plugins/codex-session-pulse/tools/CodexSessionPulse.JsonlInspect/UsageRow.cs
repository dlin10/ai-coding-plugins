using System.Globalization;

namespace CodexSessionPulse.JsonlInspect;

/// <summary>One <c>token_count</c> event as the inspector prints it.</summary>
/// <param name="Timestamp">The record's <c>timestamp</c>, as written.</param>
/// <param name="ThreadId">The owning thread: the file's first <c>session_meta</c> from its <c>subagent_history_start_ordinal</c> on, otherwise the nearest preceding <c>session_meta</c>; <see langword="null"/> before any.</param>
/// <param name="Model">The <c>model</c> of the nearest preceding <c>turn_context</c>, if any.</param>
/// <param name="TurnId">The <c>turn_id</c> of the nearest preceding <c>task_started</c>, if any.</param>
/// <param name="Usage">The <c>info.last_token_usage</c> counts, or <see langword="null"/> when <c>info</c> is null.</param>
/// <param name="Timing">What the turn's <c>task_complete</c> reported: <c>ttft+duration</c>, <c>duration</c>, <c>none</c>, or <c>open</c> while the turn has not completed.</param>
internal sealed record UsageRow(string Timestamp,
                                string? ThreadId,
                                string? Model,
                                string? TurnId,
                                TokenUsage? Usage,
                                string Timing)
{
    /// <summary>Renders the row as one line; never includes anything but the listed fields.</summary>
    /// <returns>The printable line.</returns>
    public string Format()
    {
        var head = $"{Timestamp}  thread={ThreadId ?? "-"}  model={Model ?? "-"}";
        if (Usage is null)
            return $"{head}  info=null  timing={Timing}";

        var line = string.Create(CultureInfo.InvariantCulture,
                                 $"{head}  input={Usage.Input}  cached={Usage.Cached}  cacheWrite={Usage.CacheWrite}  output={Usage.Output}  timing={Timing}");
        return Usage.IsAllZero ? line + "  note=all-zero" : line;
    }
}

/// <summary>The four counts of a <c>last_token_usage</c> object.</summary>
/// <param name="Input">The <c>input_tokens</c> count.</param>
/// <param name="Cached">The <c>cached_input_tokens</c> count.</param>
/// <param name="CacheWrite">The <c>cache_write_input_tokens</c> count.</param>
/// <param name="Output">The <c>output_tokens</c> count.</param>
internal sealed record TokenUsage(long Input, long Cached, long CacheWrite, long Output)
{
    /// <summary>Gets whether every count is zero, as in the <c>token_count</c> written right after a compaction.</summary>
    public bool IsAllZero => Input == 0 && Cached == 0 && CacheWrite == 0 && Output == 0;
}

/// <summary>What reading one rollout produced.</summary>
/// <param name="Rows">The usage rows in file order.</param>
/// <param name="UnparsedLines">How many non-empty lines were not valid JSON, such as a partially written last line.</param>
internal sealed record ReadResult(IReadOnlyList<UsageRow> Rows, int UnparsedLines);
