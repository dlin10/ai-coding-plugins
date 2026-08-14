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
            var transcriptPath = eventData.TranscriptPath;
            if (string.IsNullOrWhiteSpace(transcriptPath)) return 0;
            IReadOnlyList<TranscriptReader.Record> transcript;
            try
            {
                transcript = TranscriptReader.ReadDocument(transcriptPath);
            }
            catch
            {
                return 0;
            }

            var plan = TranscriptReader.LatestPlan(transcript);
            if (plan is null) return 0;
            PendingPlan.Write(workspace, plan);
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new HookCaptureOutput(new HookSpecificOutput(
                    "UserPromptSubmit",
                    "Forge staged the latest proposed plan, but staging is not consent to use Plan Forge Flow. Use Forge only if the user explicitly invoked $forge or directly asked to run Plan Forge Flow; generic planning, review, or implementation requests do not opt in. Without explicit opt-in, do not materialize the plan or use Forge. If the user explicitly opted in, do not materialize while the current turn is in Plan mode; on the first Default-mode implementation turn, run planforge plan materialize with the review log, completed review rounds, and pinned reviewer and builder selections before implementation.")),
                CodexJsonContext.Default.HookCaptureOutput));
            return 0;
        }
        catch
        {
            return 0;
        }
    }

}
