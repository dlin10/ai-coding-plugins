using System.Text;
using System.Text.Json;
using PlanForgeFlow.Infrastructure.Workspace;
using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Codex;

internal static class HookService
{
    private const int MaxHookInputBytes = 256 * 1024;

    public static int Run(string input)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(input) > MaxHookInputBytes) return 0;
            var eventData = JsonSerializer.Deserialize(input, CodexJsonContext.Default.HookInput) ?? throw new JsonException("JSON value is null");
            if (string.IsNullOrWhiteSpace(eventData.Cwd)) return 0;
            var workspace = RepositoryPaths.CanonicalWorkspaceRoot(eventData.Cwd);
            var prompt = eventData.Prompt ?? string.Empty;
            var mode = "unknown";
            var transcriptPath = eventData.TranscriptPath;
            var turnId = eventData.TurnId;
            IReadOnlyList<TranscriptReader.Record>? transcript = null;
            if (!string.IsNullOrWhiteSpace(transcriptPath) && !string.IsNullOrWhiteSpace(turnId))
            {
                try
                {
                    transcript = TranscriptReader.ReadDocument(transcriptPath);
                    mode = TranscriptReader.ModeForTurn(transcript, turnId);
                }
                catch { transcript = null; }
            }

            if (IsForgeInvocation(prompt) && mode != "plan")
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new HookBlockOutput("block", "Plan Forge Act 1 requires Plan mode. Toggle /plan or Shift+Tab, then resubmit the Forge prompt."),
                    CodexJsonContext.Default.HookBlockOutput));
                return 0;
            }

            if (mode != "default" || transcript is null) return 0;
            var plan = TranscriptReader.LatestPlan(transcript);
            if (plan is null) return 0;
            PendingPlan.Write(workspace, plan);
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new HookCaptureOutput(new HookSpecificOutput(
                    "UserPromptSubmit",
                    "Forge captured the latest proposed plan. Before implementation, run planforge plan materialize with the review log, completed review rounds, and pinned reviewer and builder selections.")),
                CodexJsonContext.Default.HookCaptureOutput));
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsForgeInvocation(string prompt)
        => prompt.Contains("$forge", StringComparison.OrdinalIgnoreCase) ||
           prompt.Contains("$plan-forge-flow:forge", StringComparison.OrdinalIgnoreCase);
}
