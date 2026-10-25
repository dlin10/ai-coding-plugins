using System.Text;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Advisors;
using PlanForge.Evidence;
using PlanForge.Infrastructure;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Exercises content keys, reusable outcomes, comparison marks and corrupt-file refusals.</summary>
public sealed class EvidenceStoreTests
{
    /// <summary>The canonical relevance key pins every line and its full SHA-256 literally.</summary>
    [Fact]
    public void Canonical_key_text_and_sha256_are_pinned()
    {
        var item = EvidenceStoreTestRun.Item() with { Text = "item", CurrentCheck = null };
        var text = EvidenceStore.KeyText("fixture", "model-1", new(["requirement"], ["exclusion"], ["change"]),
            item, EvidenceRubric.Judgments(item, [])[0]);
        const string expected = "evidence-key/1\nadvisor=fixture\nrequestedModel=model-1\nrubric=1\nselection=1\nschema=1\n"
            + "state=b147a930d11dd16291a3e29b1567322f1ba028280d93e9fc70046ab3bce978ea\n"
            + "item=06cee54d004862c15cd58eb7e107a327888084e4f05e01ac40a305a9bb332dea\nunit=-\nkind=relevance";
        Assert.Equal(expected, text);
        Assert.Equal("502a4cbcaf1ebc623b24954944ba9af3d980ff9544162c2cfbc67716302f19ef", EvidenceStore.Hash(text));
    }

    /// <summary>A write prunes keys that no longer belong to this call.</summary>
    [Fact]
    public async Task Write_keeps_only_current_keys()
    {
        using var run = new EvidenceStoreTestRun();
        var first = await run.CallAsync();
        var other = new string('b', 64);
        var store = EvidenceStore.Load(run.Path);
        store.Put(other, store.Judgments[first.Key]);
        store.Write([other], new(), new Dictionary<string, EvidenceItemMark>(), false);
        Assert.Equal(other, Assert.Single(EvidenceStore.Load(run.Path).Judgments).Key);
    }

    /// <summary>A persistent reason survives a second call without another Advisor request.</summary>
    [Fact]
    public async Task Persistent_reason_is_stored_and_not_asked_again()
    {
        using var run = new EvidenceStoreTestRun();
        run.Advisor.Outcome = _ => new Refused();
        Assert.Equal("refused", (await run.CallAsync()).Stored!.Questions["relevance"].Reason);
        var next = await run.CallAsync();
        Assert.True(next.CacheHit);
        Assert.Equal(1, run.Advisor.Requests);
    }

    /// <summary>A transient outcome exists only in latest and is requested again.</summary>
    [Fact]
    public async Task Transient_reason_is_only_in_latest_and_next_call_asks_again()
    {
        using var run = new EvidenceStoreTestRun();
        run.Advisor.Failure = AdvisorFailureKind.Unavailable;
        await run.CallAsync();
        var store = EvidenceStore.Load(run.Path);
        Assert.Empty(store.Judgments);
        Assert.Equal("unavailable", store.Latest!["reasons"]![0]!.GetValue<string>());
        run.Advisor.Failure = null;
        Assert.False((await run.CallAsync()).CacheHit);
        Assert.Equal(2, run.Advisor.Requests);
    }

    /// <summary>A refused obligation cannot erase a stored contradiction.</summary>
    [Fact]
    public async Task Contradicts_and_refused_obligation_round_trip_as_contradiction() =>
        await ContradictionAsync(new Refused(), "refused");

    /// <summary>An invalid obligation cannot erase a stored contradiction.</summary>
    [Fact]
    public async Task Contradicts_and_invalid_obligation_round_trip_as_contradiction() =>
        await ContradictionAsync(new Answered("unknown", new Dictionary<string, double>()), "invalid_answer");

