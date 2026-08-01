using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed record PendingPlan(string Workspace, string Plan)
{
    private const int Version = 1;

    public static void Write(string workspace, string plan)
    {
        var path = RepositoryPaths.PendingPlanPath(workspace);
        DurableFiles.WriteJson(path, new JsonObject
        {
            ["version"] = Version,
            ["workspace"] = workspace,
            ["plan"] = CanonicalText.NormalizePlan(plan),
        });
    }

    public static PendingPlan Read(string workspace)
    {
        var path = RepositoryPaths.PendingPlanPath(workspace);
        if (!File.Exists(path)) throw new CliFailure("state", "no pending Forge plan is available for materialization", 3);
        try
        {
            OwnershipGuards.EnsureRegularFile(path, "pending Forge plan");
            var value = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("object expected");
            if (value["version"]?.GetValue<int>() != Version) throw new FormatException("unsupported version");
            var capturedWorkspace = value["workspace"]?.GetValue<string>();
            var plan = value["plan"]?.GetValue<string>();
            if (!SameWorkspace(capturedWorkspace, workspace) || string.IsNullOrWhiteSpace(plan)) throw new FormatException("workspace or plan is missing");
            return new PendingPlan(workspace, CanonicalText.NormalizePlan(plan));
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
