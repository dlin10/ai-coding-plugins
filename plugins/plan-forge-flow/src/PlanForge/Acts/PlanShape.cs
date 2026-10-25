using System.Text.RegularExpressions;

namespace PlanForge.Acts;

/// <summary>Measurements of the current draft, used to offer a split before its first review.</summary>
/// <param name="Characters">All UTF-16 characters in the plan.</param>
/// <param name="TaskCount">Number of build-recognized task targets.</param>
/// <param name="TaskCharacters">Characters in all task targets.</param>
/// <param name="LargestTaskCharacters">Characters in the largest task target.</param>
/// <param name="RequirementsCharacters">Characters in the Requirements section.</param>
/// <param name="ApproachCharacters">Characters in the Approach section.</param>
/// <param name="ApproachToRequirementsRatio">Approach size divided by Requirements size, or null without Requirements.</param>
/// <param name="RequirementItemCount">Distinct R-items named in Requirements.</param>
/// <param name="NamedTestClassCount">Distinct named test classes mentioned in the plan.</param>
/// <param name="MatrixTestClassCount">Named test classes ending in MatrixTests.</param>
/// <param name="SplitSuggested">Whether the calibrated Approach size calls for a split offer.</param>
internal sealed partial record PlanShape(int Characters, int TaskCount, int TaskCharacters, int LargestTaskCharacters,
                                         int RequirementsCharacters, int ApproachCharacters,
                                         double? ApproachToRequirementsRatio, int RequirementItemCount,
                                         int NamedTestClassCount, int MatrixTestClassCount, bool SplitSuggested)
{
    private const int SplitApproachCharacters = 22_000;

    public static PlanShape Of(string plan, PlanOutline outline)
    {
        var requirements = Section(outline, "## Requirements");
        var approach = Section(outline, "## Approach");
        var tasks = outline.Targets.Where(target => target.Kind == "task").ToArray();
        var testClasses = TestClassName().Matches(plan).Select(match => match.Value)
                                         .ToHashSet(StringComparer.Ordinal);
        var requirementItems = RequirementItem().Matches(plan.AsSpan(requirements.Start, requirements.Length).ToString())
                                                .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

        return new PlanShape(outline.Characters, tasks.Length, tasks.Sum(task => task.Characters),
                             tasks.MaxBy(task => task.Characters)?.Characters ?? 0,
                             requirements.Length, approach.Length,
                             requirements.Length == 0 ? null : Math.Round((double)approach.Length / requirements.Length, 2),
                             requirementItems.Count, testClasses.Count,
                             testClasses.Count(name => name.EndsWith("MatrixTests", StringComparison.Ordinal)),
                             approach.Length >= SplitApproachCharacters);
    }

    private static (int Start, int Length) Section(PlanOutline outline, string label)
    {
        var targets = outline.Targets;
        var startIndex = -1;
        for (var index = 0; index < targets.Count; index++)
            if (targets[index].Kind == "heading" && targets[index].Label == label)
            {
                startIndex = index;
                break;
            }
        if (startIndex < 0) return (0, 0);

        var start = targets[startIndex].Start;
        for (var index = startIndex + 1; index < targets.Count; index++)
        {
            var next = targets[index];
            if (next.Kind == "heading" && (next.Label == "##" ||
                next.Label.Length > 2 && next.Label.StartsWith("##", StringComparison.Ordinal)
                && next.Label[2] is ' ' or '\t'))
                return (start, next.Start - start);
        }
        return (start, outline.Characters - start);
    }

    [GeneratedRegex(@"\bR[0-9]+[a-z]?\b", RegexOptions.CultureInvariant)]
    private static partial Regex RequirementItem();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*Tests\b", RegexOptions.CultureInvariant)]
    private static partial Regex TestClassName();
}
