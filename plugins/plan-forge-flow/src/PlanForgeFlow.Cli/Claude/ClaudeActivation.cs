using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Claude;

internal enum ClaudeActivationLifecycle
{
    [JsonStringEnumMemberName("planning")]
    Planning,
    [JsonStringEnumMemberName("materializing")]
    Materializing,
    [JsonStringEnumMemberName("executing")]
    Executing,
    [JsonStringEnumMemberName("cleaning")]
    Cleaning,
}

internal sealed record ClaudeActivation
{
    public const int SchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersionValue { get; init; } = SchemaVersion;
    public string Workspace { get; init; } = string.Empty;
    public string ScopeId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public ClaudeActivationLifecycle Lifecycle { get; init; } = ClaudeActivationLifecycle.Planning;
    public string? LastStopProgressHash { get; init; }
    public string? LastStopBlockedAt { get; init; }
    public bool CleanupRiskAuthorized { get; init; }
    public string? CleanupAuthorizationNoteHash { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
}

internal static class ClaudeActivations
{
    private static readonly Regex ScopeIdentity = new("^[a-f0-9]{24}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    public static string PathForSession(string sessionId)
    {
        ValidateSessionId(sessionId);
        return Path.Combine(RepositoryPaths.PluginData(HostKind.Claude), "claude-activations", Hashing.Sha256Hex(sessionId) + ".json");
    }

    public static bool ExistsForSession(string sessionId)
    {
        try { return File.Exists(PathForSession(sessionId)); }
        catch (CliFailure) { return false; }
    }

    public static ClaudeActivation Begin(RepositoryIdentity repository, string sessionId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        ValidateSessionId(sessionId);
        var existing = TryLoadForSession(sessionId);
        if (existing is not null)
        {
            RequireMatch(existing, repository, existing.RunId, sessionId);
            if (existing.Lifecycle == ClaudeActivationLifecycle.Planning) return existing;
            throw new CliFailure("state",
                                 $"Claude Forge run {existing.RunId} is already active in lifecycle {LifecycleName(existing.Lifecycle)}; " +
                                 "finish it or use run cleanup --host claude",
                                 3);
        }

        foreach (var candidate in LoadAll())
        {
            if (!MatchesRepository(candidate, repository)) continue;
            var recovery = candidate.Lifecycle == ClaudeActivationLifecycle.Planning
                               ? "run abandon --host claude"
                               : "run cleanup --host claude";
            throw new CliFailure("state",
                                 $"Claude Forge run {candidate.RunId} is already owned by session {candidate.SessionId} " +
                                 $"in lifecycle {LifecycleName(candidate.Lifecycle)}; use {recovery} with explicit takeover authorization",
                                 3);
        }

        var unarmed = PendingRuns.Status(repository, HostKind.Claude);
        if (unarmed is not null && unarmed.Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned))
            throw new CliFailure("unsupported-state-schema",
                                 $"Claude pending run {unarmed.RunId}, session unarmed, phase {PendingRuns.PhaseName(unarmed.Phase)} " +
                                 "predates the activation schema; remove the legacy external run state and start Forge again",
                                 3);

        var now = Timestamp();
        var activation = new ClaudeActivation
        {
            Workspace = repository.WorkspaceRoot,
            ScopeId = RepositoryPaths.ScopeId(repository),
            RunId = "claude-" + Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Write(activation);
        return activation;
    }

    public static ClaudeActivation RequirePlanning(RepositoryIdentity repository, string runId, string sessionId)
    {
        var activation = LoadForSession(sessionId);
        RequireMatch(activation, repository, runId, sessionId);
        if (activation.Lifecycle != ClaudeActivationLifecycle.Planning)
            throw new CliFailure("state", $"Claude Forge planning command requires lifecycle planning (current: {LifecycleName(activation.Lifecycle)})", 3);
        return activation;
    }

    public static ClaudeActivation RequireMaterialization(RepositoryIdentity repository, string runId, string sessionId)
    {
        var activation = LoadForSession(sessionId);
        RequireMatch(activation, repository, runId, sessionId);
        if (activation.Lifecycle is not (ClaudeActivationLifecycle.Planning or ClaudeActivationLifecycle.Materializing or ClaudeActivationLifecycle.Executing))
            throw new CliFailure("state", $"Claude materialization cannot run in lifecycle {LifecycleName(activation.Lifecycle)}", 3);
        return activation;
    }

    public static ClaudeActivation RequireExecution(RepositoryIdentity repository, ForgeState state, string sessionId)
    {
        var activation = LoadForSession(sessionId);
        RequireMatch(activation, repository, activation.RunId, sessionId);
        if (activation.Lifecycle != ClaudeActivationLifecycle.Executing)
            throw new CliFailure("state", $"Claude execution command requires lifecycle executing (current: {LifecycleName(activation.Lifecycle)})", 3);
        RequireExecutionState(activation, repository, state);
        return activation;
    }

    public static void RequireOwnedState(ClaudeActivation activation, RepositoryIdentity repository, ForgeState state)
        => RequireExecutionState(activation, repository, state);

    public static ClaudeActivation BeginMaterialization(RepositoryIdentity repository, string runId, string sessionId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        var activation = LoadForSession(sessionId);
        RequireMatch(activation, repository, runId, sessionId);
        if (activation.Lifecycle == ClaudeActivationLifecycle.Executing) return activation;
        if (activation.Lifecycle == ClaudeActivationLifecycle.Materializing) return activation;
        if (activation.Lifecycle != ClaudeActivationLifecycle.Planning)
            throw new CliFailure("state", $"Claude materialization cannot begin in lifecycle {LifecycleName(activation.Lifecycle)}", 3);
        return Replace(activation, activation with
        {
            Lifecycle = ClaudeActivationLifecycle.Materializing,
            LastStopProgressHash = null,
            LastStopBlockedAt = null,
            UpdatedAt = Timestamp(),
        });
    }

    public static ClaudeActivation CompleteMaterialization(RepositoryIdentity repository, string runId)
    {
        var sessionId = CurrentSessionId();
        var activation = LoadForSession(sessionId);
        RequireMatch(activation, repository, runId, sessionId);
        if (activation.Lifecycle == ClaudeActivationLifecycle.Executing) return activation;
        if (activation.Lifecycle != ClaudeActivationLifecycle.Materializing)
            throw new CliFailure("state", $"Claude materialization cannot complete in lifecycle {LifecycleName(activation.Lifecycle)}", 3);
        return Replace(activation, activation with
        {
            Lifecycle = ClaudeActivationLifecycle.Executing,
            LastStopProgressHash = null,
            LastStopBlockedAt = null,
            UpdatedAt = Timestamp(),
        });
    }

    public static ClaudeActivation RecordStopBlock(ClaudeActivation activation, string progressHash)
    {
        if (!Sha256.IsMatch(progressHash)) throw new CliFailure("usage", "Claude Stop progress hash is invalid");
        return Replace(activation, activation with
        {
            LastStopProgressHash = progressHash,
            LastStopBlockedAt = Timestamp(),
            UpdatedAt = Timestamp(),
        });
    }

    public static ClaudeActivation BeginCleanup(RepositoryIdentity repository,
                                                 string sessionId,
                                                 string? requestedRunId,
                                                 bool acceptRisk,
                                                 string? authorizationNote)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        ValidateSessionId(sessionId);
        if (requestedRunId is not null) PendingRuns.ValidateIdentity(requestedRunId, "--run-id");
        var activation = FindForRepository(repository, sessionId, requestedRunId) ??
                         throw new CliFailure("state", "no Claude lifecycle lease exists for this workspace; use --legacy only for audited legacy artifacts", 3);
        var foreign = activation.SessionId != sessionId;
        if (foreign && requestedRunId is null)
            throw new CliFailure("state", $"Claude run {activation.RunId} belongs to session {activation.SessionId}; takeover cleanup requires --run-id", 3);
        if (requestedRunId is not null && requestedRunId != activation.RunId)
            throw new CliFailure("state", $"Claude cleanup run-id does not match active run {activation.RunId}", 3);
        if (activation.Lifecycle == ClaudeActivationLifecycle.Planning)
            throw new CliFailure("state", "planning runs must use run abandon --host claude instead of run cleanup", 3);
        if (activation.Lifecycle == ClaudeActivationLifecycle.Cleaning)
        {
            if (foreign) RequireRiskAuthorization(acceptRisk, authorizationNote, "cross-session cleanup recovery");
            if (File.Exists(StateStore.StatePath(repository.WorkspaceRoot)))
                RequireExecutionState(activation, repository, StateStore.Load(repository.WorkspaceRoot));
            return activation;
        }

        ForgeState? state = null;
        if (File.Exists(StateStore.StatePath(repository.WorkspaceRoot)))
        {
            state = StateStore.Load(repository.WorkspaceRoot);
            RequireExecutionState(activation, repository, state);
        }
        var terminal = activation.Lifecycle == ClaudeActivationLifecycle.Executing &&
                       state?.Workflow.Phase is ForgePhase.Done or ForgePhase.DoneWithFindings;
        var riskRequired = foreign || !terminal;
        if (riskRequired) RequireRiskAuthorization(acceptRisk, authorizationNote, foreign ? "cross-session cleanup" : "nonterminal cleanup");
        return Replace(activation, activation with
        {
            Lifecycle = ClaudeActivationLifecycle.Cleaning,
            CleanupRiskAuthorized = riskRequired,
            CleanupAuthorizationNoteHash = riskRequired ? Hashing.Sha256Hex(authorizationNote!) : null,
            LastStopProgressHash = null,
            LastStopBlockedAt = null,
            UpdatedAt = Timestamp(),
        });
    }

