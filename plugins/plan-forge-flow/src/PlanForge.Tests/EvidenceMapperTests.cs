using System.Text.Json;
using PlanForge.Advisors;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Exercises bounded mapping through a scripted Advisor, a manual clock and counted store writes.</summary>
public sealed class EvidenceMapperTests
{
    /// <summary>A small map sends one packed request and stores every distinct judgment.</summary>
    [Fact]
    public async Task Small_map_completes_in_one_call()
    {
        using var run = new MapperRun();
        var map = await run.Call();
        Assert.True(map.Result.Complete);
        Assert.Equal(2, map.Result.Required);
        Assert.Equal(2, map.Result.CheckedThisCall);
        Assert.Equal(0, map.Result.Remaining);
        Assert.Single(run.Advisor.Requests);
        Assert.Equal("covered", Assert.Single(map.Observation).Status);
        Assert.Equal(2, run.Store().Judgments.Count);
    }

    /// <summary>The dispatch boundary retains completed keys and resumes only missing keys next time.</summary>
    [Fact]
    public async Task Forty_second_budget_resumes_with_cache_hits()
    {
        using var run = MapperRun.Plain(3);
        run.Advisor.Handler = (request, _) =>
        {
            run.Clock.Advance(TimeSpan.FromSeconds(20));
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        var first = await run.Call();
        Assert.False(first.Result.Complete);
        Assert.Equal(1, first.Result.Remaining);
        Assert.Equal(2, first.Result.CheckedThisCall);
        var sent = run.Advisor.Requests.Select(request => request.State["item"]!["text"]!.GetValue<string>()).ToArray();
        run.Advisor.Handler = (request, _) => Task.FromResult(FakeAdvisor.Answer(request));
        var second = await run.Call();
        Assert.True(second.Result.Complete);
        Assert.Equal(2, second.Result.CacheHits);
        Assert.Equal(1, second.Result.CheckedThisCall);
        Assert.Equal(3, run.Advisor.Requests.Count);
        Assert.DoesNotContain(run.Advisor.Requests[^1].State["item"]!["text"]!.GetValue<string>(), sent);
    }

    /// <summary>Only the configured number of simultaneous requests is started.</summary>
    [Fact]
    public async Task In_flight_requests_never_exceed_the_limit()
    {
        using var run = MapperRun.Plain(5);
        run.Advisor.Limits = run.Advisor.Limits with { MaxConcurrency = 2 };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Advisor.Handler = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return FakeAdvisor.Answer(request);
        };
        var task = run.Call();
        Assert.Equal(2, run.Advisor.Requests.Count);
        release.SetResult();
        await task;
        Assert.Equal(2, run.Advisor.PeakConcurrency);
        Assert.Equal(5, run.Advisor.Requests.Count);
    }

