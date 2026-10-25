using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlanForge.Acts;
using PlanForge.Advisors;
using PlanForge.Evidence;
using Xunit;

namespace PlanForge.Tests;

/// <summary>Checks canonical rubric inputs, adapter-measured packing, and fresh and stored validity.</summary>
public sealed class EvidenceRubricTests
{
    /// <summary>The independently specified relevance wording.</summary>
    private const string RELEVANCE = "Decide whether the evidence item in `item.text` bears on any of the Change points in `changePoints`, so that the plan must change, test, verify or decide something because of it.";

    /// <summary>The independently specified relationship wording.</summary>
    private const string RELATIONSHIP = "Compare the plan section below with the evidence item in `item.text`. How does the section relate to what the item says?";

    /// <summary>The independently specified obligation wording.</summary>
    private const string OBLIGATION = "Does the plan section below explicitly commit the plan to act on the evidence item in `item.text`?";

    /// <summary>A synthetic assignment recognized by the existing content guard.</summary>
    private const string SECRET = "api_key = AbCdEf0123456789+/AbCdEf0123456789";

    /// <summary>The relevance wording and all alternatives are exact and ordered.</summary>
    [Fact]
    public void Relevance_has_exact_wording_and_options()
    {
        var question = Assert.Single(EvidenceRubric.Judgments(Item(), [])).Questions.Single();
        Assert.Equal("relevance", question.Id);
        Assert.Equal(RELEVANCE, question.Instructions);
        Assert.Equal(new ChoiceOption[]
        {
            new("implies_action", "The item states a fact about at least one Change point that the plan must act on: change, test, verify or decide something."),
            new("context_only", "The item only describes background; nothing in it requires the plan to act for these Change points."),
            new("insufficient_context", "The state does not contain enough to tell.")
        }, question.Options);
    }

    /// <summary>The relationship wording and all alternatives are exact and ordered.</summary>
    [Fact]
    public void Relationship_has_exact_wording_and_options()
    {
        var question = Pair(Unit()).Questions[0];
        Assert.Equal(RELATIONSHIP + "\n\nScoutReport consumers\r\n", question.Instructions);
        Assert.Equal(new ChoiceOption[]
        {
            new("supports", "The section agrees with the item or relies on what it says."),
            new("contradicts", "The section states or does something the item says is false, impossible or different."),
            new("unrelated", "The section does not concern what the item says."),
            new("insufficient_context", "The section and the state do not contain enough to tell.")
        }, question.Options);
    }

    /// <summary>The obligation wording and all alternatives are exact and ordered.</summary>
    [Fact]
    public void Obligation_has_exact_wording_and_options()
    {
        var question = Pair(Unit()).Questions[1];
        Assert.Equal(OBLIGATION + "\n\nScoutReport consumers\r\n", question.Instructions);
        Assert.Equal(new ChoiceOption[]
        {
            new("explicit_obligation", "The section commits to change, test or verify what the item requires."),
            new("explicit_exclusion", "The section deliberately leaves what the item requires out of scope."),
            new("mentioned_only", "The section mentions the subject but commits to no action on it."),
            new("absent", "The section does not address what the item requires."),
            new("insufficient_context", "The section and the state do not contain enough to tell.")
        }, question.Options);
    }

    /// <summary>A requirement pair adds only the exact unit text and uses its content hash.</summary>
    [Fact]
    public void Requirement_pair_has_only_fixed_wording_and_unit_text() =>
        AssertPair(Unit("R1", "requirement", "1. **R1.** ScoutReport must be verified.\r\n"));

    /// <summary>A gate pair does not add a unit id to instructions or options.</summary>
    [Fact]
    public void Gate_pair_has_only_fixed_wording_and_unit_text() =>
        AssertPair(Unit("G1", "gate", "dotnet test\n"));

    /// <summary>A fallback pair does not add its outline label, position or kind.</summary>
    [Fact]
    public void Other_pair_has_only_fixed_wording_and_unit_text() =>
        AssertPair(Unit("s17", "other", "\nSome exact text\r\n"));

