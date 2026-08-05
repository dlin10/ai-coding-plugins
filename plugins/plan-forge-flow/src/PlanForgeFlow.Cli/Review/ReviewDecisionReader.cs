using System.Text;
using System.Text.Json;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Review;

internal static class ReviewDecisionReader
{
    public static List<string> ParsePathArray(string? raw, string option)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        if (Encoding.UTF8.GetByteCount(raw) > 256 * 1024) throw new CliFailure("usage", $"--{option} exceeds the size bound");
        string[] paths;
        try { paths = JsonSerializer.Deserialize(raw, ForgeJsonContext.Default.StringArray) ?? throw new JsonException("JSON value is null"); }
        catch (Exception error) { throw new CliFailure("usage", $"--{option} must be a JSON array: {error.Message}"); }
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || Path.IsPathRooted(path) || path.Contains('\0')) throw new CliFailure("usage", $"--{option} must contain bounded relative path strings");
            var normalized = path.Replace('\\', '/');
            if (normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal)) throw new CliFailure("usage", $"--{option} contains a traversal path");
        }

        return paths.ToList();
    }

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
