using CodexSessionPulse.Core;
using CodexSessionPulse.Infrastructure.Rollouts;
using Xunit;

namespace CodexSessionPulse.Infrastructure.Tests.Rollouts;

/// <summary>The anonymized captures of #250 and the synthetic §1.2 sample, read through the tail (spec §5, §6.1).</summary>
public sealed class RolloutFixtureTests : IDisposable
{
    private const string Main1 = "rollout-2026-11-06T23-50-33-00000000-0000-7000-8000-000000000001.jsonl";
    private const string Segment101 = "rollout-2026-11-07T00-41-23-00000000-0000-7000-8000-000000000001_00000000-0000-7000-8000-000000000101.jsonl";
    private const string Fork2 = "rollout-2026-11-06T23-54-34-00000000-0000-7000-8000-000000000002.jsonl";
    private const string Subagent3 = "rollout-2026-11-06T23-56-50-00000000-0000-7000-8000-000000000003.jsonl";
    private const string Exec48 = "rollout-2026-09-03T23-19-24-00000000-0000-7000-8000-000000000048.jsonl";
    private const string Inherited53 = "rollout-2026-09-14T11-14-32-00000000-0000-7000-8000-000000000053.jsonl";
    private const string Sample201 = "synthetic/rollout-2026-11-02T15-20-00-00000000-0000-7000-8000-000000000201.jsonl";

    private readonly RolloutTestRoot _root = new();

    /// <inheritdoc/>
    public void Dispose()
    {
        _root.Dispose();
    }

    /// <summary>The main thread: two requests from last_token_usage, model from turn_context, CLI version from session_meta.</summary>
    [Fact]
    public void Poll_MainThread_ReadsLastUsageModelAndCliVersion()
    {
        _root.CopyFixture(Main1);

        RolloutPollResult result = _root.CreateTail().Poll();

        Assert.Equal([40728L, 40755L], result.Usages.Select(u => u.InputTokens));
        TokenUsageSnapshot last = result.Usages[^1];
        Assert.Equal("00000000-0000-7000-8000-000000000001", last.ThreadId);
        Assert.Equal(40448, last.CachedInputTokens);
        Assert.Equal(0, last.CacheWriteInputTokens);
        Assert.Equal(7, last.OutputTokens);
        Assert.Equal("gpt-6.1-sol", last.Model);
        Assert.Equal("0.167.0-alpha.3.2", last.CliVersion);
        Assert.Equal(ThreadKind.Main, last.Kind);
        Assert.Equal(31, last.EventSequence);
        ThreadObservation observation = Assert.Single(result.Observations);
        Assert.Equal(ThreadKind.Main, observation.Kind);
        Assert.Equal("0.167.0-alpha.3.2", observation.CliVersion);
    }

