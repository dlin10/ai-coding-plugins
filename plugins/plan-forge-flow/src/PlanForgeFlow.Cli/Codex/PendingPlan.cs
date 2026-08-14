using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Codex;

internal sealed record PendingPlan(string Workspace, string Plan)
{
    public static void Write(string workspace, string plan)
    {
        var path = RepositoryPaths.PendingPlanPath(workspace);
        DurableFiles.WriteJson(path,
                               new PendingPlanDocument(ForgeState.SchemaVersion, HostKind.Codex, workspace, CanonicalText.NormalizePlan(plan)),
                               ForgeJsonContext.Default.PendingPlanDocument);
    }

    public static PendingPlan Read(string workspace)
    {
        var path = RepositoryPaths.PendingPlanPath(workspace);
        if (!File.Exists(path)) throw new CliFailure("state", "no pending Forge plan is available for materialization", 3);
        try
        {
            OwnershipGuards.EnsureRegularFile(path, "pending Forge plan");
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), ForgeJsonContext.Default.PendingPlanDocument) ?? throw new JsonException("JSON value is null");
            if (value.SchemaVersion != ForgeState.SchemaVersion) throw new CliFailure("unsupported-state-schema", "pending Forge plan schemaVersion is unsupported", 3);
            if (value.Host != HostKind.Codex || !SameWorkspace(value.Workspace, workspace) || string.IsNullOrWhiteSpace(value.Plan)) throw new FormatException("host, workspace, or plan is missing");
            return new PendingPlan(workspace, CanonicalText.NormalizePlan(value.Plan));
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"pending Forge plan is malformed: {error.Message}", 3); }
    }

    public static void Delete(string workspace)
    {
        var path = RepositoryPaths.PendingPlanPath(workspace);
        if (!File.Exists(path)) return;
        OwnershipGuards.EnsureRegularFile(path, "pending Forge plan");
        File.Delete(path);
    }

    internal static string? AuditLegacy(string workspace)
    {
        var path = RepositoryPaths.PendingPlanPath(workspace);
        if (!File.Exists(path)) return null;
        OwnershipGuards.EnsureRegularFile(path, "legacy pending Forge plan");
        try
        {
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), ForgeJsonContext.Default.PendingPlanDocument) ?? throw new JsonException();
            if (value.SchemaVersion != 1 || value.Host != HostKind.Codex || !SameWorkspace(value.Workspace, workspace) ||
                string.IsNullOrWhiteSpace(value.Plan))
                throw new CliFailure("state", "refusing to remove an unowned or current-schema pending Forge plan", 3);
            return path;
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"legacy pending Forge plan is malformed: {error.Message}", 3); }
    }

    private static bool SameWorkspace(string? left, string right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, WorkspacePathPolicy.Comparison);
}
