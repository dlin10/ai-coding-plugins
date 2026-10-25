using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PlanForge.Advisors;
using PlanForge.Review;

namespace PlanForge.Evidence;

/// <summary>The content shared by every request in one Evidence map, in canonical property order.</summary>
/// <param name="Requirements">The requirement units' exact texts in plan order.</param>
/// <param name="Exclusions">The exclusion units' exact texts in plan order.</param>
/// <param name="ChangePoints">The Run's ordered Change points.</param>
internal sealed record EvidenceSharedState(IReadOnlyList<string> Requirements, IReadOnlyList<string> Exclusions,
                                           IReadOnlyList<string> ChangePoints);

/// <summary>The only evidence-item fields supplied to an Advisor.</summary>
/// <param name="Kind">Either scout or implication.</param>
/// <param name="Category">The Scout category, or null for an implication.</param>
/// <param name="Text">The item's exact text.</param>
internal sealed record EvidenceRubricItem(string Kind, string? Category, string Text);

/// <summary>A question's content for a unit key, deliberately excluding its request-local id.</summary>
/// <param name="Instructions">The fixed rubric wording and exact unit text.</param>
/// <param name="Options">The alternatives in rubric order.</param>
internal sealed record EvidenceQuestionContent(string Instructions, IReadOnlyList<ChoiceOption> Options);

/// <summary>One indivisible relevance question or relationship-and-obligation pair.</summary>
/// <param name="Kind">Either relevance or pair.</param>
/// <param name="Questions">The ordered questions that must travel together.</param>
/// <param name="Unit">The pair's local plan unit, never serialized into a request.</param>
internal sealed record EvidenceJudgment(string Kind, IReadOnlyList<ChoiceQuestion> Questions, PlanUnit? Unit);

/// <summary>A request and the judgments whose answers it accounts for.</summary>
/// <param name="Request">The exact state and ordered questions to measure and send.</param>
/// <param name="Judgments">The indivisible judgments in request order.</param>
/// <param name="Size">The whole request's measured size, in the Advisor's limit unit.</param>
internal sealed record EvidenceRequest(AdviceRequest Request, IReadOnlyList<EvidenceJudgment> Judgments, int Size);

/// <summary>A judgment withheld before any Advisor request.</summary>
/// <param name="Judgment">The judgment that could not be packed.</param>
/// <param name="Reason">The persistent sensitive or too_large reason.</param>
internal sealed record EvidenceUnchecked(EvidenceJudgment Judgment, string Reason);

/// <summary>Every supplied judgment, either packed or accounted for as unchecked.</summary>
/// <param name="Requests">The greedily packed requests in judgment order.</param>
/// <param name="Unchecked">The judgments that must never be sent.</param>
internal sealed record EvidencePacking(IReadOnlyList<EvidenceRequest> Requests, IReadOnlyList<EvidenceUnchecked> Unchecked);

/// <summary>A checked question outcome, preserving valid answers without altering them.</summary>
/// <param name="Answer">The original answer when valid, otherwise null.</param>
/// <param name="Reason">The unchecked reason, otherwise null.</param>
internal sealed record EvidenceQuestionOutcome(Answered? Answer, string? Reason);

/// <summary>Checked answers and whether R11 invalidated the whole request.</summary>
/// <param name="InvalidRequest">Whether the ids or served model invalidate every answer.</param>
/// <param name="Questions">One checked outcome for every requested question id.</param>
internal sealed record EvidenceValidation(bool InvalidRequest, IReadOnlyDictionary<string, EvidenceQuestionOutcome> Questions);

/// <summary>Owns the Evidence map's canonical inputs, fixed questions, packing and answer validity.</summary>
internal static class EvidenceRubric
{
    /// <summary>The version of the fixed question wording and answer alternatives.</summary>
    public const string RUBRIC_VERSION = "1";

    /// <summary>The version of the canonical Evidence map inputs.</summary>
    public const string SCHEMA_VERSION = "1";

    /// <summary>The relevance question, with no item or plan identifiers added.</summary>
    private const string RELEVANCE = "Decide whether the evidence item in `item.text` bears on any of the Change points in `changePoints`, so that the plan must change, test, verify or decide something because of it.";

    /// <summary>The relationship question before its blank line and exact unit text.</summary>
    private const string RELATIONSHIP = "Compare the plan section below with the evidence item in `item.text`. How does the section relate to what the item says?";

    /// <summary>The obligation question before its blank line and exact unit text.</summary>
    private const string OBLIGATION = "Does the plan section below explicitly commit the plan to act on the evidence item in `item.text`?";

    /// <summary>Builds shared state without labels, ids or any other plan metadata.</summary>
    /// <param name="units">All plan units in plan order.</param>
    /// <param name="changePoints">The ordered Change points, unchanged.</param>
    public static EvidenceSharedState SharedState(IReadOnlyList<PlanUnit> units, IReadOnlyList<string> changePoints) =>
        new(units.Where(unit => unit.Kind == "requirement").Select(unit => unit.Text).ToArray(),
            units.Where(unit => unit.Kind == "exclusion").Select(unit => unit.Text).ToArray(), changePoints.ToArray());