    /// <summary>Incomplete calls retain prior complete marks; the next complete call replaces them.</summary>
    [Fact]
    public async Task Last_complete_is_replaced_only_when_complete()
    {
        using var run = new EvidenceStoreTestRun();
        var call = await run.CallAsync();
        var store = EvidenceStore.Load(run.Path);
        var initial = new EvidenceItemMark("uncertain", false);
        store.Write([call.Key], new(), new Dictionary<string, EvidenceItemMark> { ["old"] = initial }, true);
        store.Write([call.Key], new(), new Dictionary<string, EvidenceItemMark> { ["new"] = new("unchecked", true) }, false);
        Assert.Equal(initial, EvidenceStore.Load(run.Path).LastComplete["old"]);
        Assert.False(EvidenceStore.Load(run.Path).LastComplete.ContainsKey("new"));
        store.Write([call.Key], new(), new Dictionary<string, EvidenceItemMark> { ["new"] = new("covered", false) }, true);
        Assert.Equal("new", Assert.Single(EvidenceStore.Load(run.Path).LastComplete).Key);
    }

    /// <summary>Every flag is new until there has been a complete map.</summary>
    [Fact]
    public void Every_flag_is_new_before_a_complete_map()
    {
        using var run = new EvidenceStoreTestRun();
        var store = EvidenceStore.Load(run.Path);
        foreach (var status in new[] { "contradiction", "unchecked", "uncertain", "no_candidates", "no_coverage_established", "covered" })
            Assert.Equal("new", store.Change(EvidenceStoreTestRun.Item(), new(status, true)));
        store.Write([], new(), new Dictionary<string, EvidenceItemMark> { [EvidenceStoreTestRun.Item().Id] = new("unchecked", false) }, false);
        Assert.Equal("new", EvidenceStore.Load(run.Path).Change(EvidenceStoreTestRun.Item(), new("unchecked", false)));
    }

    /// <summary>Complete calls compare against the preceding complete map before replacing it.</summary>
    [Fact]
    public void New_and_carried_on_a_complete_call() => CheckChanges(true);

    /// <summary>Incomplete calls compare against the same complete map and do not advance it.</summary>
    [Fact]
    public void New_and_carried_on_an_incomplete_call() => CheckChanges(false);

    /// <summary>An implication's identity includes its text and derivation, rather than its H position.</summary>
    [Fact]
    public void Implications_are_compared_by_text_and_derived_from()
    {
        using var run = new EvidenceStoreTestRun();
        var store = EvidenceStore.Load(run.Path);
        var item = EvidenceStoreTestRun.Item() with { Id = "H1", Kind = "implication", Category = null, DerivedFrom = ["A1.confirmedFacts.1"] };
        var mark = new EvidenceItemMark("uncertain", false);
        store.Write([], new(), new Dictionary<string, EvidenceItemMark> { [EvidenceStore.ComparisonKey(item)] = mark }, true);
        Assert.Equal("carried", store.Change(item with { Id = "H8" }, mark));
        Assert.Equal("new", store.Change(item with { Text = "different implication" }, mark));
        Assert.Equal("new", store.Change(item with { DerivedFrom = ["A2.confirmedFacts.1"] }, mark));
        Assert.Equal("new", store.Change(item with { DerivedFrom = ["A1.confirmedFacts.1", "A2.confirmedFacts.1"] }, mark));
    }

    /// <summary>Broken JSON is refused without an Advisor request or a file replacement.</summary>
    [Fact]
    public async Task Broken_json_is_refused_unchanged() => await MalformedAsync(_ => "{broken");

    /// <summary>Unknown schema versions are refused without replacement.</summary>
    [Fact]
    public async Task Schema_two_is_refused_unchanged() => await MutateAsync(root => root["schema"] = 2);

    /// <summary>A truncated SHA-256 key is refused without replacement.</summary>
    [Fact]
    public async Task Key_of_63_characters_is_refused_unchanged() => await MutateAsync(root =>
    {
        var judgments = root["judgments"]!.AsObject();
        var entry = judgments.First();
        judgments.Remove(entry.Key);
        judgments.Add(entry.Key[..63], entry.Value);
    });

    /// <summary>A pair must preserve both roles, even when the relationship contradicts.</summary>
    [Fact]
    public async Task Pair_without_obligation_is_refused_unchanged() => await MutateAsync(root =>
        Questions(root).Remove("obligation"), pair: true);

