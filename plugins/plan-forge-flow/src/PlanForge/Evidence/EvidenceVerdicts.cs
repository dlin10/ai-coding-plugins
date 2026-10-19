namespace PlanForge.Evidence;

/// <summary>Settles checked Evidence judgments and items in R12's precedence order.</summary>
internal static class EvidenceVerdicts
{
    /// <summary>Settles one pair, preserving a valid contradiction even when its obligation is unchecked.</summary>
    /// <param name="relationship">The checked relationship answer or its unchecked reason.</param>
    /// <param name="obligation">The checked obligation answer or its unchecked reason.</param>
    public static string Pair(EvidenceQuestionOutcome relationship, EvidenceQuestionOutcome obligation)
    {
        if (relationship.Answer?.Choice == "contradicts") return "contradiction";
        if (relationship.Answer is null || obligation.Answer is null) return "unchecked";
        if (relationship.Answer.Choice == "insufficient_context" || obligation.Answer.Choice == "insufficient_context")
            return "uncertain";
        if (obligation.Answer.Choice == "explicit_obligation")
            return relationship.Answer.Choice == "supports" ? "covered" : "uncertain";
        if (obligation.Answer.Choice == "explicit_exclusion") return "excluded";
        return "none";
    }

    /// <summary>Settles every item once, with no relevance question for an implication.</summary>
    /// <param name="relevance">The checked relevance outcome, or null for an implication.</param>
    /// <param name="pairVerdicts">The verdicts of all candidate pairs.</param>
    /// <param name="hasNonExclusionCandidate">Whether any candidate unit is not an exclusion.</param>
    public static string Item(EvidenceQuestionOutcome? relevance, IReadOnlyList<string> pairVerdicts,
                              bool hasNonExclusionCandidate)
    {
        if (pairVerdicts.Contains("contradiction")) return "contradiction";
        if ((relevance is not null && relevance.Answer is null) || pairVerdicts.Contains("unchecked")) return "unchecked";
        if (relevance?.Answer?.Choice == "context_only") return "context_only";
        if (pairVerdicts.Contains("covered")) return "covered";
        if (pairVerdicts.Contains("excluded")) return "excluded";
        if (relevance?.Answer?.Choice == "insufficient_context" || pairVerdicts.Contains("uncertain")) return "uncertain";
        if (!hasNonExclusionCandidate) return "no_candidates";
        return "no_coverage_established";
    }

    /// <summary>Identifies every status requiring attention and every item whose source changed.</summary>
    /// <param name="status">The settled item status.</param>
    /// <param name="sourceChanged">Whether the current locator check differs from its recorded check.</param>
    public static bool IsFlag(string status, bool sourceChanged) =>
        sourceChanged || status is "contradiction" or "unchecked" or "uncertain" or "no_candidates" or "no_coverage_established";
}
