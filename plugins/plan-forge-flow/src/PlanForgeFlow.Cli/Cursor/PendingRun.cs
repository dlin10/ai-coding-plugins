using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;
using PlanForgeFlow.Claude;

namespace PlanForgeFlow.Pending;

internal enum PendingRunPhase
{
    [JsonStringEnumMemberName("reviewing")] Reviewing,
    [JsonStringEnumMemberName("revision-required")] RevisionRequired,
    [JsonStringEnumMemberName("review-approved")] ReviewApproved,
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("materializing")] Materializing,
    [JsonStringEnumMemberName("consumed")] Consumed,
    [JsonStringEnumMemberName("abandoned")] Abandoned,
}

internal sealed record CursorModelWaiver(string Role, string Model, string Effort, string CursorVersion, string Observed, string Consent, string Timestamp, string ModelGuarantee = "waived");
internal sealed record CursorReviewResponse(string DispatchId, string Stage, string Verdict, string Hash, string Response, string Timestamp);
internal sealed record CursorCapAudit(int PreviousCap, int NewCap, string AuthorizationNote, string Timestamp);
internal sealed record MaterializationTransaction(string Id, string RunId, string ScopeId, string? PlanText, string PlanHash, string ReviewHash, string ReviewerModel, string ReviewerEffort, string BuilderModel, string BuilderEffort, string[] ExpectedRefs, string[] ExpectedArtifacts, string Timestamp, string? StateHash = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderSelection? ReviewerSelection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderSelection? BuilderSelection { get; init; }
}

internal sealed record PendingRun
{
    public const int SchemaVersion = 4;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersionValue { get; init; } = SchemaVersion;
    public HostKind Host { get; init; } = HostKind.Cursor;
    public string Workspace { get; init; } = string.Empty;
    public string ScopeId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string? DraftText { get; init; }
    public PendingRunPhase Phase { get; init; } = PendingRunPhase.Reviewing;
    public int ReviewRound { get; init; }
    public int ReviewCap { get; init; } = 5;
    public List<CursorCapAudit> CapAudits { get; init; } = [];
    public List<CursorReviewResponse> Responses { get; init; } = [];
    public CursorModelWaiver? ReviewerWaiver { get; init; }
    public CursorModelWaiver? BuilderWaiver { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderSelection? ReviewerSelection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderSelection? BuilderSelection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuilderHoldId { get; init; }
    public string? ActiveDispatchId { get; init; }
    public string? TransactionId { get; init; }
    public MaterializationTransaction? Materialization { get; init; }
    public string? InvalidationReason { get; init; }
    public string ReviewerGuarantee { get; init; } = "advisory";
    public string ApprovalGuarantee { get; init; } = "advisory";
    public string CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public string UpdatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");
}

internal static partial class PendingRuns
{
    private const string MinimumCursorVersion = "3.15.6";
    private static readonly Regex SafeIdentity = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    public static string PathFor(RepositoryIdentity repository, string runId, HostKind host = HostKind.Cursor) =>
        host == HostKind.Cursor
            ? Path.Combine(DataRoot(host), "cursor-runs", RepositoryPaths.ScopeId(repository), Hashing.Sha256Hex(runId) + ".json")
            : Path.Combine(DataRoot(host), "pending-runs", host.ToString().ToLowerInvariant(), RepositoryPaths.ScopeId(repository),
                           Hashing.Sha256Hex(runId) + ".json");

    public static PendingRun Load(RepositoryIdentity repository, string runId, HostKind host = HostKind.Cursor) => TryLoad(repository, runId, host) ??
                                                                                  throw new CliFailure("state",
                                                                                                       $"no {HostName(host)} pending run exists; return to {HostName(host)} Plan Mode and start a new /forge run",
                                                                                                       3);

