using System.Text.Json;
using PlanForge.Vendors;
using PlanForge.Vendors.Claude;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// Claude says what speed it will serve at `init`, before any API call, and says it again on
/// `result`. Measured against Claude Code 2.1.282 on 2026-09-25; see docs/adr/0023.
/// </summary>
public sealed class ClaudeFastModeTests
{
    private const string InitOff =
        """
        {"type":"system","subtype":"init","session_id":"s-1","model":"claude-opus-5-5","fast_mode_state":"off","fast_mode_disabled_reason":"extra_usage_disabled"}
        """;

    private const string InitOn =
        """
        {"type":"system","subtype":"init","session_id":"s-1","model":"claude-opus-5-5","fast_mode_state":"on","fast_mode_disabled_reason":null}
        """;

    /// <summary>
    /// A Fast request nothing confirmed is refused, and `init` is the last moment that costs nothing:
    /// the exception ends the stream, which kills the process before its first API call.
    /// </summary>
    [Fact]
    public void A_fast_request_the_session_will_not_serve_is_refused_at_init()
    {
        var session = NewSession(fast: true);
        using var init = JsonDocument.Parse(InitOff);

        var refusal = Assert.Throws<VendorException>(() => session.Observe(init.RootElement));

        Assert.Contains("claude-opus-5-5", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("extra_usage_disabled", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_standard_request_does_not_care_what_fast_state_init_reports()
    {
        var session = NewSession(fast: false);
        using var init = JsonDocument.Parse(InitOff);

        Assert.Null(session.Observe(init.RootElement));
        Assert.Null(session.SpeedWarning);
    }

    /// <summary>
    /// Claude falls back to standard speed on its own when Fast is rate-limited mid-turn. The work
    /// still counts; what was served is a warning, not a failure.
    /// </summary>
    [Fact]
    public void A_fast_turn_that_fell_back_during_the_turn_is_a_warning()
    {
        var session = NewSession(fast: true);
        using var init = JsonDocument.Parse(InitOn);
        using var result = JsonDocument.Parse(
            """{"type":"result","subtype":"success","is_error":false,"fast_mode_state":"cooldown"}""");

        session.Observe(init.RootElement);
        Assert.Null(session.SpeedWarning);
        session.Observe(result.RootElement);

        Assert.Contains("cooldown", session.SpeedWarning, StringComparison.Ordinal);
        Assert.Equal("cooldown", session.ServedFastState);
    }

    [Fact]
    public void A_fast_turn_served_throughout_carries_no_warning()
    {
        var session = NewSession(fast: true);
        using var init = JsonDocument.Parse(InitOn);
        using var result = JsonDocument.Parse(
            """{"type":"result","subtype":"success","is_error":false,"fast_mode_state":"on"}""");

        session.Observe(init.RootElement);
        session.Observe(result.RootElement);

        Assert.Null(session.SpeedWarning);
        Assert.Equal("on", session.ServedFastState);
    }

    private static ClaudeCliSession NewSession(bool fast) =>
        new(new RoleSpec(VendorRole.Builder, "build"), new Selection("opus", null, Fast: fast), null);
}
