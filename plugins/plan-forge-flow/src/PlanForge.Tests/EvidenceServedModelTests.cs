using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Advisors;
using PlanForge.Advisors.Jev;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Mcp;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks that only service responses establish a served model across persistence and reporting.</summary>
public sealed class EvidenceServedModelTests
{
    /// <summary>A cached answer must retain its model identity before any fresh model can answer another item.</summary>
    /// <param name="role">The question holding a valid answer; the other pair question is refused.</param>
    /// <param name="model">The missing or blank model identity written over model A.</param>
    [Theory]
    [InlineData("relevance", null)]
    [InlineData("relevance", "")]
    [InlineData("relevance", " ")]
    [InlineData("relationship", null)]
    [InlineData("relationship", "")]
    [InlineData("relationship", " ")]
    [InlineData("obligation", null)]
    [InlineData("obligation", "")]
    [InlineData("obligation", " ")]
    public async Task Stored_answer_without_served_model_is_refused_before_fresh_requests(string role, string? model)
    {
        using var run = role == "relevance" ? MapperRun.Plain(1) : new MapperRun();
        run.Advisor.Choose = question => EvidenceStore.QuestionName(question.Id) == role || question.Id == "relevance"
            ? FakeAdvisor.Valid(question, role == "relationship" && question.Id.StartsWith("relationship:", StringComparison.Ordinal)
                ? "contradicts" : null)
            : new Refused();
        await run.Call();
        var root = JsonNode.Parse(AtomicFile.Read(run.Path))!.AsObject();
        var kind = role == "relevance" ? "relevance" : "pair";
        var stored = root["judgments"]!.AsObject().Single(entry => entry.Value!["kind"]!.GetValue<string>() == kind).Value!;
        Assert.Equal("A", stored["servedModel"]!.GetValue<string>());
        Assert.NotNull(stored["questions"]![role]!["answer"]);
        stored["servedModel"] = model;
        AtomicFile.Write(run.Path, root.ToJsonString());
        var before = File.ReadAllBytes(run.Path);
        var requests = run.Advisor.Requests.Count;
        run.Entries = [run.Entry(run.Entries[0].Report.ConfirmedFacts.Append(MapperRun.External("another fact")).ToArray())];
        run.Advisor.Handler = (request, _) => Task.FromResult(FakeAdvisor.Answer(request, "B"));

        var error = await Assert.ThrowsAsync<JsonException>(() => run.Call());

        Assert.Contains("evidence-map.json", error.Message, StringComparison.Ordinal);
        Assert.Equal(requests, run.Advisor.Requests.Count);
        Assert.Equal(before, File.ReadAllBytes(run.Path));
    }

