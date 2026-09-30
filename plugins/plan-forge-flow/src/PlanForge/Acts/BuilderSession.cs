using PlanForge.Run;
using PlanForge.Vendors;

namespace PlanForge.Acts;

/// <summary>
/// Which builder session a turn runs in, decided here for every act that starts a builder. A session
/// covers one scope, a plan task or a code-review round: the scope's first turn starts fresh, with
/// the Builder Brief and the user's instructions, and a retry inside it resumes, because a retry
/// should remember the attempt it repeats. One session for the whole run grew claude builders past
/// 900k tokens, compacted the Brief into a summary, and cost about 38% more; see docs/adr/0026.
/// </summary>
internal static class BuilderSession
{
    internal static string TaskScope(int taskNumber) => $"task {taskNumber}";

    internal static string FixScope(int codeReviewRound) => $"code review round {codeReviewRound}";

    /// <summary>The session to resume: the recorded one, when the same vendor ran it for the same scope.</summary>
    /// <param name="state">The run state holding the recorded session.</param>
    /// <param name="vendorId">The vendor about to run the builder turn.</param>
    /// <param name="scope">The turn's scope, from <see cref="TaskScope"/> or <see cref="FixScope"/>.</param>
    /// <returns>The token to resume, or <see langword="null"/> for a fresh session.</returns>
    internal static string? ResumeToken(RunState state, string vendorId, string scope) =>
        string.Equals(state.BuilderVendor, vendorId, StringComparison.Ordinal)
        && string.Equals(state.BuilderSessionScope, scope, StringComparison.Ordinal)
        && state.BuilderSessionId is { Length: > 0 } token
            ? token
            : null;

    /// <summary>
    /// The session as the run should remember it after a turn. A cut-short turn keeps its token like
    /// any other: the id arrives on the vendor's first stream line, long before the answer that never
    /// came, and without it the retry starts with no memory of the attempt it repeats. A fresh turn
    /// the vendor gave no token leaves none, since the one before belonged to another scope.
    /// </summary>
    /// <param name="state">The run state before the turn.</param>
    /// <param name="session">The vendor session the turn ran in.</param>
    /// <param name="vendorId">The vendor that ran the turn.</param>
    /// <param name="scope">The turn's scope, from <see cref="TaskScope"/> or <see cref="FixScope"/>.</param>
    /// <param name="resumedFrom">The token the turn resumed, <see langword="null"/> for a fresh turn.</param>
    /// <returns>The run state with the turn's session recorded.</returns>
    internal static RunState Record(RunState state, IVendorSession session, string vendorId, string scope,
                                    string? resumedFrom) =>
        state with
        {
            BuilderSessionId = session.ResumeToken ?? resumedFrom ?? string.Empty,
            BuilderVendor = vendorId,
            BuilderSessionScope = scope
        };

    /// <summary>No session to resume: the next builder turn starts fresh whatever its scope.</summary>
    /// <param name="state">The run state holding the session to forget.</param>
    /// <returns>The run state without a recorded session.</returns>
    internal static RunState Forget(RunState state) =>
        state with { BuilderSessionId = string.Empty, BuilderSessionScope = null };
}
