using System.Text.Json;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class ProviderUsageTests
{
    [Fact]
    public void Claude_includes_both_cache_classes_in_total_input()
    {
        var usage = ProviderUsage.Claude(Usage(
            """
            {
              "input_tokens": 11,
              "cache_read_input_tokens": 22,
              "cache_creation_input_tokens": 33,
              "output_tokens": 44,
              "output_tokens_details": { "thinking_tokens": 40 }
            }
            """));

        Assert.Equal(66, usage.InputTokens);
        Assert.Equal(22, usage.CacheReadTokens);
        Assert.Equal(33, usage.CacheCreationTokens);
        Assert.Equal(44, usage.OutputTokens);
        Assert.Equal(40, usage.ReasoningTokens);
        Assert.Null(usage.CostUsd);
        Assert.Null(usage.MalformedFields);
    }

    [Fact]
    public void Claude_names_a_malformed_cost_and_keeps_the_counters()
    {
        var usage = ProviderUsage.Claude(Usage("""{"output_tokens":4}"""), Element("\"0.42\""));

        Assert.Null(usage.CostUsd);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(["total_cost_usd"], usage.MalformedFields);
    }

    [Fact]
    public void Codex_preserves_provider_total_input()
    {
        var usage = ProviderUsage.Codex(Usage(
            """
            {
              "input_tokens": 100,
              "cached_input_tokens": 60,
              "cache_write_input_tokens": 10,
              "output_tokens": 20,
              "reasoning_output_tokens": 12
            }
            """));

        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(60, usage.CacheReadTokens);
        Assert.Equal(10, usage.CacheCreationTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(12, usage.ReasoningTokens);
        Assert.Null(usage.MalformedFields);
    }

    [Fact]
    public void Cursor_includes_both_cache_classes_in_total_input()
    {
        var usage = ProviderUsage.Cursor(Usage(
            """
            {
              "inputTokens": 7,
              "cacheReadTokens": 8,
              "cacheWriteTokens": 9,
              "outputTokens": 10
            }
            """));

        Assert.Equal(24, usage.InputTokens);
        Assert.Equal(8, usage.CacheReadTokens);
        Assert.Equal(9, usage.CacheCreationTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Null(usage.ReasoningTokens);
        Assert.Null(usage.MalformedFields);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("cursor")]
    public void Missing_usage_is_absent_not_malformed(string vendor)
    {
        var usage = Parse(vendor, null);

        Assert.Null(usage.InputTokens);
        Assert.Null(usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.ReasoningTokens);
        Assert.Null(usage.MalformedFields);
    }

    [Theory]
    [InlineData("claude", "input_tokens")]
    [InlineData("codex", "input_tokens")]
    [InlineData("cursor", "inputTokens")]
    public void Every_vendor_preserves_independent_counters_when_one_is_malformed(string vendor, string inputName)
    {
        var usage = Parse(vendor, Usage($$"""{"{{inputName}}":"bad","output{{(vendor == "cursor" ? "Tokens" : "_tokens")}}":3}"""));

        Assert.Null(usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal([$"usage.{inputName}"], usage.MalformedFields);
    }

    [Theory]
    [InlineData("\"12\"")]
    [InlineData("12.0")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("9223372036854775808")]
    public void Non_integer_or_out_of_range_counter_is_named_without_its_value(string value)
    {
        var usage = ProviderUsage.Cursor(Usage($$"""{"inputTokens":{{value}},"outputTokens":3}"""));

        Assert.Null(usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(["usage.inputTokens"], usage.MalformedFields);
    }

    [Fact]
    public void Invalid_usage_block_is_named_once()
    {
        var usage = ProviderUsage.Cursor(Element("[]"));

        Assert.Equal(["usage"], usage.MalformedFields);
    }

    [Fact]
    public void Invalid_relationships_drop_only_the_derived_values()
    {
        var usage = ProviderUsage.Codex(Usage(
            """
            {
              "input_tokens": 5,
              "cached_input_tokens": 4,
              "cache_write_input_tokens": 3,
              "output_tokens": 6,
              "reasoning_output_tokens": 7
            }
            """));

        Assert.Equal(5, usage.InputTokens);
        Assert.Equal(4, usage.CacheReadTokens);
        Assert.Equal(3, usage.CacheCreationTokens);
        Assert.Equal(6, usage.OutputTokens);
        Assert.Null(usage.ReasoningTokens);
        Assert.Equal(["usage.cache_write_input_tokens", "usage.cached_input_tokens", "usage.reasoning_output_tokens"],
                     usage.MalformedFields);
    }

    [Fact]
    public void Missing_codex_cache_component_preserves_total_input_without_calling_it_malformed()
    {
        var usage = ProviderUsage.Codex(Usage("""{"input_tokens":5,"cached_input_tokens":2}"""));

        Assert.Equal(5, usage.InputTokens);
        Assert.Equal(2, usage.CacheReadTokens);
        Assert.Null(usage.MalformedFields);
    }

    [Fact]
    public void Codex_inconsistent_cache_breakdown_preserves_total_and_names_both_cache_fields()
    {
        var usage = ProviderUsage.Codex(Usage(
            """{"input_tokens":9223372036854775807,"cached_input_tokens":9223372036854775807,"cache_write_input_tokens":1}"""));

        Assert.Equal(long.MaxValue, usage.InputTokens);
        Assert.Equal(long.MaxValue, usage.CacheReadTokens);
        Assert.Equal(1, usage.CacheCreationTokens);
        Assert.Equal(["usage.cache_write_input_tokens", "usage.cached_input_tokens"], usage.MalformedFields);
    }

    [Fact]
    public void Malformed_codex_cache_component_preserves_total_and_independent_counters()
    {
        var usage = ProviderUsage.Codex(Usage(
            """{"input_tokens":10,"cached_input_tokens":"bad","cache_write_input_tokens":3,"output_tokens":4}"""));

        Assert.Equal(10, usage.InputTokens);
        Assert.Null(usage.CacheReadTokens);
        Assert.Equal(3, usage.CacheCreationTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(["usage.cached_input_tokens"], usage.MalformedFields);
    }

    [Theory]
    [InlineData("claude",
                """{"input_tokens":1,"cache_read_input_tokens":2,"output_tokens":4}""")]
    [InlineData("cursor",
                """{"inputTokens":1,"cacheReadTokens":2,"outputTokens":4}""")]
    public void Missing_cache_component_omits_derived_total_without_calling_it_malformed(string vendor,
                                                                                         string json)
    {
        var usage = Parse(vendor, Usage(json));

        Assert.Null(usage.InputTokens);
        Assert.Equal(2, usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Null(usage.MalformedFields);
    }

    [Theory]
    [InlineData("claude",
                """{"input_tokens":1,"cache_read_input_tokens":"bad","cache_creation_input_tokens":3,"output_tokens":4}""",
                "usage.cache_read_input_tokens")]
    [InlineData("cursor",
                """{"inputTokens":1,"cacheReadTokens":"bad","cacheWriteTokens":3,"outputTokens":4}""",
                "usage.cacheReadTokens")]
    public void Malformed_cache_component_omits_derived_total_but_preserves_independent_counters(string vendor,
                                                                                                 string json,
                                                                                                 string path)
    {
        var usage = Parse(vendor, Usage(json));

        Assert.Null(usage.InputTokens);
        Assert.Null(usage.CacheReadTokens);
        Assert.Equal(3, usage.CacheCreationTokens);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal([path], usage.MalformedFields);
    }

    [Theory]
    [InlineData("claude",
                """{"input_tokens":9223372036854775807,"cache_read_input_tokens":1,"cache_creation_input_tokens":0}""",
                "usage.input_tokens")]
    [InlineData("cursor",
                """{"inputTokens":9223372036854775807,"cacheReadTokens":1,"cacheWriteTokens":0}""",
                "usage.inputTokens")]
    public void Derived_total_overflow_is_omitted_and_names_the_input_field(string vendor,
                                                                            string json,
                                                                            string path)
    {
        var usage = Parse(vendor, Usage(json));

        Assert.Null(usage.InputTokens);
        Assert.Equal(1, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal([path], usage.MalformedFields);
    }

    [Fact]
    public void Reasoning_can_survive_without_output()
    {
        var usage = ProviderUsage.Codex(Usage("""{"reasoning_output_tokens":2}"""));

        Assert.Equal(2, usage.ReasoningTokens);
        Assert.Null(usage.MalformedFields);
    }

    private static JsonElement Usage(string json) => Element(json);

    private static WorkerUsage Parse(string vendor, JsonElement? usage) => vendor switch
    {
        "claude" => ProviderUsage.Claude(usage),
        "codex" => ProviderUsage.Codex(usage),
        "cursor" => ProviderUsage.Cursor(usage),
        _ => throw new ArgumentOutOfRangeException(nameof(vendor), vendor, null)
    };

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
