using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using PlanForge.Vendors.Codex;
using PlanForge.Vendors.Cursor;
using Xunit;

namespace PlanForge.Tests;

public sealed class VendorUsageCaptureTests
{
    [Fact]
    public void Claude_keeps_usage_from_a_failed_terminal_result()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "prompt"),
                                           new Selection("model", null), null);
        using var terminal = JsonDocument.Parse(
            """
            {
              "type": "result",
              "subtype": "error_during_execution",
              "is_error": true,
              "modelUsage": { "claude-opus-5-5": { "inputTokens": 3, "outputTokens": 4 } }
            }
            """);

        session.Observe(terminal.RootElement);

        Assert.Null(session.UsageSince(null).InputTokens);
        Assert.Equal(4, session.UsageSince(null).OutputTokens);
    }

    /// <summary>
    /// A fresh Scout of run 20260925-162135-46c10c ran four subagents: its `usage` held 381,084
    /// cache-read tokens, its `modelUsage` 23,647,257.
    /// </summary>
    [Fact]
    public void Claude_counts_the_models_usage_rather_than_the_main_loops()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Scout, "prompt"),
                                           new Selection("model", null), null);
        using var terminal = JsonDocument.Parse(
            """
            {
              "type": "result",
              "subtype": "success",
              "usage": {
                "input_tokens": 3, "cache_read_input_tokens": 100, "cache_creation_input_tokens": 10,
                "output_tokens": 4
              },
              "modelUsage": {
                "claude-opus-5-5": {
                  "inputTokens": 5, "cacheReadInputTokens": 700, "cacheCreationInputTokens": 30,
                  "outputTokens": 9, "thinkingTokens": 2
                },
                "claude-haiku-4-5-20251001": {
                  "inputTokens": 1, "cacheReadInputTokens": 50, "cacheCreationInputTokens": 5,
                  "outputTokens": 3, "thinkingTokens": 0
                }
              }
            }
            """);

        session.Observe(terminal.RootElement);

        var usage = session.UsageSince(null);
        Assert.Equal(791, usage.InputTokens);
        Assert.Equal(750, usage.CacheReadTokens);
        Assert.Equal(12, usage.OutputTokens);
        Assert.Equal(2, usage.ReasoningTokens);
    }

    [Fact]
    public void Claude_keeps_the_reported_cost_of_the_attempt()
    {
        var session = new ClaudeCliSession(new RoleSpec(VendorRole.Builder, "prompt"),
                                           new Selection("model", null), null);
        using var terminal = JsonDocument.Parse(
            """
            {
              "type": "result",
              "subtype": "success",
              "total_cost_usd": 0.4213875,
              "modelUsage": { "claude-opus-5-5": { "inputTokens": 3, "outputTokens": 4 } }
            }
            """);

        session.Observe(terminal.RootElement);

        Assert.Equal(0.4213875m, session.UsageSince(null).CostUsd);
    }

    [Fact]
    public void Codex_keeps_usage_from_a_failed_terminal_turn()
    {
        var session = new CodexCliSession(new RoleSpec(VendorRole.Critic, "prompt"),
                                          new Selection("model", null), null);
        using var terminal = JsonDocument.Parse(
            """
            {
              "type": "turn.failed",
              "error": { "message": "provider refused" },
              "usage": {
                "input_tokens": 10,
                "cached_input_tokens": 4,
                "cache_write_input_tokens": 1,
                "output_tokens": 2
              }
            }
            """);

        session.Observe(terminal.RootElement);

        var usage = session.UsageSince(null);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(4, usage.CacheReadTokens);
        Assert.Equal(1, usage.CacheCreationTokens);
        Assert.Equal(2, usage.OutputTokens);
    }

    [Fact]
    public void Cursor_keeps_usage_when_the_terminal_text_is_not_valid_structured_output()
    {
        var session = new CursorAgentSession(new RoleSpec(VendorRole.Critic, "prompt"),
                                             new Selection("model", null), null);
        using var terminal = JsonDocument.Parse(
            """
            {
              "type": "result",
              "result": "not the requested object",
              "usage": {
                "inputTokens": 6,
                "cacheReadTokens": 7,
                "cacheWriteTokens": 8,
                "outputTokens": 9
              }
            }
            """);

        Assert.Equal("not the requested object", session.Observe(terminal.RootElement));
        Assert.Equal(21, session.ObservedUsage.InputTokens);
        Assert.Equal(7, session.ObservedUsage.CacheReadTokens);
        Assert.Equal(8, session.ObservedUsage.CacheCreationTokens);
        Assert.Equal(9, session.ObservedUsage.OutputTokens);
    }

    [Fact]
    public void Cursor_keeps_usage_when_the_terminal_result_has_no_text_payload()
    {
        var session = new CursorAgentSession(new RoleSpec(VendorRole.Critic, "prompt"),
                                             new Selection("model", null), null);
        using var terminal = JsonDocument.Parse(
            """{"type":"result","usage":{"inputTokens":6,"outputTokens":9}}""");

        Assert.Null(session.Observe(terminal.RootElement));
        Assert.Null(session.ObservedUsage.InputTokens);
        Assert.Equal(9, session.ObservedUsage.OutputTokens);
    }
}
