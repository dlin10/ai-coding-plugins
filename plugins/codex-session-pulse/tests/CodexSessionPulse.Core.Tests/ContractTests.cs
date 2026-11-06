using CodexSessionPulse.Core;
using Xunit;

namespace CodexSessionPulse.Core.Tests;

/// <summary>Pins the frozen §4.3 contract so that an accidental rename or reorder fails a test.</summary>
public sealed class ContractTests
{
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

    /// <summary>A snapshot is an immutable value with structural equality, and speed is absent in P0.</summary>
    [Fact]
    public void CacheSnapshot_HasValueEquality()
    {
        DateTimeOffset at = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        CacheSnapshot Create() => new(
            at, CacheState.NoSession, null, ThreadKind.Main,
            new ThreadSelection(SelectionConfidence.Unknown, null),
            null, null, null, null,
            new CacheTtl(null, TtlBasis.Unknown), null,
            new SourceInfo(SourceStatus.NotFound, null, null), null);

        Assert.Equal(Create(), Create());
        Assert.Null(Create().Speed);
    }
}
