using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PlanForge.Advisors;
using PlanForge.Infrastructure;

namespace PlanForge.Evidence;

/// <summary>One reusable judgment, retaining each question's independent outcome.</summary>
/// <param name="Kind">Either relevance or pair.</param>
/// <param name="Advisor">The Advisor id.</param>
/// <param name="RequestedModel">The requested model.</param>
/// <param name="ServedModel">The model reported by the service, or null when no response established one.</param>
/// <param name="Questions">Outcomes named relevance, relationship or obligation, without request-local hashes.</param>
internal sealed record StoredEvidenceJudgment([property: JsonRequired] string Kind,
                                              [property: JsonRequired] string Advisor,
                                              [property: JsonRequired] string RequestedModel,
                                              [property: JsonRequired] string? ServedModel,
                                              [property: JsonRequired] IReadOnlyDictionary<string, EvidenceQuestionOutcome> Questions);

/// <summary>A complete map's comparison mark for an item it judged.</summary>
/// <param name="Status">The R12 item status.</param>
/// <param name="SourceChanged">Whether the source had changed since the Scout answer.</param>
internal sealed record EvidenceItemMark([property: JsonRequired] string Status,
                                       [property: JsonRequired] bool SourceChanged);

/// <summary>Who judged the marked items, and how far into the Run's Scout answers the latest complete map read.</summary>
/// <param name="Advisor">The Advisor id of every complete map behind the marks.</param>
/// <param name="RequestedModel">The model those maps requested.</param>
/// <param name="ScoutAnswers">The Scout answers the Run had consumed when the latest complete map ran.</param>
internal sealed record EvidenceMapping([property: JsonRequired] string Advisor,
                                       [property: JsonRequired] string RequestedModel,
                                       [property: JsonRequired] int ScoutAnswers);

/// <summary>The whole, atomically replaced Evidence map file.</summary>
/// <param name="Schema">The file schema, currently 1.</param>
/// <param name="Judgments">Reusable outcomes by content-addressed key.</param>
/// <param name="LastComplete">Every item a complete map of <paramref name="Mapped"/>'s Advisor has judged, with its mark.</param>
/// <param name="Latest">The latest result without documents; never used for cache decisions.</param>
/// <param name="Mapped">
/// Who made the marks; null before a map completed, and in a file written before the mapping existed, whose
/// marks are then judged again.
/// </param>
/// <param name="BudgetUsd">The Run's Advisor budget the user named, or null for the default.</param>
internal sealed record EvidenceStoreFile([property: JsonRequired] int Schema,
                                         [property: JsonRequired] Dictionary<string, StoredEvidenceJudgment> Judgments,
                                         [property: JsonRequired] Dictionary<string, EvidenceItemMark> LastComplete,
                                         [property: JsonRequired] JsonObject? Latest,
                                         EvidenceMapping? Mapped = null,
                                         decimal? BudgetUsd = null);

/// <summary>The implication comparison identity, independent of its positional H id.</summary>
/// <param name="Text">The implication's exact text.</param>
/// <param name="DerivedFrom">The ordered Scout item ids on which it rests.</param>
internal sealed record EvidenceImplicationIdentity(string Text, IReadOnlyList<string> DerivedFrom);

/// <summary>Content-addressed persistence and comparison state for one Run's Evidence map.</summary>
internal sealed class EvidenceStore
{
    /// <summary>The schema accepted and written by this version.</summary>
    private const int SCHEMA = 1;

    /// <summary>The file loaded before any judgment can be used or written.</summary>
    private readonly string path;

    /// <summary>The validated file with outcomes accumulated during this call.</summary>
    private EvidenceStoreFile file;

    /// <summary>Creates a store only after the existing file has passed validation.</summary>
    /// <param name="path">The Run's evidence-map.json path.</param>
    /// <param name="file">Validated persisted state, or an empty initial state.</param>
    private EvidenceStore(string path, EvidenceStoreFile file)
    {
        this.path = path;
        this.file = file;
    }

    /// <summary>Exposes stored judgments for served-model invalidation by the mapper.</summary>
    public IReadOnlyDictionary<string, StoredEvidenceJudgment> Judgments => file.Judgments;