    public static void CompleteCleanup(ClaudeActivation activation)
    {
        if (activation.Lifecycle != ClaudeActivationLifecycle.Cleaning)
            throw new CliFailure("state", "Claude cleanup lease is not in lifecycle cleaning", 3);
        DeleteOwned(activation);
    }

    public static void RequireNoActivation(RepositoryIdentity repository)
    {
        var activation = FindForRepository(repository, null, null);
        if (activation is not null)
            throw new CliFailure("state", $"legacy cleanup cannot bypass active Claude run {activation.RunId} in lifecycle {LifecycleName(activation.Lifecycle)}", 3);
    }

    public static ClaudeActivation? Status(RepositoryIdentity repository, string? sessionId)
    {
        if (sessionId is null) return null;
        var activation = TryLoadForSession(sessionId);
        if (activation is null) return null;
        RequireMatch(activation, repository, activation.RunId, sessionId);
        return activation;
    }

    public static ClaudeActivation Abandon(RepositoryIdentity repository,
                                            string runId,
                                            string sessionId,
                                            bool acceptRisk,
                                            string? authorizationNote)
    {
        ValidateSessionId(sessionId);
        PendingRuns.ValidateIdentity(runId, "--run-id");
        var activation = FindForRepository(repository, null, runId) ??
                         throw new CliFailure("state", $"no Claude activation exists for run {runId}", 3);
        if (activation.Lifecycle != ClaudeActivationLifecycle.Planning)
            throw new CliFailure("state", $"run abandon applies only to lifecycle planning; use run cleanup for {LifecycleName(activation.Lifecycle)}", 3);
        if (activation.SessionId != sessionId)
            RequireRiskAuthorization(acceptRisk, authorizationNote, $"takeover of session {activation.SessionId}");
        if (PendingRuns.TryLoadForRun(repository, runId, HostKind.Claude) is not null) PendingRuns.AbandonClaude(repository, runId);
        DeleteOwned(activation);
        return activation;
    }

