using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlanForge.Acts;

/// <summary>
/// Measurements of the current draft, and whether they owe the user an offer to split the plan: the
/// first-draft rule before round 1, growth since the last offer after it. See docs/adr/0048.
/// </summary>
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
/// <param name="SplitSuggested">Whether the user is owed a split offer before the next round.</param>
/// <param name="SplitReason">
/// Why a plan past its first round is offered a split again: <c>crossed</c> the threshold or
/// <c>doubled</c> since the checkpoint. Null when no offer is owed, and for the first-draft offer.
/// </param>
/// <param name="SplitCheckpoint">The Approach size that growth is measured from; set only with <paramref name="SplitReason"/>.</param>
internal sealed partial record PlanShape(int Characters, int TaskCount, int TaskCharacters, int LargestTaskCharacters,
                                         int RequirementsCharacters, int ApproachCharacters,
                                         double? ApproachToRequirementsRatio, int RequirementItemCount,
                                         int NamedTestClassCount, int MatrixTestClassCount, bool SplitSuggested,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                         string? SplitReason = null,
                                         [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                         int? SplitCheckpoint = null)
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

    /// <summary>
    /// Settles whether this measurement owes the user a split offer. Before round 1 the first-draft
    /// rule stands as <see cref="Of"/> measured it. After it, an offer is owed only when the plan has
    /// grown since the last one: it <c>crossed</c> the threshold from below, or <c>doubled</c> from a
    /// checkpoint already above it. A plan under the threshold is never offered, however far it has
    /// doubled. The checkpoint moves to the current Approach whenever an offer is owed, so each later
    /// offer needs another doubling, and before round 1 it follows every draft.
    /// </summary>
    /// <param name="reviewRounds">Plan-review rounds run so far.</param>
    /// <param name="checkpoint">
    /// The Approach size the last offer was owed at, or the latest draft's before round 1. Null for a
    /// run that recorded none, which starts measuring here and is offered nothing.
    /// </param>
    /// <returns>This shape with its offer settled, and the Approach size the run keeps as its checkpoint.</returns>
    public (PlanShape Shape, int Checkpoint) Offer(int reviewRounds, int? checkpoint)
    {
        if (reviewRounds == 0) return (this, ApproachCharacters);
        if (checkpoint is not { } since) return (this with { SplitSuggested = false }, ApproachCharacters);

        var reason = ApproachCharacters < SplitApproachCharacters ? null
                     : since < SplitApproachCharacters ? "crossed"
                     : ApproachCharacters >= 2 * since ? "doubled"
                     : null;
        return reason is null
            ? (this with { SplitSuggested = false }, since)
            : (this with { SplitSuggested = true, SplitReason = reason, SplitCheckpoint = since }, ApproachCharacters);
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
