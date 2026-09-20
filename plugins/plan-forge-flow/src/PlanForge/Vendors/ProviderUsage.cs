using System.Text.Json;

namespace PlanForge.Vendors;

internal static class ProviderUsage
{
    public static WorkerUsage Claude(JsonElement? usage)
    {
        var reader = new UsageReader(usage);
        var input = reader.Counter("input_tokens");
        var cacheRead = reader.Counter("cache_read_input_tokens");
        var cacheCreation = reader.Counter("cache_creation_input_tokens");
        var output = reader.Counter("output_tokens");
        var reasoning = reader.NestedCounter("output_tokens_details", "thinking_tokens");
        return reader.Build(input, cacheRead, cacheCreation, output, ValidateReasoning(reader, output, reasoning,
                                                                                       "usage.output_tokens_details.thinking_tokens"));
    }

    public static WorkerUsage Codex(JsonElement? usage)
    {
        var reader = new UsageReader(usage);
        var totalInput = reader.Counter("input_tokens");
        var cacheRead = reader.Counter("cached_input_tokens");
        var cacheCreation = reader.Counter("cache_write_input_tokens");
        var output = reader.Counter("output_tokens");
        var reasoning = reader.Counter("reasoning_output_tokens");

        long? input = null;
        if (totalInput is not null && cacheRead is not null && cacheCreation is not null)
        {
            if (cacheRead <= totalInput && cacheCreation <= totalInput - cacheRead)
                input = totalInput - cacheRead - cacheCreation;
            else
                reader.Malformed("usage.input_tokens");
        }

        return reader.Build(input, cacheRead, cacheCreation, output,
                            ValidateReasoning(reader, output, reasoning, "usage.reasoning_output_tokens"));
    }

    public static WorkerUsage Cursor(JsonElement? usage)
    {
        var reader = new UsageReader(usage);
        return reader.Build(reader.Counter("inputTokens"),
                            reader.Counter("cacheReadTokens"),
                            reader.Counter("cacheWriteTokens"),
                            reader.Counter("outputTokens"),
                            reasoning: null);
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
        private readonly HashSet<string> _malformed = new(StringComparer.Ordinal);

        public UsageReader(JsonElement? usage)
        {
            _usage = usage;
            if (usage is not null && usage.Value.ValueKind is not JsonValueKind.Object)
                _malformed.Add("usage");
        }

        public long? Counter(string name)
        {
            if (_usage is null || _usage.Value.ValueKind is not JsonValueKind.Object
                || !_usage.Value.TryGetProperty(name, out var value))
            {
                return null;
            }

            if (value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0)
                return number;

            Malformed($"usage.{name}");
            return null;
        }

        public long? NestedCounter(string containerName, string name)
        {
            if (_usage is null || _usage.Value.ValueKind is not JsonValueKind.Object
                || !_usage.Value.TryGetProperty(containerName, out var container))
            {
                return null;
            }

            if (container.ValueKind is not JsonValueKind.Object)
            {
                Malformed($"usage.{containerName}");
                return null;
            }

            if (!container.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0)
                return number;

            Malformed($"usage.{containerName}.{name}");
            return null;
        }

        public void Malformed(string path) => _malformed.Add(path);

        public WorkerUsage Build(long? input,
                                 long? cacheRead,
                                 long? cacheCreation,
                                 long? output,
                                 long? reasoning) =>
            new(input, cacheRead, cacheCreation, output, reasoning,
                _malformed.Count == 0 ? null : [.. _malformed.Order(StringComparer.Ordinal)]);
    }
}