    /// <summary>The marks of every item the mapping Advisor has judged in a complete map.</summary>
    public IReadOnlyDictionary<string, EvidenceItemMark> LastComplete => file.LastComplete;

    /// <summary>The last persisted result, retained only for reporting.</summary>
    public JsonObject? Latest => file.Latest;

    /// <summary>Who made the marks and the Scout answers the latest complete map read, or null when none has completed.</summary>
    public EvidenceMapping? Mapped => file.Mapped;

    /// <summary>The Run's Advisor budget the user named, or null for the default.</summary>
    public decimal? BudgetUsd => file.BudgetUsd;

    /// <summary>Loads structural state and validates all used answers against their current questions.</summary>
    /// <param name="path">The Run's evidence-map.json path.</param>
    /// <param name="required">Current distinct judgment keys and their questions, when known.</param>
    public static EvidenceStore Load(string path, IReadOnlyDictionary<string, EvidenceJudgment>? required = null)
    {
        try
        {
            var file = File.Exists(path)
                ? JsonSerializer.Deserialize(AtomicFile.Read(path), EvidenceStoreJson.Default.EvidenceStoreFile)
                  ?? throw Malformed()
                : new EvidenceStoreFile(SCHEMA, new(StringComparer.Ordinal), new(StringComparer.Ordinal), null);
            Validate(file);
            var store = new EvidenceStore(path, file);
            if (required is not null) store.Verify(required);
            return store;
        }
        catch (JsonException) { throw Malformed(); }
    }

    /// <summary>Refuses a stored answer that does not fit the question now asked under its key.</summary>
    /// <param name="required">Current distinct judgment keys and their questions.</param>
    public void Verify(IReadOnlyDictionary<string, EvidenceJudgment> required)
    {
        foreach (var (key, judgment) in required) Find(key, judgment);
    }

    /// <summary>
    /// The items a complete map of this Advisor and model has judged, which later calls leave alone; none when the marks
    /// were made by another or by a file that did not record who made them.
    /// </summary>
    /// <param name="advisor">The Advisor of the current call.</param>
    public IReadOnlyDictionary<string, EvidenceItemMark> JudgedBy(IAdvisor advisor) =>
        file.Mapped is { } mapped && mapped.Advisor == advisor.Id && mapped.RequestedModel == advisor.RequestedModel
            ? file.LastComplete : new Dictionary<string, EvidenceItemMark>();

    /// <summary>Finds a reusable judgment, refusing incompatible answers before they can be used.</summary>
    /// <param name="key">The current judgment's content-addressed key.</param>
    /// <param name="judgment">The current questions whose options must match the stored answers.</param>
    public StoredEvidenceJudgment? Find(string key, EvidenceJudgment judgment)
    {
        if (!file.Judgments.TryGetValue(key, out var stored)) return null;
        if (stored.Kind != judgment.Kind) throw Malformed();
        foreach (var question in judgment.Questions)
        {
            var name = QuestionName(question.Id);
            if (!stored.Questions.TryGetValue(name, out var outcome)
                || (outcome.Answer is { } answer && !EvidenceRubric.ValidateStored(question, answer)))
                throw Malformed();
        }
        return stored;
    }

    /// <summary>Stores only fully reusable outcomes; a transient question keeps the whole judgment transient.</summary>
    /// <param name="key">The distinct judgment key.</param>
    /// <param name="judgment">The checked outcomes and model identity.</param>
    public bool Put(string key, StoredEvidenceJudgment judgment)
    {
        if (judgment.Questions.Values.Any(outcome => outcome.Reason is { } reason && !IsPersistent(reason)))
            return false;
        ValidateJudgment(key, judgment);
        file.Judgments[key] = judgment;
        return true;
    }

    /// <summary>Drops a judgment whose served model no longer agrees with this call.</summary>
    /// <param name="key">The incompatible stored key.</param>
    public void Remove(string key) => file.Judgments.Remove(key);

