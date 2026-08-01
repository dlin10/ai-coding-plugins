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
    
    private static (string Verdict, string? Coverage, string Path, string Hash) ReadReviewDecision(string critiquePath, string workspace, string expectedStage)
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
            if (expectedStage == "plan")
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
    
    private static void RequirePlanHash(JsonObject state, ParsedArgs parsed)
    {
        var supplied = parsed.Get("plan-sha256");
        if (supplied is null) return;
        if (!Regex.IsMatch(supplied, "^[a-f0-9]{64}$") || !string.Equals(supplied, state["approval"]!["planHash"]?.GetValue<string>(), StringComparison.Ordinal)) throw new CliFailure("state", "--plan-sha256 does not match the materialized plan", 3);
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
    
    private static JsonObject Set(string workspace, ParsedArgs parsed)
    {
        var key = parsed.GetRequired("key");
        var value = parsed.GetRequired("value");
        var state = StateStore.Update(workspace, current =>
        {
            if (key == "phase")
            {
                if (value is not ("materialized" or "locked" or "build" or "code-review" or "done" or "done-with-findings")) throw new CliFailure("usage", "phase is unsupported");
                var old = current["workflow"]!["phase"]!.GetValue<string>();
                if (old != value)
                {
                    var legal = (old, value) switch
                    {
                        ("materialized", "locked") => HasLockEvidence(workspace, current),
                        ("locked", "build") => HasBuildEvidence(current),
                        ("build", "code-review") => !current["dispatch"]!["pending"]!.GetValue<bool>() && (current["workflow"]!["taskCount"]?.GetValue<int>() ?? 0) > 0 && current["workflow"]!["nextTaskNumber"]!.GetValue<int>() > current["workflow"]!["taskCount"]!.GetValue<int>(),
                        ("code-review", "done") => current["review"]!["verdict"]?.GetValue<string>() == "APPROVED" && current["review"]!["coverage"]?.GetValue<string>() == "FULL",
                        ("code-review", "done-with-findings") => parsed.Has("accept-risk"),
                        ("code-review", "build") => parsed.Has("amendment"),
                        _ => false,
                    };
                    if (!legal) throw new CliFailure("state", $"illegal phase transition {old} -> {value}", 3);
                }
                if (value == "done")
                {
                    RequireApprovedReviewEvidence(workspace, current);
                }
                if (value == "code-review" && old == "build" && current["dispatch"]!["pending"]!.GetValue<bool>()) throw new CliFailure("state", "cannot enter code-review with a pending dispatch", 3);
                if (value == "done-with-findings") RequireAuthorizationNote(parsed);
                if (old == "code-review" && value == "build")
                {
                    RequireAuthorizationNote(parsed);
                    current["workflow"]!["amendment"] = true;
                    current["review"]!["verdict"] = null;
                    current["review"]!["coverage"] = null;
                    current["models"]!["builder"] = null;
                    current["agents"]!["builderId"] = null;
                }
                current["workflow"]!["phase"] = value;
            }
            else if (key == "round" || key == "fix-round")
            {
                throw new CliFailure("state", $"{key} is advanced only by a review verdict", 3);
            }
            else if (key == "max-rounds")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0) throw new CliFailure("usage", "max-rounds must be a non-negative integer");
                var workflow = current["workflow"]!.AsObject();
                var currentCap = workflow["maxRounds"]!.GetValue<int>();
                if (number != currentCap + 1) throw new CliFailure("state", $"max-rounds may only be extended by exactly one round ({currentCap} -> {currentCap + 1})", 3);
                if (workflow["round"]!.GetValue<int>() < currentCap || current["review"]!["verdict"]?.GetValue<string>() != "REVISE") throw new CliFailure("state", "max-rounds may only be extended after the current review cap is reached", 3);
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "max-rounds extension requires --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
                workflow["maxRounds"] = number;
            }
            else if (key == "max-fix-rounds")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0) throw new CliFailure("usage", "max-fix-rounds must be a non-negative integer");
                var workflow = current["workflow"]!.AsObject();
                var currentCap = workflow["maxFixRounds"]!.GetValue<int>();
                if (number != currentCap + 1) throw new CliFailure("state", $"max-fix-rounds may only be extended by exactly one round ({currentCap} -> {currentCap + 1})", 3);
                if (current["review"]!["fixRound"]!.GetValue<int>() < currentCap || current["review"]!["verdict"]?.GetValue<string>() != "REVISE") throw new CliFailure("state", "max-fix-rounds may only be extended after the current fix cap is reached", 3);
                if (!parsed.Has("accept-risk")) throw new CliFailure("state", "max-fix-rounds extension requires --accept-risk with --authorization-note", 3);
                RequireAuthorizationNote(parsed);
                workflow["maxFixRounds"] = number;
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
        return state;
    }
    
    private static bool HasLockEvidence(string workspace, JsonObject state)
    {
        var planPath = Path.Combine(workspace, "PLAN.md");
        if (!File.Exists(planPath) || (File.GetAttributes(planPath) & FileAttributes.ReparsePoint) != 0) return false;
        if (File.ReadLines(planPath).FirstOrDefault() != CanonicalText.OwnedMarker) return false;
        var expectedHash = state["approval"]!["planHash"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(expectedHash) || !string.Equals(expectedHash, Hashing.Sha256Hex(CanonicalText.NormalizePlan(File.ReadAllText(planPath))), StringComparison.Ordinal)) return false;
        var tasks = state["workflow"]!["tasks"] as JsonArray;
        return tasks is { Count: > 0 } && state["workflow"]!["taskCount"]?.GetValue<int>() == tasks.Count;
    }
    
    private static bool HasBuildEvidence(JsonObject state)
    {
        return !string.IsNullOrWhiteSpace(state["baselines"]!["head"]?.GetValue<string>()) &&
               !string.IsNullOrWhiteSpace(state["baselines"]!["worktree"]?.GetValue<string>()) &&
               state["models"]!["builder"] is JsonObject;
    }
}
