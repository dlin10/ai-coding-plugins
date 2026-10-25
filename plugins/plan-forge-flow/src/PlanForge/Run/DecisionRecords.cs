using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Infrastructure;
using PlanForge.Orchestration;
using PlanForge.Review;

namespace PlanForge.Run;

/// <summary>One choice a question offered, as the widget that asked it showed it.</summary>
/// <param name="Label">The option as shown to the user.</param>
/// <param name="Recommended">Whether it was offered as the recommendation; at most one option is.</param>
internal sealed record DecisionOption(
    [property: JsonPropertyOrder(0), Description("The option as shown to the user.")] string Label,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),
               Description("True on the one option offered as the recommendation.")] bool Recommended = false);

/// <summary>
/// One settled decision of a Run, as <c>forge.decision.record</c> keeps it in <c>decisions.json</c>:
/// the question as asked, the answer as given, and who gave it. A changed answer is a later record
/// that supersedes this one; nothing is ever removed. See docs/adr/0045.
/// </summary>
/// <param name="DecisionId">The record's Run-local identity, from <c>D-0001</c> on, assigned by the server.</param>
/// <param name="Phase">The Orchestrator phase the decision was made in.</param>
/// <param name="Question">The question as asked, verbatim.</param>
/// <param name="Options">The choices the question offered, the recommended one marked, or null when it offered none.</param>
/// <param name="Answer">The answer as given, verbatim.</param>
/// <param name="By"><c>user</c> or <c>orchestrator</c>, as the ledger attributes its decisions.</param>
/// <param name="BearsOn">The requirement, task, gate or finding IDs the decision governs, or null.</param>
/// <param name="Supersedes">The earlier record this one changes, or null.</param>
internal sealed record DecisionRecord(
    [property: JsonPropertyOrder(0), JsonRequired] string DecisionId,
    [property: JsonPropertyOrder(1), JsonRequired] string Phase,
    [property: JsonPropertyOrder(2), JsonRequired] string Question,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DecisionOption>? Options,
    [property: JsonPropertyOrder(4), JsonRequired] string Answer,
    [property: JsonPropertyOrder(5), JsonRequired] string By,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? BearsOn = null,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Supersedes = null);

/// <summary>What recording a decision did.</summary>
/// <param name="Record">The record that stands: the new one, or the one a replay found.</param>
/// <param name="Recorded">False when a record with the same content was already there.</param>
/// <param name="Count">How many decisions the Run holds now.</param>
internal sealed record DecisionOutcome(DecisionRecord Record, bool Recorded, int Count);

/// <summary>What <c>forge.status</c> shows of a Run's decisions.</summary>
/// <param name="Count">How many the Run has recorded, superseded ones included.</param>
/// <param name="Last">The last five, oldest first, one line each.</param>
internal sealed record DecisionsStatus(int Count, IReadOnlyList<string> Last);

/// <summary>
/// The Run's decisions: the one place the answers it rests on are kept outside the Orchestrator's
/// own conversation, which a compaction reduces to a summary of itself. Appended only, numbered by
/// the server, and read back by <c>forge.status</c>, the resume card and every review Critic.
/// </summary>
internal static class DecisionRecords
{
    /// <summary>The most characters the Critic's Decisions section lists before it leaves the oldest out.</summary>
    internal const int SECTION_LIMIT = 12_000;

    /// <summary>The most characters one line of the Critic's Decisions section holds.</summary>
    internal const int SECTION_LINE_LIMIT = 600;

    /// <summary>The most characters one line of <c>forge.status</c>'s <c>decisions.last</c> holds.</summary>
    internal const int STATUS_LINE_LIMIT = 200;

    private const int STATUS_LINES = 5;
    private const int QUESTION_LIMIT = 4_000;
    private const int ANSWER_LIMIT = 8_000;
    private const int OPTION_LIMIT = 20;
    private const int LABEL_LIMIT = 500;
    private const int BEARS_ON_LIMIT = 1_000;
    private const string USER = "user";
    private const string ORCHESTRATOR = "orchestrator";