    /// <summary>Transient reasons cannot enter the reusable judgment dictionary.</summary>
    [Fact]
    public async Task Stored_transient_reason_is_refused_unchanged() => await MutateAsync(root =>
        Questions(root)["relevance"] = new JsonObject { ["answer"] = null, ["reason"] = "cancelled" });

    /// <summary>A distribution with total 0.9 is invalid both fresh and persisted.</summary>
    [Fact]
    public async Task Probability_sum_point_nine_is_refused_unchanged() => await MutateAsync(root =>
        Questions(root)["relevance"]!["answer"]!["probabilities"]!["implies_action"] = 0.9);

    /// <summary>A chosen value absent from its distribution is invalid.</summary>
    [Fact]
    public async Task Choice_outside_probabilities_is_refused_unchanged() => await MutateAsync(root =>
        Questions(root)["relevance"]!["answer"]!["choice"] = "other");

    /// <summary>A structurally valid distribution with different options is refused before any request.</summary>
    [Fact]
    public async Task Used_answer_with_other_options_is_refused_unchanged() => await MutateAsync(root =>
    {
        var answer = Questions(root)["relevance"]!["answer"]!;
        answer["choice"] = "other";
        answer["probabilities"] = new JsonObject { ["other"] = 1.0 };
    });

    /// <summary>A complete mark needs a recognized status.</summary>
    [Fact]
    public async Task Unknown_last_complete_status_is_refused_unchanged() => await MutateAsync(root =>
        root["lastComplete"] = new JsonObject { ["item"] = new JsonObject { ["status"] = "invented", ["sourceChanged"] = false } });

    /// <summary>A source-changed mark must be explicitly present in the file.</summary>
    [Fact]
    public async Task Missing_last_complete_mark_is_refused_unchanged() => await MutateAsync(root =>
        root["lastComplete"] = new JsonObject { ["item"] = new JsonObject { ["status"] = "covered" } });

