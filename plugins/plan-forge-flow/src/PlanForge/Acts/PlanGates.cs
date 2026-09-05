using System.Text.RegularExpressions;

namespace PlanForge.Acts;

/// <param name="Label">`Gate` for a task's own gate, `G1`…`Gn` for a run-wide one.</param>
/// <param name="Command">The command as written, with a fenced block's lines joined by newlines.</param>
internal sealed record GateCommand(string Label, string Command);

/// <summary>
/// Finds the command a gate names, when it names one. A gate is executable only when the code
/// comes first — <c>**Gate:** `dotnet test …`</c> — because a gate that opens with prose and
/// mentions a file in backticks further on (<c>тесты `Rules/FooTests.cs` …</c>) would otherwise be
/// executed as that file name, and a spurious failure there is worse than a self-report.
/// </summary>
internal static partial class PlanGates
{
    /// <summary>The task's own gate: the code right after the first <c>**Gate:**</c>, or nothing.</summary>
    public static GateCommand? TaskGate(string taskText)
    {
        var label = GateLabel().Match(taskText);
        if (!label.Success) return null;

        return LeadingCode(taskText[(label.Index + label.Length)..]) is { } command
            ? new GateCommand("Gate", command)
            : null;
    }

    /// <summary>Whether the task text carries a <c>**Gate:**</c> label at all, executable or not.</summary>
    public static bool HasGate(string taskText) => GateLabel().IsMatch(taskText);

    /// <summary>
    /// The executable entries of the plan's <c>## Gates</c> section, in order. Entries that open
    /// with prose are skipped rather than reported: they are the orchestrator's to run, as before.
    /// </summary>
    public static IReadOnlyList<GateCommand> RunWideGates(string plan)
    {
        var gates = new List<GateCommand>();
        foreach (var (label, text) in RunWideEntries(plan))
        {
            if (LeadingCode(text) is { } command) gates.Add(new GateCommand(label, command));
        }

        return gates;
    }

    /// <summary>Whether the plan has a <c>## Gates</c> section with labelled entries, executable or not.</summary>
    public static bool HasRunWideGates(string plan) => RunWideEntries(plan).Any();

    private static IEnumerable<(string Label, string Text)> RunWideEntries(string plan)
    {
        var normalized = plan.Replace("\r\n", "\n", StringComparison.Ordinal);
        var heading = GatesHeading().Match(normalized);
        if (!heading.Success) yield break;

        var section = normalized[(heading.Index + heading.Length)..];
        var next = NextHeading().Match(section);
        if (next.Success) section = section[..next.Index];

        foreach (Match entry in NumberedEntry().Matches(section))
        {
            var text = entry.Groups[1].Value;
            var label = EntryLabel().Match(text);
            if (label.Success) yield return (label.Groups[1].Value, text[(label.Index + label.Length)..]);
        }
    }

    /// <summary>
    /// The code at the very start of <paramref name="text"/>: an inline span, or a fenced block
    /// whose lines become the command one per line. Anything else is prose.
    /// </summary>
    private static string? LeadingCode(string text)
    {
        var trimmed = text.TrimStart();

        var fence = Fence().Match(trimmed);
        if (fence.Success) return Normalize(fence.Groups[1].Value);

        var inline = InlineCode().Match(trimmed);
        return inline.Success ? Normalize(inline.Groups[1].Value) : null;
    }

    private static string? Normalize(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Split('\n')
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0);
        var command = string.Join('\n', lines);
        return command.Length == 0 ? null : command;
    }

    [GeneratedRegex(@"\*\*Gate:?\*\*:?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GateLabel();

    [GeneratedRegex("^## Gates$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GatesHeading();

    [GeneratedRegex("\\n##\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex NextHeading();

    [GeneratedRegex("(?ms)^\\s*\\d+\\.\\s+(.+?)(?=^\\s*\\d+\\.\\s+|\\z)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedEntry();

    [GeneratedRegex(@"\*\*(G\d+)\.?\*\*:?", RegexOptions.CultureInvariant)]
    private static partial Regex EntryLabel();

    // A single-backtick span with no newline inside it; a fence opener would also start with a
    // backtick, so the fence is tried first.
    [GeneratedRegex(@"\A`([^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\A```[^\n]*\n(.*?)\n[ \t]*```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();
}
