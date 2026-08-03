using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;

namespace PlanForgeFlow.Codex;

internal sealed record PendingPlan(string Workspace, string Plan)
{
    public static void Write(string workspace, string plan)
    {
        var path = RepositoryPaths.PendingPlanPath(workspace);
        DurableFiles.WriteJson(path,
                               new PendingPlanDocument(workspace, CanonicalText.NormalizePlan(plan)),
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
            if (!SameWorkspace(value.Workspace, workspace) || string.IsNullOrWhiteSpace(value.Plan)) throw new FormatException("workspace or plan is missing");
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

    private static bool SameWorkspace(string? left, string right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
