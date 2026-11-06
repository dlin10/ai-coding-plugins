namespace CodexSessionPulse.Core.Tests;

/// <summary>A settable clock for state-machine tests.</summary>
internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

/// <summary>Builds usage events with sensible defaults.</summary>
internal static class TestUsage
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 2, 13, 0, 0, TimeSpan.Zero);

    public static TokenUsageSnapshot Create(string threadId,
                                            DateTimeOffset atUtc,
                                            long input,
                                            long cached,
                                            long? write = 0,
                                            string? model = "gpt-6.1-sol",
                                            ThreadKind kind = ThreadKind.Main,
                                            string? rootSessionId = null,
                                            string? parentThreadId = null)
    {
        return new TokenUsageSnapshot(atUtc, threadId, rootSessionId ?? threadId, null, model, input, cached, write, 10,
                                      null, $"C:\\rollouts\\{threadId}.jsonl", atUtc.Ticks, kind, parentThreadId,
                                      "0.167.0-alpha.3.2");
    }
}
