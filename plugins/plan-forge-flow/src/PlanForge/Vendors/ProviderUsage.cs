using System.Numerics;
using System.Text.Json;

namespace PlanForge.Vendors;

internal static class ProviderUsage
{
    /// <param name="modelUsage">
    /// The result line's `modelUsage`, one entry per model, which counts subagents and context
    /// compaction as well — unlike the line's `usage`, which covers the main loop alone.
    /// </param>
    /// <param name="totalCostUsd">The result line's `total_cost_usd`; only claude reports a price.</param>
    /// <param name="previous">
    /// What the session's previous attempt reported, for a resumed one. Tokens and cost are both the
    /// session's running totals, restored by `--resume` from the transcript's `cost-state`, so the
    /// attempt's usage is what it added.
    /// </param>
    public static WorkerUsage Claude(JsonElement? modelUsage, JsonElement? totalCostUsd = null, WorkerUsage? previous = null)
    {
        var reader = new UsageReader(modelUsage, "modelUsage");
        var cacheRead = reader.ModelSum("cacheReadInputTokens");
        var cacheCreation = reader.ModelSum("cacheCreationInputTokens");
        var total = new WorkerUsage(TotalInput(reader, reader.ModelSum("inputTokens"), cacheRead, cacheCreation,
                                               "modelUsage.*.inputTokens"),
                                    cacheRead,
                                    cacheCreation,
                                    reader.ModelSum("outputTokens"),
                                    reader.ModelSum("thinkingTokens"),
                                    CostUsd: reader.Cost(totalCostUsd));
        var output = reader.Since(total.OutputTokens, previous?.OutputTokens, "modelUsage.*.outputTokens");
        var reasoning = reader.Since(total.ReasoningTokens, previous?.ReasoningTokens, "modelUsage.*.thinkingTokens");
        return reader.Build(reader.Since(total.InputTokens, previous?.InputTokens, "modelUsage.*.inputTokens"),
                            reader.Since(total.CacheReadTokens, previous?.CacheReadTokens,
                                         "modelUsage.*.cacheReadInputTokens"),
                            reader.Since(total.CacheCreationTokens, previous?.CacheCreationTokens,
                                         "modelUsage.*.cacheCreationInputTokens"),
                            output,
                            ValidateReasoning(reader, output, reasoning, "modelUsage.*.thinkingTokens"),
                            reader.Since(total.CostUsd, previous?.CostUsd, "total_cost_usd"),
                            previous is null ? null : total);
    }

    /// <param name="previous">
    /// What the thread's previous attempt reported, for a resumed one. Every counter is the thread's
    /// running total, restored from its rollout by `exec resume`, so the attempt's usage is what it
    /// added.
    /// </param>
    public static WorkerUsage Codex(JsonElement? usage, WorkerUsage? previous = null)
    {
        var reader = new UsageReader(usage);
        var total = new WorkerUsage(reader.Counter("input_tokens"),
                                    reader.Counter("cached_input_tokens"),
                                    reader.Counter("cache_write_input_tokens"),
                                    reader.Counter("output_tokens"),
                                    reader.Counter("reasoning_output_tokens"));
        var totalInput = reader.Since(total.InputTokens, previous?.InputTokens, "usage.input_tokens");
        var cacheRead = reader.Since(total.CacheReadTokens, previous?.CacheReadTokens, "usage.cached_input_tokens");
        var cacheCreation = reader.Since(total.CacheCreationTokens, previous?.CacheCreationTokens,
                                         "usage.cache_write_input_tokens");
        var output = reader.Since(total.OutputTokens, previous?.OutputTokens, "usage.output_tokens");
        var reasoning = reader.Since(total.ReasoningTokens, previous?.ReasoningTokens, "usage.reasoning_output_tokens");

        if (totalInput is not null && cacheRead is not null && cacheCreation is not null)
        {
            if (cacheRead > totalInput || cacheCreation > totalInput - cacheRead)
            {
                reader.Malformed("usage.cached_input_tokens");
                reader.Malformed("usage.cache_write_input_tokens");
            }
        }

        return reader.Build(totalInput, cacheRead, cacheCreation, output,
                            ValidateReasoning(reader, output, reasoning, "usage.reasoning_output_tokens"),
                            sessionTotal: previous is null ? null : total);
    }