    /// <summary>Projects evidence onto the three fields permitted in Advisor state and item keys.</summary>
    /// <param name="item">The indexed Scout item or implication.</param>
    public static EvidenceRubricItem Item(EvidenceItem item) => new(item.Kind, item.Category, item.Text);

    /// <summary>Serializes shared state once, unindented, through the rubric's generated contract.</summary>
    /// <param name="state">The shared state to use in a content-addressed key.</param>
    public static string Canonical(EvidenceSharedState state) =>
        JsonSerializer.Serialize(state, EvidenceRubricJson.Default.EvidenceSharedState);

    /// <summary>Serializes the same item object placed into Advisor state.</summary>
    /// <param name="item">The permitted item fields.</param>
    public static string Canonical(EvidenceRubricItem item) =>
        JsonSerializer.Serialize(item, EvidenceRubricJson.Default.EvidenceRubricItem);

    /// <summary>Serializes a pair's instructions and options without request-local question or unit ids.</summary>
    /// <param name="judgment">The pair whose content contributes to a unit key.</param>
    public static string CanonicalUnit(EvidenceJudgment judgment) =>
        JsonSerializer.Serialize(judgment.Questions.Select(question =>
            new EvidenceQuestionContent(question.Instructions, question.Options)).ToArray(),
            EvidenceRubricJson.Default.EvidenceQuestionContentArray);

    /// <summary>Builds the request's object state from the same canonical shared state and item contracts.</summary>
    /// <param name="shared">The shared requirements, exclusions and Change points.</param>
    /// <param name="item">The permitted item fields.</param>
    public static JsonObject State(EvidenceSharedState shared, EvidenceRubricItem item)
    {
        var state = JsonSerializer.SerializeToNode(shared, EvidenceRubricJson.Default.EvidenceSharedState)!.AsObject();
        state.Add("item", JsonSerializer.SerializeToNode(item, EvidenceRubricJson.Default.EvidenceRubricItem));
        return state;
    }

    /// <summary>Builds relevance first for Scouts, then every candidate pair in the supplied plan order.</summary>
    /// <param name="item">The Scout item or implication to ask about.</param>
    /// <param name="candidates">Every selected candidate unit in plan order.</param>
    public static IReadOnlyList<EvidenceJudgment> Judgments(EvidenceItem item, IReadOnlyList<PlanUnit> candidates)
    {
        var judgments = new List<EvidenceJudgment>();
        if (item.Kind == "scout")
            judgments.Add(new("relevance", [new("relevance", RELEVANCE,
            [
                new("implies_action", "The item states a fact about at least one Change point that the plan must act on: change, test, verify or decide something."),
                new("context_only", "The item only describes background; nothing in it requires the plan to act for these Change points."),
                new("insufficient_context", "The state does not contain enough to tell.")
            ])], null));
        foreach (var unit in candidates)
            judgments.Add(new("pair",
            [
                new($"relationship:{unit.Hash}", RELATIONSHIP + "\n\n" + unit.Text,
                [
                    new("supports", "The section agrees with the item or relies on what it says."),
                    new("contradicts", "The section states or does something the item says is false, impossible or different."),
                    new("unrelated", "The section does not concern what the item says."),
                    new("insufficient_context", "The section and the state do not contain enough to tell.")
                ]),
                new($"obligation:{unit.Hash}", OBLIGATION + "\n\n" + unit.Text,
                [
                    new("explicit_obligation", "The section commits to change, test or verify what the item requires."),
                    new("explicit_exclusion", "The section deliberately leaves what the item requires out of scope."),
                    new("mentioned_only", "The section mentions the subject but commits to no action on it."),
                    new("absent", "The section does not address what the item requires."),
                    new("insufficient_context", "The section and the state do not contain enough to tell.")
                ])
            ], unit));
        return judgments;
    }