    /// <summary>The phases a decision may be made in: every Orchestrator phase but <c>done</c>, which opens none.</summary>
    private static readonly string[] Phases = [.. OrchestratorPhases.Names.Where(name => name != OrchestratorPhases.DONE)];

    /// <summary>
    /// Checks a decision as the tool received it and returns it as it would be recorded, without its
    /// ID yet: an empty options list and a blank optional text are absent. Every text passes the
    /// secret guard, because the record reaches every later Critic.
    /// </summary>
    /// <param name="phase">The Orchestrator phase the decision was made in.</param>
    /// <param name="question">The question as asked.</param>
    /// <param name="options">The choices the question offered, or null.</param>
    /// <param name="answer">The answer as given.</param>
    /// <param name="by"><c>user</c> or <c>orchestrator</c>.</param>
    /// <param name="bearsOn">The IDs the decision governs, or null.</param>
    /// <param name="supersedes">The ID of the decision this one changes, or null.</param>
    /// <returns>The record to append, with an empty <see cref="DecisionRecord.DecisionId"/>.</returns>
    public static DecisionRecord Validate(string? phase, string? question, IReadOnlyList<DecisionOption?>? options,
                                          string? answer, string? by, string? bearsOn, string? supersedes)
    {
        if (phase is null || !Phases.Contains(phase, StringComparer.Ordinal))
            throw new ArgumentRejectedException(
                $"phase must be exactly one of {string.Join(", ", Phases)} (case-sensitive); '{phase}' is not");
        if (by is not (USER or ORCHESTRATOR))
            throw new ArgumentRejectedException($"by must be exactly user or orchestrator (case-sensitive); '{by}' is not");
        RequireText(question, "question", QUESTION_LIMIT);
        RequireText(answer, "answer", ANSWER_LIMIT);

        var offered = options is { Count: > 0 } ? options : null;
        if (offered is { Count: > OPTION_LIMIT })
            throw new ArgumentRejectedException($"options holds {offered.Count} choices; at most {OPTION_LIMIT} are recorded");
        for (var index = 0; index < (offered?.Count ?? 0); index++)
            if (offered![index] is not { Label: var label } || string.IsNullOrWhiteSpace(label) || label.Length > LABEL_LIMIT)
                throw new ArgumentRejectedException($"options[{index}].label must be non-blank and at most {Count(LABEL_LIMIT)} characters");
        if (offered is not null && offered.Select(option => option!.Label).Distinct(StringComparer.Ordinal).Count() < offered.Count)
            throw new ArgumentRejectedException("options must not offer the same label twice");
        if (offered?.Count(option => option!.Recommended) > 1)
            throw new ArgumentRejectedException("at most one option is recommended");

        var governs = string.IsNullOrWhiteSpace(bearsOn) ? null : bearsOn;
        if (governs is { Length: > BEARS_ON_LIMIT })
            throw new ArgumentRejectedException($"bearsOn must be at most {Count(BEARS_ON_LIMIT)} characters");
        var replaced = string.IsNullOrWhiteSpace(supersedes) ? null : supersedes;
        if (replaced is not null && Number(replaced) is null)
            throw new ArgumentRejectedException($"supersedes must be a decision ID such as D-0001; '{replaced}' is not");

        SensitiveInput.Guard(question!, "the decision's question");
        SensitiveInput.Guard(answer!, "the decision's answer");
        foreach (var option in offered ?? []) SensitiveInput.Guard(option!.Label, "a decision's option");
        if (governs is not null) SensitiveInput.Guard(governs, "the decision's bearsOn");

        return new DecisionRecord("", phase, question!, offered?.Select(option => option!).ToArray(), answer!, by, governs, replaced);
    }

