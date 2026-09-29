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
              "claude-opus-5-5": {
                "inputTokens": 11,
                "cacheReadInputTokens": 22,
                "cacheCreationInputTokens": 33,
                "outputTokens": 44,
                "thinkingTokens": 40,
                "costUSD": 0.5
              }
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

    /// <summary>
    /// A subagent on another model gets an entry of its own, and the turn used both; the result
    /// line's `usage` holds the main loop alone.
    /// </summary>
    [Fact]
    public void Claude_counts_every_model_the_turn_used()
    {
        var usage = ProviderUsage.Claude(Usage(
            """
            {
              "claude-opus-5-5": {
                "inputTokens": 10, "cacheReadInputTokens": 900, "cacheCreationInputTokens": 90,
                "outputTokens": 50, "thinkingTokens": 20
              },
              "claude-haiku-4-5-20251001": {
                "inputTokens": 5, "cacheReadInputTokens": 400, "cacheCreationInputTokens": 45,
                "outputTokens": 30, "thinkingTokens": 0
              }
            }
            """));

        Assert.Equal(1450, usage.InputTokens);
        Assert.Equal(1300, usage.CacheReadTokens);
        Assert.Equal(135, usage.CacheCreationTokens);
        Assert.Equal(80, usage.OutputTokens);
        Assert.Equal(20, usage.ReasoningTokens);
        Assert.Null(usage.MalformedFields);
    }

    /// <summary>
    /// A result before any model call reports no models. Zeros there would become a resumed
    /// session's running total, and the attempt after it would count the whole session again.
    /// </summary>
    [Fact]
    public void Claude_reporting_no_model_has_no_counters()
    {
        var usage = ProviderUsage.Claude(Usage("{}"), previous: new WorkerUsage(100, 80, 10, 5, 1));

        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.ReasoningTokens);
        Assert.Null(usage.MalformedFields);
        Assert.Null(usage.SessionTotal);
    }

    [Fact]
    public void Claude_sum_past_the_counter_range_is_named_and_omitted()
    {
        var usage = ProviderUsage.Claude(Usage(
            """{"claude-opus-5-5":{"outputTokens":9223372036854775807},"claude-haiku-4-5":{"outputTokens":1}}"""));

        Assert.Null(usage.OutputTokens);
        Assert.Equal(["modelUsage.*.outputTokens"], usage.MalformedFields);
    }

    [Fact]
    public void Claude_names_the_model_whose_entry_is_malformed()
    {
        var usage = ProviderUsage.Claude(Usage(
            """{"claude-opus-5-5":{"outputTokens":4},"claude-haiku-4-5":"bad"}"""));

        Assert.Null(usage.OutputTokens);
        Assert.Equal(["modelUsage.claude-haiku-4-5"], usage.MalformedFields);
    }

    [Fact]
    public void Claude_names_a_malformed_cost_and_keeps_the_counters()
    {
        var usage = ProviderUsage.Claude(Usage("""{"claude-opus-5-5":{"outputTokens":4}}"""), Element("\"0.42\""));

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
        Assert.Null(usage.SessionTotal);
    }

    /// <summary>
    /// codex reports the thread's running total, restored from its rollout on resume: the review_fix
    /// round 3 turn of run 20260927-212553-b98178 reported 81,766,668 input tokens, of which its own
    /// calls in the rollout came to 8,779,454.
    /// </summary>
    [Fact]
    public void Codex_resumed_turn_is_what_it_added_to_the_thread_total()
    {
        var usage = ProviderUsage.Codex(Usage(
            """
            {
              "input_tokens": 81766668,
              "cached_input_tokens": 80929920,
              "cache_write_input_tokens": 0,
              "output_tokens": 217805,
              "reasoning_output_tokens": 90000
            }
            """), new WorkerUsage(72987214, 72190720, 0, 210022, 85000));

        Assert.Equal(8779454, usage.InputTokens);
        Assert.Equal(8739200, usage.CacheReadTokens);
        Assert.Equal(0, usage.CacheCreationTokens);
        Assert.Equal(7783, usage.OutputTokens);
        Assert.Equal(5000, usage.ReasoningTokens);
        Assert.Null(usage.MalformedFields);
        Assert.Equal(new WorkerUsage(81766668, 80929920, 0, 217805, 90000), usage.SessionTotal);
    }

    [Fact]
    public void Codex_total_below_the_previous_report_is_named_and_still_kept_as_the_total()
    {
        var usage = ProviderUsage.Codex(Usage("""{"input_tokens":900,"cached_input_tokens":800,"output_tokens":50}"""),
                                        new WorkerUsage(1000, 700, OutputTokens: 40));

        Assert.Null(usage.InputTokens);
        Assert.Equal(100, usage.CacheReadTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.Equal(["usage.input_tokens"], usage.MalformedFields);
        Assert.Equal(900, usage.SessionTotal?.InputTokens);
    }

    /// <summary>
    /// claude's `modelUsage` and `total_cost_usd` are the session's, restored on `--resume` from the
    /// transcript's `cost-state`. The build task 6 turn of run 20260928-113803-31c1d7 auto-compacted
    /// a 967,179-token context: its `result.usage` held 36,293,602 input tokens, what it added to
    /// `modelUsage` was 37,260,634.
    /// </summary>
    [Fact]
    public void Claude_resumed_turn_is_what_it_added_to_the_session_totals_compaction_included()
    {
        var usage = ProviderUsage.Claude(Usage(
            """
            {
              "claude-opus-5-5": {
                "inputTokens": 4112,
                "cacheReadInputTokens": 168137446,
                "cacheCreationInputTokens": 1165238,
                "outputTokens": 565880,
                "thinkingTokens": 272185
              }
            }
            """), Element("54.2834412"),
            new WorkerUsage(132046162, 131163158, 882512, 446145, 230131, CostUsd: 42.2175956m));

        Assert.Equal(37260634, usage.InputTokens);
        Assert.Equal(36974288, usage.CacheReadTokens);
        Assert.Equal(282726, usage.CacheCreationTokens);
        Assert.Equal(119735, usage.OutputTokens);
        Assert.Equal(42054, usage.ReasoningTokens);
        Assert.Equal(12.0658456m, usage.CostUsd);
        Assert.Null(usage.MalformedFields);
        Assert.Equal(new WorkerUsage(169306796, 168137446, 1165238, 565880, 272185, CostUsd: 54.2834412m),
                     usage.SessionTotal);
    }

    [Fact]
    public void Claude_cost_below_the_previous_report_is_named_and_the_tokens_kept()
    {
        var usage = ProviderUsage.Claude(Usage("""{"claude-opus-5-5":{"outputTokens":4}}"""), Element("1.5"),
                                         new WorkerUsage(CostUsd: 2m));

        Assert.Null(usage.CostUsd);
        Assert.Equal(4, usage.OutputTokens);
        Assert.Equal(["total_cost_usd"], usage.MalformedFields);
        Assert.Equal(1.5m, usage.SessionTotal?.CostUsd);
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
    [InlineData("claude", """{"claude-opus-5-5":{"inputTokens":"bad","outputTokens":3}}""",
                "modelUsage.claude-opus-5-5.inputTokens")]
    [InlineData("codex", """{"input_tokens":"bad","output_tokens":3}""", "usage.input_tokens")]
    [InlineData("cursor", """{"inputTokens":"bad","outputTokens":3}""", "usage.inputTokens")]
    public void Every_vendor_preserves_independent_counters_when_one_is_malformed(string vendor, string json, string path)
    {
        var usage = Parse(vendor, Usage(json));

        Assert.Null(usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal([path], usage.MalformedFields);
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
                """{"claude-opus-5-5":{"inputTokens":1,"cacheReadInputTokens":2,"outputTokens":4}}""")]
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
                """{"claude-opus-5-5":{"inputTokens":1,"cacheReadInputTokens":"bad","cacheCreationInputTokens":3,"outputTokens":4}}""",
                "modelUsage.claude-opus-5-5.cacheReadInputTokens")]
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
                """{"claude-opus-5-5":{"inputTokens":9223372036854775807,"cacheReadInputTokens":1,"cacheCreationInputTokens":0}}""",
                "modelUsage.*.inputTokens")]
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
