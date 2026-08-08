using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Review;

internal static class ReviewDecisionReader
{
    public static (string Verdict, string? Coverage, string Path, string Hash) Read(string critiquePath, string workspace, DispatchStage expectedStage)
    {
        var forgeRoot = Path.Combine(workspace, ".forge");
        var path = critiquePath + ".json";
        if (!ReviewEvidence.IsContained(forgeRoot, path) || !File.Exists(path))
            throw new CliFailure("verdict", $"review decision file is missing next to the critique: {path}", 2);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(path).Length > 16 * 1024)
            throw new CliFailure("verdict", "review decision file is oversized or symlinked", 2);
        try
        {
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), ForgeJsonContext.Default.ReviewDecision) ?? throw new JsonException("JSON value is null");
            var verdict = value.Verdict.ToUpperInvariant();
            var coverage = value.Coverage?.ToUpperInvariant();
            if (verdict is not ("APPROVED" or "REVISE")) throw new FormatException("verdict must be APPROVED or REVISE");
            if (expectedStage == DispatchStage.Plan)
            {
                if (coverage is not null) throw new FormatException("plan decisions must not include coverage");
            }
            else if (coverage is not ("FULL" or "PARTIAL"))
            {
                throw new FormatException("code decisions must include FULL or PARTIAL coverage");
            }
            return (verdict, coverage, path, Hashing.Sha256File(path));
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("verdict", $"review decision file is malformed: {error.Message}", 2); }
    }
}
