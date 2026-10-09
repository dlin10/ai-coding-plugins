using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

internal enum FixCompletion
{
    Unverified,
    Closed,
    PendingFullGate
}

/// <summary>The shared rules for selecting fix checks, completing attempts and admitting review.</summary>
internal static class FixGatePolicy
{
    internal const string Full = "full";
    internal const string Targeted = "targeted";
    internal const string BeforeNextRound = "beforeNextRound";
    internal const string Final = "final";

    internal static string GateMode(string? gate) => gate switch
    {
        null => Full,
        Full or Targeted => gate,
        _ => throw new ArgumentRejectedException("gate must be exactly 'full' or 'targeted' (case-sensitive)")
    };

    internal static string FullGateTiming(string? fullGate) => fullGate switch
    {
        null => BeforeNextRound,
        BeforeNextRound or Final => fullGate,
        _ => throw new ArgumentRejectedException("fullGate must be exactly 'beforeNextRound' or 'final' (case-sensitive)")
    };

    internal static IReadOnlyList<GateCommand> SelectGates(string? gate, string plan) =>
        GateMode(gate) == Targeted ? [PlanGates.FixGate(plan)] : PlanGates.RunWideGates(plan);

    internal static FixCompletion Completion(string? gate, BuildResult result)
    {
        var mode = GateMode(gate);
        if (result.Gate?.Outcome == "passed")
            return mode == Targeted ? FixCompletion.PendingFullGate : FixCompletion.Closed;

        return mode == Full && result.Gate?.Outcome == "not_executable"
                            && result.Status == "done" && result.Verification.Outcome == "passed"
            ? FixCompletion.Closed
            : FixCompletion.Unverified;
    }

    internal static void RequireReviewReady(string? fullGate, bool pending)
    {
        if (FullGateTiming(fullGate) == BeforeNextRound && pending)
            throw new ArgumentRejectedException("code review is blocked by fixes pending full host verification before the next round");
    }
}