    public static ClaudeActivation LoadForSession(string sessionId)
        => TryLoadForSession(sessionId) ?? throw new CliFailure("state", "no Claude Forge activation exists for this session; invoke /plan-forge-flow:forge again", 3);

    public static ClaudeActivation? TryLoadForSession(string sessionId)
    {
        var path = PathForSession(sessionId);
        if (!File.Exists(path)) return null;
        return Read(path, sessionId);
    }

    public static string CurrentSessionId()
        => TryCurrentSessionId() ?? throw new CliFailure("environment", "CLAUDE_CODE_SESSION_ID is required for Claude Forge commands");

    public static string? TryCurrentSessionId()
    {
        var sessionId = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        ValidateSessionId(sessionId);
        return sessionId;
    }

    public static string LifecycleName(ClaudeActivationLifecycle lifecycle) => lifecycle switch
    {
        ClaudeActivationLifecycle.Planning => "planning",
        ClaudeActivationLifecycle.Materializing => "materializing",
        ClaudeActivationLifecycle.Executing => "executing",
        ClaudeActivationLifecycle.Cleaning => "cleaning",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
    };

    private static ClaudeActivation? FindForRepository(RepositoryIdentity repository, string? preferredSessionId, string? runId)
    {
        if (preferredSessionId is not null)
        {
            var preferred = TryLoadForSession(preferredSessionId);
            if (preferred is not null && MatchesRepository(preferred, repository) && (runId is null || preferred.RunId == runId)) return preferred;
        }
        var matches = LoadAll().Where(candidate => MatchesRepository(candidate, repository) && (runId is null || candidate.RunId == runId)).ToList();
        if (matches.Count > 1) throw Unsupported();
        return matches.SingleOrDefault();
    }