    /// <summary>
    /// Appends <paramref name="decision"/> to the Run's <c>decisions.json</c> under the file's
    /// process-local mutation lock, numbered after the last record, then to the Flow log. A record
    /// with the same content means this is a replay: nothing is written, and the outcome names the
    /// record that stands, as a decision batch's retry gets the saved result.
    /// </summary>
    /// <param name="run">The Run receiving the decision.</param>
    /// <param name="decision">The decision as <see cref="Validate"/> returned it.</param>
    /// <returns>The record that stands, whether it was written now, and how many the Run holds.</returns>
    public static DecisionOutcome Record(RunDirectory run, DecisionRecord decision)
    {
        var digest = Digest(decision);
        DecisionOutcome? outcome = null;
        AtomicFile.Update(run.DecisionsPath, text =>
        {
            var records = Read(text);
            if (records.FirstOrDefault(record => Digest(record) == digest) is { } standing)
            {
                outcome = new DecisionOutcome(standing, false, records.Count);
                return text!;
            }

            RequireSupersedable(records, decision.Supersedes);
            var recorded = decision with { DecisionId = Id(records.Count + 1) };
            outcome = new DecisionOutcome(recorded, true, records.Count + 1);
            return JsonSerializer.Serialize([.. records, recorded], DecisionRecordsJson.Readable.ListDecisionRecord);
        });

        if (outcome!.Recorded) run.AppendFlowDecision(outcome.Record);
        return outcome;
    }

