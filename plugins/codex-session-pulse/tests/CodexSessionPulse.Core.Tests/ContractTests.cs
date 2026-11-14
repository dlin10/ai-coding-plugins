using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.Core.Tests;

/// <summary>Pins the frozen §4.3 contract so that an accidental rename or reorder fails a test.</summary>
public sealed class ContractTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The state enum lists exactly the §3.2 states.</summary>
    [Fact]
    public void CacheState_ListsTheSpecStates()
    {
        string[] expected =
        [
            "NoSession", "WaitingForUsage", "Warm", "ExpiringSoon",
            "EstimatedExpired", "ObservedMiss", "Stale", "Error",
        ];

        Assert.Equal(expected, Enum.GetNames<CacheState>());
    }

    /// <summary>The thread kind allows an undetermined kind (§5.3).</summary>
    [Fact]
    public void ThreadKind_ListsMainForkSubagentAndUnknown()
    {
        Assert.Equal(new[] { "Main", "Fork", "Subagent", "Unknown" }, Enum.GetNames<ThreadKind>());
    }

    /// <summary>The anchor state lists the §3.3 display states.</summary>
    [Fact]
    public void AnchorState_ListsTheSpecStates()
    {
        Assert.Equal(new[] { "NotFound", "Attached", "Background", "Minimized" }, Enum.GetNames<AnchorState>());
    }

    /// <summary>A snapshot is an immutable value with structural equality, and speed is absent in P0.</summary>
    [Fact]
    public void CacheSnapshot_HasValueEquality()
    {
        Assert.Equal(CreateCacheSnapshot(), CreateCacheSnapshot());
        Assert.Null(CreateCacheSnapshot().Speed);
    }

    /// <summary>The snapshot carries the model and the cache-write count for the tooltip (§3.1).</summary>
    [Fact]
    public void CacheSnapshot_CarriesModelAndCacheWrite()
    {
        CacheSnapshot snapshot = CreateCacheSnapshot() with { Model = "gpt-5", CacheWrite = 1200 };

        Assert.Equal("gpt-5", snapshot.Model);
        Assert.Equal(1200, snapshot.CacheWrite);
    }

    /// <summary>The TTL carries the configured total and the remaining time separately.</summary>
    [Fact]
    public void CacheTtl_CarriesEstimateAndRemaining()
    {
        CacheTtl ttl = new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(24), TtlBasis.Assumed);

        Assert.Equal(TimeSpan.FromMinutes(30), ttl.Estimate);
        Assert.Equal(TimeSpan.FromMinutes(24), ttl.Remaining);
    }

    /// <summary>The anchor snapshot carries state, handle, work area and diagnostics (§3.3).</summary>
    [Fact]
    public void AnchorSnapshot_CarriesStateHwndWorkAreaAndDiagnostics()
    {
        AnchorSnapshot snapshot = new(
            At, false, 0, null, 1.0,
            AnchorState.NotFound, 0, new WindowBounds(0, 0, 1920, 1040), "window-not-found");

        Assert.Equal(AnchorState.NotFound, snapshot.State);
        Assert.Equal(0, snapshot.Hwnd);
        Assert.Equal(new WindowBounds(0, 0, 1920, 1040), snapshot.WorkArea);
        Assert.Equal("window-not-found", snapshot.Diagnostics);
    }

    /// <summary>An unknown cache write is null, never 0, and the usage event carries kind, parent and CLI version (§5.3, §6.1).</summary>
    [Fact]
    public void TokenUsageSnapshot_CarriesNullableCacheWriteKindParentAndCliVersion()
    {
        TokenUsageSnapshot usage = new(
            At, "t1", null, null, null, 100, 80, null, 5, null, "rollout.jsonl", 1,
            ThreadKind.Subagent, "t0", "0.1.2");

        Assert.Null(usage.CacheWriteInputTokens);
        Assert.Equal(ThreadKind.Subagent, usage.Kind);
        Assert.Equal("t0", usage.ParentThreadId);
        Assert.Equal("0.1.2", usage.CliVersion);
    }

    private static CacheSnapshot CreateCacheSnapshot() => new(
        At, CacheState.NoSession, null, ThreadKind.Main,
        new ThreadSelection(SelectionConfidence.Unknown, null),
        null, null, null, null,
        new CacheTtl(null, null, TtlBasis.Unknown), null,
        new SourceInfo(SourceStatus.NotFound, null, null), null, null, null);
}