    /// <summary>Blank reported models are not reusable identities.</summary>
    /// <param name="model">An empty or whitespace model that must remain malformed.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Blank_served_model_is_refused_unchanged(string model) => await MutateAsync(root =>
        root["judgments"]!.AsObject().First().Value!["servedModel"] = model);

    /// <summary>A judgment containing only persistent reasons accepts an unknown model across a store round trip.</summary>
    /// <param name="pair">Whether to store two persistent question outcomes instead of relevance.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_served_model_is_accepted_for_persistent_reasons_only(bool pair)
    {
        using var run = new EvidenceStoreTestRun();
        run.Advisor.Outcome = _ => new Refused();
        var judgment = EvidenceRubric.Judgments(EvidenceStoreTestRun.Item(), pair ? [EvidenceStoreTestRun.Unit()] : [])[pair ? 1 : 0];
        var first = await run.CallAsync(judgment: judgment);
        var root = JsonNode.Parse(AtomicFile.Read(run.Path))!.AsObject();
        root["judgments"]![first.Key]!["servedModel"] = null;
        AtomicFile.Write(run.Path, root.ToJsonString());
        var next = await run.CallAsync(judgment: judgment);
        Assert.True(next.CacheHit);
        Assert.Null(next.Stored!.ServedModel);
        Assert.All(next.Stored.Questions.Values, outcome => Assert.Equal("refused", outcome.Reason));
        Assert.Equal(1, run.Advisor.Requests);
    }

    /// <summary>Persistent reasons do not make an empty or whitespace served model a valid identity.</summary>
    /// <param name="model">The malformed reported model.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Blank_served_model_for_persistent_reasons_is_refused_unchanged(string model) => await MutateAsync(root =>
    {
        root["judgments"]!.AsObject().First().Value!["servedModel"] = model;
        Questions(root)["relevance"] = new JsonObject { ["answer"] = null, ["reason"] = "rejected" };
    });

    /// <summary>A question outcome cannot contain both an answer and an unchecked reason.</summary>
    [Fact]
    public async Task Answer_and_reason_together_are_refused_unchanged() => await MutateAsync(root =>
        Questions(root)["relevance"]!["reason"] = "refused");

    /// <summary>Confidence obeys the same finite unit interval as fresh answers.</summary>
    [Fact]
    public async Task Out_of_range_confidence_is_refused_unchanged() => await MutateAsync(root =>
        Questions(root)["relevance"]!["answer"]!["confidence"] = 1.1);

    /// <summary>Every persistent reason is reusable and every transient reason remains outside judgments.</summary>
    [Fact]
    public void All_reason_kinds_obey_persistence_boundary()
    {
        using var run = new EvidenceStoreTestRun();
        var store = EvidenceStore.Load(run.Path);
        var judgment = EvidenceRubric.Judgments(EvidenceStoreTestRun.Item(), [])[0];
        foreach (var reason in new[] { "too_large", "rejected", "invalid_answer", "refused", "sensitive" })
            Assert.True(store.Put(EvidenceStore.Hash(reason), EvidenceStoreTestRun.Stored(judgment, _ => new(null, reason))));
        foreach (var reason in new[] { "unauthorized", "forbidden", "rate_limited", "overloaded", "timed_out", "unavailable",
                                      "invalid_response", "cancelled", "not_yet_checked" })
            Assert.False(store.Put(EvidenceStore.Hash(reason), EvidenceStoreTestRun.Stored(judgment, _ => new(null, reason))));
        Assert.Equal(5, store.Judgments.Count);
    }

    /// <summary>Item and unit ids are absent from keys; equal content deduplicates.</summary>
    [Fact]
    public void Identical_content_shares_one_key_and_one_stored_outcome()
    {
        using var run = new EvidenceStoreTestRun();
        var item = EvidenceStoreTestRun.Item();
        var pair = EvidenceRubric.Judgments(item, [EvidenceStoreTestRun.Unit()])[1];
        var same = pair with { Unit = pair.Unit! with { Id = "s99", Start = 1000 } };
        var first = EvidenceStore.KeyText("fixture", "model-1", run.Shared, item, pair);
        Assert.Equal(first, EvidenceStore.KeyText("fixture", "model-1", run.Shared, item with { Id = "A8.confirmedFacts.3" }, same));
        var store = EvidenceStore.Load(run.Path);
        var key = EvidenceStore.Hash(first);
        store.Put(key, EvidenceStoreTestRun.Stored(pair, q => new(EvidenceStoreTestRun.Answer(q), null)));
        Assert.Same(store.Find(key, pair), store.Find(key, same));
        Assert.Single(store.Judgments);
    }

    /// <summary>The Run path is the sole JSON Evidence map path.</summary>
    [Fact]
    public void Run_directory_exposes_evidence_map_path()
    {
        using var run = new EvidenceStoreTestRun();
        Assert.Equal(run.Path, RunDirectory.FromPath(System.IO.Path.GetDirectoryName(run.Path)!).EvidenceMapPath);
    }

    /// <summary>Confirms both stored roles and cache-only contradiction settlement.</summary>
    /// <param name="obligation">A refused or invalid obligation response.</param>
    /// <param name="reason">Its expected checked reason.</param>
    private static async Task ContradictionAsync(AdviceOutcome obligation, string reason)
    {
        using var run = new EvidenceStoreTestRun();
        var pair = EvidenceRubric.Judgments(EvidenceStoreTestRun.Item(), [EvidenceStoreTestRun.Unit()])[1];
        run.Advisor.Outcome = q => q.Id.StartsWith("relationship:", StringComparison.Ordinal)
            ? EvidenceStoreTestRun.Answer(q, "contradicts") : obligation;
        await run.CallAsync(judgment: pair);
        var next = await run.CallAsync(judgment: pair);
        Assert.True(next.CacheHit);
        Assert.Equal(1, run.Advisor.Requests);
        Assert.Equal(2, next.Stored!.Questions.Count);
        Assert.Equal(reason, next.Stored.Questions["obligation"].Reason);
        Assert.Equal("contradiction", EvidenceVerdicts.Pair(next.Stored.Questions["relationship"], next.Stored.Questions["obligation"]));
    }

    /// <summary>Checks unchanged, status-changed and source-changed flags against a saved complete map.</summary>
    /// <param name="complete">Whether this subsequent call completes.</param>
    private static void CheckChanges(bool complete)
    {
        using var run = new EvidenceStoreTestRun();
        var item = EvidenceStoreTestRun.Item();
        var mark = new EvidenceItemMark("uncertain", false);
        var store = EvidenceStore.Load(run.Path);
        store.Write([], new(), new Dictionary<string, EvidenceItemMark> { [item.Id] = mark }, true);
        store = EvidenceStore.Load(run.Path);
        Assert.Equal("carried", store.Change(item, mark));
        Assert.Equal("new", store.Change(item, mark with { Status = "contradiction" }));
        Assert.Equal("new", store.Change(item, mark with { SourceChanged = true }));
        var changed = mark with { Status = "unchecked", SourceChanged = true };
        store.Write([], new(), new Dictionary<string, EvidenceItemMark> { [item.Id] = changed }, complete);
        store = EvidenceStore.Load(run.Path);
        Assert.Equal(complete ? "carried" : "new", store.Change(item, changed));
        Assert.Equal(complete ? "new" : "carried", store.Change(item, mark));
    }

    /// <summary>Returns the first stored judgment's question roles for deliberate file corruption.</summary>
    /// <param name="root">The valid file to mutate.</param>
    private static JsonObject Questions(JsonObject root) => root["judgments"]!.AsObject().First().Value!["questions"]!.AsObject();

    /// <summary>Corrupts one valid file field, then exercises the next call's load boundary.</summary>
    /// <param name="mutate">The independent corruption to introduce.</param>
    /// <param name="pair">Whether to create a pair instead of relevance.</param>
    private static Task MutateAsync(Action<JsonObject> mutate, bool pair = false) => MalformedAsync(text =>
    {
        var root = JsonNode.Parse(text)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }, pair);

    /// <summary>Proves a malformed file is refused before requests and retains its exact UTF-8 bytes.</summary>
    /// <param name="corrupt">Produces the malformed file from a valid file.</param>
    /// <param name="pair">Whether to store a pair for the corruption.</param>
    private static async Task MalformedAsync(Func<string, string> corrupt, bool pair = false)
    {
        using var run = new EvidenceStoreTestRun();
        var judgment = EvidenceRubric.Judgments(EvidenceStoreTestRun.Item(), pair ? [EvidenceStoreTestRun.Unit()] : [])[pair ? 1 : 0];
        await run.CallAsync(judgment: judgment);
        AtomicFile.Write(run.Path, corrupt(AtomicFile.Read(run.Path)));
        var before = File.ReadAllBytes(run.Path);
        var requests = run.Advisor.Requests;
        var error = await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => run.CallAsync(judgment: judgment));
        Assert.Contains("evidence-map.json", error.Message, StringComparison.Ordinal);
        Assert.Equal(requests, run.Advisor.Requests);
        Assert.Equal(before, File.ReadAllBytes(run.Path));
    }
}