    private static IReadOnlyList<ClaudeActivation> LoadAll()
    {
        var directory = Path.Combine(RepositoryPaths.PluginData(HostKind.Claude), "claude-activations");
        if (!Directory.Exists(directory)) return [];
        OwnershipGuards.EnsureSafeDirectory(directory);
        var activations = new List<ClaudeActivation>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            OwnershipGuards.EnsureRegularFile(path, "Claude activation");
            var activation = Read(path, null);
            if (!string.Equals(path, PathForSession(activation.SessionId), WorkspacePathPolicy.Comparison))
                throw new CliFailure("unsupported-state-schema", "Claude activation path is invalid", 3);
            activations.Add(activation);
        }
        return activations;
    }

    private static ClaudeActivation Read(string path, string? expectedSessionId)
    {
        try
        {
            OwnershipGuards.EnsureRegularFile(path, "Claude activation");
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) ||
                version != ClaudeActivation.SchemaVersion) throw Unsupported();
            foreach (var property in new[]
                     {
                         "workspace", "scopeId", "runId", "sessionId", "lifecycle", "lastStopProgressHash", "lastStopBlockedAt",
                         "cleanupRiskAuthorized", "cleanupAuthorizationNoteHash", "createdAt", "updatedAt",
                     })
                if (!document.RootElement.TryGetProperty(property, out _)) throw Unsupported();
            var activation = JsonSerializer.Deserialize(json, ForgeJsonContext.Default.ClaudeActivation) ?? throw Unsupported();
            ValidateLoaded(activation);
            if (expectedSessionId is not null && activation.SessionId != expectedSessionId) throw Unsupported();
            return activation;
        }
        catch (CliFailure) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliFailure("unsupported-state-schema", $"Claude activation is malformed or unsupported: {error.Message}", 3);
        }
    }

    private static ClaudeActivation Replace(ClaudeActivation expected, ClaudeActivation replacement)
    {
        var path = PathForSession(expected.SessionId);
        var stored = Read(path, expected.SessionId);
        if (stored != expected) throw new CliFailure("state", "Claude activation changed before update", 3);
        Write(replacement);
        return replacement;
    }

    private static void Write(ClaudeActivation activation)
    {
        var path = PathForSession(activation.SessionId);
        OwnershipGuards.EnsureDirectory(Path.GetDirectoryName(path)!);
        DurableFiles.WriteJson(path, activation, ForgeJsonContext.Default.ClaudeActivation);
    }

    private static void DeleteOwned(ClaudeActivation activation)
    {
        var path = PathForSession(activation.SessionId);
        if (!File.Exists(path)) return;
        var stored = Read(path, activation.SessionId);
        if (stored != activation) throw new CliFailure("state", "Claude activation changed before removal", 3);
        File.Delete(path);
    }

    private static bool MatchesRepository(ClaudeActivation activation, RepositoryIdentity repository)
        => activation.ScopeId == RepositoryPaths.ScopeId(repository) &&
           string.Equals(activation.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison);

    private static void RequireMatch(ClaudeActivation activation, RepositoryIdentity repository, string runId, string sessionId)
    {
        if (activation.SessionId != sessionId || activation.RunId != runId || !MatchesRepository(activation, repository))
            throw new CliFailure("state",
                                 $"Claude activation mismatch: run {activation.RunId}, session {activation.SessionId}, workspace {activation.Workspace}",
                                 3);
    }

    private static void RequireExecutionState(ClaudeActivation activation, RepositoryIdentity repository, ForgeState state)
    {
        if (state.Host != HostKind.Claude || state.RepositoryScopeId != activation.ScopeId ||
            state.RepositoryScopeId != RepositoryPaths.ScopeId(repository) || state.SourceRun?.Source != "claude:" + activation.RunId)
            throw new CliFailure("state", "Claude execution state does not match the active run lease", 3);
    }

    private static void RequireRiskAuthorization(bool acceptRisk, string? authorizationNote, string operation)
    {
        if (!acceptRisk) throw new CliFailure("state", $"{operation} requires --accept-risk with --authorization-note", 3);
        if (string.IsNullOrWhiteSpace(authorizationNote) || authorizationNote.Length > 16 * 1024)
            throw new CliFailure("usage", $"{operation} requires a bounded --authorization-note");
    }

    private static void ValidateLoaded(ClaudeActivation activation)
    {
        if (activation.SchemaVersionValue != ClaudeActivation.SchemaVersion || string.IsNullOrWhiteSpace(activation.Workspace) ||
            !Path.IsPathRooted(activation.Workspace) || !ScopeIdentity.IsMatch(activation.ScopeId) || !Enum.IsDefined(activation.Lifecycle) ||
            !DateTimeOffset.TryParse(activation.CreatedAt, out _) || !DateTimeOffset.TryParse(activation.UpdatedAt, out _)) throw Unsupported();
        if ((activation.LastStopProgressHash is null) != (activation.LastStopBlockedAt is null) ||
            activation.LastStopProgressHash is not null && (!Sha256.IsMatch(activation.LastStopProgressHash) || !DateTimeOffset.TryParse(activation.LastStopBlockedAt, out _))) throw Unsupported();
        if (activation.CleanupRiskAuthorized != (activation.CleanupAuthorizationNoteHash is not null) ||
            activation.CleanupAuthorizationNoteHash is not null && !Sha256.IsMatch(activation.CleanupAuthorizationNoteHash) ||
            activation.CleanupRiskAuthorized && activation.Lifecycle != ClaudeActivationLifecycle.Cleaning) throw Unsupported();
        PendingRuns.ValidateIdentity(activation.RunId, "runId");
        ValidateSessionId(activation.SessionId);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 512 || sessionId.Contains('\0') || sessionId.Any(char.IsControl))
            throw new CliFailure("usage", "Claude session ID is malformed");
    }

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");

    private static CliFailure Unsupported()
        => new("unsupported-state-schema", "Claude activation is malformed or unsupported", 3);
}