    /// <summary>Guards plain strings, measures indivisible judgments, and greedily packs those that fit.</summary>
    /// <param name="advisor">The service owning the exact wire measurement and limits.</param>
    /// <param name="shared">The shared state for this map.</param>
    /// <param name="item">The item whose judgments are being packed.</param>
    /// <param name="judgments">Relevance, when present, followed by pairs in plan order.</param>
    public static EvidencePacking Pack(IAdvisor advisor, EvidenceSharedState shared, EvidenceItem item,
                                       IReadOnlyList<EvidenceJudgment> judgments)
    {
        var requests = new List<EvidenceRequest>();
        var uncheckedJudgments = new List<EvidenceUnchecked>();
        var rubricItem = Item(item);
        if (Sensitive(shared.Requirements.Concat(shared.Exclusions).Concat(shared.ChangePoints)
                            .Concat([rubricItem.Kind, rubricItem.Category ?? "", rubricItem.Text])))
            return new(requests, judgments.Select(judgment => new EvidenceUnchecked(judgment, "sensitive")).ToArray());

        var state = State(shared, rubricItem);
        var current = new List<EvidenceJudgment>();
        var currentSize = 0;
        foreach (var judgment in judgments)
        {
            if (Sensitive(judgment.Questions.SelectMany(question =>
                new[] { question.Id, question.Instructions }.Concat(question.Options.SelectMany(option =>
                    new[] { option.Value, option.Description })))))
            {
                uncheckedJudgments.Add(new(judgment, "sensitive"));
                continue;
            }
            var alone = new AdviceRequest(state, judgment.Questions);
            var size = advisor.Measure(alone);
            if (size.Total > advisor.Limits.MaxTotal || size.StatePlusLargestQuestion > advisor.Limits.MaxStatePlusQuestion
                || judgment.Questions.Count > advisor.Limits.MaxQuestions
                || judgment.Questions.Any(question => question.Options.Count > advisor.Limits.MaxOptions))
            {
                uncheckedJudgments.Add(new(judgment, "too_large"));
                continue;
            }
            var combined = new AdviceRequest(state, current.SelectMany(part => part.Questions).Concat(judgment.Questions).ToArray());
            var tooMany = combined.Questions.Count > advisor.Limits.MaxQuestions;
            var combinedSize = current.Count > 0 && !tooMany ? advisor.Measure(combined).Total : 0;
            if (current.Count > 0 && (tooMany || combinedSize > advisor.Limits.MaxTotal))
            {
                requests.Add(Request(state, current, currentSize));
                current.Clear();
            }
            current.Add(judgment);
            currentSize = current.Count == 1 ? size.Total : combinedSize;
        }
        if (current.Count > 0) requests.Add(Request(state, current, currentSize));
        return new(requests, uncheckedJudgments);
    }

    /// <summary>Checks whole-request identity first, then validates each answer by the stored-answer rule.</summary>
    /// <param name="request">The exact questions that were sent.</param>
    /// <param name="result">The untrusted Advisor response.</param>
    public static EvidenceValidation Validate(AdviceRequest request, AdviceResult result)
    {
        var invalid = string.IsNullOrWhiteSpace(result.ServedModel) || result.Answers is null
                      || !new HashSet<string>(request.Questions.Select(question => question.Id), StringComparer.Ordinal)
                          .SetEquals(result.Answers.Keys);
        var outcomes = new Dictionary<string, EvidenceQuestionOutcome>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
        {
            EvidenceQuestionOutcome outcome;
            if (invalid) outcome = new(null, "invalid_answer");
            else if (result.Answers![question.Id] is Refused) outcome = new(null, "refused");
            else if (result.Answers![question.Id] is Answered answer && ValidateStored(question, answer))
                outcome = new(answer, null);
            else outcome = new(null, "invalid_answer");
            outcomes.Add(question.Id, outcome);
        }
        return new(invalid, outcomes);
    }

    /// <summary>Applies the same exact-option, distribution and confidence rules to fresh and stored answers.</summary>
    /// <param name="question">The current question and its allowed alternatives.</param>
    /// <param name="answer">The untrusted answer to validate.</param>
    public static bool ValidateStored(ChoiceQuestion question, Answered answer)
    {
        var options = new HashSet<string>(question.Options.Select(option => option.Value), StringComparer.Ordinal);
        return answer.Choice is not null && options.Contains(answer.Choice) && answer.Probabilities is not null
               && options.SetEquals(answer.Probabilities.Keys)
               && answer.Probabilities.Values.All(value => double.IsFinite(value) && value is >= 0 and <= 1)
               && answer.Probabilities.Values.Sum() is >= 0.99 and <= 1.01
               && (answer.Confidence is null || (double.IsFinite(answer.Confidence.Value) && answer.Confidence is >= 0 and <= 1));
    }

    /// <summary>Copies a packed judgment list so later packing cannot alter a completed request.</summary>
    /// <param name="state">The canonical object state shared by these judgments.</param>
    /// <param name="judgments">The current packed judgments.</param>
    /// <param name="size">The packed request's measured size.</param>
    private static EvidenceRequest Request(JsonObject state, IReadOnlyList<EvidenceJudgment> judgments, int size) =>
        new(new(state, judgments.SelectMany(judgment => judgment.Questions).ToArray()), judgments.ToArray(), size);

    /// <summary>Checks each plain string independently so JSON escaping cannot hide an assignment.</summary>
    /// <param name="strings">Every string that could be sent in this scope.</param>
    private static bool Sensitive(IEnumerable<string> strings)
    {
        try
        {
            foreach (var text in strings) SensitiveInput.Guard(text, "Evidence map");
            return false;
        }
        catch (SensitiveContentException) { return true; }
    }
}

/// <summary>The single unindented, reflection-free contract for rubric state and question content.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(EvidenceSharedState))]
[JsonSerializable(typeof(EvidenceRubricItem))]
[JsonSerializable(typeof(EvidenceQuestionContent[]))]
internal sealed partial class EvidenceRubricJson : JsonSerializerContext;
