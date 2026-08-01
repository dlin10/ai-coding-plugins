using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    
    private static JsonArray ParsePathArray(string? raw, string option)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new JsonArray();
        if (Encoding.UTF8.GetByteCount(raw) > 256 * 1024) throw new CliFailure("usage", $"--{option} exceeds the size bound");
        JsonArray paths;
        try { paths = JsonNode.Parse(raw)!.AsArray(); }
        catch (Exception error) { throw new CliFailure("usage", $"--{option} must be a JSON array: {error.Message}"); }
        foreach (var item in paths)
        {
            var path = item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || Path.IsPathRooted(path) || path.Contains('\0')) throw new CliFailure("usage", $"--{option} must contain bounded relative path strings");
            var normalized = path.Replace('\\', '/');
            if (normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal)) throw new CliFailure("usage", $"--{option} contains a traversal path");
        }
    
        return paths;
    }
    
    private static (string Verdict, string? Coverage, string Path, string Hash) ReadReviewDecision(string critiquePath, string workspace, DispatchStage expectedStage)
    {
        var forgeRoot = Path.Combine(workspace, ".forge");
        var path = critiquePath + ".json";
        if (!ReviewEvidence.IsContained(forgeRoot, path) || !File.Exists(path)) throw new CliFailure("verdict", $"review decision file is missing next to the critique: {path}", 2);
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(path).Length > 16 * 1024) throw new CliFailure("verdict", "review decision file is oversized or symlinked", 2);
        try
        {
            var value = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new FormatException("JSON object expected");
            var verdict = value["verdict"]?.GetValue<string>().ToUpperInvariant();
            var coverage = value["coverage"]?.GetValue<string>().ToUpperInvariant();
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
    
    private static void RequireAuthorizationNote(ParsedArgs parsed)
    {
        var note = parsed.Get("authorization-note");
        if (string.IsNullOrWhiteSpace(note) || note.Length > 16 * 1024) throw new CliFailure("usage", "accepting risk requires a bounded --authorization-note");
    }
    
    private static void RequirePlanHash(ForgeState state, ParsedArgs parsed)
    {
        var supplied = parsed.Get("plan-sha256");
        if (supplied is null) return;
        if (!Regex.IsMatch(supplied, "^[a-f0-9]{64}$") || !string.Equals(supplied, state.Approval.PlanHash, StringComparison.Ordinal)) throw new CliFailure("state", "--plan-sha256 does not match the materialized plan", 3);
    }
    
    private static string? FindPluginFile(string relative)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            if (Directory.Exists(Path.Combine(current.FullName, ".codex-plugin"))) break;
        }
    
        return null;
    }
    
    private static JsonObject Doctor(string workspace)
    {
        return new JsonObject
        {
            ["workspace"] = workspace,
            ["git"] = ToolCheck("git", ["--version"]),
            ["dotnet"] = ToolCheck("dotnet", ["--version"]),
            ["state"] = File.Exists(StateStore.StatePath(workspace)),
        };
    }
    
    private static JsonObject ToolCheck(string fileName, IReadOnlyList<string> args)
    {
        try
        {
            var result = ProcessExecution.Run(fileName, args);
            return new JsonObject { ["ok"] = result.ExitCode == 0, ["version"] = result.Stdout.Trim(), ["error"] = result.ExitCode == 0 ? null : result.Stderr.Trim() };
        }
        catch (CliFailure error)
        {
            return new JsonObject { ["ok"] = false, ["version"] = null, ["error"] = error.Message };
        }
    }
    
    private static JsonObject Set(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var originalState = context.RequireState();
        var key = parsed.GetRequired("key");
        var value = parsed.GetRequired("value");
        var state = StateStore.Update(workspace, originalState, current =>
        {
            if (key == "phase")
            {
                var target = value switch
                {
                    "materialized" => ForgePhase.Materialized,
                    "locked" => ForgePhase.Locked,
                    "build" => ForgePhase.Build,
                    "code-review" => ForgePhase.CodeReview,
                    "done" => ForgePhase.Done,
                    "done-with-findings" => ForgePhase.DoneWithFindings,
                    _ => throw new CliFailure("usage", "phase is unsupported"),
                };
                var old = current.Workflow.Phase;
                if (old != target)
                {
                    var legal = (old, target) switch
                    {
                        (ForgePhase.Materialized, ForgePhase.Locked) => HasLockEvidence(workspace, current),
                        (ForgePhase.Locked, ForgePhase.Build) => HasBuildEvidence(current),
                        (ForgePhase.Build, ForgePhase.CodeReview) => !current.Dispatch.Pending && (current.Workflow.TaskCount ?? 0) > 0 && current.Workflow.NextTaskNumber > current.Workflow.TaskCount,
                        (ForgePhase.CodeReview, ForgePhase.Done) => current.Review.Verdict == "APPROVED" && current.Review.Coverage == "FULL",
                        (ForgePhase.CodeReview, ForgePhase.DoneWithFindings) => parsed.Has("accept-risk"),
                        (ForgePhase.CodeReview, ForgePhase.Build) => parsed.Has("amendment"),
                        _ => false,
                    };
                    if (!legal) throw new CliFailure("state", $"illegal phase transition {old.ToWireName()} -> {target.ToWireName()}", 3);
                }
                if (target == ForgePhase.Done)
                {
                    RequireApprovedReviewEvidence(workspace, current);
                }
                if (target == ForgePhase.CodeReview && old == ForgePhase.Build && current.Dispatch.Pending) throw new CliFailure("state", "cannot enter code-review with a pending dispatch", 3);
                if (target == ForgePhase.DoneWithFindings) RequireAuthorizationNote(parsed);
                if (old == ForgePhase.CodeReview && target == ForgePhase.Build)
                {
                    RequireAuthorizationNote(parsed);
                    current.Workflow.Amendment = true;
                    current.Review.Verdict = null;
                    current.Review.Coverage = null;
                    current.Models.Builder = null;
                    current.Agents.BuilderId = null;
                }
                current.Workflow.Phase = target;
            }
            else if (key == "round" || key == "fix-round")
            {
                throw new CliFailure("state", $"{key} is advanced only by a review verdict", 3);
            }
            else if (key == "max-rounds")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0) throw new CliFailure("usage", "max-rounds must be a non-negative integer");
                var currentCap = current.Workflow.MaxRounds;
                if (number != currentCap + 1) throw new CliFailure("state", $"max-rounds may only be extended by exactly one round ({currentCap} -> {currentCap + 1})", 3);
                if (current.Workflow.Round < currentCap || current.Review.Verdict != "REVISE") throw new CliFailure("state", "max-rounds may only be extended after the current review cap is reached", 3);
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "max-rounds extension requires --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
                current.Workflow.MaxRounds = number;
            }
            else if (key == "max-fix-rounds")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0) throw new CliFailure("usage", "max-fix-rounds must be a non-negative integer");
                var currentCap = current.Workflow.MaxFixRounds;
                if (number != currentCap + 1) throw new CliFailure("state", $"max-fix-rounds may only be extended by exactly one round ({currentCap} -> {currentCap + 1})", 3);
                if (current.Review.FixRound < currentCap || current.Review.Verdict != "REVISE") throw new CliFailure("state", "max-fix-rounds may only be extended after the current fix cap is reached", 3);
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "max-fix-rounds extension requires --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
                current.Workflow.MaxFixRounds = number;
            }
            else if (key == "coverage")
            {
                throw new CliFailure("state", "coverage is managed only by review verdict", 3);
            }
            else if (key == "verdict")
            {
                throw new CliFailure("state", "verdict is managed only by review verdict", 3);
            }
            else throw new CliFailure("usage", $"unsupported state key: {key}");
        });
        return state.ToJson();
    }
    
    private static bool HasLockEvidence(string workspace, ForgeState state)
    {
        var planPath = Path.Combine(workspace, "PLAN.md");
        if (!File.Exists(planPath) || (File.GetAttributes(planPath) & FileAttributes.ReparsePoint) != 0) return false;
        if (File.ReadLines(planPath).FirstOrDefault() != CanonicalText.OwnedMarker) return false;
        var expectedHash = state.Approval.PlanHash;
        if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, Hashing.Sha256Hex(CanonicalText.NormalizePlan(File.ReadAllText(planPath))), StringComparison.Ordinal)) return false;
        var tasks = state.Workflow.Tasks;
        return tasks is { Count: > 0 } && state.Workflow.TaskCount == tasks.Count;
    }
    
    private static bool HasBuildEvidence(ForgeState state)
    {
        return !string.IsNullOrWhiteSpace(state.Baselines.Head) &&
               !string.IsNullOrWhiteSpace(state.Baselines.Worktree) &&
               state.Models.Builder is not null;
    }
}