    public static PendingRun? Status(RepositoryIdentity repository, HostKind host = HostKind.Cursor)
    {
        var runs = LoadAll(repository, host).OrderByDescending(run => run.UpdatedAt, StringComparer.Ordinal).ToArray();
        if (runs.Length > 1 && runs[0].Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned) &&
            runs[1].Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned))
            throw new CliFailure("state", $"{HostName(host)} status is ambiguous", 3);
        return runs.FirstOrDefault();
    }

    internal static void Save(RepositoryIdentity repository, PendingRun run) => Write(repository, run);

    internal static PendingRun? TryLoadForRun(RepositoryIdentity repository, string runId, HostKind host = HostKind.Cursor)
        => TryLoad(repository, runId, host);

    internal static void ValidateIdentity(string value, string option)
    {
        if (!SafeIdentity.IsMatch(value)) throw new CliFailure("usage", $"{option} is malformed");
    }

    private static PendingRun? TryLoad(RepositoryIdentity repository, string runId, HostKind host = HostKind.Cursor)
    {
        var path = PathFor(repository, runId, host);
        if (!File.Exists(path)) return null;
        try
        {
            var run = Deserialize(File.ReadAllText(path));
            ValidateLoaded(repository, run, host);
            if (run.RunId != runId) throw new CliFailure("unsupported-state-schema", $"{HostName(host)} pending run identity is invalid", 3);
            return run;
        }
        catch (CliFailure)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new CliFailure("unsupported-state-schema", error.Message, 3);
        }
    }

    private static void Write(RepositoryIdentity repository, PendingRun run)
    {
        var path = PathFor(repository, run.RunId, run.Host);
        OwnershipGuards.EnsureDirectory(Path.GetDirectoryName(path)!);
        DurableFiles.WriteJson(path, run, ForgeJsonContext.Default.PendingRun);
    }

    private static void ValidateRunId(string runId) => ValidateIdentity(runId, "--run-id");

    private static string DataRoot(HostKind host) => RepositoryPaths.PluginData(host);

    internal static string HostName(HostKind host) => host switch
    {
        HostKind.Cursor => "Cursor",
        HostKind.Claude => "Claude",
        HostKind.Codex => "Codex",
        _ => host.ToString(),
    };

    internal static string PhaseName(PendingRunPhase phase) => phase switch
    {
        PendingRunPhase.Reviewing => "reviewing",
        PendingRunPhase.RevisionRequired => "revision-required",
        PendingRunPhase.ReviewApproved => "review-approved",
        PendingRunPhase.Ready => "ready",
        PendingRunPhase.Materializing => "materializing",
        PendingRunPhase.Consumed => "consumed",
        PendingRunPhase.Abandoned => "abandoned",
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static IReadOnlyList<PendingRun> LoadAll(RepositoryIdentity repository, HostKind host = HostKind.Cursor)
    {
        var directory = Path.GetDirectoryName(PathFor(repository, "x", host))!;
        if (!Directory.Exists(directory)) return [];
        var runs = new List<PendingRun>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            PendingRun candidate;
            try
            {
                candidate = Deserialize(File.ReadAllText(file));
            }
            catch (Exception error)
            {
                throw new CliFailure("unsupported-state-schema", error.Message, 3);
            }
            ValidateLoaded(repository, candidate, host);
            if (!string.Equals(file, PathFor(repository, candidate.RunId, host), WorkspacePathPolicy.Comparison))
                throw new CliFailure("unsupported-state-schema", $"{HostName(host)} pending run path is invalid", 3);
            runs.Add(candidate);
        }
        return runs;
    }

    private static PendingRun Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) ||
            version != PendingRun.SchemaVersion || !root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String ||
            host.GetString() is not ("cursor" or "claude")) throw new CliFailure("unsupported-state-schema", "pending run is malformed or unsupported", 3);
        return JsonSerializer.Deserialize(json, ForgeJsonContext.Default.PendingRun) ?? throw new JsonException();
    }

    private static void ValidateLoaded(RepositoryIdentity repository, PendingRun run, HostKind host)
    {
        static CliFailure Unsupported() => new("unsupported-state-schema", "pending run is malformed or unsupported", 3);
        static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
        if (run.SchemaVersionValue != PendingRun.SchemaVersion || run.Host != host || host is not (HostKind.Cursor or HostKind.Claude) ||
            !string.Equals(run.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison) || run.ScopeId != RepositoryPaths.ScopeId(repository) ||
            !SafeIdentity.IsMatch(run.RunId)) throw Unsupported();
        if (!Enum.IsDefined(run.Phase) || run.ReviewCap < 1 || run.ReviewRound < 0 || run.ReviewRound > run.ReviewCap ||
            run.Responses.Count(response => response.Stage == "plan") != run.ReviewRound) throw Unsupported();
        var retainsDraft = run.Phase is PendingRunPhase.Reviewing or PendingRunPhase.RevisionRequired or PendingRunPhase.ReviewApproved ||
                           host == HostKind.Claude && run.Phase == PendingRunPhase.Ready;
        if (retainsDraft != run.DraftText is not null || run.DraftText is { } draft && draft != CanonicalText.Canonicalize(draft)) throw Unsupported();
        if (!DateTimeOffset.TryParse(run.CreatedAt, out _) || !DateTimeOffset.TryParse(run.UpdatedAt, out _) || run.InvalidationReason is { Length: > 512 }) throw Unsupported();
        if (host == HostKind.Cursor && (run.ReviewerGuarantee != "advisory" || run.ApprovalGuarantee != "advisory" ||
            run.ReviewerWaiver is null || !IsValidWaiver(run.ReviewerWaiver, "reviewer"))) throw Unsupported();
        if (host == HostKind.Cursor && run.Phase is PendingRunPhase.Ready or PendingRunPhase.Materializing or PendingRunPhase.Consumed &&
            (run.BuilderWaiver is null || !IsValidWaiver(run.BuilderWaiver, "builder"))) throw Unsupported();
        if (host == HostKind.Claude && (run.ReviewerSelection is null || !ValidSelection(run.ReviewerSelection) ||
            run.Phase is PendingRunPhase.Ready or PendingRunPhase.Materializing or PendingRunPhase.Consumed &&
            (run.BuilderSelection is null || !ValidSelection(run.BuilderSelection) || string.IsNullOrWhiteSpace(run.BuilderHoldId)))) throw Unsupported();
        if (run.ActiveDispatchId is { } activeDispatchId && !SafeIdentity.IsMatch(activeDispatchId)) throw Unsupported();
        foreach (var response in run.Responses)
        {
            if (!SafeIdentity.IsMatch(response.DispatchId) || response.Stage is not ("plan" or "code" or "fix-review") ||
                response.Verdict is not ("APPROVED" or "REVISE") || response.Response != CanonicalText.Canonicalize(response.Response) ||
                response.Hash != Hashing.Sha256Hex(response.Response) || !DateTimeOffset.TryParse(response.Timestamp, out _)) throw Unsupported();
        }

        var expectedCap = 5;
        foreach (var audit in run.CapAudits)
        {
            if (audit.PreviousCap != expectedCap || audit.NewCap != expectedCap + 1 || string.IsNullOrWhiteSpace(audit.AuthorizationNote) ||
                audit.AuthorizationNote.Length > 16 * 1024 || !DateTimeOffset.TryParse(audit.Timestamp, out _)) throw Unsupported();
            expectedCap = audit.NewCap;
        }
        if (run.ReviewCap != expectedCap) throw Unsupported();

        var hasTransaction = run.TransactionId is not null && run.Materialization is not null;
        if (run.TransactionId is null != run.Materialization is null ||
            run.Phase is PendingRunPhase.Materializing or PendingRunPhase.Consumed != hasTransaction) throw Unsupported();
        if (run.Materialization is { } transaction)
        {
            var expectedRefs = new[]
                { $"refs/plan-forge/{run.ScopeId}/owner", $"refs/plan-forge/{run.ScopeId}/head-base", $"refs/plan-forge/{run.ScopeId}/worktree-base" };
            if (transaction.Id != run.TransactionId || transaction.RunId != run.RunId || transaction.ScopeId != run.ScopeId || !IsHash(transaction.PlanHash) ||
                !IsHash(transaction.ReviewHash) || !transaction.ExpectedRefs.SequenceEqual(expectedRefs, StringComparer.Ordinal) ||
                !transaction.ExpectedArtifacts.SequenceEqual([".materialization-transaction", "PLAN.md", "PLAN-REVIEW-LOG.md", "state.json"],
                                                             StringComparer.Ordinal) ||
                !DateTimeOffset.TryParse(transaction.Timestamp, out _)) throw Unsupported();
            if (run.Phase == PendingRunPhase.Materializing && (transaction.PlanText is null || transaction.PlanText != CanonicalText.Canonicalize(transaction.PlanText) ||
                                                               Hashing.Sha256Hex(transaction.PlanText) != transaction.PlanHash)) throw Unsupported();
            if (run.Phase == PendingRunPhase.Consumed && transaction.PlanText is not null) throw Unsupported();
            if (transaction.StateHash is { } stateHash && !IsHash(stateHash)) throw Unsupported();
            if (host == HostKind.Claude && (!SameSelection(transaction.ReviewerSelection, run.ReviewerSelection) ||
                                            !SameSelection(transaction.BuilderSelection, run.BuilderSelection))) throw Unsupported();
        }
    }

    private static bool ValidSelection(ProviderSelection selection)
    {
        try
        {
            var valid = ProviderSelections.Validate(selection.Provider, selection.RequestedModel, selection.ResolvedModel,
                                                    selection.ModelsUsed, selection.Effort);
            return SameSelection(valid, selection);
        }
        catch (CliFailure) { return false; }
    }

    internal static bool SameSelection(ProviderSelection? left, ProviderSelection? right)
        => left is not null && right is not null && left.Provider == right.Provider && left.RequestedModel == right.RequestedModel &&
           left.ResolvedModel == right.ResolvedModel && left.Effort == right.Effort &&
           left.ModelsUsed.SequenceEqual(right.ModelsUsed, StringComparer.Ordinal);

    private static void ValidateWaiver(CursorModelWaiver waiver, string role)
    {
        if (!IsValidWaiver(waiver, role)) throw new CliFailure("usage", "Cursor model waiver is invalid");
    }

    private static bool IsValidWaiver(CursorModelWaiver waiver, string role)
    {
        var versionText = waiver.CursorVersion.Split(['-', '+'], 2)[0];
        return waiver.Role == role && !string.IsNullOrWhiteSpace(waiver.Model) && !string.IsNullOrWhiteSpace(waiver.Effort) && waiver.Model.Length <= 256 &&
               waiver.Effort.Length <= 256 && Version.TryParse(versionText, out var version) && version >= Version.Parse(MinimumCursorVersion) &&
               waiver.Observed is "Auto" or "unavailable" && !string.IsNullOrWhiteSpace(waiver.Consent) && waiver.Consent.Length <= 512 &&
               waiver.ModelGuarantee == "waived" && DateTimeOffset.TryParse(waiver.Timestamp, out _);
    }

    internal static void Fault(string point)
    {
#if DEBUG
        if (string.Equals(Environment.GetEnvironmentVariable("FORGE_FAULT_POINT"), point, StringComparison.Ordinal))
            throw new CliFailure("state", $"injected fault: {point}", 3);
#endif
    }

    internal static IReadOnlyList<string> AuditLegacyArtifacts(RepositoryIdentity repository, HostKind host)
    {
        var directory = Path.GetDirectoryName(PathFor(repository, "legacy", host))!;
        if (!Directory.Exists(directory)) return [];
        OwnershipGuards.EnsureSafeDirectory(directory);
        var owned = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            OwnershipGuards.EnsureRegularFile(path, "legacy pending run");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 3 ||
                !root.TryGetProperty("host", out var storedHost) || storedHost.GetString() != host.ToString().ToLowerInvariant() ||
                !root.TryGetProperty("workspace", out var workspace) ||
                !string.Equals(workspace.GetString(), repository.WorkspaceRoot, WorkspacePathPolicy.Comparison) ||
                !root.TryGetProperty("scopeId", out var scope) || scope.GetString() != RepositoryPaths.ScopeId(repository) ||
                !root.TryGetProperty("runId", out var runId) || !SafeIdentity.IsMatch(runId.GetString() ?? string.Empty) ||
                !string.Equals(path, PathFor(repository, runId.GetString()!, host), WorkspacePathPolicy.Comparison))
                throw new CliFailure("state", "refusing to remove an unowned legacy pending artifact", 3);
            owned.Add(path);
        }
        return owned;
    }
}