    /// <summary>Classifies a flag against the last complete map, even during an incomplete call.</summary>
    /// <param name="item">The Scout item or implication being compared.</param>
    /// <param name="mark">Its current status and source-changed mark.</param>
    public string Change(EvidenceItem item, EvidenceItemMark mark) =>
        file.LastComplete.TryGetValue(ComparisonKey(item), out var previous) && previous == mark ? "carried" : "new";

    /// <summary>
    /// Writes only current keys, and for a complete map adds the call's marks to those of the same Advisor and model,
    /// or replaces the marks of any other.
    /// </summary>
    /// <param name="required">All distinct judgment keys required by the current call.</param>
    /// <param name="latest">The current result without documents, including transient reasons.</param>
    /// <param name="currentItems">The call's item comparison keys and their marks.</param>
    /// <param name="complete">Whether every judgment has a reusable outcome.</param>
    /// <param name="mapping">Who mapped and how many Scout answers the call read, recorded only when the map is complete.</param>
    /// <param name="budgetUsd">A budget the user named in this call, or null to keep the stored one.</param>
    public void Write(IEnumerable<string> required, JsonObject latest,
                      IReadOnlyDictionary<string, EvidenceItemMark> currentItems, bool complete,
                      EvidenceMapping? mapping = null, decimal? budgetUsd = null)
    {
        var keys = new HashSet<string>(required, StringComparer.Ordinal);
        var marks = file.LastComplete;
        if (complete)
        {
            var same = file.Mapped is { } mapped && mapping is not null
                       && mapped.Advisor == mapping.Advisor && mapped.RequestedModel == mapping.RequestedModel;
            marks = same ? new(file.LastComplete, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            foreach (var (key, mark) in currentItems) marks[key] = mark;
        }
        var next = new EvidenceStoreFile(SCHEMA,
            file.Judgments.Where(entry => keys.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            marks, latest.DeepClone().AsObject(), complete ? mapping : file.Mapped, budgetUsd ?? file.BudgetUsd);
        Validate(next);
        AtomicFile.Write(path, JsonSerializer.Serialize(next, EvidenceStoreJson.Default.EvidenceStoreFile));
        file = next;
    }

    /// <summary>Builds the exact canonical text whose SHA-256 names one judgment.</summary>
    /// <param name="advisor">The Advisor id.</param>
    /// <param name="requestedModel">The requested model.</param>
    /// <param name="shared">The canonical shared state.</param>
    /// <param name="item">The item and its current local locator check.</param>
    /// <param name="judgment">Relevance or the indivisible pair.</param>
    /// <param name="rubricVersion">The fixed-question version.</param>
    /// <param name="selectionVersion">The candidate-selection version.</param>
    /// <param name="schemaVersion">The canonical-input schema version.</param>
    public static string KeyText(string advisor, string requestedModel, EvidenceSharedState shared,
                                 EvidenceItem item, EvidenceJudgment judgment,
                                 string rubricVersion = EvidenceRubric.RUBRIC_VERSION,
                                 string selectionVersion = CandidateSelection.SELECTION_VERSION,
                                 string schemaVersion = EvidenceRubric.SCHEMA_VERSION) =>
        string.Join('\n', "evidence-key/1", "advisor=" + advisor, "requestedModel=" + requestedModel,
            "rubric=" + rubricVersion, "selection=" + selectionVersion, "schema=" + schemaVersion,
            "state=" + Hash(EvidenceRubric.Canonical(shared)),
            "item=" + Hash(EvidenceRubric.Canonical(EvidenceRubric.Item(item)) + "\n" + Locator(item)),
            "unit=" + (judgment.Kind == "relevance" ? "-" : Hash(EvidenceRubric.CanonicalUnit(judgment))),
            "kind=" + judgment.Kind);

    /// <summary>Hashes canonical text as lowercase, full-length SHA-256.</summary>
    /// <param name="canonical">The exact canonical text, without a trailing line break.</param>
    public static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>Uses stable Scout identity or implication content and derivation, never the implication's position.</summary>
    /// <param name="item">The item whose last-complete mark is being compared.</param>
    public static string ComparisonKey(EvidenceItem item) => item.Kind == "scout" ? item.Id
        : Hash(JsonSerializer.Serialize(new EvidenceImplicationIdentity(item.Text, item.DerivedFrom),
            EvidenceStoreJson.Canonical.EvidenceImplicationIdentity));

    /// <summary>Identifies reasons that remain reusable for an unchanged judgment key.</summary>
    /// <param name="reason">The unchecked reason.</param>
    public static bool IsPersistent(string reason) =>
        reason is "too_large" or "rejected" or "invalid_answer" or "refused" or "sensitive";

    /// <summary>Converts a request-local question id to its persisted role.</summary>
    /// <param name="id">The relevance id or the role followed by a unit hash.</param>
    public static string QuestionName(string id) => id.Split(':', 2)[0];

    /// <summary>Includes only the local check, never the source path or surrounding repository text.</summary>
    /// <param name="item">The item with its current locator check.</param>
    internal static string Locator(EvidenceItem item) => item.Kind == "implication" ? "none"
        : item.CurrentCheck is not { } check ? "external"
        : $"{(check.Resolved ? "true" : "false")}|{check.Window}|{check.Reason}";

    /// <summary>Checks every persisted field that can decide reuse or flag comparison.</summary>
    /// <param name="file">The complete untrusted file.</param>
    private static void Validate(EvidenceStoreFile file)
    {
        if (file.Schema != SCHEMA || file.Judgments is null || file.LastComplete is null) throw Malformed();
        foreach (var (key, judgment) in file.Judgments) ValidateJudgment(key, judgment);
        foreach (var mark in file.LastComplete.Values)
            if (mark is null || mark.Status is not ("contradiction" or "unchecked" or "context_only" or "covered"
                or "excluded" or "uncertain" or "no_candidates" or "no_coverage_established")) throw Malformed();
        if (file.Mapped is { } mapped
            && (string.IsNullOrWhiteSpace(mapped.Advisor) || string.IsNullOrWhiteSpace(mapped.RequestedModel) || mapped.ScoutAnswers < 0))
            throw Malformed();
        if (file.BudgetUsd is <= 0) throw Malformed();
    }

    /// <summary>Checks structure, answered model identity and distributions using the rubric's per-question answer rules.</summary>
    /// <param name="key">The purported SHA-256 key.</param>
    /// <param name="judgment">The untrusted stored judgment.</param>
    private static void ValidateJudgment(string key, StoredEvidenceJudgment judgment)
    {
        if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigitLower(character))
            || judgment is null || judgment.Kind is not ("relevance" or "pair")
            || string.IsNullOrWhiteSpace(judgment.Advisor) || string.IsNullOrWhiteSpace(judgment.RequestedModel)
            || (judgment.ServedModel is not null && string.IsNullOrWhiteSpace(judgment.ServedModel))
            || judgment.Questions is null) throw Malformed();
        string[] names = judgment.Kind == "relevance" ? ["relevance"] : ["relationship", "obligation"];
        if (!new HashSet<string>(names, StringComparer.Ordinal).SetEquals(judgment.Questions.Keys)) throw Malformed();
        foreach (var outcome in judgment.Questions.Values)
        {
            if (outcome is null || (outcome.Answer is null) == (outcome.Reason is null)) throw Malformed();
            if (outcome.Reason is { } reason)
            {
                if (!IsPersistent(reason)) throw Malformed();
            }
            else
            {
                var answer = outcome.Answer!;
                if (judgment.ServedModel is null || answer.Probabilities is null || !EvidenceRubric.ValidateStored(
                    new("stored", "", answer.Probabilities.Keys.Select(value => new ChoiceOption(value, "")).ToArray()), answer))
                    throw Malformed();
            }
        }
    }

    /// <summary>Returns a content-free refusal naming the damaged file.</summary>
    private static JsonException Malformed() => new("evidence-map.json is malformed.");
}

/// <summary>The reflection-free contracts for the store and canonical implication comparison.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(EvidenceStoreFile))]
[JsonSerializable(typeof(EvidenceImplicationIdentity))]
internal sealed partial class EvidenceStoreJson : JsonSerializerContext
{
    /// <summary>The unindented contract for implication comparison keys.</summary>
    internal static EvidenceStoreJson Canonical =>
        field ??= new(new JsonSerializerOptions(Default.Options) { WriteIndented = false });
}
