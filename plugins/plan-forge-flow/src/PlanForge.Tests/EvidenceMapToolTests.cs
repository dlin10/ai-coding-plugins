using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PlanForge.Advisors;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>The tool boundary refuses before dispatch and finalizes every accepted call.</summary>
public sealed class EvidenceMapToolTests
{
    /// <summary>Requests that together outlast 45 seconds finish in one call, with one record of each kind timed from the opening.</summary>
    [Fact]
    public async Task A_map_whose_requests_outlast_forty_five_seconds_completes_in_one_call()
    {
        using var run = new EvidenceToolRun();
        run.Advisor.Limits = run.Advisor.Limits with { MaxQuestions = 2 };
        run.Advisor.Handler = (request, ct) =>
        {
            run.Clock.Advance(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        var result = JsonNode.Parse(await ForgeTools.EvidenceMap(SessionRoots.None, run.Root, run.Run.RunId,
            ["Change WidgetApi"], CancellationToken.None, run.Environment.GetValueOrDefault,
            (_, _) => { run.Clock.Advance(TimeSpan.FromSeconds(5)); return run.Advisor; }, run.Clock))!;
        Assert.True(result["complete"]!.GetValue<bool>());
        Assert.Equal(2, run.Advisor.Requests.Count);
        var usage = Assert.Single(run.Records());
        Assert.Equal(2, usage.GetProperty("requests").GetInt32());
        Assert.True(usage.GetProperty("complete").GetBoolean());
        Assert.Equal(TimeSpan.FromSeconds(65), TimeSpan.Parse(usage.GetProperty("duration").GetString()!));
        run.AssertRecords();
    }

    /// <summary>Malformed recorded checks refuse both append and map without replacing the Scout file.</summary>
    /// <param name="resolved">The malformed resolution mark.</param>
    /// <param name="window">The malformed window hash.</param>
    /// <param name="reason">The malformed failure reason.</param>
    [Theory]
    [InlineData(false, null, "not-an-R2-reason")]
    [InlineData(true, "0123456789abcdef", "missing_file")]
    [InlineData(true, "0123456789abcdeF", null)]
    [InlineData(true, "0123456789abcde", null)]
    [InlineData(false, "0123456789abcdef", "missing_file")]
    public async Task Invalid_recorded_locator_checks_refuse_append_and_map(bool resolved, string? window, string? reason)
    {
        using var run = new EvidenceToolRun();
        var entry = Assert.Single(run.Run.ReadScoutAnswers());
        var entries = JsonSerializer.SerializeToNode(new List<ScoutAnswerEntry> { entry }, ScoutAnswersJson.Default.ListScoutAnswerEntry)!;
        entries[0]!["report"]!["confirmedFacts"]![0]!["sourceKind"] = "repository";
        entries[0]!["report"]!["confirmedFacts"]![0]!["source"] = "missing.cs:1";
        entries[0]!["locators"]!["confirmedFacts"]![0] = JsonSerializer.SerializeToNode(new LocatorCheck(resolved, window, reason),
                                                                                     ScoutAnswersJson.Default.LocatorCheck);
        AtomicFile.Write(run.Run.ScoutAnswersPath, entries.ToJsonString());
        var before = AtomicFile.Read(run.Run.ScoutAnswersPath);
        var error = Assert.Throws<JsonException>(() => run.Run.AppendScoutAnswer(entry with { Number = 2 }));
        Assert.Contains("scout-answers.json", error.Message);
        Assert.Equal(before, AtomicFile.Read(run.Run.ScoutAnswersPath));
        await run.Refused("scout-answers.json");
        Assert.Equal(before, AtomicFile.Read(run.Run.ScoutAnswersPath));
    }

    /// <summary>Absent configuration leaves the Evidence check manual.</summary>
    [Fact]
    public async Task No_Advisor_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        run.Environment.Clear();
        await run.Refused("no Advisor is configured");
    }

    /// <summary>Ambiguous configuration requires an explicit selection.</summary>
    [Fact]
    public async Task Several_Advisors_without_a_name_are_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        run.Catalog = new([new TestAdvisorDescriptor("one"), new TestAdvisorDescriptor("two")]);
        run.Environment["one"] = "configured";
        run.Environment["two"] = "configured";
        await run.Refused("several Advisors are configured");
    }

