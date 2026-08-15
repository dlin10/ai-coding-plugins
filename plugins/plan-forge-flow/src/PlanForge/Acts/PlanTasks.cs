using System.Globalization;
using System.Text.RegularExpressions;

namespace PlanForge.Acts;

internal sealed record PlanTask(int Number, string Text);

/// <summary>
/// Splits an approved plan into the tasks the Builder works one at a time. Ported from the old
/// CanonicalText, minus the content hashing — nothing downstream binds to a plan hash any more.
/// </summary>
internal static partial class PlanTasks
{
    public static IReadOnlyList<PlanTask> Parse(string plan)
    {
        var normalized = plan.Replace("\r\n", "\n", StringComparison.Ordinal);

        var headings = ApproachHeading().Matches(normalized);
        if (headings.Count != 1)
            throw new PlanShapeException("the plan must contain exactly one '## Approach' section");

        var approach = normalized[(headings[0].Index + headings[0].Length)..];
        var nextHeading = NextHeading().Match(approach);
        if (nextHeading.Success) approach = approach[..nextHeading.Index];

        var matches = NumberedTask().Matches(approach);
        if (matches.Count == 0)
            throw new PlanShapeException("the plan's Approach section has no numbered tasks");

        var tasks = new List<PlanTask>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var number = int.Parse(matches[index].Groups[1].Value, CultureInfo.InvariantCulture);
            if (number != index + 1)
                throw new PlanShapeException("plan tasks must be numbered 1..N, in order");

            tasks.Add(new PlanTask(number, matches[index].Groups[2].Value.Trim()));
        }

        return tasks;
    }

    [GeneratedRegex("^## Approach$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ApproachHeading();

    [GeneratedRegex("\\n##\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex NextHeading();

    [GeneratedRegex("(?ms)^\\s*(\\d+)\\.\\s+(.+?)(?=^\\s*\\d+\\.\\s+|\\z)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedTask();
}

internal sealed class PlanShapeException(string message) : Exception(message);
