using System.Text;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal static class HookService
{
    private const int MaxHookInputBytes = 256 * 1024;

    public static int Run(string input)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(input) > MaxHookInputBytes) return 0;
            var eventData = JsonNode.Parse(input)?.AsObject();
            if (eventData is null || eventData["cwd"] is null) return 0;
            var workspace = RepositoryPaths.CanonicalWorkspaceRoot(eventData["cwd"]!.GetValue<string>());
            var prompt = eventData["prompt"]?.GetValue<string>() ?? string.Empty;
            var mode = "unknown";
            var transcriptPath = eventData["transcript_path"]?.GetValue<string>();
            var turnId = eventData["turn_id"]?.GetValue<string>();
            TranscriptReader.Transcript? transcript = null;
            if (!string.IsNullOrWhiteSpace(transcriptPath) && !string.IsNullOrWhiteSpace(turnId))
            {
                try
                {
                    transcript = TranscriptReader.ReadDocument(transcriptPath);
                    mode = TranscriptReader.ModeForTurn(transcript.Records, turnId);
                }
                catch { transcript = null; }
            }

            if (prompt.Contains("$forge", StringComparison.OrdinalIgnoreCase) && mode != "plan")
            {
                Console.Out.WriteLine(new JsonObject
                {
                    ["decision"] = "block",
                    ["reason"] = "Plan Forge Act 1 requires Plan mode. Toggle /plan or Shift+Tab, then resubmit the $forge prompt.",
                }.ToJsonString());
                return 0;
            }

            if (mode != "default" || transcript is null) return 0;
            var plan = TranscriptReader.LatestPlan(transcript.Records);
            if (plan is null) return 0;
            PendingPlan.Write(workspace, plan);
            Console.Out.WriteLine(new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "UserPromptSubmit",
                    ["additionalContext"] = "Forge captured the latest proposed plan. Before implementation, run planforge plan materialize with the review log, completed review rounds, and pinned reviewer and builder selections.",
                },
            }.ToJsonString());
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