/// <summary>A small store/rubric driver until the bounded mapper is added in the next task.</summary>
internal sealed class EvidenceStoreTestRun : IDisposable
{
    /// <summary>The owned temporary directory, outside the repository.</summary>
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evidence-store-" + Guid.NewGuid().ToString("N"));

    /// <summary>The store path used by successive calls.</summary>
    public string Path => System.IO.Path.Combine(directory, "evidence-map.json");

    /// <summary>A network-free Advisor with an independent byte measurement.</summary>
    public StoreAdvisor Advisor { get; } = new();

    /// <summary>Default shared inputs for focused persistence tests.</summary>
    public EvidenceSharedState Shared { get; } = new(["requirement"], ["exclusion"], ["change"]);

    /// <summary>Runs a primary judgment and any packed neighbours through load, packing, validation and persistence.</summary>
    /// <param name="shared">The current canonical shared inputs.</param>
    /// <param name="item">The current evidence item.</param>
    /// <param name="judgment">The primary judgment whose cache provenance is reported.</param>
    /// <param name="neighbours">Other judgments packed before or after it.</param>
    /// <param name="rubric">The key's rubric version.</param>
    /// <param name="selection">The key's selection version.</param>
    public async Task<StoreCall> CallAsync(EvidenceSharedState? shared = null, EvidenceItem? item = null,
                                          EvidenceJudgment? judgment = null, IReadOnlyList<EvidenceJudgment>? neighbours = null,
                                          string rubric = "1", string selection = "1")
    {
        shared ??= Shared;
        item ??= Item();
        judgment ??= EvidenceRubric.Judgments(item, [])[0];
        var judgments = (neighbours ?? []).Append(judgment).ToArray();
        /// <summary>Names each judgment solely by its current canonical inputs.</summary>
        /// <param name="part">The judgment to address.</param>
        string Key(EvidenceJudgment part) => EvidenceStore.Hash(EvidenceStore.KeyText(Advisor.Id, Advisor.RequestedModel,
            shared, item, part, rubric, selection));
        var required = judgments.DistinctBy(Key).ToDictionary(Key, part => part);
        var store = EvidenceStore.Load(Path, required);
        var key = Key(judgment);
        var hit = store.Find(key, judgment) is not null;
        var missing = required.Where(entry => store.Find(entry.Key, entry.Value) is null).Select(entry => entry.Value).ToArray();
        var packing = EvidenceRubric.Pack(Advisor, shared, item, missing);
        var reasons = new List<string>();
        foreach (var uncheckedJudgment in packing.Unchecked)
        {
            reasons.Add(uncheckedJudgment.Reason);
            store.Put(Key(uncheckedJudgment.Judgment), Stored(uncheckedJudgment.Judgment,
                _ => new(null, uncheckedJudgment.Reason), Advisor.RequestedModel));
        }
        foreach (var request in packing.Requests)
        {
            try
            {
                var result = await Advisor.AskAsync(request.Request, CancellationToken.None);
                var validation = EvidenceRubric.Validate(request.Request, result);
                foreach (var part in request.Judgments)
                    store.Put(Key(part), Stored(part, q => validation.Questions[q.Id], Advisor.RequestedModel));
            }
            catch (AdvisorException)
            {
                reasons.Add("unavailable");
                foreach (var part in request.Judgments)
                    store.Put(Key(part), Stored(part, _ => new(null, "unavailable"), Advisor.RequestedModel));
            }
        }
        store.Write(required.Keys, new JsonObject { ["complete"] = reasons.All(EvidenceStore.IsPersistent),
            ["reasons"] = new JsonArray(reasons.Select(reason => (JsonNode?)JsonValue.Create(reason)).ToArray()) },
            new Dictionary<string, EvidenceItemMark>(), reasons.All(EvidenceStore.IsPersistent));
        return new(key, hit, EvidenceStore.Load(Path, required).Find(key, judgment));
    }

