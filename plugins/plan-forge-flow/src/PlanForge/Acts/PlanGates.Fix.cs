using System.Text.RegularExpressions;
using PlanForge.Run;

namespace PlanForge.Acts;

internal static partial class PlanGates
{
    /// <summary>The unique executable Fix gate in the first real Gates section, requiring a numbered G entry.</summary>
    /// <param name="plan">The complete plan text, including its full verification gates.</param>
    public static GateCommand FixGate(string plan)
    {
        var lines = plan.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inSection = false;
        var hasG = false;
        var labels = new List<(int Line, int Length)>();
        var sectionEnd = lines.Length;
        char fenceCharacter = default;
        var fenceLength = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (fenceLength > 0)
            {
                if (ClosesFixFence(line, fenceCharacter, fenceLength)) fenceLength = 0;
                continue;
            }

            if (!inSection && line == "## Gates")
            {
                inSection = true;
                continue;
            }
            if (inSection && FixSectionEnd().IsMatch(line))
            {
                sectionEnd = i;
                break;
            }

            var code = line;
            if (inSection)
            {
                var label = FixLabel().Match(line);
                if (label.Success)
                {
                    labels.Add((i, label.Length));
                    code = line[label.Length..];
                }
                var g = RealGEntry().Match(line);
                if (g.Success)
                {
                    hasG = true;
                    code = line[g.Length..];
                }
            }
            if (OpensFixFence(code, out fenceCharacter, out var length)) fenceLength = length;
        }

        if (labels.Count != 1)
            throw new ArgumentRejectedException(labels.Count == 0
                ? "targeted requires exactly one Fix gate label in the first real ## Gates section; none was found"
                : "targeted requires exactly one Fix gate label in the first real ## Gates section; duplicate labels were found");
        if (!hasG)
            throw new ArgumentRejectedException("targeted requires numbered **G<n>** entries in the first real ## Gates section");

        var (lineIndex, labelLength) = labels[0];
        var text = string.Join('\n', lines[lineIndex..sectionEnd])[labelLength..];
        var command = FixLeadingCode(text);
        if (command is null)
            throw new ArgumentRejectedException("targeted requires an executable Fix gate: inline code or a closed fenced command must immediately follow the label");
        return new GateCommand("Fix gate", command);
    }

    private static string? FixLeadingCode(string text)
    {
        var trimmed = text.TrimStart();
        var lines = trimmed.Split('\n');
        if (OpensFixFence(lines[0], out var character, out var length))
        {
            for (var i = 1; i < lines.Length; i++)
                if (ClosesFixFence(lines[i], character, length))
                    return Normalize(string.Join('\n', lines[1..i]));
            return null;
        }
        var inline = InlineCode().Match(trimmed);
        return inline.Success ? Normalize(inline.Groups[1].Value) : null;
    }

    private static bool OpensFixFence(string line, out char character, out int length)
    {
        var trimmed = line.TrimStart();
        character = trimmed.Length > 0 ? trimmed[0] : default;
        length = 0;
        if (character is not ('`' or '~')) return false;
        while (length < trimmed.Length && trimmed[length] == character) length++;
        return length >= 3;
    }

    private static bool ClosesFixFence(string line, char character, int length)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= length && trimmed.All(value => value == character);
    }

    [GeneratedRegex(@"^[ \t]*(?:\d+\.[ \t]+)?\*\*Fix gate:?\*\*:?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FixLabel();

    [GeneratedRegex(@"^[ \t]*\d+\.[ \t]+\*\*G\d+[.:]?\*\*[:.]?(?=[ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RealGEntry();

    [GeneratedRegex(@"^#{1,2}[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex FixSectionEnd();
}
