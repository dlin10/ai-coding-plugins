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

internal sealed record ClaudeActivation
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersionValue { get; init; } = SchemaVersion;
    public string Workspace { get; init; } = string.Empty;
    public string ScopeId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
}

internal static class ClaudeActivations
{
    private static readonly Regex ScopeIdentity = new("^[a-f0-9]{24}$", RegexOptions.CultureInvariant);

    public static string PathForSession(string sessionId)
    {
        ValidateSessionId(sessionId);
        return Path.Combine(RepositoryPaths.PluginData(HostKind.Claude), "claude-activations", Hashing.Sha256Hex(sessionId) + ".json");
    }

    public static ClaudeActivation Begin(RepositoryIdentity repository, string sessionId)
    {
        using var repositoryLock = RepositoryRunLock.Acquire(repository, HostKind.Claude);
        ValidateSessionId(sessionId);
        var existing = TryLoadForSession(sessionId);
        if (existing is not null)
        {
            RequireMatch(existing, repository, existing.RunId, sessionId);
            return existing;
        }

        foreach (var candidate in LoadAll())
        {
            if (!string.Equals(candidate.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison) ||
                candidate.ScopeId != RepositoryPaths.ScopeId(repository)) continue;
            var pending = PendingRuns.TryLoadForRun(repository, candidate.RunId, HostKind.Claude);
            var phase = pending is null ? "not-staged" : PendingRuns.PhaseName(pending.Phase);
            throw new CliFailure("state",
                                 $"Claude Forge run {candidate.RunId} is already armed by session {candidate.SessionId} in phase {phase}; " +
                                 "use run abandon --host claude with explicit takeover authorization",
                                 3);
        }

        var unarmed = PendingRuns.Status(repository, HostKind.Claude);
        if (unarmed is not null && unarmed.Phase is not (PendingRunPhase.Consumed or PendingRunPhase.Abandoned))
            throw new CliFailure("unsupported-state-schema",
                                 $"Claude pending run {unarmed.RunId}, session unarmed, phase {PendingRuns.PhaseName(unarmed.Phase)} " +
                                 "predates the activation schema; remove the legacy external run state and start Forge again",
                                 3);

        var now = DateTimeOffset.UtcNow.ToString("O");
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

    public static ClaudeActivation Require(RepositoryIdentity repository, string runId, string sessionId)
    {
        var activation = LoadForSession(sessionId);
        RequireMatch(activation, repository, runId, sessionId);
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
        var activation = LoadAll().SingleOrDefault(candidate => candidate.RunId == runId &&
                                                               string.Equals(candidate.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison) &&
                                                               candidate.ScopeId == RepositoryPaths.ScopeId(repository))
                         ?? throw new CliFailure("state", $"no Claude activation exists for run {runId}", 3);
        if (activation.SessionId != sessionId)
        {
            if (!acceptRisk) throw new CliFailure("state", $"Claude run {runId} belongs to session {activation.SessionId}; takeover requires --accept-risk", 3);
            if (string.IsNullOrWhiteSpace(authorizationNote) || authorizationNote.Length > 16 * 1024)
                throw new CliFailure("usage", "cross-session abandon requires a bounded --authorization-note");
        }

        if (PendingRuns.TryLoadForRun(repository, runId, HostKind.Claude) is not null) PendingRuns.AbandonClaude(repository, runId);
        DeleteOwned(activation);
        return activation;
    }

    public static void Complete(RepositoryIdentity repository, string runId)
    {
        var sessionId = TryCurrentSessionId();
        if (sessionId is null) return;
        var activation = TryLoadForSession(sessionId);
        if (activation is null) return;
        RequireMatch(activation, repository, runId, sessionId);
        DeleteOwned(activation);
    }

    public static void Cleanup(RepositoryIdentity repository)
    {
        foreach (var activation in LoadAll().Where(candidate => string.Equals(candidate.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison) &&
                                                               candidate.ScopeId == RepositoryPaths.ScopeId(repository)))
        {
            DeleteOwned(activation);
        }
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
            var activation = JsonSerializer.Deserialize(json, ForgeJsonContext.Default.ClaudeActivation) ?? throw Unsupported();
            ValidateLoaded(activation);
            if (expectedSessionId is not null && activation.SessionId != expectedSessionId) throw Unsupported();
            return activation;
        }
        catch (CliFailure)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliFailure("unsupported-state-schema", $"Claude activation is malformed or unsupported: {error.Message}", 3);
        }
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

    private static void RequireMatch(ClaudeActivation activation, RepositoryIdentity repository, string runId, string sessionId)
    {
        if (activation.SessionId != sessionId || activation.RunId != runId || activation.ScopeId != RepositoryPaths.ScopeId(repository) ||
            !string.Equals(activation.Workspace, repository.WorkspaceRoot, WorkspacePathPolicy.Comparison))
            throw new CliFailure("state",
                                 $"Claude activation mismatch: run {activation.RunId}, session {activation.SessionId}, workspace {activation.Workspace}",
                                 3);
    }

    private static void ValidateLoaded(ClaudeActivation activation)
    {
        if (activation.SchemaVersionValue != ClaudeActivation.SchemaVersion || string.IsNullOrWhiteSpace(activation.Workspace) ||
            !Path.IsPathRooted(activation.Workspace) || !ScopeIdentity.IsMatch(activation.ScopeId) ||
            !DateTimeOffset.TryParse(activation.CreatedAt, out _) || !DateTimeOffset.TryParse(activation.UpdatedAt, out _)) throw Unsupported();
        PendingRuns.ValidateIdentity(activation.RunId, "runId");
        ValidateSessionId(activation.SessionId);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 512 || sessionId.Contains('\0') || sessionId.Any(char.IsControl))
            throw new CliFailure("usage", "Claude session ID is malformed");
    }

    private static CliFailure Unsupported()
        => new("unsupported-state-schema", "Claude activation is malformed or unsupported", 3);
}
