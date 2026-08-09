using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
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
internal sealed record MaterializationTransaction(string Id, string RunId, string ScopeId, string? PlanText, string PlanHash, string ReviewHash, string ReviewerModel, string ReviewerEffort, string BuilderModel, string BuilderEffort, string[] ExpectedRefs, string[] ExpectedArtifacts, string Timestamp, string? StateHash = null, string? StatePhase = null, string? SuccessorStateHash = null, string? SuccessorStatePhase = null);
internal sealed record PendingRun
{
    public const int SchemaVersion = 2;
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

internal static class PendingRuns
{
    private const string MinimumCursorVersion = "3.15.6";
    private static readonly Regex SafeIdentity = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    public static string PathFor(RepositoryIdentity repository, string runId) => Path.Combine(DataRoot(), "cursor-runs", RepositoryPaths.ScopeId(repository), Hashing.Sha256Hex(runId) + ".json");
    public static PendingRun Stage(RepositoryIdentity repository, string draftText, string runId, CursorModelWaiver? reviewerWaiver = null, bool acceptRisk = false, string? authorizationNote = null)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        ValidateRunId(runId);
        if (string.IsNullOrWhiteSpace(draftText)) throw new CliFailure("usage", "Cursor chat plan is required on stdin");
        var text = CanonicalText.NormalizePlan(draftText);
        if (SensitiveInput.IsSensitiveContent(text)) throw new CliFailure("usage", "Cursor chat plan contains withheld sensitive content");
        var scope = RepositoryPaths.ScopeId(repository);
        EnsureNoOtherActiveRun(repository, runId);
        var existing = TryLoad(repository, runId);
        if (existing is not null && existing.Phase == PendingRunPhase.RevisionRequired)
        {
            var reviewCap = existing.ReviewCap;
            var capAudits = existing.CapAudits;
            if (existing.ReviewRound >= existing.ReviewCap)
            {
                if (!acceptRisk || string.IsNullOrWhiteSpace(authorizationNote) || authorizationNote.Length > 16 * 1024) throw new CliFailure("state", "Cursor review cap reached; /forge resume requires --accept-risk with a bounded --authorization-note", 3);
                reviewCap++;
                capAudits = [.. existing.CapAudits, new CursorCapAudit(existing.ReviewCap, reviewCap, authorizationNote, DateTimeOffset.UtcNow.ToString("O"))];
            }
            else if (acceptRisk || authorizationNote is not null) throw new CliFailure("usage", "Cursor review cap authorization is only valid after the cap is reached");
            var restaged = existing with { DraftText = text, Phase = PendingRunPhase.Reviewing, ReviewCap = reviewCap, CapAudits = capAudits, ActiveDispatchId = null, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
            Write(repository, restaged); return restaged;
        }
        if (existing is not null && existing.Phase is not (PendingRunPhase.Abandoned or PendingRunPhase.Consumed) && existing.DraftText == text) return existing;
        if (existing is not null && existing.Phase is not (PendingRunPhase.Abandoned or PendingRunPhase.Consumed)) throw new CliFailure("state", "Cursor plan changed; finalize or abandon the existing run first", 3);
        if (reviewerWaiver is null) throw new CliFailure("usage", "Cursor stage requires a reviewer model waiver");
        ValidateWaiver(reviewerWaiver, "reviewer");
        var run = new PendingRun { Workspace = repository.WorkspaceRoot, ScopeId = scope, RunId = runId, DraftText = text, ReviewerWaiver = reviewerWaiver };
        Write(repository, run);
        return run;
    }
    public static PendingRun Load(RepositoryIdentity repository, string runId) => TryLoad(repository, runId) ?? throw new CliFailure("state", "no Cursor pending run exists; return to Cursor Plan Mode and start a new /forge run", 3);
    public static PendingRun Record(RepositoryIdentity repository, string runId, string dispatchId, string stage, string response)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        ValidateIdentity(dispatchId, "--dispatch-id");
        if (stage != "plan" || run.Phase != PendingRunPhase.Reviewing || run.ReviewerWaiver is null) throw new CliFailure("state", "Cursor review response is not legal", 3);
        if (run.Responses.Any(item => item.DispatchId == dispatchId)) throw new CliFailure("state", "Cursor reviewer dispatch identity must be fresh for every round", 3);
        var normalized = Canonical(response);
        if (Encoding.UTF8.GetByteCount(normalized) > 256 * 1024) throw new CliFailure("usage", "review response exceeds the size bound");
        var verdictLines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("VERDICT:", StringComparison.Ordinal)).ToArray();
        if (verdictLines.Length != 1 || verdictLines[0] is not ("VERDICT: APPROVED" or "VERDICT: REVISE")) throw new CliFailure("usage", "review response must contain exactly one terminal VERDICT line");
        if (!string.Equals(normalized.TrimEnd('\n').Split('\n')[^1], verdictLines[0], StringComparison.Ordinal)) throw new CliFailure("usage", "review response verdict must be terminal");
        var coverage = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("COVERAGE:", StringComparison.Ordinal)).ToArray();
        if (coverage.Length != 0) throw new CliFailure("usage", "plan review response must not contain a COVERAGE line");
        var verdict = verdictLines[0][9..];
        if (run.ReviewRound >= run.ReviewCap) throw new CliFailure("state", "Cursor review cap reached", 3);
        var reviewLog = CanonicalText.NormalizeReviewLog(string.Join("\n", run.Responses.Select(value => value.Response).Append(normalized)));
        if (SensitiveInput.IsSensitiveContent(reviewLog)) throw new CliFailure("usage", "review log contains withheld sensitive content");
        var next = run with { Phase = verdict == "APPROVED" ? PendingRunPhase.ReviewApproved : PendingRunPhase.RevisionRequired, ReviewRound = run.ReviewRound + 1, ActiveDispatchId = dispatchId, Responses = [.. run.Responses, new(dispatchId, stage, verdict, Hashing.Sha256Hex(normalized), normalized, DateTimeOffset.UtcNow.ToString("O"))], UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, next); return next;
    }
    public static PendingRun RecordCodeEvidence(RepositoryIdentity repository, string dispatchId, string stage, string response)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        ValidateIdentity(dispatchId, "--dispatch-id");
        var (normalized, verdict, coverage) = ParseCodeResponse(response);
        var run = ResolveMaterializedDispatch(repository, dispatchId, stage);
        if (run.Responses.Any(item => item.DispatchId == dispatchId)) throw new CliFailure("state", "Cursor code review response has already been recorded for this dispatch", 3);
        var forge = Path.Combine(repository.WorkspaceRoot, ".forge");
        var statePath = StateStore.StatePath(repository.WorkspaceRoot);
        if (!File.Exists(statePath)) throw new CliFailure("state", "Cursor code evidence requires matching materialized state", 3);
        using var workspaceLock = ForgeStateLock.Acquire(repository.WorkspaceRoot);
        var state = StateStore.Load(repository.WorkspaceRoot);
        if (state.Host != HostKind.Cursor || !state.Dispatch.Pending || state.Dispatch.Id != dispatchId || state.Dispatch.Stage?.ToWireName() != stage || state.SourceRun?.TransactionId != run.TransactionId) throw new CliFailure("state", "Cursor code evidence dispatch does not match materialized state", 3);
        var evidence = Path.Combine(forge, $"cursor-review-{dispatchId}.md");
        DurableFiles.WriteAtomic(evidence, normalized);
        DurableFiles.WriteJson(evidence + ".json", new ReviewDecision(verdict, coverage), ForgeJsonContext.Default.ReviewDecision);
        var next = run with { ActiveDispatchId = dispatchId, Responses = [.. run.Responses, new(dispatchId, stage, verdict, Hashing.Sha256Hex(normalized), normalized, DateTimeOffset.UtcNow.ToString("O"))], UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, next); return next;
    }
    public static PendingRun Finalize(RepositoryIdentity repository, string runId, CursorModelWaiver? builderWaiver = null)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        if (run.Phase != PendingRunPhase.ReviewApproved || builderWaiver is null) throw new CliFailure("state", "Cursor plan is not review-approved or builder waiver is missing", 3);
        var reviewerWaiver = run.ReviewerWaiver ?? throw new CliFailure("state", "Cursor reviewer waiver is missing", 3);
        ValidateWaiver(builderWaiver, "builder");
        var draft = run.DraftText ?? throw new CliFailure("state", "Cursor reviewed chat plan is unavailable; abandon and restart the run", 3);
        CanonicalText.ParseTasks(draft);
        ModelSelections.Validate("reviewer", reviewerWaiver.Model, reviewerWaiver.Effort);
        ModelSelections.Validate("builder", builderWaiver.Model, builderWaiver.Effort);
        if (SensitiveInput.IsSensitiveContent(draft)) throw new CliFailure("usage", "plan contains withheld sensitive content");
        var reviewLog = CanonicalText.NormalizeReviewLog(string.Join("\n", run.Responses.Select(response => response.Response)));
        if (SensitiveInput.IsSensitiveContent(reviewLog)) throw new CliFailure("usage", "review log contains withheld sensitive content");
        var next = run with { Phase = PendingRunPhase.Ready, DraftText = null, BuilderWaiver = builderWaiver, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }; Write(repository, next); return next;
    }
    public static PendingRun Abandon(RepositoryIdentity repository, string runId) { using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor); var current = Load(repository, runId); if (current.Phase is PendingRunPhase.Materializing or PendingRunPhase.Consumed) throw new CliFailure("state", "Cursor materializing or consumed runs cannot be abandoned", 3); var run = current with { Phase = PendingRunPhase.Abandoned, DraftText = null, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }; Write(repository, run); return run; }
    public static PendingRun Invalidate(RepositoryIdentity repository, string runId, string reason) { using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor); if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new CliFailure("usage", "--reason is required and bounded"); var current = Load(repository, runId); if (current.Phase is PendingRunPhase.Ready or PendingRunPhase.Materializing or PendingRunPhase.Consumed or PendingRunPhase.Abandoned) throw new CliFailure("state", "Cursor run cannot be invalidated in its current phase", 3); var run = current with { Phase = PendingRunPhase.RevisionRequired, ActiveDispatchId = null, InvalidationReason = reason, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }; Write(repository, run); return run; }
    public static PendingRun BeginMaterialize(RepositoryIdentity repository, string runId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        if (run.Phase is PendingRunPhase.Consumed or PendingRunPhase.Materializing) return run;
        if (run.Phase != PendingRunPhase.Ready || run.ReviewerWaiver is null || run.BuilderWaiver is null) throw new CliFailure("state", "Cursor plan is not ready for materialization", 3);
        var plan = DiscoverNativePlan(repository, run);
        var reviewer = ModelSelections.Validate("reviewer", run.ReviewerWaiver.Model, run.ReviewerWaiver.Effort);
        var builder = ModelSelections.Validate("builder", run.BuilderWaiver.Model, run.BuilderWaiver.Effort);
        var id = Hashing.Nonce();
        var review = CanonicalText.NormalizeReviewLog(string.Join("\n", run.Responses.Select(response => response.Response)));
        var refs = new[] { $"refs/plan-forge/{run.ScopeId}/owner", $"refs/plan-forge/{run.ScopeId}/head-base", $"refs/plan-forge/{run.ScopeId}/worktree-base" };
        var transaction = new MaterializationTransaction(id, run.RunId, run.ScopeId, plan, Hashing.Sha256Hex(plan), Hashing.Sha256Hex(review), reviewer.Model, reviewer.Effort, builder.Model, builder.Effort, refs, [".materialization-transaction", "PLAN.md", "PLAN-REVIEW-LOG.md", "state.json"], DateTimeOffset.UtcNow.ToString("O"));
        var next = run with { Phase = PendingRunPhase.Materializing, TransactionId = id, Materialization = transaction, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, next); Fault("snapshot-written"); return next;
    }
    public static PendingRun JournalMaterializationSuccessor(RepositoryIdentity repository, string runId, ForgeState successor, bool repositoryLockHeld = false)
    {
        using RepositoryRunLock? repositoryLock = repositoryLockHeld ? null : RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        if (run.Phase != PendingRunPhase.Materializing || run.Materialization is null) throw new CliFailure("state", "Cursor materialization state cannot be bound in the current phase", 3);
        if (successor.Workflow.Phase is not (ForgePhase.Materialized or ForgePhase.Locked or ForgePhase.Build)) throw new CliFailure("state", "Cursor materialization successor phase cannot be journaled", 3);
        var serialized = JsonSerializer.Serialize(successor, ForgeJsonContext.Default.ForgeState) + "\n";
        var transaction = run.Materialization with { SuccessorStateHash = Hashing.Sha256Hex(serialized), SuccessorStatePhase = successor.Workflow.Phase.ToWireName() };
        var next = run with { Materialization = transaction, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        Write(repository, next);
        return next;
    }
    public static PendingRun ReconcileMaterializationState(RepositoryIdentity repository, string runId, bool repositoryLockHeld = false)
    {
        using RepositoryRunLock? repositoryLock = repositoryLockHeld ? null : RepositoryRunLock.Acquire(repository, HostKind.Cursor);
        var run = Load(repository, runId);
        var transaction = run.Materialization ?? throw new CliFailure("state", "Cursor materialization transaction is missing", 3);
        var state = StateStore.Load(repository.WorkspaceRoot); var hash = Hashing.Sha256File(StateStore.StatePath(repository.WorkspaceRoot)); var phase = state.Workflow.Phase.ToWireName();
        if (hash == transaction.StateHash && phase == transaction.StatePhase)
        {
            if (transaction.SuccessorStateHash is null && transaction.SuccessorStatePhase is null) return run;
            var rolledBack = transaction with { SuccessorStateHash = null, SuccessorStatePhase = null };
            var unchanged = run with { Materialization = rolledBack, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }; Write(repository, unchanged); return unchanged;
        }
        if (hash != transaction.SuccessorStateHash || phase != transaction.SuccessorStatePhase) throw new CliFailure("state", "Cursor materialization state artifact is not an expected journal state", 3);
        var reconciled = transaction with { StateHash = hash, StatePhase = phase, SuccessorStateHash = null, SuccessorStatePhase = null };
        var next = run with { Materialization = reconciled, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }; Write(repository, next); return next;
    }
    public static PendingRun Consume(RepositoryIdentity repository, string runId, bool repositoryLockHeld = false) { using RepositoryRunLock? repositoryLock = repositoryLockHeld ? null : RepositoryRunLock.Acquire(repository, HostKind.Cursor); var run = Load(repository, runId); var transaction = run.Materialization is null ? null : run.Materialization with { PlanText = null }; var next = run with { Phase = PendingRunPhase.Consumed, Materialization = transaction, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }; Write(repository, next); return next; }
    public static void VerifyMaterialization(RepositoryIdentity repository, PendingRun run, bool requireComplete = true)
    {
        var transaction = run.Materialization ?? throw new CliFailure("state", "Cursor materialization transaction is missing", 3);
        var forge = Path.Combine(repository.WorkspaceRoot, ".forge");
        var state = StateStore.Load(repository.WorkspaceRoot);
        if (state.Host != HostKind.Cursor || state.RepositoryScopeId != transaction.ScopeId || state.SourceRun?.Source != "cursor:" + transaction.RunId || state.SourceRun.TransactionId != transaction.Id || state.Models.Reviewer is not { } reviewer || reviewer.Model != transaction.ReviewerModel || reviewer.Effort != transaction.ReviewerEffort || state.Models.Builder is not { } builder || builder.Model != transaction.BuilderModel || builder.Effort != transaction.BuilderEffort || state.ReviewerGuarantee?.Kind != "advisory" || state.ApprovalGuarantee?.Kind != "advisory") throw new CliFailure("state", "Cursor materialization state does not match its transaction", 3);
        if (run.Phase == PendingRunPhase.Materializing)
        {
            if (transaction.StateHash is null || transaction.StatePhase != state.Workflow.Phase.ToWireName() || Hashing.Sha256File(StateStore.StatePath(repository.WorkspaceRoot)) != transaction.StateHash || transaction.SuccessorStateHash is not null || transaction.SuccessorStatePhase is not null) throw new CliFailure("state", "Cursor materialization state artifact is not bound to its transaction", 3);
            var actualArtifacts = Directory.EnumerateFileSystemEntries(forge).Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var expectedArtifacts = transaction.ExpectedArtifacts.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!actualArtifacts.SequenceEqual(expectedArtifacts, StringComparer.Ordinal)) throw new CliFailure("state", "Cursor materialization directory contains conflicting artifacts", 3);
        }
        foreach (var artifact in transaction.ExpectedArtifacts)
        {
            var path = Path.Combine(forge, artifact);
            if (!File.Exists(path)) throw new CliFailure("state", $"Cursor materialization artifact is missing: {artifact}", 3);
        }
        if (File.ReadAllText(Path.Combine(forge, ".materialization-transaction")) != transaction.Id + "\n") throw new CliFailure("state", "Cursor materialization marker does not match its transaction", 3);
        if (Hashing.Sha256Hex(File.ReadAllText(Path.Combine(forge, "PLAN.md"))) != transaction.PlanHash || Hashing.Sha256Hex(File.ReadAllText(Path.Combine(forge, "PLAN-REVIEW-LOG.md"))) != transaction.ReviewHash) throw new CliFailure("state", "Cursor materialization artifacts do not match their transaction", 3);
        if (!requireComplete) return;
        if (state.Workflow.Phase != ForgePhase.Build) throw new CliFailure("state", "Cursor materialization did not reach build phase", 3);
        foreach (var reference in transaction.ExpectedRefs)
        {
            var result = new GitClient(repository.WorkspaceRoot).Run(["rev-parse", "--verify", reference]);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout)) throw new CliFailure("state", $"Cursor materialization ref is missing: {reference}", 3);
            var expected = reference.EndsWith("/worktree-base", StringComparison.Ordinal) ? state.Baselines.Worktree : state.Baselines.Head;
            if (!string.Equals(result.Stdout.Trim(), expected, StringComparison.Ordinal)) throw new CliFailure("state", $"Cursor materialization ref conflicts: {reference}", 3);
        }
        Materializer.VerifyManagedExclude(repository);
    }
    private static PendingRun? TryLoad(RepositoryIdentity repository, string runId)
    {
        var path = PathFor(repository, runId); if (!File.Exists(path)) return null;
        try { var run = Deserialize(File.ReadAllText(path)); ValidateLoaded(repository, run); if (run.RunId != runId) throw new CliFailure("unsupported-state-schema", "Cursor pending run identity is invalid", 3); return run; }
        catch (CliFailure) { throw; } catch (Exception error) { throw new CliFailure("unsupported-state-schema", error.Message, 3); }
    }
    private static void Write(RepositoryIdentity repository, PendingRun run) { var path = PathFor(repository, run.RunId); OwnershipGuards.EnsureDirectory(System.IO.Path.GetDirectoryName(path)!); DurableFiles.WriteJson(path, run, ForgeJsonContext.Default.PendingRun); }
    private static string DiscoverNativePlan(RepositoryIdentity repository, PendingRun run)
    {
        var cursorHome = Environment.GetEnvironmentVariable("CURSOR_HOME");
        var nativeRoot = Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(cursorHome) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor") : cursorHome, "plans"));
        var roots = new List<string> { nativeRoot };
#if DEBUG
        roots.Add(repository.WorkspaceRoot);
#endif
        var runMarker = $"<!-- plan-forge-flow:run={run.RunId};";
        var matches = new List<string>();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var root in roots.Distinct(pathComparer))
        {
            if (!Directory.Exists(root)) continue;
            OwnershipGuards.EnsureSafeDirectory(root);
            foreach (var candidate in Directory.EnumerateFiles(root, "*.plan.md", SearchOption.TopDirectoryOnly))
            {
                var path = Path.GetFullPath(candidate);
                if (!IsUnder(path, root)) throw new CliFailure("state", "Cursor native plan path escaped its storage root", 3);
                OwnershipGuards.EnsureRegularFile(path, "Cursor native plan");
                var oversized = new FileInfo(path).Length > 256 * 1024;
                var text = oversized ? ReadPrefix(path, 256 * 1024) : File.ReadAllText(path, Encoding.UTF8);
                if (!text.Contains(runMarker, StringComparison.Ordinal)) continue;
                if (oversized) throw new CliFailure("usage", "Cursor native plan exceeds the size bound");
                matches.Add(CanonicalText.NormalizePlan(text));
            }
        }
        if (matches.Count != 1) throw new CliFailure("state", matches.Count == 0 ? "no Cursor native plan matches this run and workspace marker" : "multiple Cursor native plans match this run and workspace marker", 3);
        var plan = matches[0];
        var marker = $"<!-- plan-forge-flow:run={run.RunId};workspace={run.ScopeId} -->";
        var preamble = ExpectedPreamble(repository, run.RunId);
        if (Count(plan, "<!-- plan-forge-flow:run=") != 1 || Count(plan, marker) != 1) throw new CliFailure("state", "Cursor native plan marker is missing or invalid", 3);
        if (Count(plan, preamble) != 1 || Count(plan, marker + "\n" + preamble) != 1) throw new CliFailure("state", "Cursor native plan preamble is missing, duplicated, misplaced, or invalid", 3);
        if (SensitiveInput.IsSensitiveContent(plan)) throw new CliFailure("usage", "Cursor native plan contains withheld sensitive content");
        CanonicalText.ParseTasks(plan);
        return plan;
    }
    internal static string ExpectedPreamble(RepositoryIdentity repository, string runId)
        => $"> **Plan Forge execution gate (advisory):** Before any repository write, run Plan Forge `{ExpectedMaterializeCommand(repository, runId)}` using the installed launcher. Stop if it does not succeed. Cursor can bypass this instruction; approval enforcement is advisory.";
    private static string ExpectedMaterializeCommand(RepositoryIdentity repository, string runId)
        => $"plan materialize --host cursor --workspace \"{repository.WorkspaceRoot}\" --run-id \"{runId}\"";
    private static bool IsUnder(string path, string root) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, WorkspacePathPolicy.Comparison);
    private static string ReadPrefix(string path, int maxChars) { using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true); var buffer = new char[maxChars]; var count = reader.ReadBlock(buffer, 0, buffer.Length); return new string(buffer, 0, count); }
    private static string Canonical(string text) => (text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n') + "\n";
    private static int Count(string value, string needle) => value.Split(needle, StringSplitOptions.None).Length - 1;
    private static void ValidateRunId(string runId) => ValidateIdentity(runId, "--run-id");
    private static void ValidateIdentity(string value, string option)
    {
        if (!SafeIdentity.IsMatch(value)) throw new CliFailure("usage", $"{option} is malformed");
    }
    private static string DataRoot()
    {
        if (Environment.GetEnvironmentVariable("FORGE_PLUGIN_DATA") is { Length: > 0 } value) return Path.GetFullPath(value);
        var cursorHome = Environment.GetEnvironmentVariable("CURSOR_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(cursorHome) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor") : Path.GetFullPath(cursorHome), "plugin-data", "plan-forge-flow");
    }
    private static void EnsureNoOtherActiveRun(RepositoryIdentity repository, string runId)
    {
        foreach (var candidate in LoadAll(repository))
        {
            if (candidate.RunId != runId && candidate.Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned)) throw new CliFailure("state", "another Cursor pending run is active for this workspace", 3);
        }
    }
    public static PendingRun ResolveActive(RepositoryIdentity repository, string dispatchId)
    {
        ValidateIdentity(dispatchId, "--dispatch-id");
        var matches = LoadAll(repository).Where(run => run.ActiveDispatchId == dispatchId).ToArray();
        return matches.Length == 1 ? matches[0] : throw new CliFailure("state", "Cursor dispatch does not resolve to one active run", 3);
    }
    public static PendingRun ResolvePlanDispatch(RepositoryIdentity repository, string dispatchId)
    {
        ValidateIdentity(dispatchId, "--dispatch-id");
        var matches = LoadAll(repository).Where(run => run.Phase == PendingRunPhase.Reviewing && (run.ActiveDispatchId is null || run.ActiveDispatchId == dispatchId)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new CliFailure("state", "Cursor dispatch does not resolve to one reviewing run", 3);
    }
    private static PendingRun ResolveMaterializedDispatch(RepositoryIdentity repository, string dispatchId, string stage) { var state = StateStore.Load(repository.WorkspaceRoot); if (state.Host != HostKind.Cursor || !state.Dispatch.Pending || state.Dispatch.Id != dispatchId || state.Dispatch.Stage?.ToWireName() != stage || state.SourceRun is not { Source: var source } identity || !source.StartsWith("cursor:", StringComparison.Ordinal)) throw new CliFailure("state", "Cursor dispatch does not match materialized state", 3); var run = Load(repository, source[7..]); return run.Phase == PendingRunPhase.Consumed && run.TransactionId == identity.TransactionId ? run : throw new CliFailure("state", "Cursor run is not consumed", 3); }
    private static (string Text, string Verdict, string Coverage) ParseCodeResponse(string response) { var normalized = Canonical(response); if (Encoding.UTF8.GetByteCount(normalized) > 512 * 1024) throw new CliFailure("usage", "review response exceeds the size bound"); var verdicts = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("VERDICT:", StringComparison.Ordinal)).ToArray(); var coverage = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("COVERAGE:", StringComparison.Ordinal)).ToArray(); if (verdicts.Length != 1 || verdicts[0] is not ("VERDICT: APPROVED" or "VERDICT: REVISE") || coverage.Length != 1 || coverage[0] is not ("COVERAGE: FULL" or "COVERAGE: PARTIAL") || normalized.TrimEnd('\n').Split('\n')[^1] != verdicts[0]) throw new CliFailure("usage", "code review response has invalid terminal evidence"); return (normalized, verdicts[0][9..], coverage[0][10..]); }
    public static PendingRun? Status(RepositoryIdentity repository) { var runs = LoadAll(repository).OrderByDescending(run => run.UpdatedAt, StringComparer.Ordinal).ToArray(); if (runs.Length > 1 && runs[0].Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned) && runs[1].Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned)) throw new CliFailure("state", "Cursor status is ambiguous", 3); return runs.FirstOrDefault(); }
    private static IReadOnlyList<PendingRun> LoadAll(RepositoryIdentity repository)
    {
        var directory = Path.GetDirectoryName(PathFor(repository, "x"))!;
        if (!Directory.Exists(directory)) return [];
        var runs = new List<PendingRun>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            PendingRun candidate;
            try { candidate = Deserialize(File.ReadAllText(file)); }
            catch (Exception error) { throw new CliFailure("unsupported-state-schema", error.Message, 3); }
            ValidateLoaded(repository, candidate);
            if (!string.Equals(file, PathFor(repository, candidate.RunId), WorkspacePathPolicy.Comparison)) throw new CliFailure("unsupported-state-schema", "Cursor pending run path is invalid", 3);
            runs.Add(candidate);
        }
        return runs;
    }
    private static PendingRun Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != PendingRun.SchemaVersion || !root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String || host.GetString() != "cursor") throw new CliFailure("unsupported-state-schema", "Cursor pending run is malformed or unsupported", 3);
        return JsonSerializer.Deserialize(json, ForgeJsonContext.Default.PendingRun) ?? throw new JsonException();
    }
    private static void ValidateLoaded(RepositoryIdentity repository, PendingRun run)
    {
        static CliFailure Unsupported() => new("unsupported-state-schema", "Cursor pending run is malformed or unsupported", 3);
        static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
        if (run.SchemaVersionValue != PendingRun.SchemaVersion || run.Host != HostKind.Cursor || !string.Equals(run.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison) || run.ScopeId != RepositoryPaths.ScopeId(repository) || !SafeIdentity.IsMatch(run.RunId)) throw Unsupported();
        if (!Enum.IsDefined(run.Phase) || run.ReviewCap < 1 || run.ReviewRound < 0 || run.ReviewRound > run.ReviewCap || run.Responses.Count(response => response.Stage == "plan") != run.ReviewRound) throw Unsupported();
        var retainsDraft = run.Phase is PendingRunPhase.Reviewing or PendingRunPhase.RevisionRequired or PendingRunPhase.ReviewApproved;
        if (retainsDraft != (run.DraftText is not null) || run.DraftText is { } draft && draft != Canonical(draft)) throw Unsupported();
        if (!DateTimeOffset.TryParse(run.CreatedAt, out _) || !DateTimeOffset.TryParse(run.UpdatedAt, out _) || run.ReviewerGuarantee != "advisory" || run.ApprovalGuarantee != "advisory" || run.InvalidationReason is { Length: > 512 }) throw Unsupported();
        if (run.ReviewerWaiver is null || !IsValidWaiver(run.ReviewerWaiver, "reviewer")) throw Unsupported();
        if ((run.Phase is PendingRunPhase.Ready or PendingRunPhase.Materializing or PendingRunPhase.Consumed) && (run.BuilderWaiver is null || !IsValidWaiver(run.BuilderWaiver, "builder"))) throw Unsupported();
        if (run.ActiveDispatchId is { } activeDispatchId && !SafeIdentity.IsMatch(activeDispatchId)) throw Unsupported();
        foreach (var response in run.Responses)
        {
            if (!SafeIdentity.IsMatch(response.DispatchId) || response.Stage is not ("plan" or "code" or "fix-review") || response.Verdict is not ("APPROVED" or "REVISE") || response.Response != Canonical(response.Response) || response.Hash != Hashing.Sha256Hex(response.Response) || !DateTimeOffset.TryParse(response.Timestamp, out _)) throw Unsupported();
        }
        var expectedCap = 5;
        foreach (var audit in run.CapAudits)
        {
            if (audit.PreviousCap != expectedCap || audit.NewCap != expectedCap + 1 || string.IsNullOrWhiteSpace(audit.AuthorizationNote) || audit.AuthorizationNote.Length > 16 * 1024 || !DateTimeOffset.TryParse(audit.Timestamp, out _)) throw Unsupported();
            expectedCap = audit.NewCap;
        }
        if (run.ReviewCap != expectedCap) throw Unsupported();
        var hasTransaction = run.TransactionId is not null && run.Materialization is not null;
        if ((run.TransactionId is null) != (run.Materialization is null) || (run.Phase is PendingRunPhase.Materializing or PendingRunPhase.Consumed) != hasTransaction) throw Unsupported();
        if (run.Materialization is { } transaction)
        {
            var expectedRefs = new[] { $"refs/plan-forge/{run.ScopeId}/owner", $"refs/plan-forge/{run.ScopeId}/head-base", $"refs/plan-forge/{run.ScopeId}/worktree-base" };
            if (transaction.Id != run.TransactionId || transaction.RunId != run.RunId || transaction.ScopeId != run.ScopeId || !IsHash(transaction.PlanHash) || !IsHash(transaction.ReviewHash) || !transaction.ExpectedRefs.SequenceEqual(expectedRefs, StringComparer.Ordinal) || !transaction.ExpectedArtifacts.SequenceEqual([".materialization-transaction", "PLAN.md", "PLAN-REVIEW-LOG.md", "state.json"], StringComparer.Ordinal) || !DateTimeOffset.TryParse(transaction.Timestamp, out _)) throw Unsupported();
            if (run.Phase == PendingRunPhase.Materializing && (transaction.PlanText is null || transaction.PlanText != Canonical(transaction.PlanText) || Hashing.Sha256Hex(transaction.PlanText) != transaction.PlanHash)) throw Unsupported();
            if (run.Phase == PendingRunPhase.Consumed && transaction.PlanText is not null) throw Unsupported();
            if ((transaction.StateHash is null) != (transaction.StatePhase is null) || transaction.StateHash is { } stateHash && (stateHash.Length != 64 || stateHash.Any(value => !Uri.IsHexDigit(value))) || transaction.StatePhase is { } statePhase && statePhase is not ("materialized" or "locked" or "build")) throw Unsupported();
            if ((transaction.SuccessorStateHash is null) != (transaction.SuccessorStatePhase is null) || transaction.SuccessorStateHash is { } successorHash && (successorHash.Length != 64 || successorHash.Any(value => !Uri.IsHexDigit(value))) || transaction.SuccessorStatePhase is { } successorPhase && successorPhase is not ("materialized" or "locked" or "build")) throw Unsupported();
        }
    }
    private static void ValidateWaiver(CursorModelWaiver waiver, string role)
    {
        if (!IsValidWaiver(waiver, role)) throw new CliFailure("usage", "Cursor model waiver is invalid");
    }
    private static bool IsValidWaiver(CursorModelWaiver waiver, string role)
    {
        var versionText = waiver.CursorVersion.Split(['-', '+'], 2)[0];
        return waiver.Role == role && !string.IsNullOrWhiteSpace(waiver.Model) && !string.IsNullOrWhiteSpace(waiver.Effort) && waiver.Model.Length <= 256 && waiver.Effort.Length <= 256 && Version.TryParse(versionText, out var version) && version >= Version.Parse(MinimumCursorVersion) && waiver.Observed is "Auto" or "unavailable" && !string.IsNullOrWhiteSpace(waiver.Consent) && waiver.Consent.Length <= 512 && waiver.ModelGuarantee == "waived" && DateTimeOffset.TryParse(waiver.Timestamp, out _);
    }
    internal static void Fault(string point) { if (string.Equals(Environment.GetEnvironmentVariable("FORGE_FAULT_POINT"), point, StringComparison.Ordinal)) throw new CliFailure("state", $"injected fault: {point}", 3); }
}
