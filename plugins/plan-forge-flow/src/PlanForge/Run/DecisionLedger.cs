using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Infrastructure;
using PlanForge.Review;
using PlanForge.Vendors;

namespace PlanForge.Run;

internal enum LedgerPhase
{
    PlanReview,
    CodeReview
}

internal static class LedgerPhaseNames
{
    public const string PLAN_REVIEW = "plan_review";
    public const string CODE_REVIEW = "code_review";
}

[JsonConverter(typeof(StrictSnakeCaseEnumConverter<LedgerDisposition>))]
internal enum LedgerDisposition
{
    Unresolved,
    Deferred,
    Rejected
}

[JsonConverter(typeof(StrictSnakeCaseEnumConverter<LedgerDecisionMaker>))]
internal enum LedgerDecisionMaker
{
    User,
    Orchestrator
}

[JsonConverter(typeof(StrictSnakeCaseEnumConverter<LedgerClosureKind>))]
internal enum LedgerClosureKind
{
    Revision,
    HostVerified,
    Duplicate,
    AutomaticGate
}

internal sealed class StrictSnakeCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<string, TEnum> VALUES = Enum.GetValues<TEnum>()
        .ToDictionary(Name, StringComparer.Ordinal);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && VALUES.TryGetValue(reader.GetString() ?? string.Empty, out var value))
            return value;

        throw new JsonException($"unsupported {typeof(TEnum).Name} value");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException($"unsupported {typeof(TEnum).Name} value '{value}'");
        writer.WriteStringValue(Name(value));
    }

    private static string Name(TEnum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}

internal sealed record DecisionLedgerSnapshot(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] int NextFindingNumber,
    [property: JsonPropertyOrder(2)] IReadOnlyList<DecisionLedgerEntry> Entries,
    [property: JsonPropertyOrder(3)] IReadOnlyList<AppliedDecisionBatch> AppliedDecisionBatches,
    [property: JsonPropertyOrder(4)] IReadOnlyList<FixAttemptRecord>? FixAttempts = null);

internal sealed record DecisionLedgerEntry(
    [property: JsonPropertyOrder(0)] string FindingId,
    [property: JsonPropertyOrder(1)] string Origin,
    [property: JsonPropertyOrder(2)] string ActivePhase,
    [property: JsonPropertyOrder(3)] Finding Finding,
    [property: JsonPropertyOrder(4)] LedgerDisposition Disposition,
    [property: JsonPropertyOrder(5)] LedgerDecisionData? Decision = null,
    [property: JsonPropertyOrder(6)] LedgerReopeningData? Reopening = null);

internal sealed record LedgerDecisionData(
    [property: JsonPropertyOrder(0)] string DecisionBatchId,
    [property: JsonPropertyOrder(1)] LedgerDecisionMaker By,
    [property: JsonPropertyOrder(2)] string Reason);

internal sealed record LedgerReopeningData(
    [property: JsonPropertyOrder(0)] string DecisionBatchId,
    [property: JsonPropertyOrder(1)] string ActivePhase,
    [property: JsonPropertyOrder(2)] LedgerDecisionMaker By,
    [property: JsonPropertyOrder(3)] string Reason,
    [property: JsonPropertyOrder(4)] string Evidence);

internal sealed record LedgerDispositionDecision(
    [property: JsonPropertyOrder(0)] string FindingId,
    [property: JsonPropertyOrder(1)] LedgerDisposition Disposition,
    [property: JsonPropertyOrder(2)] LedgerDecisionMaker By,
    [property: JsonPropertyOrder(3)] string Reason);

internal sealed record LedgerReopeningDecision(
    [property: JsonPropertyOrder(0)] string FindingId,
    [property: JsonPropertyOrder(1)] string ActivePhase,
    [property: JsonPropertyOrder(2)] LedgerDecisionMaker By,
    [property: JsonPropertyOrder(3)] string Reason,
    [property: JsonPropertyOrder(4)] string Evidence);

internal sealed record LedgerClosureDecision(
    [property: JsonPropertyOrder(0)] string FindingId,
    [property: JsonPropertyOrder(1)] LedgerClosureKind Kind,
    [property: JsonPropertyOrder(2)] LedgerDecisionMaker By,
    [property: JsonPropertyOrder(3)] string Reason,
    [property: JsonPropertyOrder(4)] string Evidence = "",
    [property: JsonPropertyOrder(5)] string DuplicateOf = "");

internal sealed record DecisionBatchRequest(
    string DecisionBatchId,
    IReadOnlyList<LedgerDispositionDecision> Decisions,
    IReadOnlyList<LedgerReopeningDecision> Reopenings,
    IReadOnlyList<LedgerClosureDecision> Closures);

/// <summary>The decision DTO the orchestrator sends through the MCP surface.</summary>
internal sealed record OrchestratorDecision(
    [property: JsonPropertyOrder(0)] string FindingId,
    [property: JsonPropertyOrder(1)] string Action,
    [property: JsonPropertyOrder(2)] string By,
    [property: JsonPropertyOrder(3)] string Reason,
    [property: JsonPropertyOrder(4)] string? Evidence = null,
    [property: JsonPropertyOrder(5)] string? DuplicateOf = null);

internal sealed record OrchestratorDecisionBatch(
    [property: JsonPropertyOrder(0)] string DecisionBatchId,
    [property: JsonPropertyOrder(1)] IReadOnlyList<OrchestratorDecision> Decisions);

internal sealed record FixAttemptRecord(
    [property: JsonPropertyOrder(0)] string FixAttemptId,
    [property: JsonPropertyOrder(1)] IReadOnlyList<string> FixFindingIds,
    [property: JsonPropertyOrder(2)] BuildResult? LastResult,
    [property: JsonPropertyOrder(3)] bool Terminal);

internal sealed record LedgerSummary(
    IReadOnlyList<string> UnresolvedFindingIds,
    IReadOnlyList<string> DeferredFindingIds,
    IReadOnlyList<string> RejectedFindingIds,
    IReadOnlyList<string> PlanReviewActiveFindingIds,
    IReadOnlyList<string> CodeReviewActiveFindingIds);

internal sealed record DecisionBatchPayload(
    [property: JsonPropertyOrder(0)] string DecisionBatchId,
    [property: JsonPropertyOrder(1)] IReadOnlyList<LedgerDispositionDecision> Decisions,
    [property: JsonPropertyOrder(2)] IReadOnlyList<LedgerReopeningDecision> Reopenings,
    [property: JsonPropertyOrder(3)] IReadOnlyList<LedgerClosureDecision> Closures);

internal sealed record DecisionBatchResult(
    [property: JsonPropertyOrder(0)] string DecisionBatchId,
    [property: JsonPropertyOrder(1)] string Outcome,
    [property: JsonPropertyOrder(2)] IReadOnlyList<string> DecisionFindingIds,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> ReopenedFindingIds,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> ClosedFindingIds);

/// <summary>
/// An applied batch is kept only as the fingerprint of its canonical decisions: a retry under the
/// same key is recognised by the digest, and the saved result is what the retry gets back.
/// </summary>
internal sealed record AppliedDecisionBatch(
    [property: JsonPropertyOrder(0)] string DecisionBatchId,
    [property: JsonPropertyOrder(1)] string Digest,
    [property: JsonPropertyOrder(2)] DecisionBatchResult Result);

