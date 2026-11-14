using CodexSessionPulse.Core;

namespace CodexSessionPulse.App.Stub;

/// <summary>
/// The development stub behind <c>--overlay --stub</c> (spec §12 Step 2: the UI is built on a stub snapshot while the
/// process part is built). It scripts one cache snapshot per <see cref="CacheState"/>, plus the selection and TTL
/// variants the line has to render. Development and test only: it reads nothing.
/// </summary>
internal static class StubSnapshotScript
{
    private const string Model = "gpt-5.5-codex";
    private const string CliVersion = "0.130.0-stub";
    private const string RolloutPath = @"C:\stub\.codex\sessions\2026\11\07\rollout-2026-11-07T10-00-00-stub.jsonl";

    /// <summary>Builds the scripted step at a position of the loop.</summary>
    /// <param name="step">The step index; it wraps around.</param>
    /// <param name="nowUtc">The snapshot time.</param>
    /// <returns>The snapshot.</returns>
    public static CacheSnapshot At(int step, DateTimeOffset nowUtc)
    {
        TimeSpan ttl = TimeSpan.FromMinutes(30);
        SourceInfo available = new(SourceStatus.Available, RolloutPath, CliVersion);
        DateTimeOffset lastRequest = nowUtc - TimeSpan.FromMinutes(6);
        CacheSnapshot warm = new(nowUtc, CacheState.Warm, "stub-thread", ThreadKind.Main,
                                 new ThreadSelection(SelectionConfidence.Confirmed, "session-start-hook"), lastRequest,
                                 245_120, 245_349, null, new CacheTtl(ttl, TimeSpan.FromMinutes(24), TtlBasis.Assumed), null,
                                 available, null, Model, 1_024);

        return (step % Count) switch
        {
            0 => warm with { State = CacheState.NoSession, ThreadId = null, Kind = ThreadKind.Unknown,
                             Selection = new ThreadSelection(SelectionConfidence.Unknown, null), LastRequestAt = null,
                             CachedInput = null, Input = null, Ttl = new CacheTtl(null, null, TtlBasis.Unknown),
                             Source = new SourceInfo(SourceStatus.NotFound, null, null), Model = null, CacheWrite = null },
            1 => warm with { State = CacheState.WaitingForUsage, Selection = new ThreadSelection(SelectionConfidence.Inferred, "latest-rollout"),
                             LastRequestAt = null, CachedInput = null, Input = null, Ttl = new CacheTtl(null, null, TtlBasis.Unknown),
                             Model = null, CacheWrite = null },
            2 => warm,
            3 => warm with { Kind = ThreadKind.Subagent, Selection = new ThreadSelection(SelectionConfidence.Inferred, "latest-rollout"),
                             Ttl = new CacheTtl(null, null, TtlBasis.Unknown), CacheWrite = null },
            4 => warm with { State = CacheState.ExpiringSoon, LastRequestAt = nowUtc - TimeSpan.FromMinutes(24),
                             Ttl = new CacheTtl(ttl, TimeSpan.FromMinutes(6), TtlBasis.Assumed) },
            5 => warm with { State = CacheState.EstimatedExpired, LastRequestAt = nowUtc - TimeSpan.FromMinutes(31),
                             Ttl = new CacheTtl(ttl, TimeSpan.Zero, TtlBasis.Assumed) },
            6 => warm with { State = CacheState.ObservedMiss, LastRequestAt = nowUtc, CachedInput = 0, Input = 198_400,
                             Ttl = new CacheTtl(ttl, ttl, TtlBasis.Assumed), CacheWrite = 198_400 },
            7 => warm with { State = CacheState.Stale, LastRequestAt = nowUtc - TimeSpan.FromHours(3),
                             Ttl = new CacheTtl(ttl, TimeSpan.Zero, TtlBasis.Assumed), Diagnostics = "no-update" },
            _ => warm with { State = CacheState.Error, Source = new SourceInfo(SourceStatus.Failed, RolloutPath, CliVersion),
                             Diagnostics = "parser-failed" },
        };
    }

    /// <summary>The number of steps in the loop.</summary>
    public const int Count = 9;
}
