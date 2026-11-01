using System.Text.Json.Nodes;
using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// When a review round past its cap runs without the user: the round before the last found more than
/// the last, the last left no blocker, and, in plan review, less than half of the last round's new
/// findings were fallout. Plan review and code review share it because they share the cap. See
/// docs/adr/0041 and docs/adr/0049.
/// </summary>
/// <remarks>
/// Automatic rounds do not raise the cap, so every round after one is decided here again, and a
/// grant still raises the cap by exactly one. No count of automatic rounds is needed to end them: a
/// finding count is never negative and must fall every time, so a round that left N findings is
/// followed by at most N automatic rounds before one cannot fall and the user is asked.
/// </remarks>
internal static class ReviewCap
{
    /// <summary>The counts a round past the cap continues on, or null when the user has to be asked.</summary>
    /// <param name="tallies">What each round of the act left, oldest first; null for a run begun before rounds were tallied.</param>
    /// <param name="fallout">
    /// How the orchestrator labelled the last round's new findings, or null where the act labels none
    /// or the round has none to label. Half the round or more as fallout ends the chain of automatic
    /// rounds: the revisions are what keeps producing findings, so another round would only find more.
    /// </param>
    /// <returns>The last two rounds' finding counts as <c>4 → 2</c>.</returns>
    internal static string? Continuation(IReadOnlyList<ReviewTally>? tallies, FalloutTally? fallout = null)
    {
        if (tallies is not { Count: >= 2 } || fallout is { Mostly: true }) return null;

        var (before, last) = (tallies[^2], tallies[^1]);
        return last.Findings < before.Findings && !last.Blocker ? $"{before.Findings} → {last.Findings}" : null;
    }

    /// <summary>
    /// What a round leaves for the next cap decision: its new findings and the unresolved ones it
    /// assessed as still present, since an open finding the critic stopped repeating is not a fall.
    /// </summary>
    /// <param name="critique">The round's identified critique.</param>
    /// <param name="ledger">The ledger the critique was ingested into, which holds the unresolved findings' severities.</param>
    /// <param name="phase">The review phase whose projection the critic assessed.</param>
    /// <returns>The round's finding count and whether any of them is a blocker.</returns>
    internal static ReviewTally Tally(Critique critique, DecisionLedger ledger, LedgerPhase phase)
    {
        var stillPresent = (critique.UnresolvedAssessments ?? []).Where(assessment => assessment.StillPresent)
                                                                  .Select(assessment => assessment.FindingId)
                                                                  .ToHashSet(StringComparer.Ordinal);
        var severities = critique.Findings.Select(finding => finding.Severity)
                                 .Concat(ledger.Project(phase)
                                               .Where(entry => stillPresent.Contains(entry.FindingId))
                                               .Select(entry => entry.Finding.Severity))
                                 .ToArray();
        return new ReviewTally(severities.Length, severities.Contains("blocker", StringComparer.Ordinal));
    }

    /// <summary>Puts the counts an automatic round ran on beside the round's result, so the orchestrator can say so.</summary>
    /// <param name="json">The round's result.</param>
    /// <param name="continuation">The counts from <see cref="Continuation"/>, or null for a round that needed none.</param>
    /// <returns>The result, with <c>continuedAutomatically</c> when the round ran past the cap without asking.</returns>
    internal static string Attach(string json, string? continuation)
    {
        if (continuation is null) return json;

        var result = JsonNode.Parse(json)!.AsObject();
        result["continuedAutomatically"] = continuation;
        return result.ToJsonString();
    }
}