    /// <summary>Builds one well-formed Scout item with a resolved local source.</summary>
    public static EvidenceItem Item() => new("A1.confirmedFacts.1", "scout", "confirmedFacts", "ConsumerCache requires consumers",
        "src/ConsumerCache.cs:1", new(true, "0123456789abcdef", null), new(true, "0123456789abcdef", null), false, []);

    /// <summary>Builds a standalone candidate unit.</summary>
    public static PlanUnit Unit() => new("s1", "other", "ConsumerCache", "Verify ConsumerCache consumers.\n",
        PlanOutline.Hash("Verify ConsumerCache consumers.\n"), 0, "Verify ConsumerCache consumers.\n".Length);

    /// <summary>Builds an exact one-hot answer to a current question.</summary>
    /// <param name="question">The question defining every option.</param>
    /// <param name="choice">The option to choose, or the first option.</param>
    public static Answered Answer(ChoiceQuestion question, string? choice = null)
    {
        choice ??= question.Options[0].Value;
        return new(choice, question.Options.ToDictionary(option => option.Value, option => option.Value == choice ? 1.0 : 0));
    }

    /// <summary>Projects current question ids onto the persisted role names.</summary>
    /// <param name="judgment">The current judgment.</param>
    /// <param name="outcome">The checked outcome of each current question.</param>
    /// <param name="model">The requested model.</param>
    public static StoredEvidenceJudgment Stored(EvidenceJudgment judgment, Func<ChoiceQuestion, EvidenceQuestionOutcome> outcome,
                                                string model = "model-1") =>
        new(judgment.Kind, "fixture", model, "served", judgment.Questions.ToDictionary(q => EvidenceStore.QuestionName(q.Id), outcome));

