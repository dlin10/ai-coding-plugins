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
              "usage": { "input_tokens": 3, "output_tokens": 4 }
            }
            """);

        session.Observe(terminal.RootElement);

        Assert.Null(session.ObservedUsage.InputTokens);
        Assert.Equal(4, session.ObservedUsage.OutputTokens);
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

        Assert.Equal(10, session.ObservedUsage.InputTokens);
        Assert.Equal(4, session.ObservedUsage.CacheReadTokens);
        Assert.Equal(1, session.ObservedUsage.CacheCreationTokens);
        Assert.Equal(2, session.ObservedUsage.OutputTokens);
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