    /// <summary>The portion deadline cancels running work while queued keys remain unsent.</summary>
    [Fact]
    public async Task Forty_five_seconds_cancels_in_flight_requests_transiently()
    {
        using var run = MapperRun.Plain(2);
        run.Advisor.Handler = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException();
        };
        var task = run.Call();
        run.Clock.Advance(TimeSpan.FromSeconds(45));
        var map = await task;
        Assert.Equal("cancelled", Outcome(map, 0).Reason);
        Assert.Equal("not_yet_checked", Outcome(map, 1).Reason);
        Assert.Equal(2, map.Result.Remaining);
        Assert.False(map.Result.Complete);
        Assert.Empty(run.Store().Judgments);
        Assert.Equal(45000, Assert.Single(run.Mapper.Progress.AskDurations));
    }

    /// <summary>Local measurement time counts toward the call deadline, rather than extending it.</summary>
    [Fact]
    public async Task Portion_deadline_includes_local_preparation_time()
    {
        using var run = MapperRun.Plain(1);
        run.Advisor.OnMeasure = () =>
        {
            run.Advisor.OnMeasure = null;
            run.Clock.Advance(TimeSpan.FromSeconds(5));
        };
        run.Advisor.Handler = (request, ct) =>
        {
            run.Clock.Advance(TimeSpan.FromSeconds(40));
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        var map = await run.Call();
        Assert.Equal("cancelled", Outcome(map, 0).Reason);
        Assert.Equal(1, map.Result.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(45), run.Mapper.Progress.Duration);
        Assert.Equal(40000d, Assert.Single(run.Mapper.Progress.AskDurations));
    }

    /// <summary>Unauthorized stops dispatch and is never cached.</summary>
    [Fact]
    public Task Unauthorized_stops_without_persistence() => Failure(AdvisorFailureKind.Unauthorized, "unauthorized", false, true);
    /// <summary>Forbidden stops dispatch and is never cached.</summary>
    [Fact]
    public Task Forbidden_stops_without_persistence() => Failure(AdvisorFailureKind.Forbidden, "forbidden", false, true);
    /// <summary>Rate limiting remains retryable on the next map call.</summary>
    [Fact]
    public Task Rate_limited_is_transient() => Failure(AdvisorFailureKind.RateLimited, "rate_limited", false);
    /// <summary>Overload remains retryable on the next map call.</summary>
    [Fact]
    public Task Overloaded_is_transient() => Failure(AdvisorFailureKind.Overloaded, "overloaded", false);
    /// <summary>A single oversized judgment is cached as too large.</summary>
    [Fact]
    public Task Too_large_is_persistent() => Failure(AdvisorFailureKind.TooLarge, "too_large", true);
    /// <summary>A single rejected judgment is cached as rejected.</summary>
    [Fact]
    public Task Rejected_is_persistent() => Failure(AdvisorFailureKind.Rejected, "rejected", true);
    /// <summary>A service timeout is distinct from caller cancellation.</summary>
    [Fact]
    public Task Timed_out_is_transient() => Failure(AdvisorFailureKind.TimedOut, "timed_out", false);
    /// <summary>An unavailable service leaves retryable work.</summary>
    [Fact]
    public Task Unavailable_is_transient() => Failure(AdvisorFailureKind.Unavailable, "unavailable", false);
    /// <summary>An invalid wire response is retryable rather than cached as an invalid answer.</summary>
    [Fact]
    public Task Invalid_response_is_transient() => Failure(AdvisorFailureKind.InvalidResponse, "invalid_response", false);

    /// <summary>A request-wide persistent failure cannot poison judgments packed beside one another.</summary>
    [Fact]
    public async Task Persistent_three_judgment_failure_reasks_each_alone()
    {
        using var run = new MapperRun { Plan = MapperRun.TwoUnits };
        run.Advisor.Handler = (_, _) => throw new AdvisorException(AdvisorFailureKind.TooLarge);
        var map = await run.Call();
        Assert.True(map.Result.Complete);
        Assert.Equal(4, run.Advisor.Requests.Count);
        Assert.Equal(5, run.Advisor.Requests[0].Questions.Count);
        Assert.Equal([1, 2, 2], run.Advisor.Requests.Skip(1).Select(request => request.Questions.Count));
        Assert.Equal(3, run.Store().Judgments.Count);
        Assert.Equal(3, map.Result.CheckedThisCall);
        Assert.All(run.Store().Judgments.Values, part => Assert.All(part.Questions.Values,
            outcome => Assert.Equal("too_large", outcome.Reason)));
    }

    /// <summary>An invalid individual answer is persisted without splitting its request.</summary>
    [Fact]
    public async Task Per_question_invalid_answer_is_persistent()
    {
        using var run = new MapperRun();
        run.Advisor.Choose = question => question.Id == "relevance"
            ? new Answered("not-an-option", new Dictionary<string, double>()) : FakeAdvisor.Valid(question);
        var map = await run.Call();
        Assert.Equal("invalid_answer", Outcome(map, 0).Reason);
        Assert.True(map.Result.Complete);
        Assert.Single(run.Advisor.Requests);
        Assert.Equal(2, (await run.Call()).Result.CacheHits);
        Assert.Single(run.Advisor.Requests);
    }

    /// <summary>A refusal is a reusable per-question outcome.</summary>
    [Fact]
    public async Task Refusal_is_persistent()
    {
        using var run = new MapperRun();
        run.Advisor.Choose = _ => new Refused();
        var map = await run.Call();
        Assert.Equal("refused", Outcome(map, 0).Reason);
        Assert.Equal(2, run.Store().Judgments.Count);
        Assert.True((await run.Call()).Result.Complete);
        Assert.Single(run.Advisor.Requests);
    }

    /// <summary>Support elsewhere cannot erase a contradiction or an independently refused obligation.</summary>
    [Fact]
    public async Task Contradiction_survives_support_and_refused_obligation()
    {
        using var run = new MapperRun { Plan = MapperRun.TwoUnits };
        run.Advisor.Choose = question => question.Instructions.Contains("## First", StringComparison.Ordinal)
            ? question.Id.StartsWith("relationship:", StringComparison.Ordinal) ? FakeAdvisor.Valid(question, "contradicts") : new Refused()
            : FakeAdvisor.Valid(question);
        Assert.Equal("contradiction", Assert.Single((await run.Call()).Result.Flags).Status);
        var next = await run.Call();
        Assert.Equal("contradiction", Assert.Single(next.Observation).Status);
        Assert.Single(run.Advisor.Requests);
    }

    /// <summary>A changed source is flagged even when the Advisor reports coverage.</summary>
    [Fact]
    public async Task Source_changed_is_flagged_with_its_current_check()
    {
        using var run = new MapperRun();
        AtomicFile.Write(System.IO.Path.Combine(run.Root, "sample.cs"), "old\n");
        run.Entries = [run.Entry(new ScoutItem { Text = "WidgetApi", SourceKind = "repository", Source = "sample.cs:1" })];
        AtomicFile.Write(System.IO.Path.Combine(run.Root, "sample.cs"), "new\n");
        var map = await run.Call();
        var flag = Assert.Single(map.Result.Flags);
        Assert.Equal("covered", flag.Status);
        Assert.True(flag.SourceChanged);
        Assert.True(Assert.Single(map.Observation).LocatorCheck!.Resolved);
        Assert.Equal(1, map.Result.Counts["sourceChanged"]);
    }

    /// <summary>Unindexed answer numbers do not make judgment completion false.</summary>
    [Fact]
    public async Task Not_indexed_answers_are_reported_separately()
    {
        using var run = new MapperRun { ScoutAnswers = 4 };
        var map = await run.Call();
        Assert.Equal([2, 3, 4], map.Result.NotIndexedAnswers);
        Assert.Equal(3, map.Result.Counts["notIndexedAnswers"]);
        Assert.True(map.Result.Complete);
    }

    /// <summary>Cancellation persists received work and distinguishes running from queued keys before throwing.</summary>
    [Fact]
    public async Task Caller_cancellation_keeps_received_and_classifies_running_and_queued()
    {
        using var run = MapperRun.Plain(4);
        using var ct = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Advisor.Handler = async (request, token) =>
        {
            if (run.Advisor.Requests.Count == 1) return FakeAdvisor.Answer(request);
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        };
        var task = run.Call(ct.Token);
        await started.Task;
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var progress = run.Mapper.Progress;
        Assert.Equal(3, progress.Result!.Remaining);
        Assert.Equal(1, progress.Result.CheckedThisCall);
        Assert.Equal([null, "cancelled", "not_yet_checked", "not_yet_checked"],
            progress.Observation.Select(item => item.Judgments[0].Questions["relevance"].Reason));
        Assert.Single(run.Store().Judgments);
        Assert.Equal(3, run.Store().Latest!["remaining"]!.GetValue<int>());
        Assert.Equal(2, run.Advisor.Requests.Count);
        Assert.True(run.Writes > 0);
    }

    /// <summary>Arrivals inside the throttle window wait for a later outcome, with an unconditional final write.</summary>
    [Fact]
    public async Task Writes_are_throttled_until_an_outcome_crosses_one_second()
    {
        using var run = MapperRun.Plain(5);
        var writesAtRequest = new List<int>();
        double[] seconds = [1, 0.1, 0.2, 0.3, 0.5];
        run.Advisor.Handler = (request, _) =>
        {
            writesAtRequest.Add(run.Writes);
            run.Clock.Advance(TimeSpan.FromSeconds(seconds[run.Advisor.Requests.Count - 1]));
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        await run.Call();
        Assert.Equal([0, 1, 1, 1, 1], writesAtRequest);
        Assert.Equal(3, run.Writes);
    }

    /// <summary>Completing inside the throttle window still replaces latest once more.</summary>
    [Fact]
    public async Task Completion_two_hundred_ms_after_write_still_writes_final_snapshot()
    {
        using var run = MapperRun.Plain(2);
        run.Advisor.Handler = (request, _) =>
        {
            run.Clock.Advance(TimeSpan.FromMilliseconds(run.Advisor.Requests.Count == 1 ? 1000 : 200));
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        await run.Call();
        Assert.Equal(2, run.Writes);
        Assert.True(run.Store().Latest!["complete"]!.GetValue<bool>());
    }

    /// <summary>Cancelling inside the throttle window still persists the cancellation snapshot.</summary>
    [Fact]
    public async Task Cancellation_inside_throttle_window_still_writes_final_snapshot()
    {
        using var run = MapperRun.Plain(2);
        using var ct = new CancellationTokenSource();
        run.Advisor.Handler = (request, token) =>
        {
            run.Clock.Advance(TimeSpan.FromMilliseconds(run.Advisor.Requests.Count == 1 ? 1000 : 200));
            if (run.Advisor.Requests.Count == 2) { ct.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Call(ct.Token));
        Assert.Equal(2, run.Writes);
        Assert.Equal("cancelled", run.Mapper.Progress.Observation[1].Judgments[0].Questions["relevance"].Reason);
        Assert.False(run.Store().Latest!["complete"]!.GetValue<bool>());
    }

    /// <summary>Flags quote local records, and the Advisor supplies no labels, sources or generated text.</summary>
    [Fact]
    public async Task Result_quotes_recorded_item_and_unit_text()
    {
        using var run = new MapperRun();
        run.Advisor.Choose = question => FakeAdvisor.Valid(question,
            question.Id.StartsWith("relationship:", StringComparison.Ordinal) ? "contradicts" : null);
        var map = await run.Call();
        var flag = Assert.Single(map.Result.Flags);
        Assert.Equal(run.Entries[0].Report.ConfirmedFacts[0].Text, flag.Text);
        Assert.Equal("https://example.com/evidence", flag.Source);
        Assert.Equal("## Work", Assert.Single(flag.Units).Label);
        Assert.DoesNotContain("example.com", run.Advisor.Requests[0].State.ToJsonString());
    }

    /// <summary>A resumed observation includes every item and both stored and newly received outcomes.</summary>
    [Fact]
    public async Task Observation_is_complete_on_a_mixed_cache_and_fresh_call()
    {
        using var run = MapperRun.Plain(3);
        run.Advisor.Handler = (request, _) =>
        {
            if (run.Advisor.Requests.Count > 1) throw new AdvisorException(AdvisorFailureKind.Unavailable);
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        await run.Call();
        run.Advisor.Handler = (request, _) => Task.FromResult(FakeAdvisor.Answer(request));
        var map = await run.Call();
        Assert.Equal(3, map.Observation.Count);
        Assert.Equal([true, false, false], map.Observation.Select(item => item.Judgments[0].FromStore));
        Assert.All(map.Observation, item => Assert.NotNull(item.Judgments[0].Questions["relevance"].Answer));
        foreach (var group in map.Observation.GroupBy(item => item.Status))
            Assert.Equal(group.Count(), map.Result.Counts[group.Key]);
        Assert.Equal(1, map.Result.CacheHits);
        Assert.Equal(2, map.Result.CheckedThisCall);
    }

    /// <summary>Splitting, adapter-internal retries, cache use, usage and invocation durations have exact accounting.</summary>
    [Fact]
    public async Task Scripted_counters_usage_and_durations_are_pinned()
    {
        using var run = new MapperRun { Plan = MapperRun.TwoUnits };
        run.SeedRelevance("A");
        run.Advisor.Handler = (request, _) =>
        {
            var number = run.Advisor.Requests.Count;
            run.Clock.Advance(TimeSpan.FromSeconds(number));
            if (number == 1) throw new AdvisorException(AdvisorFailureKind.Rejected);
            if (number == 3) throw new AdvisorException(AdvisorFailureKind.TooLarge);
            run.Advisor.InternalRetries++;
            return Task.FromResult(FakeAdvisor.Answer(request) with { Usage = new(11, 7) });
        };
        var result = (await run.Call()).Result;
        var progress = run.Mapper.Progress;
        Assert.Equal((3, 1, 2, 0), (result.Required, result.CacheHits, result.CheckedThisCall, result.Remaining));
        Assert.Equal((3, 2, 4), (progress.Requests, progress.FailedRequests, progress.JudgmentsAsked));
        Assert.Equal(1, run.Advisor.InternalRetries);
        Assert.Equal(11, progress.InputTokens);
        Assert.Equal(7, progress.OutputTokens);
        Assert.Equal([1000d, 2000d, 3000d], progress.AskDurations);
        Assert.Equal(2000d, progress.LatencyP50);
        Assert.Equal(3000d, progress.LatencyP90);
        Assert.Equal(TimeSpan.FromSeconds(6), progress.Duration);
    }

    /// <summary>Sensitive recorded text accounts for every key without sending anything.</summary>
    [Fact]
    public async Task Sensitive_item_is_persistently_unchecked_without_requests()
    {
        using var run = new MapperRun();
        run.Entries = [run.Entry(MapperRun.External("api_key=sk-abcdefghijklmnopqrstuvwxyz123456 WidgetApi"))];
        var map = await run.Call();
        Assert.Empty(run.Advisor.Requests);
        Assert.True(map.Result.Complete);
        Assert.Equal("sensitive", Outcome(map, 0).Reason);
        Assert.Equal(2, run.Store().Judgments.Count);
    }

    /// <summary>Indivisible judgments above the Advisor's measure are recorded without sending.</summary>
    [Fact]
    public async Task Locally_oversized_judgments_are_persistent()
    {
        using var run = new MapperRun();
        run.Advisor.Limits = run.Advisor.Limits with { MaxTotal = 0 };
        var map = await run.Call();
        Assert.Empty(run.Advisor.Requests);
        Assert.True(map.Result.Complete);
        Assert.Equal("too_large", Outcome(map, 0).Reason);
        Assert.Equal(2, run.Store().Judgments.Count);
    }

    /// <summary>A fresh answer sets the call's model while locally withheld judgments keep an unknown model.</summary>
    [Fact]
    public async Task First_fresh_model_leaves_locally_withheld_judgments_unlabelled()
    {
        using var run = MapperRun.Plain(2);
        run.Entries = [run.Entry(MapperRun.External("api_key=sk-abcdefghijklmnopqrstuvwxyz123456"), MapperRun.External("ordinary fact"))];
        var map = await run.Call();
        Assert.Equal("A", map.Result.ServedModel);
        Assert.True(map.Result.Complete);
        Assert.Equal("sensitive", Outcome(map, 0).Reason);
        Assert.Single(run.Advisor.Requests);
        Assert.Null(run.Store().Judgments[map.Observation[0].Judgments[0].Key].ServedModel);
        Assert.Equal("A", run.Store().Judgments[map.Observation[1].Judgments[0].Key].ServedModel);
        var next = await run.Call();
        Assert.True(next.Result.Complete);
        Assert.Equal("A", next.Result.ServedModel);
        Assert.Equal(2, next.Result.CacheHits);
        Assert.Null(run.Store().Judgments[next.Observation[0].Judgments[0].Key].ServedModel);
        Assert.Single(run.Advisor.Requests);
    }

    /// <summary>Flag change is evaluated against the preceding complete map on every call.</summary>
    [Fact]
    public async Task Completed_flags_are_new_then_carried()
    {
        using var run = MapperRun.Plain(1);
        Assert.Equal("new", Assert.Single((await run.Call()).Result.Flags).Change);
        Assert.Equal("carried", Assert.Single((await run.Call()).Result.Flags).Change);
        Assert.Equal("carried", run.Store().Latest!["flags"]![0]!["change"]!.GetValue<string>());
    }

    /// <summary>A malformed map is refused without requests or replacing the file.</summary>
    [Fact]
    public async Task Corrupt_store_is_refused_before_requests()
    {
        using var run = new MapperRun();
        AtomicFile.Write(run.Path, "{broken");
        var error = await Assert.ThrowsAsync<JsonException>(() => run.Call());
        Assert.Contains("evidence-map.json", error.Message);
        Assert.Empty(run.Advisor.Requests);
        Assert.Equal("{broken", AtomicFile.Read(run.Path));
        Assert.Equal(0, run.Writes);
    }

    /// <summary>Implications get pair questions and appear once without a relevance question or locator.</summary>
    [Fact]
    public async Task Implication_has_no_relevance_judgment()
    {
        using var run = new MapperRun { Implications = [new("WidgetApi requires verification", ["A1.confirmedFacts.1"])] };
        var map = await run.Call();
        var implication = map.Observation[1];
        Assert.Equal("H1", implication.Id);
        Assert.Equal("none", implication.Locator);
        Assert.Null(implication.LocatorCheck);
        Assert.All(implication.Judgments, part => Assert.Equal("pair", part.Kind));
        Assert.Equal("covered", implication.Status);
    }

    /// <summary>A request with wrong ids is split before invalid answers are persisted individually.</summary>
    [Fact]
    public async Task Whole_request_invalid_ids_split_before_persistence()
    {
        using var run = new MapperRun();
        run.Advisor.Handler = (_, _) => Task.FromResult(new AdviceResult(new Dictionary<string, AdviceOutcome>(), "A"));
        var map = await run.Call();
        Assert.True(map.Result.Complete);
        Assert.Equal(3, run.Advisor.Requests.Count);
        Assert.Equal(2, run.Store().Judgments.Count);
        Assert.All(map.Observation[0].Judgments, judgment => Assert.All(judgment.Questions.Values,
            outcome => Assert.Equal("invalid_answer", outcome.Reason)));
    }

    /// <summary>Caller cancellation takes precedence over a final store-write failure, leaving readable progress.</summary>
    [Fact]
    public async Task Cancellation_has_precedence_over_failed_store_write()
    {
        using var run = MapperRun.Plain(1);
        using var ct = new CancellationTokenSource();
        Directory.CreateDirectory(run.Path);
        run.Advisor.Handler = (_, token) =>
        {
            ct.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Call(ct.Token));
        Assert.Equal(1, run.Mapper.Progress.Result!.Remaining);
        Assert.Equal("cancelled", run.Mapper.Progress.Observation[0].Judgments[0].Questions["relevance"].Reason);
    }

    /// <summary>Checks the first relevance outcome in an observation.</summary>
    /// <param name="map">The completed call.</param>
    /// <param name="item">The item position.</param>
    private static EvidenceQuestionOutcome Outcome(EvidenceMap map, int item) => map.Observation[item].Judgments[0].Questions["relevance"];

    /// <summary>Checks one service-failure mapping, persistence policy and stop behavior.</summary>
    /// <param name="kind">The injected failure kind.</param>
    /// <param name="reason">The expected unchecked reason.</param>
    /// <param name="persistent">Whether the outcome must be cached.</param>
    /// <param name="stops">Whether the failure must prevent later requests.</param>
    private static async Task Failure(AdvisorFailureKind kind, string reason, bool persistent, bool stops = false)
    {
        using var run = MapperRun.Plain(2);
        run.Advisor.Handler = (_, _) => throw new AdvisorException(kind);
        var map = await run.Call();
        Assert.Equal(reason, Outcome(map, 0).Reason);
        Assert.Equal(stops ? reason : null, map.Result.StoppedBy);
        Assert.Equal(stops ? 1 : 2, run.Advisor.Requests.Count);
        Assert.Equal(persistent ? 2 : 0, run.Store().Judgments.Count);
        Assert.Equal(persistent ? 0 : 2, map.Result.Remaining);
        Assert.Equal(persistent, map.Result.Complete);
        if (stops) Assert.Equal("not_yet_checked", Outcome(map, 1).Reason);
        Assert.Equal(run.Advisor.Requests.Count, run.Mapper.Progress.FailedRequests);
    }
}

/// <summary>An isolated mapper fixture shared by the rule matrices.</summary>
internal sealed class MapperRun : IDisposable
{
    /// <summary>Two distinct candidate texts for the same identifier.</summary>
    internal const string TwoUnits = "# Plan\n## First\nUse `WidgetApi`.\n## Second\nUse `WidgetApi`.\n";
    /// <summary>The owned temporary workspace.</summary>
    public string Root { get; } = Directory.CreateTempSubdirectory("planforge-mapper-").FullName;
    /// <summary>The persisted map path.</summary>
    public string Path => System.IO.Path.Combine(Root, "evidence-map.json");
    /// <summary>The manual budget clock.</summary>
    public MapperClock Clock { get; } = new();
    /// <summary>The scripted service.</summary>
    public FakeAdvisor Advisor { get; } = new();
    /// <summary>The call owner retained for progress on failure.</summary>
    public EvidenceMapper Mapper { get; private set; } = null!;
    /// <summary>The number of successful store writes.</summary>
    public int Writes { get; private set; }
    /// <summary>The exact current plan.</summary>
    public string Plan { get; set; } = "# Plan\n## Work\nUse `WidgetApi`.\n";
    /// <summary>The current Scout entries.</summary>
    public IReadOnlyList<ScoutAnswerEntry> Entries { get; set; }
    /// <summary>The consumed answer count.</summary>
    public int ScoutAnswers { get; set; } = 1;
    /// <summary>The optional derived obligations.</summary>
    public IReadOnlyList<EvidenceImplication>? Implications { get; set; }
    /// <summary>The ordered Change points.</summary>
    public IReadOnlyList<string> ChangePoints { get; set; } = ["Change WidgetApi"];

    /// <summary>Starts with one external item and one candidate.</summary>
    public MapperRun() => Entries = [Entry(External("WidgetApi requires verification"))];

    /// <summary>Creates distinct items having no candidate identifiers, so each request has one relevance judgment.</summary>
    /// <param name="count">The number of items.</param>
    public static MapperRun Plain(int count)
    {
        var run = new MapperRun { Plan = "# Plan\nplain\n" };
        run.Entries = [run.Entry(Enumerable.Range(1, count).Select(number => External($"fact number {number}")).ToArray())];
        return run;
    }

    /// <summary>Invokes a new bounded call while retaining disk state across calls.</summary>
    /// <param name="ct">Caller cancellation.</param>
    public Task<EvidenceMap> Call(CancellationToken ct = default)
    {
        Mapper = new(Clock, () => Writes++);
        return Mapper.MapAsync(Root, Plan, Entries, ScoutAnswers, ChangePoints, Advisor, Path, Implications, ct);
    }

    /// <summary>Loads the actual persisted file after a call.</summary>
    public EvidenceStore Store() => EvidenceStore.Load(Path);

    /// <summary>Records an answer with checks matching its current repository files.</summary>
    /// <param name="facts">Confirmed facts in their exact order.</param>
    public ScoutAnswerEntry Entry(params ScoutItem[] facts)
    {
        var report = new ScoutReport
        {
            Summary = "summary", ConfirmedFacts = facts, MaterialAssumptions = [], OpenDecisions = [],
            LikelyChangeSurface = [], VerificationEvidence = [], DependentsAndPinnedBehaviour = []
        };
        return new(1, "question", report, ScoutLocators.Of(Root, report));
    }

    /// <summary>Builds an external item, whose URL is never fetched or sent.</summary>
    /// <param name="text">The exact item text.</param>
    public static ScoutItem External(string text) => new() { Text = text, SourceKind = "external", Source = "https://example.com/evidence" };

    /// <summary>Seeds only relevance so later pairs still need fresh answers.</summary>
    /// <param name="model">The stored served model.</param>
    public void SeedRelevance(string model)
    {
        var units = PlanUnits.Of(Plan);
        var shared = EvidenceRubric.SharedState(units, ChangePoints);
        var item = EvidenceIndex.Of(Entries, ScoutAnswers, null, Root).Items[0];
        var judgment = EvidenceRubric.Judgments(item, [])[0];
        var key = EvidenceStore.Hash(EvidenceStore.KeyText(Advisor.Id, Advisor.RequestedModel, shared, item, judgment));
        var store = Store();
        store.Put(key, new("relevance", Advisor.Id, Advisor.RequestedModel, model,
            new Dictionary<string, EvidenceQuestionOutcome> { ["relevance"] = new(FakeAdvisor.Valid(judgment.Questions[0]), null) }));
        store.Write([key], new(), new Dictionary<string, EvidenceItemMark>(), false);
    }

    /// <summary>Removes only this fixture's temporary workspace.</summary>
    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
    }
}

/// <summary>A scripted Advisor retaining every exact request and actual concurrent invocation count.</summary>
internal sealed class FakeAdvisor : IAdvisor
{
    /// <summary>The fixture Advisor identity.</summary>
    public string Id => "fixture";
    /// <summary>The pinned requested model, separate from the scripted served model.</summary>
    public string RequestedModel => "requested-1";
    /// <summary>Limits can be narrowed independently by each test.</summary>
    public AdvisorLimits Limits { get; set; } = new("questions", 1000, 1000, 64, 255, 1, TimeSpan.FromSeconds(30));
    /// <summary>The requests in invocation order.</summary>
    public List<AdviceRequest> Requests { get; } = [];
    /// <summary>The response script, when the default question script is insufficient.</summary>
    public Func<AdviceRequest, CancellationToken, Task<AdviceResult>>? Handler { get; set; }
    /// <summary>The per-question script.</summary>
    public Func<ChoiceQuestion, AdviceOutcome> Choose { get; set; } = question => Valid(question);
    /// <summary>An optional local measurement step, used to account for preparation time.</summary>
    public Action? OnMeasure { get; set; }
    /// <summary>The greatest simultaneous invocation count.</summary>
    public int PeakConcurrency { get; private set; }
    /// <summary>The adapter's internal retries, deliberately invisible to the mapper.</summary>
    public int InternalRetries { get; set; }
    /// <summary>The current invocation count.</summary>
    private int inFlight;

    /// <summary>Measures in questions so packing boundaries are easy to script.</summary>
    /// <param name="request">The exact state and questions.</param>
    public AdviceSize Measure(AdviceRequest request)
    {
        OnMeasure?.Invoke();
        return new(request.Questions.Count, request.Questions.Count == 0 ? 0 : 1);
    }

    /// <summary>Records and runs one request, including any script-owned retries.</summary>
    /// <param name="request">The request to retain.</param>
    /// <param name="ct">The mapper's linked cancellation.</param>
    public async Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        inFlight++;
        PeakConcurrency = Math.Max(PeakConcurrency, inFlight);
        try
        {
            return Handler is not null ? await Handler(request, ct)
                : new(request.Questions.ToDictionary(question => question.Id, Choose), "A");
        }
        finally { inFlight--; }
    }

    /// <summary>Answers all questions with valid default choices and the requested served model.</summary>
    /// <param name="request">The questions to answer.</param>
    /// <param name="model">The response model.</param>
    public static AdviceResult Answer(AdviceRequest request, string model = "A") =>
        new(request.Questions.ToDictionary(question => question.Id, question => (AdviceOutcome)Valid(question)), model);

    /// <summary>Builds a normalized distribution over exactly the asked alternatives.</summary>
    /// <param name="question">The asked question.</param>
    /// <param name="choice">An optional choice overriding action, support or obligation.</param>
    public static Answered Valid(ChoiceQuestion question, string? choice = null)
    {
        choice ??= question.Id == "relevance" ? "implies_action"
            : question.Id.StartsWith("relationship:", StringComparison.Ordinal) ? "supports" : "explicit_obligation";
        return new(choice, question.Options.ToDictionary(option => option.Value, option => option.Value == choice ? 1d : 0d));
    }
}

/// <summary>A manual monotonic clock whose timers fire only when explicitly advanced.</summary>
internal sealed class MapperClock : TimeProvider
{
    /// <summary>The owned timer set.</summary>
    private readonly List<ManualTimer> timers = [];
    /// <summary>The current monotonic time in ticks.</summary>
    private long ticks;
    /// <summary>The number of timestamp ticks per second.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    /// <summary>The current monotonic timestamp.</summary>
    public override long GetTimestamp() => ticks;
    /// <summary>The current wall clock derived from the same monotonic origin.</summary>
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);

    /// <summary>Advances both clocks and fires due callbacks, including cancellation deadlines.</summary>
    /// <param name="span">The elapsed interval.</param>
    public void Advance(TimeSpan span)
    {
        ticks += span.Ticks;
        foreach (var timer in timers.ToArray())
            if (timer.Due <= ticks) timer.Fire();
    }

    /// <summary>Creates a manually scheduled timer.</summary>
    /// <param name="callback">The callback to invoke.</param>
    /// <param name="state">The callback state.</param>
    /// <param name="dueTime">The first interval.</param>
    /// <param name="period">The repeat interval.</param>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>A timer tied to the manual clock.</summary>
    /// <param name="clock">The timer's clock.</param>
    /// <param name="callback">The timer callback.</param>
    /// <param name="state">The callback state.</param>
    private sealed class ManualTimer(MapperClock clock, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>The next monotonic deadline.</summary>
        public long Due { get; private set; }
        /// <summary>The repeat interval.</summary>
        private TimeSpan period;
        /// <summary>Schedules the next callback.</summary>
        /// <param name="dueTime">The first interval.</param>
        /// <param name="period">The repeat interval.</param>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            this.period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
            return true;
        }
        /// <summary>Fires the callback and reschedules a recurring timer.</summary>
        public void Fire()
        {
            Due = period == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + period.Ticks;
            callback(state);
        }
        /// <summary>Unschedules this timer.</summary>
        public void Dispose() => Due = long.MaxValue;
        /// <summary>Unschedules this timer synchronously.</summary>
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
