using System.Text.RegularExpressions;
using PlanForge.Acts;

namespace PlanForge.Evidence;

/// <summary>A contiguous piece of the plan used by the Evidence map.</summary>
/// <param name="Id">The outline, task or section-entry id, disambiguated in document order.</param>
/// <param name="Kind">Requirement, exclusion, gate, fixGate, task or other.</param>
/// <param name="Label">The first non-blank line, trimmed and cut to eighty characters.</param>
/// <param name="Text">The exact original plan text belonging to this unit.</param>
/// <param name="Hash">The PlanOutline hash of the exact text.</param>
/// <param name="Start">The starting character offset in the original plan.</param>
/// <param name="Length">The number of original characters in the unit.</param>
internal sealed record PlanUnit(string Id, string Kind, string Label, string Text, string Hash, int Start, int Length);

/// <summary>Refines the plan outline into a partition of evidence-bearing units.</summary>
internal static partial class PlanUnits
{
    /// <summary>Partitions the original text without changing outline task boundaries or gate grammar.</summary>
    /// <param name="plan">The complete plan, with its original line endings.</param>
    public static IReadOnlyList<PlanUnit> Of(string plan)
    {
        var targets = PlanOutline.Of(plan).Targets;
        var refinements = Refine(plan, targets, "## Requirements")
            .Concat(Refine(plan, targets, "## Gates")).ToDictionary(unit => unit.Start);
        var units = new List<PlanUnit>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var task = 0;
        var end = 0;
        foreach (var target in targets)
        {
            var id = target.Kind == "task" ? $"T{++task}" : target.Id;
            if (target.Start < end) continue;
            if (refinements.TryGetValue(target.Start, out var refined))
            {
                do
                {
                    units.Add(refined);
                    end = refined.Start + refined.Length;
                } while (refinements.TryGetValue(end, out refined));
            }
            else
            {
                units.Add(Unit(plan, id, target.Kind == "task" ? "task" : "other", target.Start, target.Start + target.Length));
            }
        }

        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            counts.TryGetValue(unit.Id, out var count);
            counts[unit.Id] = ++count;
            if (count > 1) units[index] = unit with { Id = $"{unit.Id}~{count}" };
        }
        return units;
    }

    /// <summary>Refines the first real section only when both boundaries agree with the outline.</summary>
    /// <param name="plan">The complete original plan text.</param>
    /// <param name="targets">The outline targets whose boundaries must be preserved.</param>
    /// <param name="heading">The exact Requirements or Gates heading to find.</param>
    private static IReadOnlyList<PlanUnit> Refine(string plan, IReadOnlyList<PlanTarget> targets, string heading)
    {
        var start = -1;
        var end = plan.Length;
        var offset = 0;
        var inGates = false;
        var sawGates = false;
        char fenceCharacter = default;
        var fenceLength = 0;
        var entries = new List<(int Start, string Id, string Kind)>();
        foreach (var originalLine in plan.Split('\n'))
        {
            var lineStart = offset;
            offset += originalLine.Length + 1;
            var line = originalLine.EndsWith('\r') ? originalLine[..^1] : originalLine;
            if (fenceLength > 0)
            {
                if (PlanGates.ClosesFixFence(line, fenceCharacter, fenceLength)) fenceLength = 0;
                continue;
            }
            if (PlanGates.FixSectionEnd().IsMatch(line))
            {
                inGates = !sawGates && line == "## Gates";
                if (inGates) sawGates = true;
            }
            if (start < 0 && line == heading)
            {
                start = lineStart;
                continue;
            }
            if (start >= 0 && PlanGates.FixSectionEnd().IsMatch(line))
            {
                end = lineStart;
                break;
            }

            var code = line;
            if (start >= 0 && heading == "## Requirements")
            {
                var requirement = RequirementEntry().Match(line);
                if (requirement.Success) entries.Add((lineStart, requirement.Groups[1].Value, "requirement"));
                else if (ExclusionEntry().IsMatch(line)) entries.Add((lineStart, "X", "exclusion"));
            }
            if (inGates)
            {
                var fix = PlanGates.FixLabel().Match(line);
                var gate = PlanGates.RealGEntry().Match(line);
                if (fix.Success)
                {
                    if (start >= 0 && heading == "## Gates") entries.Add((lineStart, "F", "fixGate"));
                    code = line[fix.Length..];
                }
                if (gate.Success)
                {
                    if (start >= 0 && heading == "## Gates")
                        entries.Add((lineStart, gate.Value.Split("**")[1].TrimEnd('.', ':'), "gate"));
                    code = line[gate.Length..];
                }
            }
            if (PlanGates.OpensFixFence(code, out fenceCharacter, out var length)) fenceLength = length;
        }

        var first = targets.FirstOrDefault(target => target.Start == start);
        if (first is null || (end != plan.Length && !targets.Any(target => target.Start == end))) return [];
        entries.Insert(0, (start, first.Id, "other"));
        var units = new List<PlanUnit>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            units.Add(Unit(plan, entry.Id, entry.Kind, entry.Start, index + 1 < entries.Count ? entries[index + 1].Start : end));
        }
        return units;
    }

    /// <summary>Builds a unit from an exact slice of the original plan.</summary>
    /// <param name="plan">The original plan text.</param>
    /// <param name="id">The unit's undisambiguated id.</param>
    /// <param name="kind">The semantic kind of the unit.</param>
    /// <param name="start">The inclusive start offset.</param>
    /// <param name="end">The exclusive end offset.</param>
    private static PlanUnit Unit(string plan, string id, string kind, int start, int end)
    {
        var text = plan[start..end];
        var label = text.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "(blank)";
        if (label.Length > 80) label = label[..80];
        return new PlanUnit(id, kind, label, text, PlanOutline.Hash(text), start, end - start);
    }

    /// <summary>Matches only a numbered, case-sensitive R entry at the start of a line.</summary>
    [GeneratedRegex(@"^[ \t]*\d+\.[ \t]+\*\*(R\d+)[.:]?\*\*[:.]?(?=[ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RequirementEntry();

    /// <summary>Matches an Out-of-scope block at the start of a line, ignoring case.</summary>
    [GeneratedRegex(@"^[ \t]*\*\*Out of scope[.:]?\*\*[:.]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExclusionEntry();
}