    /// <summary>Shared state contains only exact requirement and exclusion texts and ordered Change points.</summary>
    [Fact]
    public void Shared_state_contains_exact_texts_in_plan_order()
    {
        PlanUnit[] units =
        [
            Unit("R2", "requirement", "second\r\n"), Unit("G1", "gate", "not state"),
            Unit("X", "exclusion", " excluded \n"), Unit("R1", "requirement", "first\n"),
            Unit("X~2", "exclusion", "excluded again"), Unit("s9", "other", "not state either")
        ];
        var shared = EvidenceRubric.SharedState(units, ["point 2", "point 1"]);
        using var document = JsonDocument.Parse(EvidenceRubric.Canonical(shared));
        var root = document.RootElement;
        Assert.Equal(["requirements", "exclusions", "changePoints"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["second\r\n", "first\n"], root.GetProperty("requirements").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal([" excluded \n", "excluded again"], root.GetProperty("exclusions").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["point 2", "point 1"], root.GetProperty("changePoints").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>Request state appends only item, whose three properties exclude all local metadata.</summary>
    [Fact]
    public void Request_state_has_canonical_property_order_and_no_locator_or_ids()
    {
        var state = EvidenceRubric.State(Shared(), EvidenceRubric.Item(Item()));
        Assert.Equal(["requirements", "exclusions", "changePoints", "item"], state.Select(property => property.Key));
        Assert.Equal(["kind", "category", "text"], state["item"]!.AsObject().Select(property => property.Key));
        Assert.Equal("{\"kind\":\"scout\",\"category\":\"confirmedFacts\",\"text\":\"ScoutReport needs consumers\"}",
            EvidenceRubric.Canonical(EvidenceRubric.Item(Item())));
        Assert.Equal(EvidenceRubric.Canonical(EvidenceRubric.Item(Item())), state["item"]!.ToJsonString());
    }

    /// <summary>Identical inputs yield identical unindented UTF-8 bytes through the generated context.</summary>
    [Fact]
    public void Canonical_bytes_are_identical_for_identical_inputs()
    {
        var first = EvidenceRubric.Canonical(EvidenceRubric.SharedState([Unit("R1", "requirement", "é\n")], ["point"]));
        var second = EvidenceRubric.Canonical(EvidenceRubric.SharedState([Unit("R1", "requirement", "é\n")], ["point"]));
        Assert.Equal(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
        Assert.DoesNotContain('\n', first);
        Assert.Equal("1", EvidenceRubric.RUBRIC_VERSION);
        Assert.Equal("1", EvidenceRubric.SCHEMA_VERSION);
    }

    /// <summary>Key inputs and request inputs use the same shared state, item, instructions and alternatives.</summary>
    [Fact]
    public void Canonical_content_matches_the_objects_the_advisor_receives()
    {
        var shared = Shared();
        var pair = Pair(Unit());
        var packed = EvidenceRubric.Pack(new ByteAdvisor(), shared, Item(), [pair]);
        var state = Assert.Single(packed.Requests).Request.State.DeepClone().AsObject();
        var item = state["item"]!.ToJsonString();
        state.Remove("item");
        Assert.Equal(EvidenceRubric.Canonical(shared), state.ToJsonString());
        Assert.Equal(EvidenceRubric.Canonical(EvidenceRubric.Item(Item())), item);
        using var content = JsonDocument.Parse(EvidenceRubric.CanonicalUnit(pair));
        Assert.Equal(2, content.RootElement.GetArrayLength());
        for (var index = 0; index < 2; index++)
        {
            var question = content.RootElement[index];
            Assert.Equal(["instructions", "options"], question.EnumerateObject().Select(property => property.Name));
            Assert.Equal(pair.Questions[index].Instructions, question.GetProperty("instructions").GetString());
            Assert.Equal(pair.Questions[index].Options.Select(option => option.Value),
                question.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("value").GetString()));
            Assert.Equal(pair.Questions[index].Options.Select(option => option.Description),
                question.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("description").GetString()));
        }
    }

    /// <summary>Inserting non-state, non-candidate text changes neither request bodies nor unit key content.</summary>
    [Fact]
    public void Inserting_other_unit_before_candidate_leaves_all_request_bytes_identical()
    {
        const string plan = "## Requirements\n1. **R1.** Keep the contract.\n## Details\nScoutReport consumers\n";
        const string revised = "## Requirements\n1. **R1.** Keep the contract.\n## Added\nUnrelated prose.\n## Details\nScoutReport consumers\n";
        var advisor = new ByteAdvisor(maxQuestions: 2);
        var before = MapRequests(plan, advisor);
        var after = MapRequests(revised, advisor);
        Assert.Equal(before.Select(request => ByteAdvisor.Serialize(request.Request)),
            after.Select(request => ByteAdvisor.Serialize(request.Request)));
        var originalPair = before.SelectMany(request => request.Judgments).Single(judgment => judgment.Kind == "pair");
        var revisedPair = after.SelectMany(request => request.Judgments).Single(judgment => judgment.Kind == "pair");
        Assert.NotEqual(originalPair.Unit!.Id, revisedPair.Unit!.Id);
        Assert.Equal(EvidenceRubric.CanonicalUnit(originalPair), EvidenceRubric.CanonicalUnit(revisedPair));
    }

    /// <summary>An implication has only candidate pairs and retains a null category in state.</summary>
    [Fact]
    public void Implication_has_no_relevance_question()
    {
        var item = Item() with { Kind = "implication", Category = null };
        var judgments = EvidenceRubric.Judgments(item, [Unit()]);
        Assert.Equal("pair", Assert.Single(judgments).Kind);
        Assert.All(judgments.SelectMany(judgment => judgment.Questions), question => Assert.NotEqual("relevance", question.Id));
        Assert.Equal("{\"kind\":\"implication\",\"category\":null,\"text\":\"ScoutReport needs consumers\"}",
            EvidenceRubric.Canonical(EvidenceRubric.Item(item)));
    }

    /// <summary>Relevance precedes every pair, whose plan order remains unchanged across request splits.</summary>
    [Fact]
    public void Relevance_is_packed_before_pairs_in_plan_order()
    {
        var first = Unit("G1", "gate", "first");
        var second = Unit("R1", "requirement", "second");
        var judgments = EvidenceRubric.Judgments(Item(), [first, second]);
        var packed = EvidenceRubric.Pack(new ByteAdvisor(maxQuestions: 2), Shared(), Item(), judgments);
        Assert.Equal(["relevance", "pair", "pair"], packed.Requests.SelectMany(request => request.Judgments).Select(judgment => judgment.Kind));
        Assert.Equal([first, second], packed.Requests.SelectMany(request => request.Judgments).Where(judgment => judgment.Unit is not null).Select(judgment => judgment.Unit));
    }

    /// <summary>A pair stays together even when relevance has filled part of the question budget.</summary>
    [Fact]
    public void Pairs_two_questions_always_share_a_request()
    {
        var packed = Pack(new ByteAdvisor(maxQuestions: 2));
        Assert.Equal([1, 2], packed.Requests.Select(request => request.Request.Questions.Count));
        var pairRequest = packed.Requests[1];
        Assert.Equal(["relationship:", "obligation:"], pairRequest.Request.Questions.Select(question => question.Id[..(question.Id.IndexOf(':') + 1)]));
        Assert.Single(pairRequest.Judgments);
    }

    /// <summary>An exact total-size limit includes the entire request.</summary>
    [Fact]
    public void Total_equal_to_limit_is_packed_whole()
    {
        var full = Assert.Single(Pack(new ByteAdvisor()).Requests).Request;
        var size = new ByteAdvisor().Measure(full);
        var packed = Pack(new ByteAdvisor(maxTotal: size.Total));
        Assert.Single(packed.Requests);
        Assert.Empty(packed.Unchecked);
    }

    /// <summary>One extra serialized byte starts a new request without modifying either judgment.</summary>
    [Fact]
    public void One_more_total_byte_starts_a_new_request()
    {
        var full = Assert.Single(Pack(new ByteAdvisor()).Requests).Request;
        var limit = new ByteAdvisor().Measure(full).Total;
        var judgments = EvidenceRubric.Judgments(Item(), [Unit()]);
        var pair = judgments[1] with
        {
            Questions = [judgments[1].Questions[0] with { Instructions = judgments[1].Questions[0].Instructions + "a" }, judgments[1].Questions[1]]
        };
        var oneByte = new AdviceRequest(full.State, [full.Questions[0], pair.Questions[0], pair.Questions[1]]);
        Assert.Equal(limit + 1, new ByteAdvisor().Measure(oneByte).Total);
        var packed = EvidenceRubric.Pack(new ByteAdvisor(maxTotal: limit), Shared(), Item(), [judgments[0], pair]);
        Assert.Equal(2, packed.Requests.Count);
        Assert.Empty(packed.Unchecked);
    }

    /// <summary>The independently measured state-plus-largest-question boundary is inclusive.</summary>
    [Fact]
    public void Pair_equal_to_state_plus_question_limit_is_sent()
    {
        var pair = Pair(Unit());
        var size = new ByteAdvisor().Measure(Request(pair));
        var packed = EvidenceRubric.Pack(new ByteAdvisor(maxState: size.StatePlusLargestQuestion), Shared(), Item(), [pair]);
        Assert.Single(packed.Requests);
        Assert.Empty(packed.Unchecked);
    }

    /// <summary>A single byte beyond that boundary withholds the whole pair even if its total fits.</summary>
    [Fact]
    public void Pair_one_byte_above_state_plus_question_limit_is_unchecked()
    {
        var advisor = new ByteAdvisor();
        var pair = Pair(Unit());
        var limit = advisor.Measure(Request(pair)).StatePlusLargestQuestion;
        var largest = pair.Questions.Select((question, index) => (Size: ByteAdvisor.QuestionBytes(question), Index: index)).MaxBy(part => part.Size).Index;
        var questions = pair.Questions.ToArray();
        questions[largest] = questions[largest] with { Instructions = questions[largest].Instructions + "a" };
        pair = pair with { Questions = questions };
        Assert.Equal(limit + 1, advisor.Measure(Request(pair)).StatePlusLargestQuestion);
        Assert.True(advisor.Measure(Request(pair)).Total < advisor.Limits.MaxTotal);
        AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(maxState: limit), Shared(), Item(), [pair]), "too_large", 1);
    }

    /// <summary>Question-count packing splits at the first pair that would exceed the count.</summary>
    [Fact]
    public void Question_count_limit_splits_requests_greedily()
    {
        var judgments = EvidenceRubric.Judgments(Item(), [Unit(), Unit("s2", text: "next")]);
        var packed = EvidenceRubric.Pack(new ByteAdvisor(maxQuestions: 3), Shared(), Item(), judgments);
        Assert.Equal([3, 2], packed.Requests.Select(request => request.Request.Questions.Count));
        Assert.Equal([2, 1], packed.Requests.Select(request => request.Judgments.Count));
    }

    /// <summary>Each packed request carries its own measured size, whether a split or the size limit closed it.</summary>
    [Fact]
    public void Every_packed_request_carries_its_measure()
    {
        var judgments = EvidenceRubric.Judgments(Item(), [Unit(), Unit("s2", text: "next"), Unit("s3", text: "third")]);
        var advisor = new ByteAdvisor(maxQuestions: 3);
        var firstTwo = new AdviceRequest(EvidenceRubric.State(Shared(), EvidenceRubric.Item(Item())),
                                         [.. judgments[0].Questions, .. judgments[1].Questions]);
        var bySize = EvidenceRubric.Pack(new ByteAdvisor(maxTotal: advisor.Measure(firstTwo).Total), Shared(), Item(), judgments);
        foreach (var packed in new[] { EvidenceRubric.Pack(advisor, Shared(), Item(), judgments), bySize })
        {
            Assert.True(packed.Requests.Count > 1);
            Assert.All(packed.Requests, request => Assert.Equal(advisor.Measure(request.Request).Total, request.Size));
        }
    }

    /// <summary>A relevance judgment too large alone is never sent.</summary>
    [Fact]
    public void Relevance_too_large_by_total_is_unchecked()
    {
        var relevance = EvidenceRubric.Judgments(Item(), [])[0];
        var size = new ByteAdvisor().Measure(Request(relevance));
        AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(maxTotal: size.Total - 1), Shared(), Item(), [relevance]), "too_large", 1);
    }

    /// <summary>A pair too large alone remains indivisible and is never sent.</summary>
    [Fact]
    public void Pair_too_large_by_total_is_unchecked()
    {
        var pair = Pair(Unit());
        var size = new ByteAdvisor().Measure(Request(pair));
        AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(maxTotal: size.Total - 1), Shared(), Item(), [pair]), "too_large", 1);
    }

    /// <summary>A relevance question must obey the separate state-plus-question bound.</summary>
    [Fact]
    public void Relevance_too_large_by_state_plus_question_is_unchecked()
    {
        var relevance = EvidenceRubric.Judgments(Item(), [])[0];
        var size = new ByteAdvisor().Measure(Request(relevance));
        AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(maxState: size.StatePlusLargestQuestion - 1), Shared(), Item(), [relevance]), "too_large", 1);
    }

    /// <summary>A service that cannot fit two questions cannot receive half a pair.</summary>
    [Fact]
    public void Pair_above_question_count_alone_is_unchecked()
    {
        var packed = Pack(new ByteAdvisor(maxQuestions: 1));
        Assert.Single(packed.Requests);
        Assert.Single(packed.Requests[0].Request.Questions);
        Assert.Equal("too_large", Assert.Single(packed.Unchecked).Reason);
    }

    /// <summary>All alternatives are retained; a smaller service option limit withholds the judgment.</summary>
    [Fact]
    public void Pair_above_option_limit_is_unchecked()
    {
        var packed = Pack(new ByteAdvisor(maxOptions: 4));
        Assert.Single(packed.Requests);
        Assert.Equal("relevance", Assert.Single(packed.Requests[0].Judgments).Kind);
        Assert.Equal("too_large", Assert.Single(packed.Unchecked).Reason);
    }

    /// <summary>A requirement assignment makes every judgment sensitive before any serialization for sending.</summary>
    [Fact]
    public void Secret_in_requirement_withholds_every_judgment()
    {
        var shared = EvidenceRubric.SharedState([Unit("R1", "requirement", SECRET)], ["ScoutReport"]);
        var judgments = EvidenceRubric.Judgments(Item(), [Unit(), Unit("s2", text: "other")]);
        AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(), shared, Item(), judgments), "sensitive", 3);
    }

    /// <summary>An item's assignment withholds relevance and every pair for that item.</summary>
    [Fact]
    public void Secret_in_item_withholds_every_judgment_of_that_item()
    {
        var item = Item() with { Text = SECRET };
        AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(), Shared(), item, EvidenceRubric.Judgments(item, [Unit()])), "sensitive", 2);
        Assert.Empty(Pack(new ByteAdvisor()).Unchecked);
    }

    /// <summary>A secret in a non-state unit withholds only that unit's pair.</summary>
    [Fact]
    public void Secret_in_unit_withholds_only_its_pair()
    {
        var judgments = EvidenceRubric.Judgments(Item(), [Unit(text: SECRET), Unit("s2", text: "clean")]);
        var packed = EvidenceRubric.Pack(new ByteAdvisor(), Shared(), Item(), judgments);
        Assert.Equal(judgments[1], Assert.Single(packed.Unchecked).Judgment);
        Assert.Equal("sensitive", packed.Unchecked[0].Reason);
        Assert.Equal([judgments[0], judgments[2]], packed.Requests.SelectMany(request => request.Judgments));
        Assert.All(packed.Requests, request => Assert.DoesNotContain("AbCdEf0123456789", ByteAdvisor.Serialize(request.Request)));
    }

    /// <summary>Every shared string, including exclusions and Change points, is checked as plain text.</summary>
    [Fact]
    public void Secret_in_exclusion_or_change_point_withholds_all_judgments()
    {
        var judgments = EvidenceRubric.Judgments(Item(), [Unit()]);
        foreach (var shared in new[] { new EvidenceSharedState([], [SECRET], []), new EvidenceSharedState([], [], [SECRET]) })
            AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(), shared, Item(), judgments), "sensitive", 2);
    }

    /// <summary>Question ids, option values and descriptions each pass the same string guard.</summary>
    [Fact]
    public void Secret_in_any_question_string_withholds_only_that_judgment()
    {
        var relevance = EvidenceRubric.Judgments(Item(), [])[0];
        var question = relevance.Questions[0];
        ChoiceQuestion[] mutations =
        [
            question with { Id = SECRET }, question with { Instructions = SECRET },
            question with { Options = [new(SECRET, "safe")] }, question with { Options = [new("safe", SECRET)] }
        ];
        foreach (var mutation in mutations)
            AssertUnchecked(EvidenceRubric.Pack(new ByteAdvisor(), Shared(), Item(),
                [relevance with { Questions = [mutation] }]), "sensitive", 1);
    }

    /// <summary>The fake's independent serialization counts UTF-8 bytes rather than .NET characters.</summary>
    [Fact]
    public void Adapter_measure_uses_independent_total_and_largest_question_byte_counts()
    {
        var pair = Pair(Unit(text: "é漢字"));
        var request = Request(pair);
        var measured = new ByteAdvisor().Measure(request);
        Assert.Equal(Encoding.UTF8.GetByteCount(ByteAdvisor.Serialize(request)), measured.Total);
        Assert.Equal(Encoding.UTF8.GetByteCount(request.State.ToJsonString()) + pair.Questions.Max(ByteAdvisor.QuestionBytes),
            measured.StatePlusLargestQuestion);
        Assert.True(measured.Total > measured.StatePlusLargestQuestion);
    }

    /// <summary>A missing id invalidates every question in the response.</summary>
    [Fact]
    public void Missing_id_invalidates_whole_request()
    {
        var request = Request(Pair(Unit()));
        var answers = ValidResult(request).Answers.ToDictionary();
        answers.Remove(request.Questions[0].Id);
        AssertWholeInvalid(request, new(answers, "served"));
    }

    /// <summary>An extra id invalidates every question in the response.</summary>
    [Fact]
    public void Extra_id_invalidates_whole_request()
    {
        var request = Request(Pair(Unit()));
        var answers = ValidResult(request).Answers.ToDictionary();
        answers.Add("extra", new Refused());
        AssertWholeInvalid(request, new(answers, "served"));
    }

    /// <summary>Whitespace is not a served model, so no question from it is accepted.</summary>
    [Fact]
    public void Blank_served_model_invalidates_whole_request()
    {
        var request = Request(Pair(Unit()));
        AssertWholeInvalid(request, ValidResult(request) with { ServedModel = " \t" });
    }

    /// <summary>A choice outside the supplied alternatives fails fresh and stored validation.</summary>
    [Fact]
    public void Choice_not_an_option_is_invalid() => AssertInvalid(Answer() with { Choice = "unknown" });

    /// <summary>A distribution may not name an extra option.</summary>
    [Fact]
    public void Probability_for_unknown_option_is_invalid() =>
        AssertInvalid(new("implies_action", new Dictionary<string, double>(Probabilities()) { ["unknown"] = 0 }));

    /// <summary>A distribution must account for every option, even a zero-probability one.</summary>
    [Fact]
    public void Missing_option_probability_is_invalid()
    {
        var probabilities = Probabilities();
        probabilities.Remove("context_only");
        AssertInvalid(new("implies_action", probabilities));
    }

    /// <summary>Negative probabilities are invalid even when their sum is one.</summary>
    [Fact]
    public void Negative_probability_is_invalid() => AssertInvalid(WithProbability(-0.1));

    /// <summary>NaN is not a probability.</summary>
    [Fact]
    public void NaN_probability_is_invalid() => AssertInvalid(WithProbability(double.NaN));

    /// <summary>A probability cannot exceed one.</summary>
    [Fact]
    public void Probability_above_one_is_invalid() => AssertInvalid(WithProbability(1.1));

    /// <summary>Finite, individually valid probabilities must still sum to one within tolerance.</summary>
    [Fact]
    public void Sum_off_by_more_than_tolerance_is_invalid() => AssertInvalid(WithProbability(0.9));

    /// <summary>Reported confidence obeys the same unit interval.</summary>
    [Fact]
    public void Confidence_above_one_is_invalid() => AssertInvalid(Answer() with { Confidence = 1.1 });

    /// <summary>Nonfinite probabilities and confidence, and negative confidence, are rejected.</summary>
    [Fact]
    public void Other_nonfinite_and_negative_values_are_invalid()
    {
        AssertInvalid(WithProbability(double.PositiveInfinity));
        AssertInvalid(WithProbability(double.NegativeInfinity));
        foreach (var confidence in new[] { -0.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            AssertInvalid(Answer() with { Confidence = confidence });
    }

    /// <summary>A service refusal is an unchecked reason rather than an invalid request.</summary>
    [Fact]
    public void Refusal_is_preserved_as_unchecked()
    {
        var request = Request(EvidenceRubric.Judgments(Item(), [])[0]);
        var validation = EvidenceRubric.Validate(request, new(new Dictionary<string, AdviceOutcome> { ["relevance"] = new Refused() }, "served"));
        Assert.False(validation.InvalidRequest);
        Assert.Equal(new EvidenceQuestionOutcome(null, "refused"), Assert.Single(validation.Questions).Value);
    }

    /// <summary>A valid answer preserves its exact choice, probabilities and reported confidence.</summary>
    [Fact]
    public void Fully_valid_answer_is_unchanged()
    {
        var request = Request(EvidenceRubric.Judgments(Item(), [])[0]);
        var answer = Answer() with { Confidence = 0.8 };
        var validation = EvidenceRubric.Validate(request, new(new Dictionary<string, AdviceOutcome> { ["relevance"] = answer }, "served"));
        Assert.False(validation.InvalidRequest);
        var outcome = Assert.Single(validation.Questions).Value;
        Assert.Same(answer, outcome.Answer);
        Assert.Null(outcome.Reason);
        Assert.True(EvidenceRubric.ValidateStored(request.Questions[0], answer));
    }

    /// <summary>The tolerance allows small rounding errors, and optional confidence may be absent.</summary>
    [Fact]
    public void Sum_within_tolerance_and_missing_confidence_are_valid()
    {
        var question = EvidenceRubric.Judgments(Item(), [])[0].Questions[0];
        Assert.True(EvidenceRubric.ValidateStored(question, WithProbability(0.995)));
        Assert.True(EvidenceRubric.ValidateStored(question, Answer()));
        Assert.True(EvidenceRubric.ValidateStored(question, Answer() with { Confidence = 0 }));
        Assert.True(EvidenceRubric.ValidateStored(question, Answer() with { Confidence = 1 }));
    }

    /// <summary>Both exact tolerance boundaries remain valid despite subtraction rounding in binary floating point.</summary>
    [Fact]
    public void Exact_sum_tolerance_boundaries_are_inclusive()
    {
        var question = EvidenceRubric.Judgments(Item(), [])[0].Questions[0];
        Assert.True(EvidenceRubric.ValidateStored(question, WithProbability(0.99)));
        var probabilities = Probabilities();
        probabilities["context_only"] = 0.01;
        Assert.True(EvidenceRubric.ValidateStored(question, new("implies_action", probabilities)));
        AssertInvalid(WithProbability(0.9899));
        probabilities["context_only"] = 0.0101;
        AssertInvalid(new("implies_action", probabilities));
    }

    /// <summary>A valid contradiction remains present beside an invalid or refused obligation.</summary>
    [Fact]
    public void Invalid_pair_question_does_not_erase_valid_contradiction()
    {
        var request = Request(Pair(Unit()));
        var relationship = request.Questions[0];
        var contradiction = new Answered("contradicts", relationship.Options.ToDictionary(option => option.Value,
            option => option.Value == "contradicts" ? 1.0 : 0));
        foreach (var obligation in new AdviceOutcome[] { new Refused(), new Answered("unknown", new Dictionary<string, double>()) })
        {
            var result = new AdviceResult(new Dictionary<string, AdviceOutcome>
            {
                [relationship.Id] = contradiction, [request.Questions[1].Id] = obligation
            }, "served");
            var validation = EvidenceRubric.Validate(request, result);
            Assert.False(validation.InvalidRequest);
            Assert.Same(contradiction, validation.Questions[relationship.Id].Answer);
            Assert.NotNull(validation.Questions[request.Questions[1].Id].Reason);
        }
    }

    /// <summary>Stored validation compares option names ordinally, regardless of a dictionary's comparer.</summary>
    [Fact]
    public void Option_names_are_case_sensitive_in_fresh_and_stored_answers()
    {
        var probabilities = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["IMPLIES_ACTION"] = 1, ["context_only"] = 0, ["insufficient_context"] = 0
        };
        AssertInvalid(new("implies_action", probabilities));
    }

    /// <summary>No candidates on an implication create no judgment or request.</summary>
    [Fact]
    public void Implication_without_candidates_has_no_request()
    {
        var item = Item() with { Kind = "implication", Category = null };
        var packed = EvidenceRubric.Pack(new ByteAdvisor(), Shared(), item, EvidenceRubric.Judgments(item, []));
        Assert.Empty(packed.Requests);
        Assert.Empty(packed.Unchecked);
    }

    /// <summary>Builds one independently labelled unit with the production content hash.</summary>
    /// <param name="id">The local unit id.</param>
    /// <param name="kind">The semantic unit kind.</param>
    /// <param name="text">The exact unit text.</param>
    private static PlanUnit Unit(string id = "s1", string kind = "other", string text = "ScoutReport consumers\r\n") =>
        new(id, kind, "label must not be sent", text, PlanOutline.Hash(text), 42, text.Length);

    /// <summary>Builds a Scout item with local metadata that must never enter request state.</summary>
    private static EvidenceItem Item() => new("A7.confirmedFacts.2", "scout", "confirmedFacts", "ScoutReport needs consumers",
        "src/Scout.cs:12", null, null, false, []);

    /// <summary>Builds the shared state used for focused packing tests.</summary>
    private static EvidenceSharedState Shared() => EvidenceRubric.SharedState([], ["ScoutReport"]);

    /// <summary>Builds only the candidate pair, omitting the preceding Scout relevance.</summary>
    /// <param name="unit">The pair's exact plan unit.</param>
    private static EvidenceJudgment Pair(PlanUnit unit) => EvidenceRubric.Judgments(Item(), [unit])[1];

    /// <summary>Builds a request for a single judgment using production canonical state.</summary>
    /// <param name="judgment">The indivisible judgment.</param>
    private static AdviceRequest Request(EvidenceJudgment judgment) =>
        new(EvidenceRubric.State(Shared(), EvidenceRubric.Item(Item())), judgment.Questions);

    /// <summary>Packs a Scout relevance and one pair using adapter limits.</summary>
    /// <param name="advisor">The byte-measuring fake Advisor.</param>
    private static EvidencePacking Pack(ByteAdvisor advisor) =>
        EvidenceRubric.Pack(advisor, Shared(), Item(), EvidenceRubric.Judgments(Item(), [Unit()]));

    /// <summary>Runs unit partitioning, candidate selection, rubric building and packing on a complete plan.</summary>
    /// <param name="plan">The complete plan text.</param>
    /// <param name="advisor">The adapter's independent wire measure.</param>
    private static IReadOnlyList<EvidenceRequest> MapRequests(string plan, ByteAdvisor advisor)
    {
        var units = PlanUnits.Of(plan);
        var candidates = CandidateSelection.Select(Item(), units, ["ScoutReport"]);
        return EvidenceRubric.Pack(advisor, EvidenceRubric.SharedState(units, ["ScoutReport"]), Item(),
            EvidenceRubric.Judgments(Item(), candidates)).Requests;
    }

    /// <summary>Checks that no local metadata is inserted into either pair question.</summary>
    /// <param name="unit">The independently chosen requirement, gate or fallback unit.</param>
    private static void AssertPair(PlanUnit unit)
    {
        var pair = Pair(unit);
        Assert.Equal(RELATIONSHIP + "\n\n" + unit.Text, pair.Questions[0].Instructions);
        Assert.Equal(OBLIGATION + "\n\n" + unit.Text, pair.Questions[1].Instructions);
        Assert.Equal("relationship:" + PlanOutline.Hash(unit.Text), pair.Questions[0].Id);
        Assert.Equal("obligation:" + PlanOutline.Hash(unit.Text), pair.Questions[1].Id);
        var renamed = Pair(unit with { Id = "renamed", Label = "different", Start = 700 });
        Assert.Equal(EvidenceRubric.CanonicalUnit(pair), EvidenceRubric.CanonicalUnit(renamed));
    }

    /// <summary>Checks complete withholding and one reason for each judgment.</summary>
    /// <param name="packed">The packing result.</param>
    /// <param name="reason">The expected persistent reason.</param>
    /// <param name="count">The number of withheld judgments.</param>
    private static void AssertUnchecked(EvidencePacking packed, string reason, int count)
    {
        Assert.Empty(packed.Requests);
        Assert.Equal(count, packed.Unchecked.Count);
        Assert.All(packed.Unchecked, judgment => Assert.Equal(reason, judgment.Reason));
    }

    /// <summary>Creates a valid relevance distribution with every option present.</summary>
    private static Dictionary<string, double> Probabilities() => new()
    {
        ["implies_action"] = 1, ["context_only"] = 0, ["insufficient_context"] = 0
    };

    /// <summary>Creates a complete valid relevance answer without confidence.</summary>
    private static Answered Answer() => new("implies_action", Probabilities());

    /// <summary>Changes one probability while retaining every required option name.</summary>
    /// <param name="value">The selected option's probability.</param>
    private static Answered WithProbability(double value)
    {
        var probabilities = Probabilities();
        probabilities["implies_action"] = value;
        return new("implies_action", probabilities);
    }

    /// <summary>Checks the same invalid answer through the fresh and stored paths.</summary>
    /// <param name="answer">The malformed relevance answer.</param>
    private static void AssertInvalid(Answered answer)
    {
        var request = Request(EvidenceRubric.Judgments(Item(), [])[0]);
        var validation = EvidenceRubric.Validate(request, new(new Dictionary<string, AdviceOutcome> { ["relevance"] = answer }, "served"));
        Assert.False(validation.InvalidRequest);
        Assert.Equal(new EvidenceQuestionOutcome(null, "invalid_answer"), Assert.Single(validation.Questions).Value);
        Assert.False(EvidenceRubric.ValidateStored(request.Questions[0], answer));
    }

    /// <summary>Builds a valid response to every question independently of rubric validation.</summary>
    /// <param name="request">The questions to answer.</param>
    private static AdviceResult ValidResult(AdviceRequest request) => new(request.Questions.ToDictionary(question => question.Id,
        question => (AdviceOutcome)new Answered(question.Options[0].Value, question.Options.ToDictionary(option => option.Value,
            option => option == question.Options[0] ? 1.0 : 0))), "served");

    /// <summary>Checks that a whole-request failure marks every asked question invalid.</summary>
    /// <param name="request">The original questions.</param>
    /// <param name="result">The malformed result.</param>
    private static void AssertWholeInvalid(AdviceRequest request, AdviceResult result)
    {
        var validation = EvidenceRubric.Validate(request, result);
        Assert.True(validation.InvalidRequest);
        Assert.Equal(request.Questions.Count, validation.Questions.Count);
        Assert.All(validation.Questions.Values, outcome => Assert.Equal(new EvidenceQuestionOutcome(null, "invalid_answer"), outcome));
    }

    /// <summary>A network-free Advisor whose body and individual question measures are independent.</summary>
    private sealed class ByteAdvisor : IAdvisor
    {
        /// <summary>Sets the fake service limits in actual serialized UTF-8 bytes.</summary>
        /// <param name="maxTotal">The whole body bound.</param>
        /// <param name="maxState">The state plus one question bound.</param>
        /// <param name="maxQuestions">The question-count bound.</param>
        /// <param name="maxOptions">The option-count bound.</param>
        public ByteAdvisor(int maxTotal = int.MaxValue, int maxState = int.MaxValue, int maxQuestions = 64,
                           int maxOptions = 255) =>
            Limits = new("bytes", maxTotal, maxState, maxQuestions, maxOptions, 8, TimeSpan.FromSeconds(30));

        /// <summary>The test service id.</summary>
        public string Id => "bytes";

        /// <summary>The pinned test model.</summary>
        public string RequestedModel => "test-1";

        /// <summary>The independently configurable wire limits.</summary>
        public AdvisorLimits Limits { get; }

        /// <summary>Packing never prices a request.</summary>
        public decimal InputUsdPerMillion => 0m;

        /// <summary>Counts the whole body and separately serializes state and every individual question.</summary>
        /// <param name="request">The exact request to measure.</param>
        public AdviceSize Measure(AdviceRequest request) => new(Encoding.UTF8.GetByteCount(Serialize(request)),
            Encoding.UTF8.GetByteCount(request.State.ToJsonString()) + request.Questions.Select(QuestionBytes).DefaultIfEmpty(0).Max());

        /// <summary>Answers locally with valid first-option choices; never contacts a service.</summary>
        /// <param name="request">The exact request the mapper would send.</param>
        /// <param name="ct">Caller cancellation.</param>
        public Task<AdviceResult> AskAsync(AdviceRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ValidResult(request));
        }

        /// <summary>Serializes the fake service's own body shape without the rubric's generated context.</summary>
        /// <param name="request">The exact state and ordered questions.</param>
        public static string Serialize(AdviceRequest request)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("state");
                request.State.WriteTo(writer);
                writer.WritePropertyName("questions");
                writer.WriteStartArray();
                foreach (var question in request.Questions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", question.Id);
                    writer.WriteString("instructions", question.Instructions);
                    writer.WritePropertyName("options");
                    writer.WriteStartArray();
                    foreach (var option in question.Options)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("value", option.Value);
                        writer.WriteString("description", option.Description);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>Measures an individual question through a separately built JSON object.</summary>
        /// <param name="question">The question to serialize independently.</param>
        public static int QuestionBytes(ChoiceQuestion question)
        {
            var options = new JsonArray();
            foreach (var option in question.Options)
                options.Add(new JsonObject { ["value"] = option.Value, ["description"] = option.Description });
            var json = new JsonObject { ["id"] = question.Id, ["instructions"] = question.Instructions, ["options"] = options };
            return Encoding.UTF8.GetByteCount(json.ToJsonString());
        }
    }
}