    /// <summary>
    /// Taken as reported for a resumed chat too: whether cursor's counters are the turn's or the
    /// chat's has not been measured.
    /// </summary>
    public static WorkerUsage Cursor(JsonElement? usage)
    {
        var reader = new UsageReader(usage);
        var input = reader.Counter("inputTokens");
        var cacheRead = reader.Counter("cacheReadTokens");
        var cacheCreation = reader.Counter("cacheWriteTokens");
        return reader.Build(TotalInput(reader, input, cacheRead, cacheCreation, "usage.inputTokens"),
                            cacheRead,
                            cacheCreation,
                            reader.Counter("outputTokens"),
                            reasoning: null);
    }

    private static long? TotalInput(UsageReader reader, long? input, long? cacheRead, long? cacheCreation, string path)
    {
        if (input is null || cacheRead is null || cacheCreation is null) return null;

        try
        {
            return checked(input.Value + cacheRead.Value + cacheCreation.Value);
        }
        catch (OverflowException)
        {
            reader.Malformed(path);
            return null;
        }
    }

    private static long? ValidateReasoning(UsageReader reader, long? output, long? reasoning, string path)
    {
        if (output is not null && reasoning > output)
        {
            reader.Malformed(path);
            return null;
        }

        return reasoning;
    }

    private sealed class UsageReader
    {
        private readonly JsonElement? _usage;
        private readonly string _root;
        private readonly HashSet<string> _malformed = new(StringComparer.Ordinal);

        /// <param name="root">What the usage block is called in a malformed path.</param>
        public UsageReader(JsonElement? usage, string root = "usage")
        {
            _usage = usage;
            _root = root;
            if (usage is not null && usage.Value.ValueKind is not JsonValueKind.Object)
                _malformed.Add(root);
        }

        public long? Counter(string name)
        {
            if (_usage is null || _usage.Value.ValueKind is not JsonValueKind.Object
                || !_usage.Value.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (IsCount(value, out var number)) return number;

            Malformed($"{_root}.{name}");
            return null;
        }

        /// <summary>
        /// A counter summed over the models of claude's `modelUsage`. Absent when no model reported,
        /// since an empty report is no total to count from, or when one model lacks the counter.
        /// </summary>
        public long? ModelSum(string name)
        {
            if (_usage is not { ValueKind: JsonValueKind.Object } models) return null;

            long? sum = null;
            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind is not JsonValueKind.Object)
                {
                    Malformed($"{_root}.{model.Name}");
                    return null;
                }

                if (!model.Value.TryGetProperty(name, out var value)) return null;
                if (!IsCount(value, out var count))
                {
                    Malformed($"{_root}.{model.Name}.{name}");
                    return null;
                }

                try
                {
                    sum = checked((sum ?? 0) + count);
                }
                catch (OverflowException)
                {
                    Malformed($"{_root}.*.{name}");
                    return null;
                }
            }

            return sum;
        }

        private static bool IsCount(JsonElement value, out long count)
        {
            count = 0;
            return value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out count) && count >= 0;
        }

        public decimal? Cost(JsonElement? value)
        {
            if (value is null) return null;
            if (value.Value.ValueKind is JsonValueKind.Number && value.Value.TryGetDecimal(out var cost) && cost >= 0)
                return cost;

            Malformed("total_cost_usd");
            return null;
        }

        /// <summary>
        /// What a counter the Vendor reports as its session's running total grew by since the
        /// session's previous report. A total below that report is inconsistent with it.
        /// </summary>
        public T? Since<T>(T? total, T? previous, string path) where T : struct, INumber<T>
        {
            if (total is null || previous is null) return total;
            if (total.Value < previous.Value)
            {
                Malformed(path);
                return null;
            }

            return total.Value - previous.Value;
        }

        public void Malformed(string path) => _malformed.Add(path);

        /// <param name="sessionTotal">The running totals a resumed attempt reported; one with no counter is none.</param>
        public WorkerUsage Build(long? input,
                                 long? cacheRead,
                                 long? cacheCreation,
                                 long? output,
                                 long? reasoning,
                                 decimal? costUsd = null,
                                 WorkerUsage? sessionTotal = null) =>
            new(input, cacheRead, cacheCreation, output, reasoning,
                _malformed.Count == 0 ? null : [.. _malformed.Order(StringComparer.Ordinal)],
                costUsd,
                sessionTotal == new WorkerUsage() ? null : sessionTotal);
    }
}