    /// <summary>Production Jev sizing withholds an oversized state without inventing a model on cache reuse.</summary>
    [Fact]
    public async Task Jev_oversized_state_keeps_unknown_model_in_results_and_records_on_both_calls()
    {
        using var run = new EvidenceToolRun();
        run.Run.WritePlan("## Requirements\n1. **R1.** Change WidgetApi.\n".PadRight(120_058, 'x'));
        var handler = new UnusedHandler();
        using var advisor = new JevAdvisor("test-key", "jev-1.13.0", handler);
        for (var call = 0; call < 2; call++)
        {
            if (call == 1) run.ForgetMapping();
            var result = JsonNode.Parse(await ForgeTools.EvidenceMap(SessionRoots.None, run.Root, run.Run.RunId,
                ["Change WidgetApi"], CancellationToken.None, run.Environment.GetValueOrDefault,
                (_, _) => advisor, run.Clock))!;
            Assert.Null(result["servedModel"]);
            Assert.True(result["complete"]!.GetValue<bool>());
            Assert.Equal(2, result["required"]!.GetValue<int>());
            Assert.Equal(call == 0 ? 0 : 2, result["cacheHits"]!.GetValue<int>());
            Assert.Equal(call == 0 ? 2 : 0, result["checkedThisCall"]!.GetValue<int>());
            var store = EvidenceStore.Load(run.Run.EvidenceMapPath);
            Assert.Null(store.Latest!["servedModel"]);
            Assert.Equal(2, store.Judgments.Count);
            Assert.All(store.Judgments.Values, judgment =>
            {
                Assert.Null(judgment.ServedModel);
                Assert.All(judgment.Questions.Values, outcome => Assert.Equal("too_large", outcome.Reason));
            });
        }
        Assert.Equal(0, handler.Requests);
        var flow = AtomicFile.Read(run.Run.FlowLogPath);
        Assert.Equal(2, flow.Split("Served model: unknown", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, run.Records().Length);
        Assert.All(run.Records(), record =>
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, record.GetProperty("servedModel").ValueKind);
            Assert.Equal(0, record.GetProperty("requests").GetInt32());
        });
    }

    /// <summary>Local withholding remains model-independent even after reading its stored outcome.</summary>
    /// <param name="reason">The locally decided persistent reason.</param>
    [Theory]
    [InlineData("sensitive")]
    [InlineData("too_large")]
    public async Task Local_outcomes_have_no_served_model_on_either_call(string reason)
    {
        using var run = MapperRun.Plain(1);
        if (reason == "sensitive") run.ChangePoints = ["api_key=sk-abcdefghijklmnopqrstuvwxyz123456"];
        else run.Advisor.Limits = run.Advisor.Limits with { MaxStatePlusQuestion = 0 };
        for (var call = 0; call < 2; call++)
        {
            if (call == 1) run.ForgetMapping();
            var map = await run.Call();
            Assert.Null(map.Result.ServedModel);
            Assert.Null(run.Store().Latest!["servedModel"]);
            Assert.All(run.Store().Judgments.Values, judgment => Assert.Null(judgment.ServedModel));
            var judgment = Assert.Single(Assert.Single(map.Observation).Judgments);
            Assert.Equal(reason, judgment.Questions["relevance"].Reason);
            Assert.Equal(call == 1, judgment.FromStore);
        }
        Assert.Empty(run.Advisor.Requests);
    }

    /// <summary>A persistent single-judgment failure keeps no model, even when another request reported one.</summary>
    /// <param name="failure">The persistent request failure.</param>
    /// <param name="answeredFirst">Whether an earlier item is answered by model A.</param>
    [Theory]
    [InlineData("Rejected", false)]
    [InlineData("TooLarge", false)]
    [InlineData("Rejected", true)]
    [InlineData("TooLarge", true)]
    public async Task Persistent_request_failure_keeps_unknown_model_on_cache_reuse(string failure, bool answeredFirst)
    {
        using var run = MapperRun.Plain(answeredFirst ? 2 : 1);
        run.Advisor.Handler = (request, _) =>
        {
            if (answeredFirst && run.Advisor.Requests.Count == 1) return Task.FromResult(FakeAdvisor.Answer(request, "A"));
            throw new AdvisorException(Enum.Parse<AdvisorFailureKind>(failure));
        };
        for (var call = 0; call < 2; call++)
        {
            if (call == 1) run.ForgetMapping();
            var map = await run.Call();
            Assert.Equal(answeredFirst ? "A" : null, map.Result.ServedModel);
            Assert.Null(run.Store().Judgments.Values.Last().ServedModel);
            Assert.True(map.Result.Complete);
            Assert.Equal(call == 1, map.Observation.Last().Judgments[0].FromStore);
        }
        Assert.Equal(answeredFirst ? 2 : 1, run.Advisor.Requests.Count);
    }

    /// <summary>Unknown-model cache entries coexist with known models and survive a later model change.</summary>
    /// <param name="sensitiveFirst">Whether the unknown-model entry is read before the model A entry.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_model_cache_entries_do_not_set_or_conflict_with_the_call_model(bool sensitiveFirst)
    {
        using var run = MapperRun.Plain(2);
        var sensitive = MapperRun.External("api_key=sk-abcdefghijklmnopqrstuvwxyz123456");
        var ordinary = MapperRun.External("ordinary fact");
        var facts = sensitiveFirst ? new[] { sensitive, ordinary } : new[] { ordinary, sensitive };
        run.Entries = [run.Entry(facts)];
        var sensitiveIndex = sensitiveFirst ? 0 : 1;
        await run.Call();
        run.ForgetMapping();
        var cached = await run.Call();
        Assert.Equal("A", cached.Result.ServedModel);
        Assert.Equal(2, cached.Result.CacheHits);
        Assert.All(cached.Observation, item => Assert.True(item.Judgments[0].FromStore));
        Assert.Null(run.Store().Judgments[cached.Observation[sensitiveIndex].Judgments[0].Key].ServedModel);
        run.Entries = [run.Entry(facts.Append(MapperRun.External("another fact")).ToArray())];
        run.Advisor.Handler = (request, _) => Task.FromResult(FakeAdvisor.Answer(request, "B"));
        run.ForgetMapping();
        var changed = await run.Call();
        Assert.Equal("B", changed.Result.ServedModel);
        var retained = changed.Observation[sensitiveIndex].Judgments[0];
        Assert.True(retained.FromStore);
        Assert.Equal("sensitive", retained.Questions["relevance"].Reason);
        Assert.Null(run.Store().Judgments[retained.Key].ServedModel);
        Assert.Equal("not_yet_checked", changed.Observation[1 - sensitiveIndex].Judgments[0].Questions["relevance"].Reason);
        Assert.Equal("B", run.Store().Judgments[changed.Observation[2].Judgments[0].Key].ServedModel);
    }

    /// <summary>A transport that makes any accidental network dispatch fail immediately.</summary>
    private sealed class UnusedHandler : HttpMessageHandler
    {
        /// <summary>The number of attempted HTTP requests.</summary>
        public int Requests { get; private set; }

        /// <summary>Counts unexpected dispatch and returns a deterministic local rejection.</summary>
        /// <param name="request">The unexpected outgoing request.</param>
        /// <param name="cancellationToken">The request cancellation token.</param>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }
}