internal sealed record DecisionBatchResponse(
    string Outcome,
    DecisionBatchResult Result,
    AppliedDecisionBatch? ExistingBatch = null)
{
    internal void ThrowIfConflict()
    {
        if (Outcome != "conflict") return;

        throw new DecisionLedgerRequestException(
            $"decisionBatchId '{Result.DecisionBatchId}' conflicts with the saved decisions; "
            + $"saved result: outcome={Result.Outcome}, decisions=[{string.Join(", ", Result.DecisionFindingIds)}], "
            + $"reopenings=[{string.Join(", ", Result.ReopenedFindingIds)}], "
            + $"closures=[{string.Join(", ", Result.ClosedFindingIds)}]");
    }
}

internal sealed record OrchestratorDecisionPayload(
    [property: JsonPropertyOrder(0)] string DecisionBatchId,
    [property: JsonPropertyOrder(1)] IReadOnlyList<OrchestratorDecision> Decisions);

/// <summary>
/// The Run-local source of truth. Mutations are serialized per path in this process only; every
/// replacement goes through AtomicFile and no cross-process lock or recovery path exists.
/// </summary>
internal sealed class DecisionLedger
{
    private const int CURRENT_SCHEMA_VERSION = 2;
    private static readonly ConcurrentDictionary<string, object> GATES = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    private DecisionLedger(string path) => _path = System.IO.Path.GetFullPath(path);

    internal string Path => _path;

    internal static void Create(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        WriteSnapshot(fullPath, EmptySnapshot());
    }

    internal static DecisionLedger Open(RunDirectory run) => Open(run.DecisionLedgerPath);

    internal static DecisionLedger Open(string path)
    {
        var ledger = new DecisionLedger(path);
        ledger.ReadSnapshot();
        return ledger;
    }

    internal DecisionLedgerSnapshot Snapshot => ReadSnapshot();

    internal IReadOnlyList<DecisionLedgerEntry> Project(LedgerPhase phase)
    {
        var phaseName = PhaseName(phase);
        var snapshot = ReadSnapshot();
        var entries = phase == LedgerPhase.PlanReview
            ? snapshot.Entries.Where(entry => entry.ActivePhase == phaseName)
            : snapshot.Entries.Where(entry => entry.ActivePhase == phaseName
                                              || (entry.Origin == LedgerPhaseNames.PLAN_REVIEW
                                                  && entry.Disposition != LedgerDisposition.Unresolved));
        return entries.ToArray();
    }

    internal string RenderProjection(LedgerPhase phase)
    {
        var projection = Project(phase);
        var output = new StringBuilder()
            .AppendLine("# Decision ledger projection")
            .Append("Phase: ").AppendLine(PhaseName(phase))
            .AppendLine("Only this projection is authoritative; prior critique and Flow history are not input.")
            .AppendLine();

        if (projection.Count == 0)
            return output.AppendLine("Entries: (none)").ToString();

        output.AppendLine("Entries:");
        foreach (var entry in projection)
        {
            output.Append("- ").Append(entry.FindingId)
                  .Append(" | origin=").Append(entry.Origin)
                  .Append(" | activePhase=").Append(entry.ActivePhase)
                  .Append(" | disposition=").AppendLine(DispositionName(entry.Disposition))
                  .Append("  severity: ").AppendLine(entry.Finding.Severity)
                  .Append("  where: ").AppendLine(entry.Finding.Where)
                  .Append("  what: ").AppendLine(entry.Finding.What);

            if (entry.Decision is { } decision)
                output.Append("  decision: ").Append(DecisionMakerName(decision.By)).Append(" — ").AppendLine(decision.Reason);
            if (entry.Reopening is { } reopening)
                output.Append("  reopening: ").Append(DecisionMakerName(reopening.By)).Append(" — ").Append(reopening.Reason)
                      .Append("; evidence: ").AppendLine(reopening.Evidence);
        }

        return output.ToString();
    }

    internal LedgerSummary Summary
    {
        get
        {
            var entries = ReadSnapshot().Entries;
            return new LedgerSummary(
                entries.Where(entry => entry.Disposition == LedgerDisposition.Unresolved)
                       .Select(entry => entry.FindingId).ToArray(),
                entries.Where(entry => entry.Disposition == LedgerDisposition.Deferred)
                       .Select(entry => entry.FindingId).ToArray(),
                entries.Where(entry => entry.Disposition == LedgerDisposition.Rejected)
                       .Select(entry => entry.FindingId).ToArray(),
                entries.Where(entry => entry.ActivePhase == LedgerPhaseNames.PLAN_REVIEW)
                       .Select(entry => entry.FindingId).ToArray(),
                entries.Where(entry => entry.ActivePhase == LedgerPhaseNames.CODE_REVIEW)
                       .Select(entry => entry.FindingId).ToArray());
        }
    }

    /// <summary>Renders only the unresolved code entries named by a fix attempt.</summary>
    internal string RenderFixFindings(IReadOnlyList<string> findingIds)
    {
        if (findingIds is null) throw new DecisionLedgerRequestException("fixFindingIds must not be null");
        var ids = NormalizeFindingIds(findingIds, "fixFindingIds");
        var entries = ReadSnapshot().Entries.ToDictionary(entry => entry.FindingId, StringComparer.Ordinal);
        var selected = new List<DecisionLedgerEntry>(ids.Count);
        foreach (var id in ids)
        {
            if (!entries.TryGetValue(id, out var entry))
                throw new DecisionLedgerRequestException($"finding '{id}' does not exist");
            if (entry.ActivePhase != LedgerPhaseNames.CODE_REVIEW
                || entry.Disposition != LedgerDisposition.Unresolved)
                throw new DecisionLedgerRequestException($"finding '{id}' is not an unresolved active code-review entry");
            SensitiveInput.Guard(entry.Finding.Severity, $"finding severity for {id}");
            SensitiveInput.Guard(entry.Finding.Where, $"finding location for {id}");
            SensitiveInput.Guard(entry.Finding.What, $"finding text for {id}");
            selected.Add(entry);
        }

        var output = new StringBuilder().AppendLine("# Fix these review findings").AppendLine();
        foreach (var entry in selected)
        {
            output.Append("- ").Append(entry.FindingId).Append(" | ")
                  .Append(entry.Finding.Severity).Append(" | ")
                  .Append(entry.Finding.Where).Append(" — ")
                  .AppendLine(entry.Finding.What);
        }

        return output.ToString();
    }

    internal FixAttemptRecord? FindFixAttempt(string fixAttemptId)
    {
        RequireText(fixAttemptId, "fixAttemptId");
        return ReadSnapshot().FixAttempts?.FirstOrDefault(attempt =>
            string.Equals(attempt.FixAttemptId, fixAttemptId, StringComparison.Ordinal));
    }

    internal IReadOnlyList<string> NormalizeFixFindingIds(IReadOnlyList<string> findingIds) =>
        NormalizeFindingIds(findingIds, "fixFindingIds");

