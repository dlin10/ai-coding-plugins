using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed record SessionCapture(int Version,
                                      string? SessionId,
                                      string Cwd,
                                      string? TurnId,
                                      string? TranscriptPath,
                                      string? Model,
                                      string? PermissionMode,
                                      string? Effort,
                                      bool EffortKnown,
                                      string CollaborationMode,
                                      string? CollaborationModeReason,
                                      string PromptKind,
                                      bool ImplementationCandidate,
                                      string? WrapperHash,
                                      string? EnvelopeNonce,
                                      string ObservedAt)
{
    public JsonObject ToJson() => new()
    {
        ["version"] = Version,
        ["sessionId"] = SessionId,
        ["cwd"] = Cwd,
        ["turnId"] = TurnId,
        ["transcriptPath"] = TranscriptPath,
        ["model"] = Model,
        ["permissionMode"] = PermissionMode,
        ["effort"] = Effort,
        ["effortKnown"] = EffortKnown,
        ["collaborationMode"] = CollaborationMode,
        ["collaborationModeReason"] = CollaborationModeReason,
        ["promptKind"] = PromptKind,
        ["implementationCandidate"] = ImplementationCandidate,
        ["wrapperHash"] = WrapperHash,
        ["envelopeNonce"] = EnvelopeNonce,
        ["observedAt"] = ObservedAt,
    };

    public static SessionCapture? Read(string workspaceRoot, string? explicitPath = null)
    {
        var path = string.IsNullOrWhiteSpace(explicitPath) ? RepositoryPaths.SessionCapturePath(workspaceRoot) : Path.GetFullPath(explicitPath);
        if (!File.Exists(path)) return null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null && parent.Exists; parent = parent.Parent)
            {
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            }
            var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (node is null) return null;
            var capture = new SessionCapture(
                                             node["version"]?.GetValue<int>() ?? 0,
                                             node["sessionId"]?.GetValue<string>(),
                                             node["cwd"]?.GetValue<string>() ?? string.Empty,
                                             node["turnId"]?.GetValue<string>(),
                                             node["transcriptPath"]?.GetValue<string>(),
                                             node["model"]?.GetValue<string>(),
                                             node["permissionMode"]?.GetValue<string>(),
                                             node["effort"]?.GetValue<string>(),
                                             node["effortKnown"]?.GetValue<bool>() ?? false,
                                             node["collaborationMode"]?.GetValue<string>() ?? "unknown",
                                             node["collaborationModeReason"]?.GetValue<string>(),
                                             node["promptKind"]?.GetValue<string>() ?? "other",
                                             node["implementationCandidate"]?.GetValue<bool>() ?? false,
                                             node["wrapperHash"]?.GetValue<string>(),
                                             node["envelopeNonce"]?.GetValue<string>(),
                                             node["observedAt"]?.GetValue<string>() ?? string.Empty);
            if (capture.Version != 2 || !SameWorkspace(capture.Cwd, RepositoryPaths.CanonicalWorkspaceRoot(workspaceRoot))) return null;
            if (capture.CollaborationMode is not ("plan" or "default")) return null;
            if (!DateTimeOffset.TryParse(capture.ObservedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var observed)) return null;
            var configured = Environment.GetEnvironmentVariable("FORGE_SESSION_MAX_AGE_MS");
            var maxAge = double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
                             ? TimeSpan.FromMilliseconds(value)
                             : TimeSpan.FromMinutes(30);
            var age = DateTimeOffset.UtcNow - observed;
            return age >= TimeSpan.Zero && age <= maxAge ? capture : null;
        }
        catch { return null; }
    }

    private static bool SameWorkspace(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}