    /// <summary>A partly configured explicit selection names its missing variable.</summary>
    [Fact]
    public async Task A_partly_configured_name_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        run.Environment.Clear();
        run.Environment["PLANFORGE_JEV_MODEL"] = "jev-1.13.0";
        await run.Refused("PLANFORGE_JEV_API_KEY: missing", advisor: "jev");
    }

    /// <summary>An absent explicit selection names the required variable.</summary>
    [Fact]
    public async Task An_absent_name_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        run.Environment.Clear();
        await run.Refused("required variables: PLANFORGE_JEV_API_KEY", advisor: "jev");
    }

    /// <summary>An unknown selection names the supported ids.</summary>
    [Fact]
    public async Task An_unknown_name_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        await run.Refused("known Advisors: jev", advisor: "unknown");
    }

    /// <summary>There must be a plan on disk.</summary>
    [Fact]
    public async Task No_plan_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        File.Delete(run.Run.PlanPath);
        await run.Refused("PLAN.md");
    }

    /// <summary>Whitespace is not a plan.</summary>
    [Fact]
    public async Task A_blank_plan_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        run.Run.WritePlan(" \r\n\t");
        await run.Refused("PLAN.md");
    }

    /// <summary>At least one Change point is required.</summary>
    [Fact]
    public async Task No_change_point_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        await run.Refused("changePoints", changePoints: []);
    }

    /// <summary>The first entry beyond the bound is identified.</summary>
    [Fact]
    public async Task Two_hundred_and_one_change_points_are_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        await run.Refused("changePoints[200]", changePoints: Enumerable.Repeat("WidgetApi", 201).ToArray());
    }

    /// <summary>Sensitive input is refused without dispatch.</summary>
    [Fact]
    public async Task A_sensitive_change_point_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        await run.Refused("changePoints[0]", changePoints: ["api_key = 'AbCd1234_secretValue'"]);
    }

    /// <summary>Unknown implication references identify both position and bad id.</summary>
    [Fact]
    public async Task A_bad_derivedFrom_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        await run.Refused("implications[0].derivedFrom[0]", implications: [new("WidgetApi needs work", ["A99.confirmedFacts.1"])]);
        Assert.Contains("A99.confirmedFacts.1", AtomicFile.Read(run.Run.DiagnosticLogPath));
    }

    /// <summary>Each Change point is bounded and non-blank.</summary>
    /// <param name="length">The invalid text length.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task An_invalid_change_point_names_its_index(int length)
    {
        using var run = new EvidenceToolRun();
        await run.Refused("changePoints[1]", changePoints: ["valid", new string('a', length)]);
    }

    /// <summary>Each implication obeys all text, count and reference bounds.</summary>
    /// <param name="kind">The invalid implication axis.</param>
    [Theory]
    [InlineData("blank")]
    [InlineData("long")]
    [InlineData("sensitive")]
    [InlineData("emptyReferences")]
    [InlineData("manyReferences")]
    [InlineData("manyImplications")]
    public async Task Invalid_implication_arguments_are_refused_before_requests_or_records(string kind)
    {
        using var run = new EvidenceToolRun();
        var text = kind switch { "blank" => " ", "long" => new string('a', 2001),
            "sensitive" => "api_key = 'AbCd1234_secretValue'", _ => "WidgetApi needs work" };
        string[] references = kind switch { "emptyReferences" => [],
            "manyReferences" => Enumerable.Repeat("A1.confirmedFacts.1", 21).ToArray(), _ => ["A1.confirmedFacts.1"] };
        var implications = Enumerable.Repeat(new EvidenceImplication(text, references), kind == "manyImplications" ? 501 : 1).ToArray();
        await run.Refused("implications[", implications: implications);
    }

    /// <summary>A broken Scout store is neither ignored nor replaced.</summary>
    /// <param name="text">The malformed file contents.</param>
    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task A_malformed_scout_file_is_refused_before_requests_or_records(string text)
    {
        using var run = new EvidenceToolRun();
        AtomicFile.Write(run.Run.ScoutAnswersPath, text);
        await run.Refused("scout-answers.json");
        Assert.Equal(text, AtomicFile.Read(run.Run.ScoutAnswersPath));
    }

    /// <summary>The Scout counter bounds the indexed answers.</summary>
    [Fact]
    public async Task An_answer_above_the_state_counter_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        run.Run.WriteState(run.Run.ReadState() with { ScoutAnswers = 0 });
        await run.Refused("scout-answers.json");
    }

    /// <summary>A malformed map refuses without replacing the existing bytes.</summary>
    [Fact]
    public async Task A_malformed_map_is_refused_before_requests_or_records()
    {
        using var run = new EvidenceToolRun();
        AtomicFile.Write(run.Run.EvidenceMapPath, "{}");
        await run.Refused("evidence-map.json");
        Assert.Equal("{}", AtomicFile.Read(run.Run.EvidenceMapPath));
    }

    /// <summary>The wire result is exactly the public accounting contract, without observations.</summary>
    [Fact]
    public async Task A_successful_result_has_exactly_the_R6_members()
    {
        using var run = new EvidenceToolRun();
        var result = JsonNode.Parse(await run.Call())!.AsObject();
        Assert.Equal(["advisor", "requestedModel", "servedModel", "complete", "required", "alreadyMapped", "cacheHits",
                      "checkedThisCall", "remaining", "stoppedBy", "estimatedCostUsd", "costUsd", "runCostUsd", "budgetUsd",
                      "counts", "flags", "notIndexedAnswers", "path", "documents"], result.Select(pair => pair.Key));
        Assert.True(result["complete"]!.GetValue<bool>());
        Assert.Equal(run.Run.EvidenceMapPath, result["path"]!.GetValue<string>());
        Assert.Equal(run.Run.FlowLogPath, result["documents"]!["flowLog"]!["path"]!.GetValue<string>());
        Assert.Equal(run.Run.PlanPath, result["documents"]!["plan"]!["path"]!.GetValue<string>());
        Assert.Null(result["documents"]!["scout"]);
        run.AssertRecords();
    }

    /// <summary>The Flow log records accounting but no evidence or plan text.</summary>
    [Fact]
    public async Task The_flow_entry_has_the_R15_lines_without_content()
    {
        using var run = new EvidenceToolRun();
        run.Run.WriteState(run.Run.ReadState() with { ScoutAnswers = 2 });
        run.Advisor.Choose = question => FakeAdvisor.Valid(question, "insufficient_context");
        await run.Call();
        var flow = AtomicFile.Read(run.Run.FlowLogPath);
        foreach (var line in new[] { "## Evidence map", "Advisor: fixture", "Requested model: requested-1", "Served model: A",
            "required: 2", "checkedThisCall: 2", "remaining: 0", "cacheHits: 0", "alreadyMapped: 0", "uncertain: 1 (new 1, carried 0)",
            "notIndexedAnswers: 2", "complete: true", "estimatedCostUsd: 0.0000", "costUsd: unknown",
            "runCostUsd: 0.0000 of budgetUsd 5.0000" }) Assert.Contains(line, flow);
        Assert.DoesNotContain("stoppedBy", flow);
        Assert.DoesNotContain(EvidenceToolRun.ItemMarker, flow);
        Assert.DoesNotContain(EvidenceToolRun.PlanMarker, flow);
        await run.Call();
        Assert.Contains("alreadyMapped: 1", AtomicFile.Read(run.Run.FlowLogPath));
        run.ForgetMapping();
        await run.Call();
        Assert.Contains("uncertain: 1 (new 0, carried 1)", AtomicFile.Read(run.Run.FlowLogPath));
    }

    /// <summary>A map the budget cannot pay for sends nothing and says what it would cost; a named budget lets it run and stays.</summary>
    [Fact]
    public async Task A_map_over_the_budget_sends_nothing_until_the_user_names_a_larger_one()
    {
        using var run = new EvidenceToolRun();
        run.Advisor.InputUsdPerMillion = 3_000_000m;
        var stopped = JsonNode.Parse(await run.Call())!;
        Assert.Empty(run.Advisor.Requests);
        Assert.Equal("budget", stopped["stoppedBy"]!.GetValue<string>());
        Assert.False(stopped["complete"]!.GetValue<bool>());
        Assert.Equal(0, stopped["checkedThisCall"]!.GetValue<int>());
        Assert.Equal(9m, stopped["estimatedCostUsd"]!.GetValue<decimal>());
        Assert.Equal(0m, stopped["runCostUsd"]!.GetValue<decimal>());
        Assert.Equal(5m, stopped["budgetUsd"]!.GetValue<decimal>());
        Assert.Contains("stoppedBy: budget", AtomicFile.Read(run.Run.FlowLogPath));
        Assert.Equal("budget", Assert.Single(run.Records()).GetProperty("stoppedBy").GetString());

        var raised = JsonNode.Parse(await run.Call(budgetUsd: 10m))!;
        Assert.True(raised["complete"]!.GetValue<bool>());
        Assert.Null(raised["stoppedBy"]);
        Assert.Equal(10m, raised["budgetUsd"]!.GetValue<decimal>());
        Assert.Equal(10m, EvidenceStore.Load(run.Run.EvidenceMapPath).BudgetUsd);
        Assert.Single(run.Advisor.Requests);
    }

    /// <summary>
    /// The Run's earlier map calls count against the budget: their recorded cost, or, for a record without one, their
    /// input tokens at the current Advisor's price.
    /// </summary>
    [Fact]
    public async Task Earlier_calls_count_against_the_budget()
    {
        using var run = new EvidenceToolRun();
        run.Advisor.InputUsdPerMillion = 3_000_000m;
        AtomicFile.Write(run.Run.TelemetryPath, """
            [{"at":"1960-01-01 00:00:00","event":"advisor.usage","advisor":"fixture","inputTokens":1},
             {"at":"1960-01-01 00:00:01","event":"advisor.usage","advisor":"fixture","inputTokens":7,"costUsd":1.5},
             {"at":"1960-01-01 00:00:02","event":"vendor.usage","inputTokens":1000}]
            """);
        var stopped = JsonNode.Parse(await run.Call(budgetUsd: 10m))!;
        Assert.Equal("budget", stopped["stoppedBy"]!.GetValue<string>());
        Assert.Equal(4.5m, stopped["runCostUsd"]!.GetValue<decimal>());
        Assert.Empty(run.Advisor.Requests);

        run.Advisor.Handler = (request, _) => Task.FromResult(FakeAdvisor.Answer(request) with { Usage = new(3, 1) });
        var paid = JsonNode.Parse(await run.Call(budgetUsd: 20m))!;
        Assert.True(paid["complete"]!.GetValue<bool>());
        Assert.Equal(9m, paid["costUsd"]!.GetValue<decimal>());
        Assert.Equal(13.5m, paid["runCostUsd"]!.GetValue<decimal>());
        Assert.Equal(9m, run.Records().Last(record => record.GetProperty("event").GetString() == "advisor.usage")
                             .GetProperty("costUsd").GetDecimal());
    }

    /// <summary>A budget is a positive amount of at most 1000 dollars.</summary>
    /// <param name="budget">The invalid amount.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1000.01)]
    public async Task A_budget_outside_its_range_is_refused_before_requests_or_records(double budget)
    {
        using var run = new EvidenceToolRun();
        await run.Refused("budgetUsd", budgetUsd: (decimal)budget);
    }

    /// <summary>A status that is never flagged still reports the result's count, without a change split.</summary>
    [Fact]
    public async Task The_flow_entry_counts_unflagged_statuses_from_the_result()
    {
        using var run = new EvidenceToolRun();
        var counts = JsonNode.Parse(await run.Call())!["counts"]!;
        Assert.Equal(1, counts["covered"]!.GetValue<int>());
        var lines = AtomicFile.Read(run.Run.FlowLogPath).Split(Environment.NewLine);
        Assert.Contains("covered: 1", lines);
        Assert.Contains("context_only: 0", lines);
        Assert.Contains("uncertain: 0 (new 0, carried 0)", lines);
    }

    /// <summary>The Advisor record uses the opening time and preserves both other record kinds.</summary>
    [Fact]
    public async Task Advisor_usage_has_all_fields_and_survives_beside_both_existing_kinds()
    {
        using var run = new EvidenceToolRun();
        var context = new WorkerTelemetryContext(run.Run.TelemetryPath, run.Run.Log, "build");
        var attempt = new VendorTurn(new RoleSpec(VendorRole.Builder, "role", Telemetry: context), new("model", null), "codex").Start(1, null);
        attempt.Succeeded();
        attempt.Finish(new WorkerUsage(InputTokens: 17), "session");
        WorkerTelemetryFile.Append(run.Run.TelemetryPath, run.Run.Log, new OrchestratorUsageRecord("2000-01-01 00:00:00",
            "orchestrator.usage", "planning", null, null, null, null, null, null, "00:00:01", "00:00:01", "00:00:00",
            null, null, null, null, null, null, false, null));
        run.Advisor.Handler = (request, _) =>
        {
            run.Clock.Advance(TimeSpan.FromMilliseconds(250));
            return Task.FromResult(FakeAdvisor.Answer(request) with { Usage = new(12, 3) });
        };
        await run.Call();
        var usage = Assert.Single(run.Records(), record => record.GetProperty("event").GetString() == "advisor.usage");
        Assert.Equal(["at", "event", "advisor", "requestedModel", "servedModel", "requests", "failedRequests", "judgmentsAsked",
                      "inputTokens", "outputTokens", "cachedInputTokens", "cacheWriteTokens", "cacheHits", "latencyP50",
                      "latencyP90", "duration", "complete", "stoppedBy", "costUsd"],
                     usage.EnumerateObject().Select(property => property.Name));
        Assert.Equal(VendorAttempt.FormatLocalTimestamp(DateTimeOffset.UnixEpoch.ToLocalTime()), usage.GetProperty("at").GetString());
        Assert.Equal(1, usage.GetProperty("requests").GetInt32());
        Assert.Equal(0, usage.GetProperty("failedRequests").GetInt32());
        Assert.Equal(2, usage.GetProperty("judgmentsAsked").GetInt32());
        Assert.Equal(12, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(3, usage.GetProperty("outputTokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("cachedInputTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("cacheWriteTokens").ValueKind);
        Assert.Equal(250, usage.GetProperty("latencyP50").GetDouble());
        Assert.Equal(250, usage.GetProperty("latencyP90").GetDouble());
        Assert.Equal(0m, usage.GetProperty("costUsd").GetDecimal());
        Assert.Contains(run.Records(), record => record.GetProperty("event").GetString() == "vendor.usage" && record.GetProperty("inputTokens").GetInt64() == 17);
        Assert.Contains(run.Records(), record => record.GetProperty("event").GetString() == "orchestrator.usage");
    }

    /// <summary>The Advisor record carries the reply's cached and written counts and their cost at the cache rates.</summary>
    [Fact]
    public async Task Advisor_usage_records_cache_counts()
    {
        using var run = new EvidenceToolRun();
        (run.Advisor.InputUsdPerMillion, run.Advisor.CacheReadUsdPerMillion, run.Advisor.CacheWriteUsdPerMillion) = (0.10m, 0.01m, 0.125m);
        run.Advisor.Handler = (request, _) =>
            Task.FromResult(FakeAdvisor.Answer(request) with { Usage = new(1_000_000, 3, 200_000, 300_000) });
        var result = JsonNode.Parse(await run.Call(budgetUsd: 10m))!;
        Assert.Equal(0.0895m, result["costUsd"]!.GetValue<decimal>());
        var usage = Assert.Single(run.Records(), record => record.GetProperty("event").GetString() == "advisor.usage");
        Assert.Equal(1_000_000, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(200_000, usage.GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal(300_000, usage.GetProperty("cacheWriteTokens").GetInt64());
        Assert.Equal(0.0895m, usage.GetProperty("costUsd").GetDecimal());
    }

    /// <summary>
    /// A record written before it carried a cost is priced from its input, cached and written counts at the current
    /// Advisor's three rates; an absent or null cache count is priced as uncached input.
    /// </summary>
    [Fact]
    public async Task An_older_record_without_cost_is_priced_with_its_cache_counts()
    {
        using var run = new EvidenceToolRun();
        (run.Advisor.InputUsdPerMillion, run.Advisor.CacheReadUsdPerMillion, run.Advisor.CacheWriteUsdPerMillion) = (0.10m, 0.01m, 0.125m);
        AtomicFile.Write(run.Run.TelemetryPath, """
            [{"at":"1960-01-01 00:00:00","event":"advisor.usage","advisor":"fixture","inputTokens":1000000,"cachedInputTokens":200000,"cacheWriteTokens":300000},
             {"at":"1960-01-01 00:00:01","event":"advisor.usage","advisor":"fixture","inputTokens":1000000,"cachedInputTokens":500000},
             {"at":"1960-01-01 00:00:02","event":"advisor.usage","advisor":"fixture","inputTokens":10000,"cachedInputTokens":null,"cacheWriteTokens":null,"costUsd":null},
             {"at":"1960-01-01 00:00:03","event":"advisor.usage","advisor":"fixture","cachedInputTokens":900000}]
            """);
        var result = JsonNode.Parse(await run.Call(budgetUsd: 10m))!;
        Assert.True(result["complete"]!.GetValue<bool>());
        Assert.Equal(0.0895m + 0.055m + 0.001m, result["runCostUsd"]!.GetValue<decimal>());
    }

    /// <summary>Cancellation before a response still finalizes both records once.</summary>
    [Fact]
    public async Task Caller_cancellation_before_any_response_persists_one_record_of_each_kind()
    {
        using var run = new EvidenceToolRun();
        using var cancellation = new CancellationTokenSource();
        run.Advisor.Handler = async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("cancelled request returned");
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Call(cancellation.Token));
        run.AssertRecords();
        var usage = Assert.Single(run.Records());
        Assert.False(usage.GetProperty("complete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("servedModel").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("inputTokens").ValueKind);
    }

    /// <summary>Accepted responses survive caller cancellation before the final write.</summary>
    [Fact]
    public async Task Caller_cancellation_after_a_response_persists_one_record_of_each_kind()
    {
        using var run = new EvidenceToolRun();
        using var cancellation = new CancellationTokenSource();
        run.Advisor.Handler = (request, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Call(cancellation.Token));
        run.AssertRecords();
        Assert.Equal(2, EvidenceStore.Load(run.Run.EvidenceMapPath).Judgments.Count);
        Assert.True(Assert.Single(run.Records()).GetProperty("complete").GetBoolean());
    }

    /// <summary>A failed final store replacement still attempts both records.</summary>
    [Fact]
    public async Task A_failed_store_write_persists_one_record_of_each_kind()
    {
        using var run = new EvidenceToolRun();
        run.SeedMap();
        run.Advisor.Handler = (request, _) =>
        {
            run.Lock(run.Run.EvidenceMapPath);
            return Task.FromResult(FakeAdvisor.Answer(request));
        };
        var error = await Record.ExceptionAsync(() => run.Call());
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        run.Unlock();
        run.AssertRecords();
        Assert.Empty(EvidenceStore.Load(run.Run.EvidenceMapPath).Judgments);
    }

    /// <summary>Telemetry replacement failure preserves prior bytes and leaves the result unchanged.</summary>
    [Fact]
    public async Task A_failed_telemetry_write_preserves_prior_bytes_and_does_not_stop_the_flow_entry()
    {
        using var run = new EvidenceToolRun();
        const string before = "[ {\"event\":\"prior\",\"nested\":[1,2]} ]";
        AtomicFile.Write(run.Run.TelemetryPath, before);
        run.Lock(run.Run.TelemetryPath);
        var result = JsonNode.Parse(await run.Call())!;
        run.Unlock();
        Assert.True(result["complete"]!.GetValue<bool>());
        Assert.Equal(before, AtomicFile.Read(run.Run.TelemetryPath));
        run.AssertRecords(telemetryFails: true);
    }

    /// <summary>A blocked Flow log does not stop usage or replace success.</summary>
    [Fact]
    public async Task A_failed_flow_write_is_logged_and_does_not_stop_usage()
    {
        using var run = new EvidenceToolRun();
        AtomicFile.Write(run.Run.FlowLogPath, "prior\n");
        run.Lock(run.Run.FlowLogPath);
        var result = JsonNode.Parse(await run.Call())!;
        run.Unlock();
        Assert.True(result["complete"]!.GetValue<bool>());
        Assert.Equal("prior\n", AtomicFile.Read(run.Run.FlowLogPath));
        run.AssertRecords(flowFails: true);
    }

    /// <summary>Configured secrets never enter tool results or any Run file.</summary>
    [Fact]
    public async Task A_distinctive_key_appears_in_no_Run_file_or_result()
    {
        using var run = new EvidenceToolRun();
        var key = run.Environment["PLANFORGE_JEV_API_KEY"]!;
        var result = await run.Call();
        Assert.DoesNotContain(key, result);
        foreach (var path in Directory.EnumerateFiles(run.Run.Path, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(key, AtomicFile.Read(path));
    }

    /// <summary>A second call cannot race the first call's content-addressed store.</summary>
    [Fact]
    public async Task A_second_call_while_one_is_running_is_refused()
    {
        using var run = new EvidenceToolRun();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Advisor.Handler = async (request, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return FakeAdvisor.Answer(request);
        };
        var first = run.Call();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var error = await Assert.ThrowsAsync<ArgumentRejectedException>(() => run.Call());
            Assert.Contains("already running", error.Message);
            Assert.Single(run.Advisor.Requests);
            Assert.False(File.Exists(run.Run.FlowLogPath));
            Assert.Empty(run.Records());
        }
        finally { release.TrySetResult(); await first; }
        run.AssertRecords();
        await run.Call();
        Assert.Equal(2, run.Records().Length);
    }

    /// <summary>The source-generated argument contract is available to the real MCP schema builder.</summary>
    [Fact]
    public void Implications_have_a_source_generated_argument_contract()
    {
        var json = JsonSerializer.Serialize(new[] { new EvidenceImplication("work", ["A1.confirmedFacts.1"]) },
                                            ToolArgumentJson.Default.EvidenceImplicationArray);
        Assert.Contains("derivedFrom", json);
        Assert.Single(JsonSerializer.Deserialize(json, ToolArgumentJson.Default.EvidenceImplicationArray)!);
    }

    /// <summary>The SDK can publish the full tool schema with reflection JSON disabled.</summary>
    [Fact]
    public void The_tool_schema_requires_change_points_and_never_a_credential()
    {
        using var services = new ServiceCollection().AddSingleton(SessionRoots.None).BuildServiceProvider();
        var tool = McpServerTool.Create(typeof(ForgeTools).GetMethod(nameof(ForgeTools.EvidenceMap))!,
            options: new McpServerToolCreateOptions { Services = services, SerializerOptions = ToolArgumentJson.ArgumentOptions });
        var schema = tool.ProtocolTool.InputSchema;
        Assert.Equal(["workspaceRoot", "runId", "changePoints", "advisor", "implications", "budgetUsd"],
                     schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Equal(["workspaceRoot", "runId", "changePoints"],
                     schema.GetProperty("required").EnumerateArray().Select(property => property.GetString()));
        var text = schema.GetRawText();
        Assert.Contains("derivedFrom", text);
        Assert.DoesNotContain("API_KEY", text);
        Assert.DoesNotContain("credential", text);
    }
}

/// <summary>An isolated tool fixture with production selection and a fake transport.</summary>
internal sealed class EvidenceToolRun : IDisposable
{
    /// <summary>A distinctive item excerpt that records must omit.</summary>
    internal const string ItemMarker = "ITEM-CONTENT-73c9 WidgetApi needs verification";
    /// <summary>A distinctive plan excerpt that records must omit.</summary>
    internal const string PlanMarker = "PLAN-CONTENT-82d4";
    /// <summary>The isolated workspace.</summary>
    public string Root { get; } = Directory.CreateTempSubdirectory("planforge-evidence-tool-").FullName;
    /// <summary>The Run receiving tool calls.</summary>
    public RunDirectory Run { get; }
    /// <summary>The service used after production configuration selection succeeds.</summary>
    public FakeAdvisor Advisor { get; } = new();
    /// <summary>The manual clock for the call's bound and accounting.</summary>
    public MapperClock Clock { get; } = new();
    /// <summary>The environment lookup, separate from tool arguments.</summary>
    public Dictionary<string, string?> Environment { get; } = new() { ["PLANFORGE_JEV_API_KEY"] = "DISTINCTIVE-JEV-CREDENTIAL-19ec85" };
    /// <summary>The discovery contract, replaceable for multi-Advisor refusal tests.</summary>
    public AdvisorCatalog Catalog { get; set; } = AdvisorCatalog.Default;
    /// <summary>File handles intentionally blocking atomic replacements or exclusive appends.</summary>
    private readonly List<FileStream> locks = [];

    /// <summary>Records one sourced item and a matching plan without running a Vendor.</summary>
    public EvidenceToolRun()
    {
        Run = RunDirectory.Create(Root, "run");
        Run.WritePlan("## Approach\n\n1. Build WidgetApi. " + PlanMarker + "\n");
        Run.WriteState(new RunState(Run.RunId, Root, "Text", DateTimeOffset.UnixEpoch, 0, 5,
            BaselineHead: "base", ScoutAnswers: 1, Scout: new ScoutState(true, "codex", "model", null)));
        var report = new ScoutReport { Summary = "summary", ConfirmedFacts = [MapperRun.External(ItemMarker)],
            MaterialAssumptions = [], OpenDecisions = [], LikelyChangeSurface = [], VerificationEvidence = [], DependentsAndPinnedBehaviour = [] };
        Run.AppendScoutAnswer(new(1, "question", report, ScoutLocators.Of(Root, report)));
    }

    /// <summary>Calls the tool with real local validation and a fake Advisor after configuration selection.</summary>
    /// <param name="ct">Caller cancellation.</param>
    /// <param name="changePoints">Change points, or the fixture's matching default.</param>
    /// <param name="advisor">An explicit Advisor selection.</param>
    /// <param name="implications">Optional derived obligations.</param>
    /// <param name="budgetUsd">A Run budget the user named.</param>
    public Task<string> Call(CancellationToken ct = default, string[]? changePoints = null, string? advisor = null,
                             EvidenceImplication[]? implications = null, decimal? budgetUsd = null) =>
        ForgeTools.EvidenceMap(SessionRoots.None, Root, Run.RunId, changePoints ?? ["Change WidgetApi"], ct,
            Environment.GetValueOrDefault, (environment, id) => { Catalog.Select(environment, id); return Advisor; },
            Clock, advisor, implications, budgetUsd);

    /// <summary>Asserts refusal without any network dispatch or final records.</summary>
    /// <param name="message">The required diagnostic fragment.</param>
    /// <param name="changePoints">Optional invalid Change points.</param>
    /// <param name="advisor">Optional explicit Advisor selection.</param>
    /// <param name="implications">Optional invalid implications.</param>
    /// <param name="budgetUsd">An optional invalid budget.</param>
    public async Task Refused(string message, string[]? changePoints = null, string? advisor = null,
                              EvidenceImplication[]? implications = null, decimal? budgetUsd = null)
    {
        var error = await Record.ExceptionAsync(() => Call(changePoints: changePoints, advisor: advisor, implications: implications,
                                                           budgetUsd: budgetUsd));
        Assert.NotNull(error);
        Assert.Contains(message, error.Message);
        Assert.Empty(Advisor.Requests);
        Assert.False(File.Exists(Run.FlowLogPath));
        Assert.Empty(Records());
    }

    /// <summary>Makes the next call judge every item again, as after a map written before mappings existed.</summary>
    public void ForgetMapping() => MapperRun.ForgetMapping(Run.EvidenceMapPath);

    /// <summary>Seeds an empty valid store so an Advisor response can lock its final replacement.</summary>
    public void SeedMap() => EvidenceStore.Load(Run.EvidenceMapPath).Write([], new(), new Dictionary<string, EvidenceItemMark>(), false);

    /// <summary>Opens a readable handle that denies replacing or exclusively appending to a file.</summary>
    /// <param name="path">The existing file to hold.</param>
    public void Lock(string path) => locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    /// <summary>Releases every intentional failure handle before inspecting final files.</summary>
    public void Unlock()
    {
        foreach (var handle in locks) handle.Dispose();
        locks.Clear();
    }

    /// <summary>Reads cloned telemetry records without holding their JSON document open.</summary>
    public JsonElement[] Records()
    {
        if (!File.Exists(Run.TelemetryPath)) return [];
        using var document = JsonDocument.Parse(AtomicFile.Read(Run.TelemetryPath));
        return document.RootElement.EnumerateArray().Select(record => record.Clone()).ToArray();
    }

    /// <summary>Checks exactly one attempt for each record, whether persisted or logged as failed.</summary>
    /// <param name="flowFails">Whether the Flow-log attempt must fail.</param>
    /// <param name="telemetryFails">Whether the telemetry attempt must fail.</param>
    public void AssertRecords(bool flowFails = false, bool telemetryFails = false)
    {
        var flow = File.Exists(Run.FlowLogPath) ? AtomicFile.Read(Run.FlowLogPath) : "";
        var flowCount = flow.Split("## Evidence map", StringSplitOptions.None).Length - 1;
        Assert.Equal(flowFails ? 0 : 1, flowCount);
        Assert.Equal(telemetryFails ? 0 : 1, Records().Count(record => record.GetProperty("event").GetString() == "advisor.usage"));
        var events = AtomicFile.Read(Run.DiagnosticLogPath).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("event").GetString();
        }).ToArray();
        Assert.Equal(flowFails ? 1 : 0, events.Count(name => name == "evidence.flow.write.failed"));
        Assert.Equal(telemetryFails ? 1 : 0, events.Count(name => name == "telemetry.write.failed"));
    }

    /// <summary>Releases failure handles and removes only this fixture's workspace.</summary>
    public void Dispose()
    {
        Unlock();
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
    }
}

/// <summary>A second descriptor exercises catalogue ambiguity without constructing an external client.</summary>
/// <param name="id">The descriptor id and required variable name.</param>
internal sealed class TestAdvisorDescriptor(string id) : AdvisorDescriptor
{
    /// <summary>The test service id.</summary>
    public override string Id => id;
    /// <summary>The required test variable.</summary>
    public override IReadOnlyList<AdvisorVariable> Variables => [new(id, true, false)];
    /// <summary>Returns the test's pinned model.</summary>
    /// <param name="environment">The unused test environment lookup.</param>
    public override string Model(Func<string, string?> environment) => "requested-1";
    /// <summary>Creates a local fake, never a network client.</summary>
    /// <param name="environment">The test environment lookup.</param>
    public override IAdvisor Create(Func<string, string?> environment) => new FakeAdvisor();
    /// <summary>Accepts every non-blank test variable.</summary>
    /// <param name="variable">The test variable name.</param>
    /// <param name="value">Its non-blank value.</param>
    protected override bool IsValid(string variable, string value) => true;
}