    /// <summary>Reads and checks the records, or none when the file is absent.</summary>
    /// <param name="text">The file's contents, or null when there is no file.</param>
    public static IReadOnlyList<DecisionRecord> Read(string? text)
    {
        if (text is null) return [];

        List<DecisionRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize(text, DecisionRecordsJson.Default.ListDecisionRecord);
        }
        catch (JsonException error)
        {
            throw new DecisionRecordsException($"it is not a JSON array of decision records ({error.Message})");
        }
        if (records is null) throw new DecisionRecordsException("it is not a JSON array of decision records");

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record is null || record.DecisionId != Id(index + 1))
                throw new DecisionRecordsException($"entry {index + 1} is not {Id(index + 1)}");
            if (!Phases.Contains(record.Phase, StringComparer.Ordinal) || record.By is not (USER or ORCHESTRATOR)
                || string.IsNullOrWhiteSpace(record.Question) || string.IsNullOrWhiteSpace(record.Answer))
                throw new DecisionRecordsException($"{record.DecisionId} is not a well-formed record");
            if (record.Supersedes is { } replaced
                && (Number(replaced) is not { } number || number > index
                    || records.Take(index).Any(earlier => earlier.Supersedes == replaced)))
                throw new DecisionRecordsException($"{record.DecisionId} supersedes {replaced}, which is not an earlier record still standing");
        }

        return records;
    }

    /// <summary>
    /// The Decisions section of a Critic's projection: the Run's current records, one line each,
    /// oldest first, or empty when there are none. A record a later one superseded is left out — the
    /// later line names it. Over <see cref="SECTION_LIMIT"/> the oldest are left out, and the section
    /// says which: what the interview settled is in the plan's requirements already, while what was
    /// settled between rounds may be nowhere else a Critic reads.
    /// </summary>
    /// <param name="records">The Run's records, as <see cref="Read"/> returned them.</param>
    public static string Section(IReadOnlyList<DecisionRecord> records)
    {
        var current = Current(records);
        if (current.Count == 0) return "";

        var lines = current.Select(record => Line(record, SECTION_LINE_LIMIT)).ToArray();
        var first = lines.Length;
        var size = 0;
        while (first > 0 && size + lines[first - 1].Length <= SECTION_LIMIT) size += lines[--first].Length;

        var section = new StringBuilder()
            .AppendLine("# Decisions")
            .AppendLine()
            .AppendLine("What this run settled, as the user or the orchestrator answered it, oldest first; an answer a later one superseded is left out.");
        if (first > 0)
            section.Append("Left out to keep this section within ").Append(Count(SECTION_LIMIT))
                   .Append(" characters, the oldest: ").Append(string.Join(", ", current.Take(first).Select(record => record.DecisionId)))
                   .AppendLine(".");
        section.AppendLine();
        foreach (var line in lines[first..]) section.Append("- ").AppendLine(line);
        return section.ToString();
    }

    /// <summary>What <c>forge.status</c> shows of the records: how many there are, and the last five.</summary>
    /// <param name="records">The Run's records, as <see cref="Read"/> returned them.</param>
    public static DecisionsStatus Status(IReadOnlyList<DecisionRecord> records) =>
        new(records.Count, [.. records.TakeLast(STATUS_LINES).Select(record => Line(record, STATUS_LINE_LIMIT))]);

    /// <summary>
    /// The record as one line, <c>D-0007 (user): question → answer</c>, its whitespace collapsed and
    /// cut to <paramref name="limit"/> characters with an ellipsis.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="limit">The most characters the line may hold.</param>
    internal static string Line(DecisionRecord record, int limit)
    {
        var line = $"{record.DecisionId} ({record.By}{(record.Supersedes is { } replaced ? $", supersedes {replaced}" : "")}): "
                   + $"{Flat(record.Question)} → {Flat(record.Answer)}";
        if (line.Length <= limit) return line;

        var cut = limit - 1;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut] + "…";
    }

    /// <summary>The records no later record supersedes, in ID order.</summary>
    /// <param name="records">The Run's records.</param>
    private static IReadOnlyList<DecisionRecord> Current(IReadOnlyList<DecisionRecord> records)
    {
        var superseded = records.Select(record => record.Supersedes).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return [.. records.Where(record => !superseded.Contains(record.DecisionId))];
    }

    /// <summary>Refuses a <paramref name="supersedes"/> naming no record, or one a later record already superseded.</summary>
    /// <param name="records">The Run's records.</param>
    /// <param name="supersedes">The ID the new record supersedes, or null.</param>
    private static void RequireSupersedable(IReadOnlyList<DecisionRecord> records, string? supersedes)
    {
        if (supersedes is null) return;

        if (Number(supersedes) is not { } number || number > records.Count)
            throw new ArgumentRejectedException(
                $"supersedes {supersedes} names no recorded decision; " +
                (records.Count == 0 ? "none is recorded yet" : $"the last is {Id(records.Count)}"));
        if (records.FirstOrDefault(record => record.Supersedes == supersedes) is { } later)
            throw new ArgumentRejectedException($"{supersedes} is already superseded by {later.DecisionId}: supersede that one instead");
    }

    private static void RequireText(string? value, string name, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit)
            throw new ArgumentRejectedException($"{name} must be non-blank and at most {Count(limit)} characters");
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Id(int number) => "D-" + number.ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>The number <paramref name="id"/> names, or null when it is not a decision ID as <see cref="Id"/> writes one.</summary>
    /// <param name="id">The text to read.</param>
    private static int? Number(string id) =>
        id.StartsWith("D-", StringComparison.Ordinal)
        && int.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0 && Id(number) == id
            ? number
            : null;

    /// <summary>The text on one line: every run of whitespace, line breaks included, becomes one space, and the ends are trimmed.</summary>
    /// <param name="text">The text to put on one line.</param>
    /// <returns>The text with its whitespace collapsed.</returns>
    internal static string Flat(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The fingerprint of what a record says, its ID aside: equal for a replay of the same decision.</summary>
    /// <param name="record">The record.</param>
    private static string Digest(DecisionRecord record) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record with { DecisionId = "" },
                                                                                 DecisionRecordsJson.Default.DecisionRecord)))
               .ToLowerInvariant();
}

/// <summary>
/// A <c>decisions.json</c> that does not read back as the records this server wrote. Only a hand
/// edit makes one, and the Run's decisions cannot be trusted past it, so the call stops.
/// </summary>
/// <param name="detail">What is wrong with the file.</param>
internal sealed class DecisionRecordsException(string detail)
    : Exception($"decisions.json is malformed: {detail}. Do not edit run files by hand; record the failure with forge.log.append, tell the user, and start a new run.");

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DecisionRecord>))]
[JsonSerializable(typeof(DecisionRecord))]
internal sealed partial class DecisionRecordsJson : JsonSerializerContext
{
    /// <summary>For the file a person opens: non-ASCII text as written, not <c>\uXXXX</c>.</summary>
    internal static DecisionRecordsJson Readable =>
        field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