    /// <summary>Deletes only this driver's explicitly owned temporary directory.</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
    }
}

/// <summary>The primary judgment's key, provenance and stored result after a simulated call.</summary>
/// <param name="Key">The canonical content key.</param>
/// <param name="CacheHit">Whether the call found its outcome before packing.</param>
/// <param name="Stored">The stored checked outcome, or null for a transient failure.</param>
internal sealed record StoreCall(string Key, bool CacheHit, StoredEvidenceJudgment? Stored);

/// <summary>A fake service measuring the actual state, model and question JSON in bytes.</summary>
internal sealed class StoreAdvisor : IAdvisor
{
    /// <summary>The fake service's stable identity.</summary>
    public string Id => "fixture";
    /// <summary>The selected model, adjustable for invalidation tests.</summary>
    public string RequestedModel { get; set; } = "model-1";
    /// <summary>The byte and count limits, adjustable at boundary cells.</summary>
    public AdvisorLimits Limits { get; set; } = new("bytes", int.MaxValue, int.MaxValue, 64, 255, 1, TimeSpan.FromSeconds(30));
    /// <summary>The store tests never price a request.</summary>
    public decimal InputUsdPerMillion => 0m;
    /// <summary>The number of calls actually sent to the fake service.</summary>
    public int Requests { get; private set; }
    /// <summary>The outcome factory for each question.</summary>
    public Func<ChoiceQuestion, AdviceOutcome> Outcome { get; set; } = question => EvidenceStoreTestRun.Answer(question);
    /// <summary>An optional failure for testing transient persistence.</summary>
    public AdvisorFailureKind? Failure { get; set; }

    /// <summary>Measures the exact body and state plus its largest individual question entry.</summary>
    /// <param name="request">The unmodified request.</param>
    public AdviceSize Measure(AdviceRequest request)
    {
        var questions = new JsonObject();
        foreach (var q in request.Questions)
        {
            var options = new JsonObject();
            foreach (var option in q.Options) options.Add(option.Value, option.Description);
            questions.Add(q.Id, new JsonObject { ["instructions"] = q.Instructions, ["criteria"] = options });
        }
        var body = new JsonObject { ["state"] = request.State.DeepClone(), ["model"] = RequestedModel, ["questions"] = questions };
        return new(Bytes(body.ToJsonString()), Bytes(request.State.ToJsonString())
            + request.Questions.Select(q => Bytes(new JsonObject { [q.Id] = questions[q.Id]!.DeepClone() }.ToJsonString())).DefaultIfEmpty().Max());
    }

    /// <summary>Answers without network and asserts that every measured limit is respected.</summary>
    /// <param name="request">The exact request that packing accepted.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    public Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests++;
        var size = Measure(request);
        Assert.True(size.Total <= Limits.MaxTotal);
        Assert.True(size.StatePlusLargestQuestion <= Limits.MaxStatePlusQuestion);
        if (Failure is { } failure) throw new AdvisorException(failure);
        return Task.FromResult(new AdviceResult(request.Questions.ToDictionary(q => q.Id, Outcome), "served"));
    }

    /// <summary>Counts UTF-8 bytes independently of the production key serializers.</summary>
    /// <param name="text">The serialized JSON text.</param>
    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
}
