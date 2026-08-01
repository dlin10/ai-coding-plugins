using System.Text;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed partial class CliApplication
{
    private static JsonObject MaterializePlan(CommandContext context)
    {
        var input = Console.In.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(input) > 2 * 1024 * 1024) throw new CliFailure("usage", "materialization input exceeds the size bound");
        var request = JsonNode.Parse(input)?.AsObject() ?? throw new CliFailure("usage", "materialization input must be a JSON object");
        var keys = request.Select(item => item.Key).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (!keys.SequenceEqual(new[] { "builder", "completedReviewRounds", "maxRounds", "reviewLog", "reviewer" }, StringComparer.Ordinal)) throw new CliFailure("usage", "materialization input keys are not exact");
        var reviewLog = RequestString(request, "reviewLog");
        var completedRounds = RequestInt(request, "completedReviewRounds");
        var maxRounds = RequestInt(request, "maxRounds");
        if (request["reviewer"] is not JsonObject reviewer || request["builder"] is not JsonObject builder) throw new CliFailure("usage", "materialization reviewer and builder must be JSON objects");
        var pending = PendingPlan.Read(context.Workspace);
        var result = Materializer.Materialize(RepositoryPaths.Identify(context.Workspace), pending.Plan, reviewLog, completedRounds, maxRounds, reviewer, builder, context.Args.Has("amendment"));
        PendingPlan.Delete(context.Workspace);
        return result;
    }
    
    private static string RequestString(JsonObject request, string key)
    {
        try
        {
            var value = request[key]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value)) throw new FormatException("must be a non-empty string");
            return value;
        }
        catch (Exception error)
        {
            throw new CliFailure("usage", $"materialization input {key} {error.Message}");
        }
    }
    
    private static int RequestInt(JsonObject request, string key)
    {
        try
        {
            var value = request[key]?.GetValue<int>() ?? throw new FormatException("must be an integer");
            return value;
        }
        catch (Exception error)
        {
            throw new CliFailure("usage", $"materialization input {key} {error.Message}");
        }
    }
    
    private static ForgeState LockPlan(CommandContext context)
    {
        var workspace = context.Workspace;
        var parsed = context.Args;
        var planPath = Path.Combine(workspace, ".forge", "PLAN.md");
        if (!File.Exists(planPath)) throw new CliFailure("state", "PLAN.md is missing", 3);
        var plan = CanonicalText.NormalizePlan(File.ReadAllText(planPath));
        var state = context.RequireState();
        var phase = state.Workflow.Phase;
        if (phase is not (ForgePhase.Materialized or ForgePhase.Locked))
        {
            if (!(parsed.Has("relock") && parsed.Has("amendment") && phase is (ForgePhase.Build or ForgePhase.CodeReview)))
            {
                throw new CliFailure("state", $"plan lock is not legal in phase {phase.ToWireName()}", 3);
            }
        }
    
        var tasks = CanonicalText.ParseTasks(plan);
        var completedTasks = 0;
        if (parsed.Has("relock") && parsed.Has("amendment"))
        {
            completedTasks = Math.Max(0, state.Workflow.NextTaskNumber - 1);
            var oldTasks = state.Workflow.Tasks;
            if (completedTasks > tasks.Count) throw new CliFailure("state", "relock removes completed tasks", 3);
            for (var index = 0; index < completedTasks; index++)
            {
                var oldTask = oldTasks is not null && index < oldTasks.Count ? oldTasks[index] : null;
                if (oldTask is null || oldTask.Hash != tasks[index].Hash) throw new CliFailure("state", $"relock changes completed task {index + 1}", 3);
            }
        }
        var head = new GitClient(workspace).Run(["rev-parse", "HEAD"]);
        if (head.ExitCode != 0) throw new CliFailure("environment", "could not establish the Git HEAD baseline");
        var untrackedPaths = ReviewEvidence.PathList(workspace, ["ls-files", "--others", "--exclude-standard", "-z"], "could not establish the untracked plan baseline");
        return StateStore.Update(workspace, state, current =>
        {
            current.Workflow.Phase = ForgePhase.Locked;
            current.Workflow.Tasks = tasks.ToList();
            current.Workflow.TaskCount = tasks.Count;
            current.Workflow.NextTaskNumber = completedTasks + 1;
            current.Workflow.Amendment = parsed.Has("amendment");
            if (!(parsed.Has("relock") && parsed.Has("amendment")))
            {
                current.Baselines.Head = head.Stdout.Trim();
                current.Baselines.Worktree = head.Stdout.Trim();
                current.Baselines.Untracked = ReviewEvidence.BaselineEntries(workspace, untrackedPaths);
            }
        });
    }
}
