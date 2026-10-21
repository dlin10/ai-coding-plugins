using System.Globalization;
using System.Text.RegularExpressions;

namespace PlanForge.Acts;

internal sealed record PlanTask(int Number, string Text);

internal sealed record PlanTaskLayout(int Start, int End, IReadOnlyList<int> TaskStarts);

/// <summary>
/// Splits an approved plan into the tasks the Builder works one at a time. Ported from the old
/// CanonicalText, minus the content hashing — nothing downstream binds to a plan hash any more.
/// </summary>
internal static partial class PlanTasks
{
    internal static PlanTaskLayout? Locate(string plan)
    {
        var normalized = plan.Replace("\r\n", "\n", StringComparison.Ordinal);
        var headings = ApproachHeading().Matches(normalized);
        if (headings.Count != 1) return null;

        var originalIndices = new int[normalized.Length + 1];
        var normalizedIndex = 0;
        for (var index = 0; index < plan.Length; index++)
        {
            if (plan[index] == '\r' && index + 1 < plan.Length && plan[index + 1] == '\n') continue;
            originalIndices[normalizedIndex++] = index;
        }
        originalIndices[normalized.Length] = plan.Length;

        var approachStart = headings[0].Index + headings[0].Length;
        var approach = normalized[approachStart..];
        var nextHeading = NextHeading().Match(approach);
        var end = nextHeading.Success ? approachStart + nextHeading.Index + 1 : normalized.Length;
        if (nextHeading.Success) approach = approach[..nextHeading.Index];

        var taskStarts = new List<int>();
        foreach (Match match in NumberedTask().Matches(approach))
        {
            var digit = approachStart + match.Groups[1].Index;
            var lineStart = normalized.LastIndexOf('\n', digit - 1) + 1;
            taskStarts.Add(originalIndices[lineStart]);
        }

        return new PlanTaskLayout(originalIndices[headings[0].Index], originalIndices[end], taskStarts);
    }

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

    /// <summary>
    /// The first task whose text differs between two versions of a plan, which is where a build of the
    /// later version has to start again. A task inserted or removed there shifts every task after it,
    /// so they differ too; text inserted between two tasks belongs to the one before it, as it does
    /// for the build. Task numbers and line breaks are not compared, since the Builder reads neither.
    /// </summary>
    /// <param name="before">The plan as it was.</param>
    /// <param name="after">The plan as it is now.</param>
    /// <returns>
    /// The first differing task position, numbered from 1, or null when both have the same tasks; 1
    /// when either has no task shape, since nothing built can be matched to it.
    /// </returns>
    internal static int? FirstChanged(string before, string after)
    {
        IReadOnlyList<PlanTask> was, now;
        try
        {
            was = Parse(before);
            now = Parse(after);
        }
        catch (Exception error) when (error is PlanShapeException or FormatException or OverflowException)
        {
            return 1;
        }

        var shared = Math.Min(was.Count, now.Count);
        for (var index = 0; index < shared; index++)
            if (!string.Equals(was[index].Text, now[index].Text, StringComparison.Ordinal)) return index + 1;
        return was.Count == now.Count ? null : shared + 1;
    }

    public static string Brief(string plan)
    {
        var heading = ApproachHeading().Match(plan);
        return plan[..heading.Index];
    }

    [GeneratedRegex("^## Approach\\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ApproachHeading();

    [GeneratedRegex("\\n##\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex NextHeading();

    [GeneratedRegex("(?ms)^\\s*(\\d+)\\.\\s+(.+?)(?=^\\s*\\d+\\.\\s+|\\z)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedTask();
}

internal sealed class PlanShapeException(string message) : Exception(message);
