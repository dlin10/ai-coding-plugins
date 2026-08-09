using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlanForgeFlow.Cli;
using PlanForgeFlow.Infrastructure.Process;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Workflow.Planning;

internal static class CanonicalText
{
    public static string NormalizePlan(string value)
    {
        value = NormalizeText(value, "human plan");
        if (value.Contains("<proposed_plan>", StringComparison.Ordinal) || value.Contains("</proposed_plan>", StringComparison.Ordinal))
        {
            throw new CliFailure("usage", "human plan contains a proposed-plan terminator");
        }

        return value;
    }

    public static string NormalizeReviewLog(string value) => NormalizeText(value, "review log");

    private static string NormalizeText(string value, string label)
    {
        if (value is null) throw new CliFailure("usage", $"{label} is required");
        if (value.Contains('\0')) throw new CliFailure("usage", $"{label} contains a NUL byte");
        if (Encoding.UTF8.GetByteCount(value) > 256 * 1024) throw new CliFailure("usage", $"{label} exceeds the size bound");
        if (value.Length > 0 && value[0] == '\uFEFF') value = value[1..];
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        value = value.TrimEnd('\n') + "\n";

        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }

            throw new CliFailure("usage", "human plan contains an unpaired surrogate");
        }

        return value;
    }

    public static IReadOnlyList<PlanTask> ParseTasks(string plan)
    {
        var normalized = NormalizePlan(plan);
        var approachHeadings = Regex.Matches(normalized, "^## Approach$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (approachHeadings.Count != 1) throw new CliFailure("state", "PLAN.md must contain exactly one ## Approach section", 3);
        var approach = normalized[(approachHeadings[0].Index + approachHeadings[0].Length)..];
        var nextHeading = Regex.Match(approach, "\\n##\\s+", RegexOptions.CultureInvariant);
        if (nextHeading.Success) approach = approach[..nextHeading.Index];
        var matches = Regex.Matches(approach, "(?ms)^\\s*(\\d+)\\.\\s+(.+?)(?=^\\s*\\d+\\.\\s+|\\z)");
        if (matches.Count == 0) throw new CliFailure("state", "PLAN.md Approach section has no numbered tasks", 3);
        var tasks = new List<PlanTask>();
        for (var index = 0; index < matches.Count; index++)
        {
            var number = int.Parse(matches[index].Groups[1].Value, CultureInfo.InvariantCulture);
            if (number != index + 1) throw new CliFailure("state", "PLAN.md tasks must be numbered 1..N in order", 3);
            var text = matches[index].Groups[2].Value.Trim();
            tasks.Add(new PlanTask(number, Hashing.Sha256Hex(text), text));
        }

        return tasks;
    }
}