    internal void ValidateFixFindingIds(IReadOnlyList<string> findingIds)
    {
        var ids = NormalizeFindingIds(findingIds, "fixFindingIds");
        var entries = ReadSnapshot().Entries.ToDictionary(entry => entry.FindingId, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!entries.TryGetValue(id, out var entry))
                throw new DecisionLedgerRequestException($"finding '{id}' does not exist");
            if (entry.ActivePhase != LedgerPhaseNames.CODE_REVIEW
                || entry.Disposition != LedgerDisposition.Unresolved)
                throw new DecisionLedgerRequestException($"finding '{id}' is not an unresolved active code-review entry");
        }
    }

    internal void RecordFixAttempt(string fixAttemptId, IReadOnlyList<string> findingIds,
                                   BuildResult? result, bool terminal)
    {
        RequireText(fixAttemptId, "fixAttemptId");
        var ids = NormalizeFindingIds(findingIds, "fixFindingIds");
        lock (Gate())
        {
            var snapshot = ReadSnapshot();
            var attempts = snapshot.FixAttempts?.ToList() ?? [];
            var index = attempts.FindIndex(attempt =>
                string.Equals(attempt.FixAttemptId, fixAttemptId, StringComparison.Ordinal));
            if (index >= 0)
            {
                if (!attempts[index].FixFindingIds.SequenceEqual(ids, StringComparer.Ordinal))
                    throw new DecisionLedgerRequestException($"fixAttemptId '{fixAttemptId}' was used with a different fixFindingIds set");
                attempts[index] = attempts[index] with { LastResult = result, Terminal = terminal };
            }
            else
            {
                attempts.Add(new FixAttemptRecord(fixAttemptId, ids, result, terminal));
            }

            WriteSnapshot(_path, snapshot with { FixAttempts = attempts });
        }
    }

    internal DecisionBatchResponse Apply(OrchestratorDecisionBatch batch, LedgerPhase phase)
    {
        var digest = Digest(CanonicalBytes(batch));
        lock (Gate())
        {
            var existing = ReadSnapshot().AppliedDecisionBatches.FirstOrDefault(applied =>
                string.Equals(applied.DecisionBatchId, batch.DecisionBatchId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (existing.Digest == digest)
                    return new DecisionBatchResponse("no_op", existing.Result, existing);
                return new DecisionBatchResponse("conflict", existing.Result, existing);
            }
        }

        var request = NormalizeOrchestratorBatch(batch, phase, out var acceptedIds, out var declinedIds);
        if (request.Decisions.Count + request.Reopenings.Count + request.Closures.Count == 0)
        {
            var declined = new DecisionBatchResult(batch.DecisionBatchId, "declined",
                                                    acceptedIds.Concat(declinedIds).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                                                    [], []);
            var applied = new AppliedDecisionBatch(batch.DecisionBatchId, digest, declined);
            lock (Gate())
            {
                var snapshot = ReadSnapshot();
                var existing = snapshot.AppliedDecisionBatches.FirstOrDefault(saved =>
                    string.Equals(saved.DecisionBatchId, batch.DecisionBatchId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (existing.Digest == digest)
                        return new DecisionBatchResponse("no_op", existing.Result, existing);
                    return new DecisionBatchResponse("conflict", existing.Result, existing);
                }

                WriteSnapshot(_path, snapshot with
                {
                    AppliedDecisionBatches = [.. snapshot.AppliedDecisionBatches, applied]
                });
            }

            return new DecisionBatchResponse("declined", declined, applied);
        }

        var payload = new OrchestratorDecisionPayload(batch.DecisionBatchId,
            batch.Decisions.OrderBy(decision => decision.FindingId, StringComparer.Ordinal).ToArray());
        lock (Gate())
        {
            var snapshot = ReadSnapshot();
            var existing = snapshot.AppliedDecisionBatches.FirstOrDefault(applied =>
                string.Equals(applied.DecisionBatchId, payload.DecisionBatchId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (existing.Digest == digest)
                    return new DecisionBatchResponse("no_op", existing.Result, existing);
                return new DecisionBatchResponse("conflict", existing.Result, existing);
            }

            var normalized = ValidateAndNormalize(request);
            ValidateLegalTransitions(snapshot, normalized);
            var entries = snapshot.Entries.ToDictionary(entry => entry.FindingId, StringComparer.Ordinal);
            ApplyEntries(entries, normalized);

            var result = new DecisionBatchResult(
                payload.DecisionBatchId,
                "applied",
                payload.Decisions.Select(decision => decision.FindingId).ToArray(),
                payload.Decisions.Where(decision => NormalizeAction(decision.Action) == "accept")
                                 .Select(decision => decision.FindingId).ToArray(),
                payload.Decisions.Where(decision => NormalizeAction(decision.Action) is "addressedByRevision" or "hostVerified" or "duplicateOf")
                                 .Select(decision => decision.FindingId).ToArray());
            var applied = new AppliedDecisionBatch(payload.DecisionBatchId, digest, result);
            var updated = snapshot with
            {
                Entries = entries.Values.OrderBy(entry => FindingNumber(entry.FindingId)).ToArray(),
                AppliedDecisionBatches = [.. snapshot.AppliedDecisionBatches, applied]
            };
            WriteSnapshot(_path, updated);
            return new DecisionBatchResponse("applied", result, applied);
        }
    }

    internal void ValidateOrchestratorBatch(OrchestratorDecisionBatch batch, LedgerPhase phase)
    {
        var digest = Digest(CanonicalBytes(batch));
        lock (Gate())
        {
            var existing = ReadSnapshot().AppliedDecisionBatches.FirstOrDefault(applied =>
                string.Equals(applied.DecisionBatchId, batch.DecisionBatchId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (existing.Digest != digest)
                    new DecisionBatchResponse("conflict", existing.Result, existing).ThrowIfConflict();
                return;
            }
        }

        var request = NormalizeOrchestratorBatch(batch, phase, out _, out _);
        if (request.Decisions.Count + request.Reopenings.Count + request.Closures.Count == 0) return;

        lock (Gate())
        {
            var snapshot = ReadSnapshot();
            var existing = snapshot.AppliedDecisionBatches.FirstOrDefault(applied =>
                string.Equals(applied.DecisionBatchId, request.DecisionBatchId, StringComparison.Ordinal));
            var normalized = ValidateAndNormalize(request);
            if (existing is null)
            {
                ValidateLegalTransitions(snapshot, normalized);
            }
            else
            {
                if (existing.Digest != digest)
                    new DecisionBatchResponse("conflict", existing.Result, existing).ThrowIfConflict();
            }
        }
    }

    internal Critique IngestCritique(VendorCritique critique, LedgerPhase phase)
    {
        if (critique is null) throw new DecisionLedgerCritiqueException("critique must not be null");
        ValidateCritiqueText(critique.Verdict, "verdict");
        if (critique.Verdict is not "approve" and not "revise")
            throw new DecisionLedgerCritiqueException($"unsupported verdict '{critique.Verdict}'");
        if (critique.Summary is null)
            throw new DecisionLedgerCritiqueException("summary must not be null");
        if (critique.Findings is null)
            throw new DecisionLedgerCritiqueException("findings must not be null");
        if (critique.UnresolvedAssessments is null)
            throw new DecisionLedgerCritiqueException("unresolvedAssessments must not be null");
        if (critique.Reopenings is null)
            throw new DecisionLedgerCritiqueException("reopenings must not be null");

        foreach (var finding in critique.Findings)
        {
            if (finding is null) throw new DecisionLedgerCritiqueException("findings must not contain null");
            ValidateCritiqueSeverity(finding.Severity);
            ValidateCritiqueText(finding.Where, "finding where");
            ValidateCritiqueText(finding.What, "finding what");
        }

        lock (Gate())
        {
            var snapshot = ReadSnapshot();
            var projection = phase == LedgerPhase.PlanReview
                ? snapshot.Entries.Where(entry => entry.ActivePhase == LedgerPhaseNames.PLAN_REVIEW).ToArray()
                : snapshot.Entries.Where(entry => entry.ActivePhase == LedgerPhaseNames.CODE_REVIEW
                                                  || (entry.Origin == LedgerPhaseNames.PLAN_REVIEW
                                                      && entry.Disposition != LedgerDisposition.Unresolved)).ToArray();
            var unresolved = projection.Where(entry => entry.Disposition == LedgerDisposition.Unresolved)
                                      .Select(entry => entry.FindingId).ToHashSet(StringComparer.Ordinal);
            var settled = projection.Where(entry => entry.Disposition is LedgerDisposition.Deferred
                                                    or LedgerDisposition.Rejected)
                                    .Select(entry => entry.FindingId).ToHashSet(StringComparer.Ordinal);

            ValidateAssessments(critique.UnresolvedAssessments, unresolved);
            ValidateReopeningProposals(critique.Reopenings, settled);

            var entries = snapshot.Entries.ToList();
            var identified = new List<Finding>(critique.Findings.Count);
            var next = snapshot.NextFindingNumber;
            foreach (var finding in critique.Findings)
            {
                var id = FormatFindingId(next++);
                entries.Add(new DecisionLedgerEntry(id, PhaseName(phase), PhaseName(phase),
                                                    new Finding(finding.Severity, finding.Where, finding.What),
                                                    LedgerDisposition.Unresolved));
                identified.Add(new Finding(finding.Severity, finding.Where, finding.What, id));
            }

            if (identified.Count > 0)
            {
                var updated = snapshot with
                {
                    NextFindingNumber = next,
                    Entries = entries.OrderBy(entry => FindingNumber(entry.FindingId)).ToArray()
                };
                WriteSnapshot(_path, updated);
            }

            return new Critique(critique.Verdict, identified, critique.Summary,
                                critique.UnresolvedAssessments.ToArray(), critique.Reopenings.ToArray());
        }
    }

    internal DecisionLedgerEntry AddFinding(Finding finding, LedgerPhase phase) =>
        AddFinding(finding, PhaseName(phase));

    private DecisionLedgerEntry AddFinding(Finding finding, string phase)
    {
        var entries = AddFindings([finding], phase);
        return entries[0];
    }

    internal IReadOnlyList<DecisionLedgerEntry> AddFindings(IReadOnlyList<Finding> findings, LedgerPhase phase) =>
        AddFindings(findings, PhaseName(phase));

    private IReadOnlyList<DecisionLedgerEntry> AddFindings(IReadOnlyList<Finding> findings, string phase)
    {
        if (findings is null) throw new DecisionLedgerRequestException("findings must not be null");
        ValidatePhase(phase, "phase");
        foreach (var finding in findings) ValidateFinding(finding);
        if (findings.Count == 0) return [];

        lock (Gate())
        {
            var snapshot = ReadSnapshot();
            var entries = snapshot.Entries.ToList();
            var added = new List<DecisionLedgerEntry>(findings.Count);
            var next = snapshot.NextFindingNumber;

            foreach (var finding in findings)
            {
                var entry = new DecisionLedgerEntry(FormatFindingId(next++), phase, phase, finding,
                                                    LedgerDisposition.Unresolved);
                entries.Add(entry);
                added.Add(entry);
            }

            WriteSnapshot(_path, snapshot with { NextFindingNumber = next, Entries = entries });
            return added;
        }
    }

    internal DecisionBatchResponse Apply(DecisionBatchRequest request)
    {
        var payload = ValidateAndNormalize(request);
        var digest = Digest(JsonSerializer.SerializeToUtf8Bytes(
            payload, DecisionLedgerCanonicalJson.Default.DecisionBatchPayload));

        lock (Gate())
        {
            var snapshot = ReadSnapshot();
            var existing = snapshot.AppliedDecisionBatches.FirstOrDefault(
                batch => string.Equals(batch.DecisionBatchId, payload.DecisionBatchId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (existing.Digest == digest)
                    return new DecisionBatchResponse("no_op", existing.Result, existing);

                return new DecisionBatchResponse("conflict", existing.Result, existing);
            }

            ValidateLegalTransitions(snapshot, payload);
            var entries = snapshot.Entries.ToDictionary(entry => entry.FindingId, StringComparer.Ordinal);

            foreach (var decision in payload.Decisions)
            {
                var entry = entries[decision.FindingId];
                entries[decision.FindingId] = entry with
                {
                    Disposition = decision.Disposition,
                    Decision = new LedgerDecisionData(payload.DecisionBatchId, decision.By, decision.Reason)
                };
            }

            foreach (var reopening in payload.Reopenings)
            {
                var entry = entries[reopening.FindingId];
                entries[reopening.FindingId] = entry with
                {
                    ActivePhase = reopening.ActivePhase,
                    Disposition = LedgerDisposition.Unresolved,
                    Reopening = new LedgerReopeningData(payload.DecisionBatchId, reopening.ActivePhase,
                                                         reopening.By, reopening.Reason, reopening.Evidence)
                };
            }

            foreach (var closure in payload.Closures)
                entries.Remove(closure.FindingId);

            var result = new DecisionBatchResult(
                payload.DecisionBatchId,
                "applied",
                payload.Decisions.Select(decision => decision.FindingId).ToArray(),
                payload.Reopenings.Select(reopening => reopening.FindingId).ToArray(),
                payload.Closures.Select(closure => closure.FindingId).ToArray());
            var applied = new AppliedDecisionBatch(payload.DecisionBatchId, digest, result);
            var updated = snapshot with
            {
                Entries = entries.Values.OrderBy(entry => FindingNumber(entry.FindingId)).ToArray(),
                AppliedDecisionBatches = [.. snapshot.AppliedDecisionBatches, applied]
            };

            WriteSnapshot(_path, updated);
            return new DecisionBatchResponse("applied", result, applied);
        }
    }

    internal static byte[] CanonicalBytes(DecisionBatchRequest request)
    {
        var payload = ValidateAndNormalize(request);
        return JsonSerializer.SerializeToUtf8Bytes(payload, DecisionLedgerCanonicalJson.Default.DecisionBatchPayload);
    }

    internal static byte[] CanonicalBytes(OrchestratorDecisionBatch batch)
    {
        if (batch is null) throw new DecisionLedgerRequestException("decision batch must not be null");
        RequireText(batch.DecisionBatchId, "decisionBatchId");
        if (batch.Decisions is null) throw new DecisionLedgerRequestException("decisions must not be null");
        var payload = new OrchestratorDecisionPayload(batch.DecisionBatchId,
            batch.Decisions.OrderBy(decision => decision.FindingId, StringComparer.Ordinal).ToArray());
        ValidateOrchestratorInput(payload);
        foreach (var decision in payload.Decisions)
        {
            SensitiveInput.Guard(decision.Reason, $"decision reason for {decision.FindingId}");
            if (decision.Evidence is not null)
            {
                RequireText(decision.Evidence, "decision evidence");
                SensitiveInput.Guard(decision.Evidence, $"decision evidence for {decision.FindingId}");
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(payload, DecisionLedgerCanonicalJson.Default.OrchestratorDecisionPayload);
    }

    private static void ApplyEntries(Dictionary<string, DecisionLedgerEntry> entries,
                                     DecisionBatchPayload payload)
    {
        foreach (var decision in payload.Decisions)
        {
            var entry = entries[decision.FindingId];
            entries[decision.FindingId] = entry with
            {
                Disposition = decision.Disposition,
                Decision = new LedgerDecisionData(payload.DecisionBatchId, decision.By, decision.Reason)
            };
        }

        foreach (var reopening in payload.Reopenings)
        {
            var entry = entries[reopening.FindingId];
            entries[reopening.FindingId] = entry with
            {
                ActivePhase = reopening.ActivePhase,
                Disposition = LedgerDisposition.Unresolved,
                Reopening = new LedgerReopeningData(payload.DecisionBatchId, reopening.ActivePhase,
                                                     reopening.By, reopening.Reason, reopening.Evidence)
            };
        }

        foreach (var closure in payload.Closures)
            entries.Remove(closure.FindingId);
    }

    private object Gate() => GATES.GetOrAdd(_path, static _ => new object());

    private DecisionLedgerSnapshot ReadSnapshot()
    {
        try
        {
            var json = AtomicFile.Read(_path);
            var snapshot = JsonSerializer.Deserialize(json, DecisionLedgerJson.Default.DecisionLedgerSnapshot)
                           ?? throw new DecisionLedgerStateException("decision ledger is null");
            snapshot = snapshot with { FixAttempts = snapshot.FixAttempts ?? [] };
            ValidateSnapshot(snapshot);
            return snapshot;
        }
        catch (DecisionLedgerRequestException error)
        {
            throw new DecisionLedgerStateException("decision ledger invariants are invalid", error);
        }
        catch (DecisionLedgerException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or FormatException)
        {
            throw new DecisionLedgerStateException("decision ledger is malformed", error);
        }
    }

    private static void WriteSnapshot(string path, DecisionLedgerSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        var json = JsonSerializer.Serialize(snapshot, DecisionLedgerJson.Readable.DecisionLedgerSnapshot);
        AtomicFile.Write(path, json);
    }

    private static DecisionLedgerSnapshot EmptySnapshot() => new(CURRENT_SCHEMA_VERSION, 1, [], [], []);

    private DecisionBatchRequest NormalizeOrchestratorBatch(OrchestratorDecisionBatch batch,
                                                            LedgerPhase phase,
                                                            out IReadOnlyList<string> acceptedIds,
                                                            out IReadOnlyList<string> declinedIds)
    {
        if (batch is null) throw new DecisionLedgerRequestException("decision batch must not be null");
        RequireText(batch.DecisionBatchId, "decisionBatchId");
        if (batch.Decisions is null) throw new DecisionLedgerRequestException("decisions must not be null");
        if (batch.Decisions.Count == 0) throw new DecisionLedgerRequestException("decisions must not be empty");

        var dispositions = new List<LedgerDispositionDecision>();
        var reopenings = new List<LedgerReopeningDecision>();
        var closures = new List<LedgerClosureDecision>();
        var accepted = new List<string>();
        var declined = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = ReadSnapshot();
        var entries = snapshot.Entries.ToDictionary(entry => entry.FindingId, StringComparer.Ordinal);

        foreach (var decision in batch.Decisions)
        {
            if (decision is null) throw new DecisionLedgerRequestException("decisions must not contain null");
            ValidateFindingId(decision.FindingId);
            if (!ids.Add(decision.FindingId))
                throw new DecisionLedgerRequestException($"finding '{decision.FindingId}' appears more than once");
            var decisionMaker = ValidateDecision(decision.By, decision.Reason);
            SensitiveInput.Guard(decision.Reason, $"decision reason for {decision.FindingId}");
            if (decision.Evidence is not null)
            {
                RequireText(decision.Evidence, "decision evidence");
                SensitiveInput.Guard(decision.Evidence, $"decision evidence for {decision.FindingId}");
            }

            var action = NormalizeAction(decision.Action);
            if (!entries.TryGetValue(decision.FindingId, out var entry))
                throw new DecisionLedgerRequestException($"finding '{decision.FindingId}' does not exist");

            switch (action)
            {
                case "defer":
                case "reject":
                    RequireActiveUnresolved(entry, phase, decision.FindingId);
                    dispositions.Add(new LedgerDispositionDecision(decision.FindingId,
                        action == "defer" ? LedgerDisposition.Deferred : LedgerDisposition.Rejected,
                        decisionMaker, decision.Reason));
                    accepted.Add(decision.FindingId);
                    break;

                case "addressedByRevision":
                    if (phase != LedgerPhase.PlanReview)
                        throw new DecisionLedgerRequestException("addressedByRevision is only valid during plan review");
                    RequireActiveUnresolved(entry, phase, decision.FindingId);
                    RequireOrchestrator(decisionMaker, "only the orchestrator may close a finding");
                    if (decision.Evidence is not null)
                        throw new DecisionLedgerRequestException("addressedByRevision does not accept evidence");
                    closures.Add(new LedgerClosureDecision(decision.FindingId, LedgerClosureKind.Revision,
                                                           decisionMaker, decision.Reason));
                    accepted.Add(decision.FindingId);
                    break;

                case "hostVerified":
                    if (phase != LedgerPhase.CodeReview)
                        throw new DecisionLedgerRequestException("hostVerified is only valid during code review");
                    RequireActiveUnresolved(entry, phase, decision.FindingId);
                    RequireOrchestrator(decisionMaker, "only the orchestrator may close a finding");
                    RequireText(decision.Evidence, "verified closure evidence");
                    closures.Add(new LedgerClosureDecision(decision.FindingId, LedgerClosureKind.HostVerified,
                                                           decisionMaker, decision.Reason, decision.Evidence!));
                    accepted.Add(decision.FindingId);
                    break;

                case "duplicateOf":
                    if (phase != LedgerPhase.PlanReview && phase != LedgerPhase.CodeReview)
                        throw new DecisionLedgerRequestException("duplicateOf is not valid in this phase");
                    RequireActiveUnresolved(entry, phase, decision.FindingId);
                    RequireOrchestrator(decisionMaker, "only the orchestrator may close a finding");
                    ValidateFindingId(decision.DuplicateOf!);
                    if (decision.DuplicateOf == decision.FindingId || !entries.ContainsKey(decision.DuplicateOf!))
                        throw new DecisionLedgerRequestException("duplicateOf must reference a different known finding");
                    if (decision.Evidence is not null)
                        throw new DecisionLedgerRequestException("duplicateOf does not accept evidence");
                    closures.Add(new LedgerClosureDecision(decision.FindingId, LedgerClosureKind.Duplicate,
                                                           decisionMaker, decision.Reason, DuplicateOf: decision.DuplicateOf!));
                    accepted.Add(decision.FindingId);
                    break;

                case "accept":
                    RequireSettledProjectionEntry(entry, phase, decision.FindingId);
                    RequireOrchestrator(decisionMaker, "only the orchestrator may accept a reopening");
                    RequireText(decision.Evidence, "reopening evidence");
                    reopenings.Add(new LedgerReopeningDecision(decision.FindingId, PhaseName(phase),
                                                               decisionMaker, decision.Reason, decision.Evidence!));
                    accepted.Add(decision.FindingId);
                    break;

                case "decline":
                    RequireSettledProjectionEntry(entry, phase, decision.FindingId);
                    if (decision.Evidence is not null)
                        throw new DecisionLedgerRequestException("decline does not accept evidence");
                    declined.Add(decision.FindingId);
                    break;

                default:
                    throw new DecisionLedgerRequestException($"unsupported decision action '{decision.Action}'");
            }
        }

        acceptedIds = accepted;
        declinedIds = declined;
        return new DecisionBatchRequest(batch.DecisionBatchId, dispositions, reopenings, closures);
    }

    private static string NormalizeAction(string action) => action switch
    {
        "defer" => "defer",
        "reject" => "reject",
        "addressedByRevision" or "revision" => "addressedByRevision",
        "hostVerified" or "verifiedClosure" => "hostVerified",
        "duplicateOf" => "duplicateOf",
        "accept" => "accept",
        "decline" => "decline",
        _ => action
    };

    private static void RequireActiveUnresolved(DecisionLedgerEntry entry, LedgerPhase phase, string findingId)
    {
        if (entry.ActivePhase != PhaseName(phase) || entry.Disposition != LedgerDisposition.Unresolved)
            throw new DecisionLedgerRequestException($"finding '{findingId}' is not an unresolved active {PhaseName(phase)} entry");
    }

    private static void RequireSettledProjectionEntry(DecisionLedgerEntry entry, LedgerPhase phase, string findingId)
    {
        var visible = phase == LedgerPhase.PlanReview
            ? entry.ActivePhase == LedgerPhaseNames.PLAN_REVIEW
            : entry.ActivePhase == LedgerPhaseNames.CODE_REVIEW
              || (entry.Origin == LedgerPhaseNames.PLAN_REVIEW && entry.Disposition != LedgerDisposition.Unresolved);
        if (!visible || entry.Disposition is not LedgerDisposition.Deferred and not LedgerDisposition.Rejected)
            throw new DecisionLedgerRequestException($"finding '{findingId}' is not a settled {PhaseName(phase)} projection entry");
    }

    private static void RequireOrchestrator(LedgerDecisionMaker by, string message)
    {
        if (by != LedgerDecisionMaker.Orchestrator) throw new DecisionLedgerRequestException(message);
    }

    private static IReadOnlyList<string> NormalizeFindingIds(IReadOnlyList<string> findingIds, string name)
    {
        if (findingIds is null) throw new DecisionLedgerRequestException($"{name} must not be null");
        var ids = findingIds.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            ValidateFindingId(id);
            if (!seen.Add(id)) throw new DecisionLedgerRequestException($"{name} contains duplicate '{id}'");
        }

        return ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private static DecisionBatchPayload ValidateAndNormalize(DecisionBatchRequest request)
    {
        if (request is null) throw new DecisionLedgerRequestException("decision batch must not be null");
        RequireText(request.DecisionBatchId, "decisionBatchId");
        if (request.Decisions is null) throw new DecisionLedgerRequestException("decisions must not be null");
        if (request.Reopenings is null) throw new DecisionLedgerRequestException("reopenings must not be null");
        if (request.Closures is null) throw new DecisionLedgerRequestException("closures must not be null");
        if (request.Decisions.Count + request.Reopenings.Count + request.Closures.Count == 0)
            throw new DecisionLedgerRequestException("decision batch must contain at least one operation");

        foreach (var decision in request.Decisions)
        {
            if (decision is null) throw new DecisionLedgerRequestException("decisions must not contain null");
            ValidateFindingId(decision.FindingId);
            if (decision.Disposition is not LedgerDisposition.Deferred and not LedgerDisposition.Rejected)
                throw new DecisionLedgerRequestException($"unsupported disposition '{decision.Disposition}'");
            ValidateDecision(decision.By, decision.Reason);
        }

        foreach (var reopening in request.Reopenings)
        {
            if (reopening is null) throw new DecisionLedgerRequestException("reopenings must not contain null");
            ValidateFindingId(reopening.FindingId);
            ValidatePhase(reopening.ActivePhase, "reopening activePhase");
            if (reopening.By != LedgerDecisionMaker.Orchestrator)
                throw new DecisionLedgerRequestException("only the orchestrator may accept a reopening");
            ValidateDecision(reopening.By, reopening.Reason);
            RequireText(reopening.Evidence, "reopening evidence");
        }

        foreach (var closure in request.Closures)
        {
            if (closure is null) throw new DecisionLedgerRequestException("closures must not contain null");
            ValidateFindingId(closure.FindingId);
            ValidateClosure(closure);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in request.Decisions.Select(decision => decision.FindingId)
                     .Concat(request.Reopenings.Select(reopening => reopening.FindingId))
                     .Concat(request.Closures.Select(closure => closure.FindingId)))
        {
            if (!ids.Add(id)) throw new DecisionLedgerRequestException($"finding '{id}' appears more than once");
        }

        return new DecisionBatchPayload(
            request.DecisionBatchId,
            request.Decisions.OrderBy(decision => decision.FindingId, StringComparer.Ordinal).ToArray(),
            request.Reopenings.OrderBy(reopening => reopening.FindingId, StringComparer.Ordinal).ToArray(),
            request.Closures.OrderBy(closure => closure.FindingId, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateLegalTransitions(DecisionLedgerSnapshot snapshot, DecisionBatchPayload payload)
    {
        var entries = snapshot.Entries.ToDictionary(entry => entry.FindingId, StringComparer.Ordinal);
        foreach (var decision in payload.Decisions)
        {
            if (!entries.TryGetValue(decision.FindingId, out var entry))
                throw new DecisionLedgerRequestException($"finding '{decision.FindingId}' does not exist");
            if (entry.Disposition != LedgerDisposition.Unresolved)
                throw new DecisionLedgerRequestException($"finding '{decision.FindingId}' is not unresolved");
        }

        foreach (var reopening in payload.Reopenings)
        {
            if (!entries.TryGetValue(reopening.FindingId, out var entry))
                throw new DecisionLedgerRequestException($"finding '{reopening.FindingId}' does not exist");
            if (entry.Disposition is not LedgerDisposition.Deferred and not LedgerDisposition.Rejected)
                throw new DecisionLedgerRequestException($"finding '{reopening.FindingId}' is not settled");
            if (entry.Origin == LedgerPhaseNames.CODE_REVIEW && reopening.ActivePhase != LedgerPhaseNames.CODE_REVIEW)
                throw new DecisionLedgerRequestException("a code-review finding cannot reopen into plan review");
        }

        foreach (var closure in payload.Closures)
        {
            if (!entries.ContainsKey(closure.FindingId))
                throw new DecisionLedgerRequestException($"finding '{closure.FindingId}' does not exist");
        }
    }

    private static void ValidateSnapshot(DecisionLedgerSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CURRENT_SCHEMA_VERSION)
            throw new DecisionLedgerStateException($"unsupported decision ledger schema version {snapshot.SchemaVersion}");
        if (snapshot.NextFindingNumber < 1)
            throw new DecisionLedgerStateException("nextFindingNumber must be positive");
        if (snapshot.Entries is null)
            throw new DecisionLedgerStateException("entries must not be null");
        if (snapshot.AppliedDecisionBatches is null)
            throw new DecisionLedgerStateException("appliedDecisionBatches must not be null");
        if (snapshot.FixAttempts is null)
            throw new DecisionLedgerStateException("fixAttempts must not be null");

        var previousNumber = 0;
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null) throw new DecisionLedgerStateException("entries must not contain null");
            var number = ValidateFindingId(entry.FindingId);
            if (number <= previousNumber)
                throw new DecisionLedgerStateException("entries must be ordered by finding id");
            if (number >= snapshot.NextFindingNumber)
                throw new DecisionLedgerStateException("nextFindingNumber must be greater than every entry id");
            if (!entryIds.Add(entry.FindingId))
                throw new DecisionLedgerStateException($"duplicate finding id '{entry.FindingId}'");
            previousNumber = number;

            ValidateOriginAndPhase(entry.Origin, entry.ActivePhase);
            ValidateFinding(entry.Finding);
            if (!Enum.IsDefined(entry.Disposition))
                throw new DecisionLedgerStateException($"unsupported disposition '{entry.Disposition}'");

            if (entry.Disposition is LedgerDisposition.Deferred or LedgerDisposition.Rejected)
            {
                if (entry.Decision is null) throw new DecisionLedgerStateException("settled entry needs a decision");
                ValidateDecisionData(entry.Decision);
            }
            else if (entry.Decision is not null && entry.Reopening is null)
            {
                throw new DecisionLedgerStateException("unresolved entry has a decision without a reopening");
            }

            if (entry.Reopening is not null)
            {
                ValidatePhase(entry.Reopening.ActivePhase, "reopening activePhase");
                if (entry.ActivePhase != entry.Reopening.ActivePhase)
                    throw new DecisionLedgerStateException("entry phase does not match reopening phase");
                ValidateReopeningData(entry.Reopening);
                if (entry.Origin == LedgerPhaseNames.CODE_REVIEW
                    && entry.Reopening.ActivePhase != LedgerPhaseNames.CODE_REVIEW)
                    throw new DecisionLedgerStateException("code-review origin cannot reopen into plan review");
            }
        }

        var batchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in snapshot.AppliedDecisionBatches)
        {
            if (batch is null) throw new DecisionLedgerStateException("applied batches must not contain null");
            RequireText(batch.DecisionBatchId, "applied decisionBatchId");
            if (!batchIds.Add(batch.DecisionBatchId))
                throw new DecisionLedgerStateException($"duplicate decision batch '{batch.DecisionBatchId}'");
        }

        var attemptIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in snapshot.FixAttempts)
        {
            if (attempt is null) throw new DecisionLedgerStateException("fixAttempts must not contain null");
            RequireText(attempt.FixAttemptId, "fixAttemptId");
            if (!attemptIds.Add(attempt.FixAttemptId))
                throw new DecisionLedgerStateException($"duplicate fix attempt '{attempt.FixAttemptId}'");
            var ids = ValidateSortedFindingIds(attempt.FixFindingIds, "fixFindingIds");
            if (ids.Any(id => ValidateFindingId(id) >= snapshot.NextFindingNumber))
                throw new DecisionLedgerStateException("fix attempt references a finding beyond nextFindingNumber");
            if (attempt.LastResult is not null)
                ValidateFixAttemptResult(attempt.LastResult);
            if (attempt.Terminal && attempt.LastResult is null)
                throw new DecisionLedgerStateException("terminal fix attempt needs a result");
            if (ids.Count == 0)
                throw new DecisionLedgerStateException("fix attempt must contain at least one finding id");
        }

        var appliedIds = batchIds;
        foreach (var entry in snapshot.Entries)
        {
            if (entry.Decision is not null && !appliedIds.Contains(entry.Decision.DecisionBatchId))
                throw new DecisionLedgerStateException("entry decision has no applied decision batch");
            if (entry.Reopening is not null && !appliedIds.Contains(entry.Reopening.DecisionBatchId))
                throw new DecisionLedgerStateException("entry reopening has no applied decision batch");
        }
    }

    private static void ValidateOrchestratorInput(OrchestratorDecisionPayload payload)
    {
        if (payload.Decisions is null || payload.Decisions.Count == 0)
            throw new DecisionLedgerRequestException("decisions must not be empty");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in payload.Decisions)
        {
            if (decision is null) throw new DecisionLedgerRequestException("decisions must not contain null");
            ValidateFindingId(decision.FindingId);
            if (!seen.Add(decision.FindingId))
                throw new DecisionLedgerRequestException($"finding '{decision.FindingId}' appears more than once");
            RequireText(decision.Action, "decision action");
            ValidateDecision(decision.By, decision.Reason);
        }
    }

    private static IReadOnlyList<string> ValidateSortedFindingIds(IReadOnlyList<string> ids, string name)
    {
        if (ids is null) throw new DecisionLedgerStateException($"{name} must not be null");
        var previous = string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            ValidateFindingId(id);
            if (!seen.Add(id) || (previous.Length > 0 && string.CompareOrdinal(previous, id) >= 0))
                throw new DecisionLedgerStateException($"{name} must be unique and sorted");
            previous = id;
        }

        return ids;
    }

    private static void ValidateFixAttemptResult(BuildResult result)
    {
        RequireText(result.Status, "fix attempt result status");
        if (result.FilesChanged is null || result.Verification is null)
            throw new DecisionLedgerStateException("fix attempt result is incomplete");
        RequireText(result.Verification.Outcome, "fix attempt verification outcome");
        if (result.Verification.Evidence is null || result.Summary is null)
            throw new DecisionLedgerStateException("fix attempt result text must not be null");
    }

    private static void ValidateClosure(LedgerClosureDecision closure)
    {
        if (!Enum.IsDefined(closure.Kind))
            throw new DecisionLedgerRequestException($"unsupported closure kind '{closure.Kind}'");
        if (closure.By != LedgerDecisionMaker.Orchestrator)
            throw new DecisionLedgerRequestException("only the orchestrator may close a finding");
        RequireText(closure.Reason, "closure reason");
        if (closure.Kind is LedgerClosureKind.HostVerified or LedgerClosureKind.AutomaticGate)
            RequireText(closure.Evidence, "closure evidence");
        if (closure.Kind == LedgerClosureKind.Duplicate)
            ValidateFindingId(closure.DuplicateOf);
    }

    private static LedgerDecisionMaker ValidateDecision(string by, string reason)
    {
        var decisionMaker = by switch
        {
            "user" => LedgerDecisionMaker.User,
            "orchestrator" => LedgerDecisionMaker.Orchestrator,
            _ => throw new DecisionLedgerRequestException($"unsupported decision-maker '{by}'")
        };
        ValidateDecision(decisionMaker, reason);
        return decisionMaker;
    }

    private static void ValidateDecision(LedgerDecisionMaker by, string reason)
    {
        if (!Enum.IsDefined(by))
            throw new DecisionLedgerRequestException($"unsupported decision-maker '{by}'");
        RequireText(reason, "decision reason");
    }

    private static void ValidateDecisionData(LedgerDecisionData data)
    {
        if (data is null) throw new DecisionLedgerStateException("decision data must not be null");
        RequireText(data.DecisionBatchId, "decision batch id");
        ValidateDecision(data.By, data.Reason);
    }

    private static void ValidateReopeningData(LedgerReopeningData data)
    {
        if (data is null) throw new DecisionLedgerStateException("reopening data must not be null");
        RequireText(data.DecisionBatchId, "reopening decision batch id");
        if (data.By != LedgerDecisionMaker.Orchestrator)
            throw new DecisionLedgerStateException("only the orchestrator may accept a reopening");
        RequireText(data.Reason, "reopening reason");
        RequireText(data.Evidence, "reopening evidence");
    }

    private static void ValidateOriginAndPhase(string origin, string activePhase)
    {
        ValidatePhase(origin, "origin");
        ValidatePhase(activePhase, "activePhase");
        if (origin == LedgerPhaseNames.CODE_REVIEW && activePhase != LedgerPhaseNames.CODE_REVIEW)
            throw new DecisionLedgerStateException("code-review origin cannot be active in plan review");
    }

    private static void ValidatePhase(string phase, string label)
    {
        if (phase is not LedgerPhaseNames.PLAN_REVIEW and not LedgerPhaseNames.CODE_REVIEW)
            throw new DecisionLedgerRequestException($"unsupported {label} '{phase}'");
    }

    private static void ValidateFinding(Finding finding)
    {
        if (finding is null) throw new DecisionLedgerRequestException("finding must not be null");
        RequireText(finding.Severity, "finding severity");
        RequireText(finding.Where, "finding where");
        RequireText(finding.What, "finding what");
    }

    private static void ValidateAssessments(IReadOnlyList<UnresolvedAssessment> assessments,
                                            IReadOnlySet<string> unresolved)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assessment in assessments)
        {
            if (assessment is null) throw new DecisionLedgerCritiqueException("unresolvedAssessments must not contain null");
            ValidateCritiqueId(assessment.FindingId);
            ValidateCritiqueText(assessment.Evidence, $"assessment evidence for {assessment.FindingId}");
            if (!unresolved.Contains(assessment.FindingId))
                throw new DecisionLedgerCritiqueException($"unexpected unresolved assessment ID '{assessment.FindingId}'");
            if (!seen.Add(assessment.FindingId))
                throw new DecisionLedgerCritiqueException($"duplicate unresolved assessment ID '{assessment.FindingId}'");
        }

        var missing = unresolved.Where(id => !seen.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new DecisionLedgerCritiqueException($"missing unresolved assessment IDs: {string.Join(", ", missing)}");
    }

    private static void ValidateReopeningProposals(IReadOnlyList<ReopeningProposal> reopenings,
                                                   IReadOnlySet<string> settled)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reopening in reopenings)
        {
            if (reopening is null) throw new DecisionLedgerCritiqueException("reopenings must not contain null");
            ValidateCritiqueId(reopening.FindingId);
            ValidateCritiqueText(reopening.Evidence, $"reopening evidence for {reopening.FindingId}");
            if (!settled.Contains(reopening.FindingId))
                throw new DecisionLedgerCritiqueException($"unexpected reopening ID '{reopening.FindingId}'");
            if (!seen.Add(reopening.FindingId))
                throw new DecisionLedgerCritiqueException($"duplicate reopening ID '{reopening.FindingId}'");
        }
    }

    private static void ValidateCritiqueId(string id)
    {
        try { ValidateFindingId(id); }
        catch (DecisionLedgerException error)
        {
            throw new DecisionLedgerCritiqueException(error.Message);
        }
    }

    private static void ValidateCritiqueSeverity(string severity)
    {
        if (severity is not "blocker" and not "major" and not "minor")
            throw new DecisionLedgerCritiqueException($"unsupported finding severity '{severity}'");
    }

    private static void ValidateCritiqueText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DecisionLedgerCritiqueException($"{name} must not be empty");
    }

    private static int ValidateFindingId(string id)
    {
        RequireText(id, "findingId");
        if (!id.StartsWith("F-", StringComparison.Ordinal)
            || !int.TryParse(id.AsSpan(2), out var number)
            || number < 1
            || !string.Equals(id, FormatFindingId(number), StringComparison.Ordinal))
            throw new DecisionLedgerRequestException($"invalid finding id '{id}'");
        return number;
    }

    private static int FindingNumber(string id) => ValidateFindingId(id);

    private static string FormatFindingId(int number) => $"F-{number:0000}";

    private static string PhaseName(LedgerPhase phase) => phase switch
    {
        LedgerPhase.PlanReview => LedgerPhaseNames.PLAN_REVIEW,
        LedgerPhase.CodeReview => LedgerPhaseNames.CODE_REVIEW,
        _ => throw new DecisionLedgerRequestException($"unsupported phase '{phase}'")
    };

    private static string DispositionName(LedgerDisposition disposition) => disposition switch
    {
        LedgerDisposition.Unresolved => "unresolved",
        LedgerDisposition.Deferred => "deferred",
        LedgerDisposition.Rejected => "rejected",
        _ => throw new DecisionLedgerStateException($"unsupported disposition '{disposition}'")
    };

    private static string DecisionMakerName(LedgerDecisionMaker decisionMaker) => decisionMaker switch
    {
        LedgerDecisionMaker.User => "user",
        LedgerDecisionMaker.Orchestrator => "orchestrator",
        _ => throw new DecisionLedgerStateException($"unsupported decision-maker '{decisionMaker}'")
    };

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DecisionLedgerRequestException($"{name} must not be empty");
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal class DecisionLedgerException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class DecisionLedgerStateException(string message, Exception? inner = null)
    : DecisionLedgerException(message, inner);