    /// <summary>The fork keeps its own id and parent, and the all-zero record after /Compact is no request.</summary>
    [Fact]
    public void Poll_Fork_IsItsOwnThreadAndSkipsTheCompactionZero()
    {
        _root.CopyFixture(Fork2);
        RolloutTail tail = _root.CreateTail();

        RolloutPollResult result = tail.Poll();

        Assert.Equal([51361L, 37371L, 37425L, 37505L, 37587L], result.Usages.Select(u => u.InputTokens));
        Assert.All(result.Usages, u => Assert.Equal("00000000-0000-7000-8000-000000000002", u.ThreadId));
        Assert.All(result.Usages, u => Assert.Equal(ThreadKind.Fork, u.Kind));
        Assert.All(result.Usages, u => Assert.Equal("00000000-0000-7000-8000-000000000001", u.ParentThreadId));
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.AllZeroUsage));
        Assert.Equal(53, result.Usages[0].EventSequence);
    }

    /// <summary>The subagent is recognized by thread_source and parent_thread_id; its root is the parent's session.</summary>
    [Fact]
    public void Poll_Subagent_IsMarkedWithItsParent()
    {
        _root.CopyFixture(Subagent3);

        TokenUsageSnapshot usage = Assert.Single(_root.CreateTail().Poll().Usages);

        Assert.Equal("00000000-0000-7000-8000-000000000003", usage.ThreadId);
        Assert.Equal(ThreadKind.Subagent, usage.Kind);
        Assert.Equal("00000000-0000-7000-8000-000000000002", usage.ParentThreadId);
        Assert.Equal("00000000-0000-7000-8000-000000000002", usage.RootSessionId);
    }

    /// <summary>Inherited history: both token_count records after the boundary belong to thread 53, not to the parent.</summary>
    [Fact]
    public void Poll_InheritedHistory_AttributesByOrdinalBoundary()
    {
        _root.CopyFixture(Inherited53);

        RolloutPollResult result = _root.CreateTail().Poll();

        Assert.Equal([29624L, 30900L], result.Usages.Select(u => u.InputTokens));
        Assert.All(result.Usages, u => Assert.Equal("00000000-0000-7000-8000-000000000053", u.ThreadId));
        Assert.All(result.Usages, u => Assert.Equal(ThreadKind.Subagent, u.Kind));
        Assert.Equal("00000000-0000-7000-8000-000000000053", Assert.Single(result.Observations).ThreadId);
    }

    /// <summary>A token_count below the ordinal boundary is a copied parent prefix and is not counted for anyone.</summary>
    [Fact]
    public void Poll_TokenCountInsideCopiedPrefix_IsSkipped()
    {
        string path = _root.RolloutPath(RolloutTestRoot.ThreadA);
        File.WriteAllText(path, RolloutTestRoot.Meta(RolloutTestRoot.ThreadA, extra: ",\"forked_from_id\":\"p\",\"subagent_history_start_ordinal\":5") + "\n"
                              + RolloutTestRoot.Meta(RolloutTestRoot.ThreadB) + "\n"
                              + RolloutTestRoot.TokenCount(3, 1, 9000, 8000) + "\n"
                              + RolloutTestRoot.TokenCount(6, 2, 10000, 9000) + "\n");
        RolloutTail tail = _root.CreateTail();

        TokenUsageSnapshot usage = Assert.Single(tail.Poll().Usages);

        Assert.Equal(RolloutTestRoot.ThreadA, usage.ThreadId);
        Assert.Equal(10000, usage.InputTokens);
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.InheritedPrefix));
    }

    /// <summary>The edit segment (_rollout-id) continues thread 1.</summary>
    [Fact]
    public void Poll_EditSegment_BelongsToTheSameThread()
    {
        _root.CopyFixture(Main1);
        _root.CopyFixture(Segment101);

        RolloutPollResult result = _root.CreateTail().Poll();

        Assert.Equal(8, result.Usages.Count);
        Assert.All(result.Usages, u => Assert.Equal("00000000-0000-7000-8000-000000000001", u.ThreadId));
        Assert.Contains(result.Usages, u => u.InputTokens == 64244 && u.EventSequence == 104);
    }

    /// <summary>info: null is skipped and counted, not read as zero tokens.</summary>
    [Fact]
    public void Poll_InfoNull_IsSkippedNotZero()
    {
        _root.CopyFixture(Exec48);
        RolloutTail tail = _root.CreateTail();

        RolloutPollResult result = tail.Poll();

        Assert.Empty(result.Usages);
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.InfoNull));
        Assert.Equal("0.147.0", Assert.Single(result.Observations).CliVersion);
    }

    /// <summary>The §1.2 sample: five requests, the duplicate, info: null and the compaction zero dropped.</summary>
    [Fact]
    public void Poll_SpecSample_YieldsFiveRequestsEndingAt245349()
    {
        _root.CopyFixture(Sample201);
        RolloutTail tail = _root.CreateTail();

        RolloutPollResult result = tail.Poll();

        Assert.Equal([239071L, 240392L, 242386L, 244157L, 245349L], result.Usages.Select(u => u.InputTokens));
        TokenUsageSnapshot last = result.Usages[^1];
        Assert.Equal(245120, last.CachedInputTokens);
        Assert.Equal(26328, last.OutputTokens);
        Assert.Equal("gpt-5.5", last.Model);
        Assert.Equal("00000000-0000-7000-8000-000000000301", last.TurnId);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 13, 32, 2, 506, TimeSpan.Zero), last.ObservedAtUtc);
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.InfoNull));
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.DuplicateUsage));
        Assert.Equal(1, tail.Diagnostics.Count(DiagnosticCodes.AllZeroUsage));
    }

    /// <summary>A .jsonl.zst next to the plain file is never read.</summary>
    [Fact]
    public void Poll_CompressedRollout_IsNotRead()
    {
        _root.CopyFixture(Main1 + ".zst");
        RolloutTail tail = _root.CreateTail();

        RolloutPollResult result = tail.Poll();

        Assert.Empty(result.Usages);
        Assert.Equal(0, tail.TrackedFiles);
    }

    /// <summary>Every fixture through the tail into the engine: separate threads, the subagent hidden, the latest main shown as inferred.</summary>
    [Fact]
    public void Engine_AllFixtures_ShowsTheLatestMainThreadInferred()
    {
        foreach (string name in new[] { Main1, Segment101, Fork2, Subagent3, Exec48, Inherited53, Sample201 })
        {
            _root.CopyFixture(name);
        }

        RolloutPollResult result = _root.CreateTail().Poll();
        PulseEngine engine = new(CacheTrackerOptions.Default, _root.Clock);

        foreach (ThreadObservation observation in result.Observations)
        {
            engine.Observe(observation);
        }

        foreach (TokenUsageSnapshot usage in result.Usages)
        {
            engine.Apply(usage);
        }

        _root.Clock.UtcNow = result.Usages.Max(u => u.ObservedAtUtc).AddMinutes(1);
        CacheSnapshot snapshot = engine.Tick();

        Assert.Equal("00000000-0000-7000-8000-000000000001", snapshot.ThreadId);
        Assert.Equal(64244, snapshot.Input);
        Assert.Equal(64000, snapshot.CachedInput);
        Assert.Equal(ThreadKind.Main, snapshot.Kind);
        Assert.Equal(new ThreadSelection(SelectionConfidence.Inferred, SelectionReasons.LatestJsonlActivity), snapshot.Selection);
        Assert.Equal(CacheState.Warm, snapshot.State);
        Assert.Null(snapshot.Speed);
    }

    /// <summary>The reader's diagnostics are codes only: no text, path or identifier ever becomes a key.</summary>
    [Fact]
    public void Diagnostics_AreDiagnosticCodesOnly()
    {
        foreach (string name in new[] { Fork2, Exec48, Sample201 })
        {
            _root.CopyFixture(name);
        }

        RolloutTail tail = _root.CreateTail();
        tail.Poll();
        HashSet<string> codes = typeof(DiagnosticCodes).GetFields()
                                                       .Select(f => (string)f.GetRawConstantValue()!)
                                                       .ToHashSet();

        Assert.NotEmpty(tail.Diagnostics.Snapshot());
        Assert.All(tail.Diagnostics.Snapshot().Keys, key => Assert.Contains(key, codes));
    }
}
