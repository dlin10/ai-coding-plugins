namespace ConcurrencyHunter.Analysis;

/// <summary>The single owner of the outcome of a deciding library call across a scope's analyses.</summary>
public static class CallSiteOutcomes
{
    /// <summary>Classifies a call using its library eligibility and the admission of the analyses.</summary>
    /// <param name="eligibility">The library classification, or null for an executable package member.</param>
    /// <param name="admitted">Whether any analysis admitted the call, including as a producer.</param>
    /// <param name="decidesLast">Whether the call decides a check in the last analysis.</param>
    /// <param name="overBound">Whether the call's measured body count exceeds the bound.</param>
    public static string Of(string? eligibility, bool admitted, bool decidesLast, bool overBound) =>
        eligibility == "framework" ? "framework" :
        eligibility is not null ? "no-body" :
        admitted ? decidesLast ? "no-answer" : "answered" :
        overBound ? "bound" : "last-round";
}

/// <summary>The call-site answers and pair changes from the settled generated layer onward, with the scope's total analysis count.</summary>
/// <param name="Analyses">The number of analyses of the scope.</param>
/// <param name="Calls">Every package or framework call deciding from the settled analysis onward, ordered by callee and source identity.</param>
/// <param name="PairsRemoved">Pair identities of the settled generated layer absent from the last analysis.</param>
/// <param name="PairsAdded">Pair identities of the last analysis absent from the settled generated layer.</param>
public sealed record CallSiteCoverage(int Analyses, IReadOnlyList<CallSiteCallCoverage> Calls, int PairsRemoved, int PairsAdded);

/// <summary>How one deciding package or framework call ended.</summary>
/// <param name="Callee">The callee as the deciding-gap list names it.</param>
/// <param name="Place">The call's target-relative path and line number.</param>
/// <param name="Producers">Its producers' target-relative paths and line numbers, in source identity order.</param>
/// <param name="Bodies">The reachable library body count, or zero when never counted.</param>
/// <param name="Outcome">The call-site answer or the reason the call was not admitted.</param>
public sealed record CallSiteCallCoverage(string Callee, string Place, IReadOnlyList<string> Producers, int Bodies, string Outcome);
