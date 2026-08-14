using System.Globalization;
using System.Text.Json;
using PlanForgeFlow.Claude;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Cli.Commands;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Pending;
using PlanForgeFlow.Review;
using PlanForgeFlow.Serialization;
using PlanForgeFlow.Workflow.Planning;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Workflow;

internal static partial class ForgeWorkflow
{
    private const int MaxRoslynConfigBytes = 16 * 1024;

    internal static void RequireAuthorizationNote(ParsedArgs parsed)
    {
        var note = parsed.Get("authorization-note");
        if (string.IsNullOrWhiteSpace(note) || note.Length > 16 * 1024) throw new CliFailure("usage", "accepting risk requires a bounded --authorization-note");
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
    
    internal static DoctorData Doctor(string workspace,
                                      HostKind host,
                                      Func<string, string?>? readEnvironment = null,
                                      Func<ToolCheckData>? claudeProbe = null)
    {
        if (host == HostKind.Claude) AnthropicModels.ValidateDoctorEnvironment(readEnvironment ?? Environment.GetEnvironmentVariable, requireClaudeMarker: false);
        var claude = host == HostKind.Claude ? (claudeProbe ?? ClaudeCapabilities.Probe)() : null;
        var git = ToolCheck("git", ["--version"]);
        if (!git.Ok) return new DoctorData(workspace, git, File.Exists(StateStore.StatePath(workspace)), RoslynUnavailable("repository identity is unavailable because Git is unavailable"), Claude: claude);
        RepositoryIdentity repository;
        try
        {
            repository = RepositoryPaths.Identify(workspace);
        }
        catch (CliFailure error)
        {
            return new DoctorData(workspace, new ToolCheckData(false, null, error.Message), File.Exists(StateStore.StatePath(workspace)), RoslynUnavailable("repository identity is unavailable"), Claude: claude);
        }

        var forgeTarget = Path.Combine(repository.WorkspaceRoot, ".forge");
        if (host is HostKind.Cursor or HostKind.Claude && (Directory.Exists(forgeTarget) || File.Exists(forgeTarget)) &&
            !(host == HostKind.Claude && IsClaudeRecovery(repository)))
        {
            throw new CliFailure("state", $"{PendingRuns.HostName(host)} workspace already contains .forge; inspect and remove or archive the previous Forge run before starting a new run", 3);
        }

        return new DoctorData(workspace, git, File.Exists(StateStore.StatePath(workspace)), ProbeRoslynReadiness(repository), RepositoryPaths.ScopeId(repository), claude);
    }

    private static bool IsClaudeRecovery(RepositoryIdentity repository)
    {
        try
        {
            var state = StateStore.Load(repository.WorkspaceRoot);
            if (state.Host != HostKind.Claude || state.SourceRun is not { Source: var source, TransactionId: var transactionId } ||
                !source.StartsWith("claude:", StringComparison.Ordinal)) return false;
            var run = PendingRuns.Load(repository, source[7..], HostKind.Claude);
            return run.Phase is (PendingRunPhase.Materializing or PendingRunPhase.Consumed) && run.TransactionId == transactionId;
        }
        catch (CliFailure)
        {
            return false;
        }
    }

    private static RoslynReadinessData ProbeRoslynReadiness(RepositoryIdentity repository)
    {
        try
        {
            var projects = ProcessExecution.Run("git", ["-C", repository.WorkspaceRoot, "ls-files", "--cached", "--others", "--exclude-standard", "--", ":(glob)**/*.csproj", ":(glob)**/*.cs"]);
            if (projects.ExitCode != 0)
            {
                return RoslynUnavailable("could not inspect the repository for C# projects");
            }
            if (string.IsNullOrWhiteSpace(projects.Stdout))
            {
                return new RoslynReadinessData("not-applicable", false, null, null);
            }

            var configPath = Path.Combine(repository.WorkspaceRoot, ".roslynmcp.json");
            if (!File.Exists(configPath))
            {
                return new RoslynReadinessData("not-configured", true, false, "optional Roslyn MCP is not configured for this C# repository; review may use an explicit audited text fallback");
            }
            if ((File.GetAttributes(configPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(configPath).Length > MaxRoslynConfigBytes)
            {
                return new RoslynReadinessData("invalid-config", true, false, "optional Roslyn MCP config must be a small regular file", configPath);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("port", out var portElement) ||
                portElement.ValueKind != JsonValueKind.Number || !portElement.TryGetInt32(out var port) || port is < 1024 or > 65535)
            {
                return new RoslynReadinessData("invalid-config", true, false, "optional Roslyn MCP config must contain an integer port in 1024..65535", configPath);
            }

            return new RoslynReadinessData("configured", true, true, "configuration found; the host must still verify Roslyn MCP and the returned solution/project identity", configPath, port);
        }
        catch (Exception error) when (error is CliFailure or IOException or UnauthorizedAccessException or JsonException)
        {
            return RoslynUnavailable($"optional Roslyn readiness probe was inconclusive: {error.Message}");
        }
    }

    private static RoslynReadinessData RoslynUnavailable(string warning)
        => new("unavailable", null, null, warning);

    internal static void Cleanup(string workspace, bool purgeGeneratedAgents, HostKind host = HostKind.Codex)
        => Materializer.Cleanup(workspace, purgeGeneratedAgents, host);

    private static ToolCheckData ToolCheck(string fileName, IReadOnlyList<string> args)
    {
        try
        {
            var result = ProcessExecution.Run(fileName, args);
            return new ToolCheckData(result.ExitCode == 0, result.Stdout.Trim(), result.ExitCode == 0 ? null : result.Stderr.Trim());
        }
        catch (CliFailure error)
        {
            return new ToolCheckData(false, null, error.Message);
        }
    }
    
    internal static ForgeState Set(CommandContext context)
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
                var target = ForgePhases.Parse(value);
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
                    ForgeReview.RequireApprovedReviewEvidence(workspace, current);
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
        return state;
    }
    
    private static bool HasLockEvidence(string workspace, ForgeState state)
    {
        var planPath = Path.Combine(workspace, ".forge", "PLAN.md");
        if (!File.Exists(planPath) || (File.GetAttributes(planPath) & FileAttributes.ReparsePoint) != 0) return false;
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
