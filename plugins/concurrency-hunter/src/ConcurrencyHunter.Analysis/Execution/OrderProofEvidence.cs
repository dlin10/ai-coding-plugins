using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Execution;

/// <summary>The branch of <see cref="ExecutionAnalysis.Timing"/> that decided a pair.</summary>
internal enum TimingBranch
{
    /// <summary><c>NoOverlap</c>: both accesses are of one execution whose policy does not let it overlap itself.</summary>
    SerializedExecution,

    /// <summary><c>NoOverlap</c>: both accesses are of one execution that runs once per run of the creator of their
    /// <c>TreeConfined</c> region (ADR 0020).</summary>
    CreatorRunsOnce,

    /// <summary><c>Ordered</c>: a happens-before path of the graph orders the two, whatever pass they are read in.</summary>
    HappensBeforePath,

    /// <summary><c>Ordered</c>: the completion proof of the queried passes orders the two; no path did.</summary>
    PassCompletion,

    /// <summary><c>Concurrent</c>: no branch above holds.</summary>
    Unordered
}

/// <summary>How the order of two accesses was decided, read on a diagnostic request only: never by pairing, findings or metrics.</summary>
/// <param name="Timing">The production timing of the pair.</param>
/// <param name="Branch">The branch of the production decision that gave <paramref name="Timing"/>.</param>
/// <param name="Creator">The creator of the <c>TreeConfined</c> region whose one run the query read, or null for the whole scope.</param>
/// <param name="Facts">What the analysis held that the branch read: the execution and its policy, the creator, or the path's direction.</param>
/// <param name="Frames">Every start the pass completion proof considered, in the order it considered them; empty when the production
/// decision did not run that proof.</param>
/// <param name="Steps">Every normal, exceptional and next-start transition the pass completion proof read, with its outcome; empty when
/// the production decision did not run that proof.</param>
/// <param name="Models">The library model records a proven pass completion read; empty for a branch that reads none.</param>
internal sealed record OrderProofEvidence(PairTiming Timing, TimingBranch Branch, string? Creator, IReadOnlyList<string> Facts,
                                          IReadOnlyList<PassFrameEvidence> Frames, IReadOnlyList<PassStepEvidence> Steps,
                                          IReadOnlyList<ModelRecordEvidence> Models);

/// <summary>A traced replay of the pass completion proof for one pair.</summary>
/// <param name="Proven">What the proof answered.</param>
/// <param name="Frames">Every start it considered.</param>
/// <param name="Steps">Every transition check it made.</param>
/// <param name="Models">The library model records the checks that proved the pair read.</param>
internal sealed record PassCompletionTrace(bool Proven, IReadOnlyList<PassFrameEvidence> Frames, IReadOnlyList<PassStepEvidence> Steps,
                                           IReadOnlyList<ModelRecordEvidence> Models);

/// <summary>One start the pass completion proof considered and the joins of its own pass it found for it.</summary>
/// <param name="Execution">The started execution.</param>
/// <param name="Parent">The execution that starts it, or null when it has no joinable start.</param>
/// <param name="Anchor">The kind of start: a spawn or an async call's return.</param>
/// <param name="Spawn">The spawn kind of the start, or null for a timer's creation or when it has no joinable start.</param>
/// <param name="Caller">The instance making the start.</param>
/// <param name="Operation">The operation that starts the execution.</param>
/// <param name="Joins">The joins proven to wait for this pass's start, with whether a completed fault still means completion.</param>
/// <param name="Rejected">The joins of the parent this pass's start cannot be proven to be the handle of, with the reason.</param>
internal sealed record PassFrameEvidence(string Execution, string? Parent, string? Anchor, string? Spawn, string? Caller, int Operation,
                                         IReadOnlyList<PassJoinEvidence> Joins, IReadOnlyList<PassJoinEvidence> Rejected);

/// <summary>A join the pass completion proof read.</summary>
/// <param name="Instance">The instance making the join.</param>
/// <param name="Operation">The join operation.</param>
/// <param name="CompletedFault">Whether a faulted outcome of the join still follows the work's completion.</param>
/// <param name="Reason">Why the join was rejected, or null for an accepted join.</param>
/// <param name="Model">The library model record of the joining call, or null when the join is not a modelled call.</param>
internal sealed record PassJoinEvidence(string Instance, int Operation, bool CompletedFault, string? Reason, ModelRecordEvidence? Model = null);

/// <summary>One transition check of the pass completion proof, in the order the proof made it.</summary>
/// <param name="Step">The check: <c>complete-before-next</c>, <c>complete-before-access</c>, <c>inner-pass-returns</c> or
/// <c>parent-allows</c>.</param>
/// <param name="Execution">The execution whose pass the check read.</param>
/// <param name="Other">The other execution the check relates it to, or null.</param>
/// <param name="Outcomes">Each flow question the check asked, in order, with its answer; a question it did not reach is absent.</param>
/// <param name="Result">What the check answered.</param>
internal sealed record PassStepEvidence(string Step, string Execution, string? Other, IReadOnlyDictionary<string, bool> Outcomes, bool Result)
{
    /// <summary>A check with its outcomes.</summary>
    /// <param name="step">The check.</param>
    /// <param name="execution">The execution whose pass the check read.</param>
    /// <param name="other">The other execution, or null.</param>
    /// <param name="result">What the check answered.</param>
    /// <param name="outcomes">The flow questions it asked, in order, with their answers.</param>
    internal static PassStepEvidence Of(string step, string execution, string? other, bool result, params (string Name, bool Value)[] outcomes) =>
        new(step, execution, other, outcomes.ToDictionary(outcome => outcome.Name, outcome => outcome.Value, StringComparer.Ordinal), result);
}

/// <summary>A library model record a proof read: which call it was applied to and the record's own identity.</summary>
/// <param name="Use">What the proof read it for: <c>join</c>.</param>
/// <param name="Instance">The instance making the call.</param>
/// <param name="Operation">The call operation.</param>
/// <param name="MemberId">The documentation id of the member the record describes.</param>
/// <param name="Layer">The layer the record came from.</param>
/// <param name="Relation">The run relation the proof read from the record, or null.</param>
internal sealed record ModelRecordEvidence(string Use, string Instance, int Operation, string MemberId, IrModelLayer Layer, RunRelation? Relation)
{
    /// <summary>The record of a modelled call.</summary>
    /// <param name="use">What the proof read it for.</param>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="operation">The call operation.</param>
    /// <param name="library">The model's word on the call.</param>
    /// <param name="relation">The run relation the proof read, or null.</param>
    internal static ModelRecordEvidence Of(string use, string instance, int operation, IrLibraryCall library, RunRelation? relation) =>
        new(use, instance, operation, library.MemberId, library.Layer, relation);
}
