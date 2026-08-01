using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal sealed record PlanTask(int Number, string Hash, string Text)
{
    public JsonObject ToJson() => new() { ["number"] = Number, ["hash"] = Hash, ["text"] = Text };
}

internal sealed record RepositoryIdentity(string WorkspaceRoot, string GitCommonDir)
{
    public JsonObject ToJson() => new()
    {
        ["workspaceRoot"] = WorkspaceRoot,
        ["gitCommonDir"] = GitCommonDir,
    };
}

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

internal static class ModelSelections
{
    private static readonly Regex ModelPattern = new("^[A-Za-z0-9][A-Za-z0-9._\\[\\]=,-]{0,255}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EffortPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static JsonObject Validate(string role, string model, string effort)
    {
        if (!ModelPattern.IsMatch(model))
        {
            throw new CliFailure("usage", $"{role} model is malformed");
        }

        if (!EffortPattern.IsMatch(effort))
        {
            throw new CliFailure("usage", $"{role} effort is malformed");
        }

        if (string.Equals(effort, "ultra", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliFailure("usage", $"{role} effort ultra is prohibited");
        }

        return new JsonObject { ["model"] = model, ["effort"] = effort.ToLowerInvariant() };
    }
}
