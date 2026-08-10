using System.Globalization;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Workflow.State;

internal static class StateStore
{
    public static string StatePath(string workspace) => Path.Combine(workspace, ".forge", "state.json");

    public static ForgeState CreateEmpty(HostKind host = HostKind.Codex, string? repositoryScopeId = null) => ForgeState.CreateEmpty(host, repositoryScopeId);

    public static ForgeState Load(string workspace)
    {
        var path = StatePath(workspace);
        if (!File.Exists(path)) throw new CliFailure("state", "no Forge state exists", 3);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "Forge state must not be a symlink", 3);
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null && parent.Exists; parent = parent.Parent)
            {
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "Forge state path contains a symlinked directory", 3);
            }
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion) || schemaVersion != ForgeState.SchemaVersion || !root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String || host.GetString() is not ("codex" or "cursor")) throw new CliFailure("unsupported-state-schema", "Forge state schemaVersion or host is unsupported", 3);
            var state = JsonSerializer.Deserialize(text, ForgeJsonContext.Default.ForgeState) ?? throw new JsonException("JSON value is null");
            if (string.IsNullOrWhiteSpace(state.RepositoryScopeId) || state.RepositoryScopeId.Length != 24 || state.RepositoryScopeId.Any(character => !Uri.IsHexDigit(character))) throw new CliFailure("unsupported-state-schema", "Forge state repositoryScopeId is invalid", 3);
            if (state.SourceRun is { } source && (string.IsNullOrWhiteSpace(source.Source) || string.IsNullOrWhiteSpace(source.TransactionId))) throw new CliFailure("unsupported-state-schema", "Forge state source run is invalid", 3);
            if (state.ReviewerGuarantee is { Kind.Length: 0 } || state.ApprovalGuarantee is { Kind.Length: 0 }) throw new CliFailure("unsupported-state-schema", "Forge state guarantee is invalid", 3);
            return state;
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"state is malformed: {error.Message}", 3); }
    }

    public static ForgeState Update(string workspace, Action<ForgeState> update) => Update(workspace, null, update);

    public static ForgeState Update(string workspace, ForgeState? expectedState, Action<ForgeState> update, Action<ForgeState>? beforePersist = null)
    {
        using var stateLock = ForgeStateLock.Acquire(workspace);
        var state = Load(workspace);
        if (expectedState is not null && !string.Equals(Hashing.Sha256Hex(JsonSerializer.Serialize(state, ForgeJsonContext.Default.ForgeState)), Hashing.Sha256Hex(JsonSerializer.Serialize(expectedState, ForgeJsonContext.Default.ForgeState)), StringComparison.Ordinal)) throw new CliFailure("state", "Forge state changed during the operation; retry against the current dispatch", 3);
        update(state);
        state.UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        beforePersist?.Invoke(state);
        DurableFiles.WriteJson(StatePath(workspace), state, ForgeJsonContext.Default.ForgeState);
        return state;
    }
}
