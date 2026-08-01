namespace PlanForgeFlow;

internal static class PromptClassifier
{
    public const string DesktopEmbeddedPrefix = "PLEASE IMPLEMENT THIS PLAN:";
    public const string ClearContextPrefix = "A previous agent produced the plan below to accomplish the user's task. Implement the plan in a fresh context. Treat the plan as the source of user intent, re-read files as needed, and carry the work through implementation and verification.";

    public static string Classify(string prompt)
    {
        if (prompt.TrimEnd('\r', '\n') is "Implement the plan." or "Yes, implement this plan") return "same-context";
        if (prompt.StartsWith(DesktopEmbeddedPrefix + "\n", StringComparison.Ordinal)) return "same-context-embedded";
        if (prompt.StartsWith(ClearContextPrefix + "\n\n", StringComparison.Ordinal)) return "clear-context";
        return "other";
    }
}