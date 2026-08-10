using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Cursor;

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
internal sealed record MaterializationTransaction(string Id, string RunId, string ScopeId, string? PlanText, string PlanHash, string ReviewHash, string ReviewerModel, string ReviewerEffort, string BuilderModel, string BuilderEffort, string[] ExpectedRefs, string[] ExpectedArtifacts, string Timestamp, string? StateHash = null);

internal sealed record PendingRun
{
    public const int SchemaVersion = 3;

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

    public static string PathFor(RepositoryIdentity repository, string runId) =>
        Path.Combine(DataRoot(), "cursor-runs", RepositoryPaths.ScopeId(repository), Hashing.Sha256Hex(runId) + ".json");

    public static PendingRun Load(RepositoryIdentity repository, string runId) => TryLoad(repository, runId) ??
                                                                                  throw new CliFailure("state",
                                                                                                       "no Cursor pending run exists; return to Cursor Plan Mode and start a new /forge run",
                                                                                                       3);

    public static PendingRun? Status(RepositoryIdentity repository)
    {
        var runs = LoadAll(repository).OrderByDescending(run => run.UpdatedAt, StringComparer.Ordinal).ToArray();
        if (runs.Length > 1 && runs[0].Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned) &&
            runs[1].Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned))
            throw new CliFailure("state", "Cursor status is ambiguous", 3);
        return runs.FirstOrDefault();
    }

    internal static void Save(RepositoryIdentity repository, PendingRun run) => Write(repository, run);

    internal static void ValidateIdentity(string value, string option)
    {
        if (!SafeIdentity.IsMatch(value)) throw new CliFailure("usage", $"{option} is malformed");
    }

    private static PendingRun? TryLoad(RepositoryIdentity repository, string runId)
    {
        var path = PathFor(repository, runId);
        if (!File.Exists(path)) return null;
        try
        {
            var run = Deserialize(File.ReadAllText(path));
            ValidateLoaded(repository, run);
            if (run.RunId != runId) throw new CliFailure("unsupported-state-schema", "Cursor pending run identity is invalid", 3);
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
        var path = PathFor(repository, run.RunId);
        OwnershipGuards.EnsureDirectory(Path.GetDirectoryName(path)!);
        DurableFiles.WriteJson(path, run, ForgeJsonContext.Default.PendingRun);
    }

    private static string Canonical(string text) =>
        (text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n') + "\n";

    private static void ValidateRunId(string runId) => ValidateIdentity(runId, "--run-id");

    private static string DataRoot()
    {
        if (Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA") is { Length: > 0 } value) return Path.GetFullPath(value);
        var cursorHome = Environment.GetEnvironmentVariable("CURSOR_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(cursorHome)
                                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor")
                                : Path.GetFullPath(cursorHome),
                            "plugin-data",
                            "plan-forge-flow");
    }

    private static IReadOnlyList<PendingRun> LoadAll(RepositoryIdentity repository)
    {
        var directory = Path.GetDirectoryName(PathFor(repository, "x"))!;
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
            ValidateLoaded(repository, candidate);
            if (!string.Equals(file, PathFor(repository, candidate.RunId), WorkspacePathPolicy.Comparison))
                throw new CliFailure("unsupported-state-schema", "Cursor pending run path is invalid", 3);
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
            host.GetString() != "cursor") throw new CliFailure("unsupported-state-schema", "Cursor pending run is malformed or unsupported", 3);
        return JsonSerializer.Deserialize(json, ForgeJsonContext.Default.PendingRun) ?? throw new JsonException();
    }

    private static void ValidateLoaded(RepositoryIdentity repository, PendingRun run)
    {
        static CliFailure Unsupported() => new("unsupported-state-schema", "Cursor pending run is malformed or unsupported", 3);
        static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
        if (run.SchemaVersionValue != PendingRun.SchemaVersion || run.Host != HostKind.Cursor ||
            !string.Equals(run.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison) || run.ScopeId != RepositoryPaths.ScopeId(repository) ||
            !SafeIdentity.IsMatch(run.RunId)) throw Unsupported();
        if (!Enum.IsDefined(run.Phase) || run.ReviewCap < 1 || run.ReviewRound < 0 || run.ReviewRound > run.ReviewCap ||
            run.Responses.Count(response => response.Stage == "plan") != run.ReviewRound) throw Unsupported();
        var retainsDraft = run.Phase is PendingRunPhase.Reviewing or PendingRunPhase.RevisionRequired or PendingRunPhase.ReviewApproved;
        if (retainsDraft != run.DraftText is not null || run.DraftText is { } draft && draft != Canonical(draft)) throw Unsupported();
        if (!DateTimeOffset.TryParse(run.CreatedAt, out _) || !DateTimeOffset.TryParse(run.UpdatedAt, out _) || run.ReviewerGuarantee != "advisory" ||
            run.ApprovalGuarantee != "advisory" || run.InvalidationReason is { Length: > 512 }) throw Unsupported();
        if (run.ReviewerWaiver is null || !IsValidWaiver(run.ReviewerWaiver, "reviewer")) throw Unsupported();
        if (run.Phase is PendingRunPhase.Ready or PendingRunPhase.Materializing or PendingRunPhase.Consumed &&
            (run.BuilderWaiver is null || !IsValidWaiver(run.BuilderWaiver, "builder"))) throw Unsupported();
        if (run.ActiveDispatchId is { } activeDispatchId && !SafeIdentity.IsMatch(activeDispatchId)) throw Unsupported();
        foreach (var response in run.Responses)
        {
            if (!SafeIdentity.IsMatch(response.DispatchId) || response.Stage is not ("plan" or "code" or "fix-review") ||
                response.Verdict is not ("APPROVED" or "REVISE") || response.Response != Canonical(response.Response) ||
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
            if (run.Phase == PendingRunPhase.Materializing && (transaction.PlanText is null || transaction.PlanText != Canonical(transaction.PlanText) ||
                                                               Hashing.Sha256Hex(transaction.PlanText) != transaction.PlanHash)) throw Unsupported();
            if (run.Phase == PendingRunPhase.Consumed && transaction.PlanText is not null) throw Unsupported();
            if (transaction.StateHash is { } stateHash && !IsHash(stateHash)) throw Unsupported();
        }
    }

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
}