internal sealed class DecisionLedgerRequestException(string message)
    : DecisionLedgerException(message);

internal sealed class DecisionLedgerCritiqueException(string message)
    : DecisionLedgerException(message);

[JsonSourceGenerationOptions(WriteIndented = true,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DecisionLedgerSnapshot))]
[JsonSerializable(typeof(DecisionLedgerEntry))]
[JsonSerializable(typeof(LedgerDecisionData))]
[JsonSerializable(typeof(LedgerReopeningData))]
[JsonSerializable(typeof(Finding))]
[JsonSerializable(typeof(AppliedDecisionBatch))]
[JsonSerializable(typeof(DecisionBatchResult))]
[JsonSerializable(typeof(FixAttemptRecord))]
[JsonSerializable(typeof(BuildResult))]
[JsonSerializable(typeof(Verification))]
[JsonSerializable(typeof(GateRun))]
internal sealed partial class DecisionLedgerJson : JsonSerializerContext
{
    /// <summary>For the file a person opens: non-ASCII text as written, not <c>\uXXXX</c>.</summary>
    internal static DecisionLedgerJson Readable =>
        field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DecisionBatchPayload))]
[JsonSerializable(typeof(LedgerDispositionDecision))]
[JsonSerializable(typeof(LedgerReopeningDecision))]
[JsonSerializable(typeof(LedgerClosureDecision))]
[JsonSerializable(typeof(OrchestratorDecisionPayload))]
[JsonSerializable(typeof(OrchestratorDecision))]
internal sealed partial class DecisionLedgerCanonicalJson : JsonSerializerContext;
