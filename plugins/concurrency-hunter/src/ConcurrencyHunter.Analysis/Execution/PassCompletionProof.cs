using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Execution;

internal sealed partial class HappensBefore
{
    /// <summary>
    /// The completion proof of the queried passes: work a pass of its parent starts — <c>Task.Run</c>, <c>StartNew</c>,
    /// <c>Thread.Start</c> or an async call's task — and waits for on a handle proven to be this pass's own, on every path from the start
    /// to the next pass of the same start, completes before that next pass and before what the parent does after the wait. Unlike a
    /// happens-before path it reads one pass at a time, so it holds for work started again and again; it needs the parent not to overlap
    /// itself. A call that runs a delegate again (<see cref="HeapSolution.RepeatedRuns"/>) may start its next run before this one waits,
    /// so the work of one run is ordered only with the code after that call, unless the call is admitted as running the delegate one run
    /// at a time (<see cref="ExecutionModel.AdmittedCall"/>): its runs are then passes of the call, and the repetition is a boundary
    /// between them; a sequential-awaited call's continuation of one run completes before the next run's prefix, and, for a call
    /// admitted per pass, before what the execution making it does after the wait, where the call's task is waited for at once
    /// (<see cref="Flow.WaitAtOnce"/>). Work is complete at a wait only on the exits of that wait that follow its completion, as the
    /// join reading gives them, and, for code reached through the suspension of an async body, only where that body's task is waited
    /// by its caller, level by level: a body that awaits the one below at once suspends in turn, and a delegate that an admitted call
    /// runs is complete for its next run where the library awaits its task, and for what follows the call where the call's own task is
    /// waited for (<see cref="OuterPass"/>). The iterations of one <c>Parallel.For</c> or
    /// <c>Parallel.ForEach</c>, completed when the call returns, and the callbacks of one timer, completed after an awaited
    /// <c>DisposeAsync()</c> or a <c>WaitOne()</c> after <c>Dispose(handle)</c>, overlap one another: their pass completes before the
    /// parent's later accesses and its next pass, never before itself.
    /// </summary>
    /// <param name="order">The happens-before graph whose flows and handle proofs the proof reads.</param>
    private sealed class PassCompletionProof(HappensBefore order)
    {
        /// <summary>How a pass starts its work.</summary>
        /// <param name="Kind">The spawn kind, or null for a timer's creation.</param>
        /// <param name="CallOperationId">The call that starts the work.</param>
        private sealed record PassStart(IrSpawnKind? Kind, int CallOperationId)
        {
            /// <summary>Whether the start's own return completes the work: the call of <c>Parallel.For</c> or <c>Parallel.ForEach</c>.</summary>
            internal bool ReturnCompletes => Kind is IrSpawnKind.ParallelFor or IrSpawnKind.ParallelForEach;
        }

        /// <summary>The pass of an admitted call that runs the delegate a pass of work starts in: its library awaits each run's task, so
        /// what the delegate waits for is complete before its next run, and the call's own task, where it is waited for, is the one
        /// that completes after the last run. The work is complete for what the execution does after the call returns only on the
        /// exits of that wait.</summary>
        /// <param name="After">The point after the call.</param>
        /// <param name="Joins">The waits that stand for the call's task, with whether a faulted outcome of each still follows the work's
        /// completion; none for a call whose task is not waited for at once.</param>
        /// <param name="Done">Whether the call returns only when its runs are done: a member that returns no task.</param>
        private sealed record OuterPass(PointKey After, IReadOnlyList<(PointKey Point, bool CompletedFault)> Joins, bool Done);

        private readonly Dictionary<(string SourceExecution, string DestinationExecution, PointKey Source, PointKey Destination, string? Creator), bool> _proofs = [];
        private readonly Dictionary<string, Flow> _flows = new(StringComparer.Ordinal);

        /// <summary>The prefix of a caller's id in <see cref="_climbing"/>, which tells it from a tail's.</summary>
        private const string UP = "up:";

        /// <summary>The tails whose waits and the callers whose awaits <see cref="Climb"/> is reading, to stop a chain of entries that
        /// comes back to itself.</summary>
        private readonly HashSet<string> _climbing = new(StringComparer.Ordinal);

        /// <summary>Where a diagnostic request collects every start the proof considers; null for the analysis itself.</summary>
        internal List<PassFrameEvidence>? Trace { get; init; }

        /// <summary>Where a diagnostic request collects every transition check the proof makes; null for the analysis itself.</summary>
        internal List<PassStepEvidence>? Steps { get; init; }

        /// <summary>Where a diagnostic request collects the library model records a check that held read; null for the analysis itself.</summary>
        internal List<ModelRecordEvidence>? Models { get; init; }

        /// <summary>Whether the completion of the queried passes orders two accesses: of one execution, its passes do not overlap each
        /// other; of two, the work of one completes before the other on every pass.</summary>
        /// <param name="first">One access.</param>
        /// <param name="second">The other access.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        internal bool Ordered(Access first, Access second, string? creator)
        {
            var source = new PointKey(first.InstanceId, first.OperationId, false);
            var destination = new PointKey(second.InstanceId, second.OperationId, false);
            var key = (first.ExecutionId, second.ExecutionId, source, destination, creator);
            if (!_proofs.TryGetValue(key, out var proven))
                _proofs.Add(key, proven = (first.ExecutionId == second.ExecutionId ? CompleteBeforeNext(first.ExecutionId, creator)
                    : CompleteBeforeAccess(first, second, creator) || CompleteBeforeAccess(second, first, creator)) ||
                    SequentialRuns(first, second, creator) || SequentialRuns(second, first, creator));
            return proven;
        }

        /// <summary>Whether a continuation of an admitted sequential-awaited call's delegate is ordered with another access: with the same
        /// continuation or the synchronous part of the delegate (its own prefix and what it calls there) of this and later passes of the
        /// call, and, for a call admitted per pass, with what the execution making it does after the wait. One call of the library runs
        /// the delegate one run after another and completes each run's task before the next run starts and before its own task
        /// completes. The passes of the call — the next iteration of a
        /// loop, the next call of a helper, the next run of a delegate of a <c>sequential</c> or <c>sequential-awaited</c> record — are
        /// ordered only when the call's task is waited for at once (<see cref="Flow.WaitAtOnce"/>), every path from the call back to
        /// itself passes that wait, the exits of the wait are read as completion where its form and environment say so, and each async
        /// entry of the body completes before the next entry starts: the body runs once as the root of its execution, is entered
        /// synchronously, or is entered through async calls each of which completes before the next one starts, and a body that
        /// suspends gives its task to its caller, so the work is complete only where the caller waits for that task, up every level of
        /// async entries (<see cref="Climb"/>), through a caller that awaits the body at once as through an async call
        /// (<see cref="EntryPoints"/>) and through the call whose delegate holds the body (<see cref="RunPassAllows"/>). An access of the
        /// execution making the call is judged by every place that reaches it
        /// (<see cref="OccurrenceOf"/>): a method both the runs and the pass call is ordered where the pass puts its calls, and one
        /// another callback of the call reaches is left alone. What a body runs after its suspension is parent code after the wait when
        /// each suspension of the bodies on the way up is reached only through the wait below (<see cref="TailFollows"/>).</summary>
        /// <param name="continuation">The access in a continuation of the call's delegate.</param>
        /// <param name="other">The access in the same continuation, in the synchronous part of the delegate run by that call, or, for a
        /// call admitted per pass, in the execution making the call outside the call's delegates or in the tail of a body on the way up
        /// from the body making it.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        private bool SequentialRuns(Access continuation, Access other, string? creator)
        {
            // A non-async target the admission runs as one async call of its body anchors that call's continuation in its own instance.
            if (!order._anchorOfChild.TryGetValue(continuation.ExecutionId, out var anchor) || anchor.Kind != SpawnAnchorKind.AsyncReturn)
                return false;
            // One continuation execution can stand for the call in several instances of its body, each admitted as a call of its own:
            // each needs its own proof, and the entries of all of them are one set.
            var calls = order._awaitedCalls.Where(call => call.Execution == anchor.ExecutionId && call.Continuations.Contains(continuation.ExecutionId))
                             .ToArray();
            var anchors = order._anchors.Values.SelectMany(list => list).ToArray();
            if (calls.Length == 0 || anchors.Count(candidate => candidate.ChildId == continuation.ExecutionId) != calls.Length)
                return false;
            // The admission leaves the one delegate it read as the call's every async target, at the call or at the async call a
            // non-async target runs as; other delegates the call runs, such as callbacks a holder keeps, are executions of their own and
            // do not decide this one's order.
            var children = calls.SelectMany(call => call.Continuations).ToHashSet(StringComparer.Ordinal);
            var sites = anchors.Where(candidate => children.Contains(candidate.ChildId)).Select(site => (site.CallerInstance, site.OperationId)).ToHashSet();
            var occurrence = other.ExecutionId == anchor.ExecutionId ? OccurrenceOf(anchor.ExecutionId, calls, sites, other.InstanceId) : (Occurrence?)null;
            if (occurrence == Occurrence.Callback)
                return false;
            var inDelegate = occurrence == Occurrence.Run;
            // The code of the making execution after the wait is ordered with the continuation only by this proof when the call was
            // admitted per pass: a call admitted per run has the join of its task (SPEC TD-065). A method the runs and the pass both
            // call has a place in the pass whatever the threshold, and the join orders no place of the runs.
            var perPass = calls.All(call => call.Threshold == ExecutionModel.AdmissionThreshold.PerPass);
            var inParent = occurrence == Occurrence.Both || occurrence == Occurrence.Pass && perPass;
            // What a body that suspends runs after the suspension — the tail of the body making the call, or of a body above it that calls
            // that one — is an async call's execution in the execution making the call, and parent code after the wait too.
            var inTail = !inDelegate && perPass && !children.Contains(other.ExecutionId) &&
                         anchors.Any(candidate => candidate.ExecutionId == anchor.ExecutionId && candidate.Kind == SpawnAnchorKind.AsyncReturn &&
                                                  candidate.ChildId == other.ExecutionId);
            if (other.ExecutionId != continuation.ExecutionId && !inDelegate && !inParent && !inTail)
                return false;

            var target = new PointKey(other.InstanceId, other.OperationId, false);
            var entries = new List<(string Caller, int Site, string Body)>();
            var models = new List<ModelRecordEvidence>();
            bool nextCall = true, allowed = true;
            foreach (var admitted in calls)
            {
                WorkCancellation.Check();
                if (!order._heap.Instances.TryGetValue(admitted.Instance, out var instance) ||
                    !order._scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                    return false;
                // The call runs in the prefix of its body; the execution making it passes no place of it again after a suspension of the
                // body, since the admission has found one execution making the call.
                var block = body.Blocks.FirstOrDefault(candidate => candidate.Operations.Any(operation => operation.Id == admitted.Operation));
                if (block?.Operations.FirstOrDefault(operation => operation.Id == admitted.Operation) is not IrCallOperation call ||
                    !order._segments.Runs(instance.BodyId, BodySegment.Prefix, call.Id))
                    return false;

                // The pass waits for the call's work at once, or the call returns only when it is done.
                if (order.PlainFlow(anchor.ExecutionId).WaitAtOnce(body, call, admitted.Task is not null) is not { } wait)
                    return false;
                var after = new PointKey(instance.Id, call.Id, true);
                var next = true;
                bool normal = true, exceptional = true;
                List<(PointKey Point, bool CompletedFault)> waits = [];
                if (wait.Id != call.Id)
                {
                    if (instance.Summary.Joins.FirstOrDefault(join => join.OperationId == wait.Id) is not { } waited)
                        return false;
                    var point = new PointKey(instance.Id, wait.Id, false);
                    waits.Add((point, order.ThrowsAfterCompletion(anchor.ExecutionId, instance.Id, waited, proven: true)));
                    var flow = PassFlow(anchor, waits);
                    PointKey[] cuts = [point];
                    // Every path from the call back to the call passes the wait, and from the call to what the parent does after it.
                    next = flow.BeforeNext(after, cuts) && (!inParent || flow.BeforeDestination(after, target, cuts, overlappingRuns: true));
                    (normal, exceptional) = (flow.BeforeReturn(after, cuts, exceptionalExits: false), flow.BeforeReturn(after, cuts, exceptionalExits: true));
                }
                nextCall &= next;
                allowed &= next && ParentAllows(anchor.ExecutionId, normal, exceptional, creator, []);
                if (!allowed)
                    break;
                // The tail runs after a suspension of its body, which the body reaches only through the wait for the work.
                if (inTail && !TailFollows(anchor, instance, after, waits, other.ExecutionId, []))
                {
                    Steps?.Add(PassStepEvidence.Of("sequential-runs", continuation.ExecutionId, other.ExecutionId, false,
                        ("awaited-at-once", true), ("next-call-after-wait", nextCall), ("parent-allows", allowed), ("tail-follows-wait", false)));
                    return false;
                }
                // A body that waits for the work without suspending — a blocking wait at once, or a member that returns when it is done —
                // has finished it before it returns or suspends, whatever its caller does with the task.
                if (wait is IrAwaitOperation)
                {
                    entries.AddRange(order._heap.Edges.Where(edge => edge.CalleeInstance == instance.Id)
                                          .Select(edge => (edge.CallerInstance, edge.OperationId, instance.Id)));
                }
                if (call.Library is { } library)
                    models.Add(ModelRecordEvidence.Of("sequential-runs", instance.Id, call.Id, library, RunRelation.SequentialAwaited));
            }

            // The body is the root of its execution, which runs once, or waits for the work without suspending, or each of its entries
            // is synchronous or an async call whose pass completes before the next pass of the entry and before the next entry starts:
            // a suspension of the body gives its task to the caller, and the work is complete only where the caller waits for that task,
            // up every level of the chain: a caller that awaits the body at once suspends in turn, and its own callers are the entries.
            entries = EntryPoints(anchor.ExecutionId, entries);
            var entryStarts = entries.Select(entry => new PointKey(entry.Caller, entry.Site, false)).Distinct().ToArray();
            // The tail is the rest of a call this pass makes, which the next pass may start before the tail is done unless it completes first.
            var entriesComplete = allowed && (!inTail || CompleteBeforeNext(other.ExecutionId, creator, admittedPass: true));
            foreach (var (caller, site, bodyInstance) in entries)
            {
                WorkCancellation.Check();
                if (!entriesComplete)
                    break;
                // The delegate of an admitted call is entered by the call, whose own pass says where the work is complete.
                if (order._awaitedCalls.FirstOrDefault(call => call.Instance == caller && call.Operation == site) is { } runner)
                {
                    entriesComplete = RunPassAllows(anchor, runner, inParent ? [.. entryStarts, target] : entryStarts, creator);
                    continue;
                }
                var outer = AsyncCallsAt(anchor.ExecutionId, caller, site);
                // A synchronous entry runs the whole body in the execution making the call before that execution goes on.
                var inline = outer.Length == 0 && !order._segments.IsAsyncSpawn(caller, site, bodyInstance) &&
                             (order._visits.GetValueOrDefault(anchor.ExecutionId) ?? []).Any(visit => visit.InstanceId == bodyInstance &&
                                                                                                     visit.Segment == BodySegment.Whole);
                entriesComplete = inline || outer is [var asyncCall] && CompleteBeforeNext(asyncCall, creator, admittedPass: true) &&
                                  CompleteBefore(asyncCall, entryStarts.Length > 1 ? entryStarts : [], admittedPass: true) &&
                                  (!inParent || CompleteBefore(asyncCall, [target], admittedPass: true));
            }
            Steps?.Add(PassStepEvidence.Of("sequential-runs", continuation.ExecutionId, other.ExecutionId, entriesComplete,
                ("awaited-at-once", true), ("next-call-after-wait", nextCall), ("parent-allows", allowed), ("entries-complete", entriesComplete)));
            if (entriesComplete)
                Models?.AddRange(models);
            return entriesComplete;
        }

        /// <summary>Whether the code a body runs after a suspension is reached only after the work of an admitted call is complete: the
        /// body is the one making the call or one above it, entered at an async call whose task the body below is waited by, and each of
        /// its suspensions is reached from the call, or from the call of the body below, only through the wait for that call's task —
        /// the call's own wait in the body making it, a join of the tail below in a body above. A body entered whole runs inside its
        /// caller before the caller goes on, so the caller's suspensions follow the same wait; one a caller awaits at once in its prefix
        /// has its tail joined to the caller's, which suspends at that await; one that an admitted call runs as its delegate is waited
        /// for by the call's own wait (<see cref="WaitOfCall"/>). A call whose work is done when it returns has no wait to pass.</summary>
        /// <param name="anchor">The start of the continuation of the call's delegate.</param>
        /// <param name="body">The body at this level of the chain of async entries.</param>
        /// <param name="start">The point after the call this level makes: the admitted call, or the async call of the body below.</param>
        /// <param name="waits">The waits at this level for the work of the call or the task of the body below, with whether a faulted
        /// outcome of each still follows the work's completion; none for a call whose work is done when it returns.</param>
        /// <param name="tail">The execution running the code after a suspension of a body in the chain.</param>
        /// <param name="seen">The bodies already asked on the way down to this one, which the chain does not come back to.</param>
        private bool TailFollows(SpawnAnchor anchor, MethodInstance body, PointKey start, List<(PointKey Point, bool CompletedFault)> waits, string tail,
                                 HashSet<string> seen)
        {
            WorkCancellation.Check();
            if (!seen.Add(body.Id))
                return false;
            if (waits.Count != 0)
            {
                var flow = PassFlow(anchor, waits);
                var cuts = waits.Select(wait => wait.Point).ToArray();
                foreach (var suspension in order.PrefixAwaits(body).Select(operation => new PointKey(body.Id, operation, false))
                                                .Where(point => !cuts.Contains(point)))
                {
                    WorkCancellation.Check();
                    if (!flow.BeforeDestination(start, suspension, cuts, overlappingRuns: false))
                        return false;
                }
            }

            var calls = (order._steps.GetValueOrDefault(anchor.ExecutionId) ?? []).Where(step => step.Callee == body.Id).ToArray();
            var entries = calls.Where(step => step.CalleeSegment == BodySegment.Prefix).Select(step => (step.Caller, step.OperationId)).Distinct().ToArray();
            var tails = (order._anchors.GetValueOrDefault(anchor.ExecutionId) ?? [])
                        .Where(candidate => candidate.Kind == SpawnAnchorKind.AsyncReturn && entries.Contains((candidate.CallerInstance, candidate.OperationId)))
                        .GroupBy(candidate => (candidate.CallerInstance, candidate.OperationId))
                        .ToDictionary(group => group.Key, group => group.Select(candidate => candidate.ChildId).Distinct().ToArray());
            if (tails.Values.Any(children => children.Contains(tail)))
                return true;
            // The delegate of an admitted call runs in the call, whose own task its caller waits for, and the work is complete for the
            // caller's tail where that wait resumes; a call that returns when its runs are done has no wait to pass.
            foreach (var (callerId, operation) in entries)
            {
                WorkCancellation.Check();
                if (order._awaitedCalls.FirstOrDefault(call => call.Instance == callerId && call.Operation == operation) is not { } runner ||
                    WaitOfCall(anchor.ExecutionId, runner) is not { } outer || !outer.Done && outer.Wait is null)
                    continue;
                if (TailFollows(anchor, outer.Caller, outer.After, outer.Wait is { } wait ? [wait] : [], tail, [.. seen]))
                    return true;
            }
            // A body that a caller awaits at once in its prefix has its tail joined to the caller's: the caller suspends at that await,
            // so its own suspensions are reached only through it, and its tail follows what the body's does.
            foreach (var (callerId, operation) in entries.Where(entry => !tails.ContainsKey(entry)))
            {
                WorkCancellation.Check();
                if (AwaitedAtOnce(anchor.ExecutionId, callerId, operation) is { } awaited &&
                    order._heap.Instances.TryGetValue(callerId, out var awaiting) &&
                    TailFollows(anchor, awaiting, new PointKey(callerId, operation, true), [awaited], tail, [.. seen]))
                    return true;
            }
            // A body entered whole runs inside its caller's visit before the caller goes on, so the caller reaches its own suspensions
            // only through the same wait.
            foreach (var caller in calls.Where(step => step.CalleeSegment == BodySegment.Whole).Select(step => step.Caller).Distinct(StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                if (order._heap.Instances.TryGetValue(caller, out var inline) && TailFollows(anchor, inline, start, waits, tail, [.. seen]))
                    return true;
            }
            foreach (var ((caller, operation), children) in tails)
            {
                WorkCancellation.Check();
                if (!order._heap.Instances.TryGetValue(caller, out var above))
                    continue;
                var aboveWaits = new List<(PointKey Point, bool CompletedFault)>();
                foreach (var child in children)
                {
                    WorkCancellation.Check();
                    if (!Joins(child, overlappingWork: false, rejected: null, out _, out _, out var joins, out var suspended))
                    {
                        aboveWaits.Clear();
                        break;
                    }
                    aboveWaits.AddRange(joins.Concat(suspended.Select(join => (join.Point, join.CompletedFault))).Where(join => join.Point.Instance == caller));
                }
                if (aboveWaits.Count != 0 && TailFollows(anchor, above, new PointKey(caller, operation, true), aboveWaits.Distinct().ToList(), tail, [.. seen]))
                    return true;
            }

            return false;
        }

        /// <summary>Where the places of an instance stand against the runs of an admitted call, in the execution making the call: the
        /// instance has a place in the synchronous part of a run when the delegate or something a run calls reaches it, a place in the
        /// pass when the execution reaches it without the call, and a place in another callback when something else the call runs
        /// reaches it. A place of the pass is an access of the parent, ordered by where the pass puts it; a method the runs and the pass
        /// both call is both.</summary>
        private enum Occurrence
        {
            /// <summary>Only the pass reaches the instance, outside the call.</summary>
            Pass,

            /// <summary>Only the synchronous part of a run reaches it.</summary>
            Run,

            /// <summary>Both a run and the pass reach it.</summary>
            Both,

            /// <summary>The call runs it for something else than the delegate it admitted, which the admission does not order.</summary>
            Callback
        }

        /// <summary>The places of an instance against the runs of the admitted calls that start a delegate.</summary>
        /// <param name="execution">The execution making the calls.</param>
        /// <param name="calls">The admitted calls whose continuation is queried.</param>
        /// <param name="sites">The places that start the delegate, each by its caller instance and operation.</param>
        /// <param name="instance">The instance.</param>
        private Occurrence OccurrenceOf(string execution, IReadOnlyList<ExecutionModel.AdmittedCall> calls,
                                        IReadOnlySet<(string CallerInstance, int OperationId)> sites, string instance)
        {
            WorkCancellation.Check();
            var admitted = calls.Select(call => (call.Instance, call.Operation)).ToHashSet();
            var steps = order._steps.GetValueOrDefault(execution) ?? [];
            var delegates = order._heap.AsyncSpawns.Where(site => sites.Contains((site.CallerInstance, site.OperationId)))
                                 .SelectMany(site => site.Callees).ToHashSet(StringComparer.Ordinal);
            var started = steps.Where(step => admitted.Contains((step.Caller, step.OperationId))).Select(step => step.Callee)
                               .ToHashSet(StringComparer.Ordinal);
            // What the call runs besides the delegate, down to where it hands over to the delegate.
            if (Reachable(execution, started.Except(delegates), step => !delegates.Contains(step.Callee)).Contains(instance))
                return Occurrence.Callback;
            if (!Reachable(execution, delegates, _ => true).Contains(instance))
                return Occurrence.Pass;
            var entries = (order._entries.GetValueOrDefault(execution) ?? []).Select(entry => entry.InstanceId);
            return Reachable(execution, entries, step => !admitted.Contains((step.Caller, step.OperationId))).Contains(instance)
                ? Occurrence.Both
                : Occurrence.Run;
        }

        /// <summary>The instances an execution's calls reach from some instances.</summary>
        /// <param name="execution">The execution whose calls are followed.</param>
        /// <param name="roots">The instances to start from.</param>
        /// <param name="follows">Whether a call is followed.</param>
        private HashSet<string> Reachable(string execution, IEnumerable<string> roots, Func<ExecutionStep, bool> follows)
        {
            var steps = order._steps.GetValueOrDefault(execution) ?? [];
            var inside = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(roots);
            while (pending.TryPop(out var current))
            {
                WorkCancellation.Check();
                if (!inside.Add(current))
                    continue;
                foreach (var step in steps.Where(step => step.Caller == current && follows(step)))
                {
                    WorkCancellation.Check();
                    pending.Push(step.Callee);
                }
            }

            return inside;
        }

        /// <summary>Whether each pass of an execution's start completes the work before any of the points given is reached from the
        /// start, in a parent that does not overlap itself; none given is any pass completing it.</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="destinations">The points, each of them before an operation.</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call (<see cref="Frame"/>).</param>
        private bool CompleteBefore(string execution, IReadOnlyList<PointKey> destinations, bool admittedPass = false)
        {
            WorkCancellation.Check();
            List<OuterPass>? outers = admittedPass ? [] : null;
            if (!Frame(execution, overlappingWork: false, out var anchor, out var spawn, out var joins, admittedPass, outers))
                return false;
            var flow = PassFlow(anchor, joins);
            var start = StartPoint(anchor, spawn);
            var cuts = joins.Select(join => join.Point).ToArray();
            return destinations.All(destination => flow.BeforeDestination(start, destination, cuts, overlappingRuns: true)) &&
                   (outers is null || OutersAllow(anchor, outers, destinations, out _, out _));
        }

        /// <summary>Whether each pass of an execution's start completes the work before the next pass starts it again, in a parent that
        /// does not overlap itself.</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call (<see cref="Frame"/>).</param>
        private bool CompleteBeforeNext(string execution, string? creator, bool admittedPass = false)
        {
            WorkCancellation.Check();
            List<OuterPass>? outers = admittedPass ? [] : null;
            return Frame(execution, overlappingWork: false, out var anchor, out var spawn, out var joins, admittedPass, outers) &&
                   CompleteBeforeNext(execution, creator, anchor, spawn, joins, outers);
        }

        /// <summary>Whether each pass of an execution's start completes the work before the next pass, with the pass's joins given.</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="joins">The pass's joins, with whether a faulted outcome of each still follows the work's completion.</param>
        /// <param name="outers">The passes of the admitted calls whose delegates the pass is in, which complete the work for what follows
        /// their calls, or null for a pass of no admitted call.</param>
        private bool CompleteBeforeNext(string execution, string? creator, SpawnAnchor anchor, PassStart spawn,
                                        List<(PointKey Point, bool CompletedFault)> joins, IReadOnlyList<OuterPass>? outers = null)
        {
            var flow = PassFlow(anchor, joins);
            var start = StartPoint(anchor, spawn);
            var cuts = joins.Select(join => join.Point).ToArray();
            if (!flow.BeforeNext(start, cuts))
            {
                Steps?.Add(PassStepEvidence.Of("complete-before-next", execution, anchor.ExecutionId, false, ("before-next", false)));
                return false;
            }
            var normal = flow.BeforeReturn(start, cuts, exceptionalExits: false);
            var all = flow.BeforeReturn(start, cuts, exceptionalExits: true);
            if (outers is not null)
            {
                // The call that runs the delegate may be passed again before its own task is waited for.
                if (!OutersAllow(anchor, outers, [start with { AfterCall = false }], out var outerNormal, out var outerAll))
                {
                    Steps?.Add(PassStepEvidence.Of("complete-before-next", execution, anchor.ExecutionId, false, ("before-next", true), ("outer-passes", false)));
                    return false;
                }
                (normal, all) = (normal && outerNormal, all && outerAll);
            }
            var allowed = ParentAllows(anchor.ExecutionId, normal, all, creator, []);
            Steps?.Add(PassStepEvidence.Of("complete-before-next", execution, anchor.ExecutionId, allowed,
                ("before-next", true), ("normal-return", normal), ("exceptional-return", all), ("parent-allows", allowed)));
            return allowed;
        }

        /// <summary>Whether a join of the parent on one handle that may carry the pass's own would, were it that handle, complete the
        /// work of one execution before its next pass. Its fault follows the work's completion as a frame's join on that handle would:
        /// the form, the environment and the scheduler of the task the handle carries decide it.</summary>
        /// <param name="instanceId">The instance making the join.</param>
        /// <param name="operation">The join operation.</param>
        /// <param name="first">One access of the work.</param>
        /// <param name="second">The other access of the same work.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        internal bool JoinWouldOrder(string instanceId, int operation, Access first, Access second, string? creator)
        {
            if (first.ExecutionId != second.ExecutionId || !Start(first.ExecutionId, overlappingWork: false, out var anchor, out var spawn) ||
                !order._heap.Instances.TryGetValue(instanceId, out var instance) ||
                !(order._visits.GetValueOrDefault(anchor.ExecutionId) ?? []).Any(visit => visit.InstanceId == instanceId &&
                    order._segments.Runs(instance.BodyId, visit.Segment, operation)) ||
                instance.Summary.Joins.FirstOrDefault(join => join.OperationId == operation) is not { HandlesKnown: true, Handles.Count: 1 } join ||
                CarriedHandle(join.Handles[0], instance, anchor, spawn, operation, []) is not ({ } carrier, { } carried))
                return false;
            Frame(first.ExecutionId, overlappingWork: false, out _, out _, out var joins);
            var point = new PointKey(instanceId, operation, false);
            var completedFault = order.ThrowsAfterCompletion(anchor.ExecutionId, join, carrier.Id, [carried], proven: true);
            var suspends = join.Kind == SummaryJoinKind.Await &&
                           (order._visits.GetValueOrDefault(anchor.ExecutionId) ?? []).Any(visit => visit.InstanceId == instanceId &&
                               visit.Segment == BodySegment.Prefix && order._segments.Runs(instance.BodyId, visit.Segment, operation));
            foreach (var above in suspends ? Climb(anchor, spawn, instance, point, completedFault, admittedPass: false) : [(point, completedFault)])
            {
                WorkCancellation.Check();
                if (!joins.Contains(above))
                    joins.Add(above);
            }
            return CompleteBeforeNext(first.ExecutionId, creator, anchor, spawn, joins);
        }

        /// <summary>The pass's own handle a value may carry, with the instance expressing it: the value itself, an argument of a call the
        /// analysis cannot see into, or a return of a callee it comes from; null when it can carry none.</summary>
        /// <param name="value">The value.</param>
        /// <param name="instance">The instance expressing it.</param>
        /// <param name="anchor">The pass's start.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="operation">The operation the handle is judged at.</param>
        /// <param name="seen">The calls already followed.</param>
        private (MethodInstance Instance, SummaryValue Value)? CarriedHandle(SummaryValue value, MethodInstance instance, SpawnAnchor anchor,
                                                                            PassStart spawn, int operation, HashSet<(string Instance, int Call)> seen)
        {
            WorkCancellation.Check();
            if (order.ProveHandleSource(anchor.ExecutionId, instance, value, operation) == anchor.ChildId && Fresh(value, instance, anchor, spawn))
                return (instance, value);
            foreach (var call in value.SourceCalls.Concat(value.Producers.Calls).Concat(value.Values.OfType<CallResultValue>().Select(result => result.OperationId))
                                      .Distinct())
            {
                WorkCancellation.Check();
                if (!seen.Add((instance.Id, call)))
                    continue;
                if (instance.Summary.OpaqueCalls.FirstOrDefault(candidate => candidate.OperationId == call) is { IsKnown: false } opaque)
                {
                    foreach (var argument in opaque.Arguments)
                    {
                        WorkCancellation.Check();
                        if (CarriedHandle(new SummaryValue(argument.Values, argument.UnknownSources)
                            {
                                SourceCalls = argument.SourceCalls, Completions = argument.Completions,
                                CompletedCalls = argument.CompletedCalls, Producers = argument.Producers
                            }, instance, anchor, spawn, operation, seen) is { } carried)
                            return carried;
                    }
                }
                foreach (var edge in order._heap.Edges.Where(edge => edge.CallerInstance == instance.Id && edge.OperationId == call))
                {
                    WorkCancellation.Check();
                    if (!order._heap.Instances.TryGetValue(edge.CalleeInstance, out var callee))
                        continue;
                    foreach (var returned in callee.Summary.Returns)
                    {
                        WorkCancellation.Check();
                        if (CarriedHandle(new SummaryValue(returned.Values, returned.UnknownSources)
                            {
                                SourceCalls = returned.SourceCalls, Completions = returned.Completions,
                                CompletedCalls = returned.CompletedCalls, Producers = returned.Producers
                            }, callee, anchor, spawn, operation, seen) is { } carried)
                            return carried;
                    }
                }
            }
            return null;
        }

        /// <summary>Whether the work of the source's execution completes before the destination on every pass: the destination is in
        /// the parent after the pass's wait, or in an ancestor after the wait for a parent whose every normal return follows it. Work
        /// whose units overlap within one pass — iterations of one <c>Parallel</c> call, callbacks of one timer — completes here too.</summary>
        /// <param name="source">The access in the work.</param>
        /// <param name="destination">The access the work completes before.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        private bool CompleteBeforeAccess(Access source, Access destination, string? creator)
        {
            var current = source.ExecutionId;
            var faultCompletion = true;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(current) && Frame(current, overlappingWork: true, out var anchor, out var spawn, out var joins))
            {
                WorkCancellation.Check();
                if (!faultCompletion)
                    joins = joins.Select(join => (join.Point, false)).ToList();
                var start = StartPoint(anchor, spawn);
                var flow = PassFlow(anchor, joins);
                var cuts = joins.Select(join => join.Point).ToArray();
                if (!flow.BeforeDestination(start, start with { AfterCall = false }, cuts, overlappingRuns: false))
                {
                    Steps?.Add(PassStepEvidence.Of("complete-before-access", current, destination.ExecutionId, false, ("before-own-start", false)));
                    return false;
                }
                if (destination.ExecutionId == anchor.ExecutionId)
                {
                    if (!flow.BeforeDestination(start, new PointKey(destination.InstanceId, destination.OperationId, false), cuts, overlappingRuns: true))
                    {
                        Steps?.Add(PassStepEvidence.Of("complete-before-access", current, destination.ExecutionId, false,
                            ("before-own-start", true), ("before-destination", false)));
                        return false;
                    }
                    var normal = flow.BeforeReturn(start, cuts, exceptionalExits: false);
                    var all = flow.BeforeReturn(start, cuts, exceptionalExits: true);
                    var allowed = ParentAllows(anchor.ExecutionId, normal, all, creator, []);
                    Steps?.Add(PassStepEvidence.Of("complete-before-access", current, destination.ExecutionId, allowed, ("before-own-start", true),
                        ("before-destination", true), ("normal-return", normal), ("exceptional-return", all), ("parent-allows", allowed)));
                    return allowed;
                }
                if (!flow.BeforeReturn(start, cuts, exceptionalExits: false))
                {
                    Steps?.Add(PassStepEvidence.Of("inner-pass-returns", current, anchor.ExecutionId, false,
                        ("before-own-start", true), ("normal-return", false)));
                    return false;
                }
                // Only normal completion has been established for the inner work. A fault of the parent may leave it running, so the
                // wait for the parent completes it on a fault only when every exit of the parent waited for it as well, the exception of an
                // earlier await that skips its join included — and for the work below it, which a join whose fault does not follow that
                // work's completion has not.
                faultCompletion = faultCompletion && flow.BeforeReturn(start, cuts, exceptionalExits: true);
                Steps?.Add(PassStepEvidence.Of("inner-pass-returns", current, anchor.ExecutionId, true,
                    ("before-own-start", true), ("normal-return", true), ("exceptional-return", faultCompletion)));
                current = anchor.ExecutionId;
            }
            return false;
        }

        /// <summary>Whether the parent of a pass does not overlap itself, so its passes run one after another: it runs at most once and
        /// is serialized, runs once per run of the creator, or is serialized and waited for the work on every exit; otherwise it is work
        /// of a pass in turn, which waited for the work on every normal return and whose own pass the same holds for.</summary>
        /// <param name="parent">The execution running the passes.</param>
        /// <param name="normalCompletion">Whether every normal return of the parent follows the wait for the work.</param>
        /// <param name="allCompletion">Whether every exit of the parent, exceptional ones included, follows the wait for the work.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        /// <param name="seen">The parents already asked on this chain.</param>
        private bool ParentAllows(string parent, bool normalCompletion, bool allCompletion, string? creator, HashSet<string> seen)
        {
            WorkCancellation.Check();
            if (!seen.Add(parent))
            {
                Steps?.Add(PassStepEvidence.Of("parent-allows", parent, null, false, ("reached-again", true)));
                return false;
            }
            var policy = order._executions[parent].Policy;
            if (policy is { Multiplicity: Multiplicity.AtMostOnce, SelfOverlap: SelfOverlap.Serialized } ||
                creator is not null && CreatorRuns.Once(order._executions, parent, creator))
            {
                Steps?.Add(PassStepEvidence.Of("parent-allows", parent, null, true, ("runs-once", true)));
                return true;
            }
            if (policy.SelfOverlap == SelfOverlap.Serialized && allCompletion)
            {
                Steps?.Add(PassStepEvidence.Of("parent-allows", parent, null, true, ("runs-once", false), ("serialized-after-every-exit", true)));
                return true;
            }
            if (!normalCompletion || !Frame(parent, overlappingWork: false, out var anchor, out var spawn, out var joins))
            {
                Steps?.Add(PassStepEvidence.Of("parent-allows", parent, null, false, ("runs-once", false), ("serialized-after-every-exit", false),
                    ("normal-return", normalCompletion)));
                return false;
            }
            if (!allCompletion)
                joins = joins.Select(join => (join.Point, false)).ToList();
            var flow = PassFlow(anchor, joins);
            var start = StartPoint(anchor, spawn);
            var cuts = joins.Select(join => join.Point).ToArray();
            if (!flow.BeforeNext(start, cuts))
            {
                Steps?.Add(PassStepEvidence.Of("parent-allows", parent, anchor.ExecutionId, false, ("runs-once", false),
                    ("serialized-after-every-exit", false), ("normal-return", true), ("own-pass-before-next", false)));
                return false;
            }
            var normal = flow.BeforeReturn(start, cuts, exceptionalExits: false);
            // A join whose fault does not follow the work's completion leaves it running on that exit, however the joins sit.
            var all = allCompletion && flow.BeforeReturn(start, cuts, exceptionalExits: true);
            var allowed = ParentAllows(anchor.ExecutionId, normal, all, creator, seen);
            Steps?.Add(PassStepEvidence.Of("parent-allows", parent, anchor.ExecutionId, allowed, ("runs-once", false),
                ("serialized-after-every-exit", false), ("normal-return", true), ("own-pass-before-next", true), ("parent-allows", allowed)));
            return allowed;
        }

        /// <summary>The flow of a pass's parent, in which the exception of a join of the pass that throws only after the work completed
        /// comes after that join, as <see cref="FlowOf"/> reads proven joins, an <c>await</c> among them — and so does that of a site
        /// calling such a join where the hoisting of <see cref="AtCallSites"/> says the call throws only after it: a path asking about
        /// the pass's work stops there. The exception of an <c>await</c> that is no join of the pass is a step of the exits as it is.</summary>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="joins">The pass's joins, with whether a faulted outcome of each still follows the work's completion.</param>
        private Flow PassFlow(SpawnAnchor anchor, List<(PointKey Point, bool CompletedFault)> joins)
        {
            var key = anchor.ExecutionId + "|" + string.Join(";", joins.Select(join => $"{join.Point}:{join.CompletedFault}").Order(StringComparer.Ordinal));
            if (_flows.TryGetValue(key, out var flow))
                return flow;
            var waits = joins.Select(join => new Candidate(anchor.ExecutionId, join.Point, [anchor.ChildId], join.CompletedFault)).ToArray();
            var throwsAfter = order.ThrowingAfter(waits.Concat(order.Hoisted(waits, [anchor.ExecutionId])).Where(wait => wait.ThrowsAfter)
                                                       .Select(wait => (wait.Point, wait.Targets)).ToArray());
            _flows.Add(key, flow = order.NewFlow(anchor.ExecutionId, throwsAfter));
            return flow;
        }

        /// <summary>The start of an execution and the joins of its parent that wait for this pass's start: a join on one handle, proven
        /// to name the execution and to come from the pass's own start; for a timer, an awaited <c>DisposeAsync()</c> or a
        /// <c>WaitOne()</c> after <c>Dispose(handle)</c> of such a timer; for a <c>Parallel</c> call, the call's own return.</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="overlappingWork">Whether work whose units overlap within one pass — a <c>Parallel</c> call's iterations, a
        /// timer's callbacks — has a frame.</param>
        /// <param name="anchor">Receives the start.</param>
        /// <param name="spawn">Receives how the pass starts its work.</param>
        /// <param name="joins">Receives the pass's joins, with whether a faulted outcome of each still follows the work's completion.</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call. Where its work is inside a delegate another admitted
        /// call runs, the delegate's <c>await</c> ends it for that call's next run, but for what follows the call's return only where the
        /// call's own task is waited at once (<see cref="ClimbRuns"/>); other work keeps the <c>await</c> as its join.</param>
        /// <param name="outers">Receives, for an admitted pass, the passes of the admitted calls whose delegates the work is in, each
        /// completing the work for what follows its call (<see cref="OutersAllow"/>); null for other work.</param>
        private bool Frame(string execution, bool overlappingWork, out SpawnAnchor anchor, out PassStart spawn,
                           out List<(PointKey Point, bool CompletedFault)> joins, bool admittedPass = false, List<OuterPass>? outers = null)
        {
            WorkCancellation.Check();
            var rejected = Trace is null ? null : new List<PassJoinEvidence>();
            if (!Joins(execution, overlappingWork, rejected, out anchor, out spawn, out joins, out var suspended))
            {
                Trace?.Add(new PassFrameEvidence(execution, null, null, null, null, -1, [], []));
                return false;
            }
            foreach (var (point, completedFault, instance) in suspended)
            {
                WorkCancellation.Check();
                foreach (var above in Climb(anchor, spawn, instance, point, completedFault, admittedPass, outers))
                {
                    WorkCancellation.Check();
                    if (!joins.Contains(above))
                        joins.Add(above);
                }
            }
            Trace?.Add(new PassFrameEvidence(execution, anchor.ExecutionId, anchor.Kind.ToString(), spawn.Kind?.ToString(), anchor.CallerInstance,
                anchor.OperationId, joins.Select(join => new PassJoinEvidence(join.Point.Instance, join.Point.Operation, join.CompletedFault, null,
                    JoinModel(join.Point))).ToArray(),
                rejected?.ToArray() ?? []));
            return joins.Count != 0;
        }

        /// <summary>The joins of the parent that wait for a pass's start, as the parent's visits hold them: those that stop the paths of the
        /// parent, and, apart, the <c>await</c>s that suspend the body of a prefix visit, which stop only the paths that stay in that body
        /// (<see cref="Climb"/>).</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="overlappingWork">Whether work whose units overlap within one pass has a frame.</param>
        /// <param name="rejected">Receives the joins of the parent the start cannot be proven to be the handle of, or null.</param>
        /// <param name="anchor">Receives the start.</param>
        /// <param name="spawn">Receives how the pass starts its work.</param>
        /// <param name="joins">Receives the joins that stop the parent's paths, with whether a faulted outcome of each still follows the
        /// work's completion.</param>
        /// <param name="suspended">Receives the awaits that suspend the body of a prefix visit.</param>
        private bool Joins(string execution, bool overlappingWork, List<PassJoinEvidence>? rejected, out SpawnAnchor anchor, out PassStart spawn,
                           out List<(PointKey Point, bool CompletedFault)> joins,
                           out List<(PointKey Point, bool CompletedFault, MethodInstance Instance)> suspended)
        {
            joins = [];
            suspended = [];
            WorkCancellation.Check();
            if (!Start(execution, overlappingWork, out anchor, out spawn))
                return false;
            var parent = order._executions[anchor.ExecutionId];
            // A fault out of the call follows the iterations' completion, as the happens-before graph reads the call's own join.
            if (spawn.ReturnCompletes)
                joins.Add((StartPoint(anchor, spawn), true));
            foreach (var visit in order._visits.GetValueOrDefault(parent.Id) ?? [])
            {
                WorkCancellation.Check();
                if (spawn.ReturnCompletes || !order._heap.Instances.TryGetValue(visit.InstanceId, out var instance))
                    continue;
                if (anchor.Kind == SpawnAnchorKind.Timer)
                {
                    TimerJoins(execution, parent.Id, visit, instance, anchor, spawn, joins, suspended, rejected);
                    continue;
                }
                foreach (var join in instance.Summary.Joins)
                {
                    WorkCancellation.Check();
                    if (!order._segments.Runs(instance.BodyId, visit.Segment, join.OperationId))
                        continue;
                    // The one handle of a frame's join is proven below, so only the form and the environment decide its fault.
                    var completedFault = order.ThrowsAfterCompletion(parent.Id, instance.Id, join, proven: true);
                    if (!join.HandlesKnown || join.Handles.Count != 1)
                    {
                        rejected?.Add(new PassJoinEvidence(instance.Id, join.OperationId, completedFault, "handles"));
                        continue;
                    }
                    var handle = join.Handles[0];
                    if (order.ProveHandleSource(parent.Id, instance, handle, join.OperationId) != execution)
                    {
                        rejected?.Add(new PassJoinEvidence(instance.Id, join.OperationId, completedFault, "source"));
                        continue;
                    }
                    if (!Fresh(handle, instance, anchor, spawn))
                    {
                        rejected?.Add(new PassJoinEvidence(instance.Id, join.OperationId, completedFault, "not-this-pass"));
                        continue;
                    }
                    var point = new PointKey(instance.Id, join.OperationId, false);
                    if (join.Kind == SummaryJoinKind.Await && visit.Segment == BodySegment.Prefix)
                        suspended.Add((point, completedFault, instance));
                    else if (!joins.Contains((point, completedFault)))
                        joins.Add((point, completedFault));
                }
            }
            return true;
        }

        /// <summary>The joins that end a pass where its parent suspends: an <c>await</c> in the prefix of an async body gives the body's task to
        /// its caller, and the paths of the parent leave the body at it with the work still running. The work is complete on them only where
        /// the caller waits for that task, and where the caller itself suspends, only where its own caller waits for its task, and so on up
        /// the async entries. The <c>await</c> stands for those waits when the body leaves its prefix on every path only through it, the
        /// exceptional ones too for its fault to follow the work; and it stands for itself when the body is the entry of the parent's
        /// execution, or is run by an admitted call, whose library awaits each run's task: work that is not that of an admitted pass keeps
        /// the <c>await</c> as its join there, and an admitted pass keeps it for the next run and reads the call's own task for what
        /// follows the call (<see cref="ClimbRuns"/>), because the paths that leave the call reach that before the delegate's work is
        /// done unless that task is waited. A caller that awaits the body at once, whose tail is joined to the body's, suspends at that
        /// await, and the climb goes on from it. Nothing stands for the <c>await</c> when some entry's task is not waited, or the body
        /// may leave around it.</summary>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="instance">The instance whose body holds the <c>await</c>.</param>
        /// <param name="point">The <c>await</c>.</param>
        /// <param name="completedFault">Whether a faulted outcome of the <c>await</c> still follows the work's completion.</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call (<see cref="Frame"/>).</param>
        /// <param name="outers">Receives, for an admitted pass, the passes of the admitted calls the climb meets (<see cref="ClimbRuns"/>);
        /// null for other work.</param>
        private IReadOnlyList<(PointKey Point, bool CompletedFault)> Climb(SpawnAnchor anchor, PassStart spawn, MethodInstance instance,
                                                                           PointKey point, bool completedFault, bool admittedPass,
                                                                           List<OuterPass>? outers = null)
        {
            WorkCancellation.Check();
            var entries = (order._steps.GetValueOrDefault(anchor.ExecutionId) ?? [])
                          .Where(step => step.Callee == instance.Id && step.CalleeSegment == BodySegment.Prefix)
                          .Select(step => (step.Caller, step.OperationId)).Distinct().ToArray();
            if (entries.Length == 0)
                return [(point, completedFault)];
            var runners = entries.Select(entry => order._awaitedCalls.FirstOrDefault(call => call.Instance == entry.Caller && call.Operation == entry.OperationId))
                                 .ToArray();
            if (runners.Any(runner => runner is not null))
                return !admittedPass ? [(point, completedFault)] : runners.All(runner => runner is not null) ? ClimbRuns(anchor, spawn, point, completedFault, runners!, outers) : [];

            var tails = new List<string>();
            var awaiting = new List<(MethodInstance Caller, PointKey Point, bool CompletedFault)>();
            foreach (var (caller, operation) in entries)
            {
                WorkCancellation.Check();
                var started = AsyncCallsAt(anchor.ExecutionId, caller, operation);
                if (started.Length != 0)
                {
                    tails.AddRange(started);
                    continue;
                }
                // A body entered by a call its caller awaits at once in a prefix has its tail joined to the caller's: the caller suspends
                // at that await, the work is complete where it resumes, and the climb goes on from there to the caller's own callers.
                if (AwaitedAtOnce(anchor.ExecutionId, caller, operation) is not { } awaited || !order._heap.Instances.TryGetValue(caller, out var calling))
                {
                    Steps?.Add(PassStepEvidence.Of("climb", anchor.ChildId, anchor.ExecutionId, false, ("entry-is-async-call", false)));
                    return [];
                }
                awaiting.Add((calling, awaited.Point, awaited.CompletedFault));
            }

            var flow = PassFlow(anchor, [(point, completedFault)]);
            var start = StartPoint(anchor, spawn);
            var bodyWaits = flow.BeforeReturn(start, [point], exceptionalExits: false);
            if (!bodyWaits)
            {
                Steps?.Add(PassStepEvidence.Of("climb", anchor.ChildId, anchor.ExecutionId, false, ("entry-is-async-call", true), ("body-waits", false)));
                return [];
            }
            var faultFollows = completedFault && flow.BeforeReturn(start, [point], exceptionalExits: true);
            var above = new List<(PointKey Point, bool CompletedFault)>();
            foreach (var tail in tails.Distinct())
            {
                WorkCancellation.Check();
                List<(PointKey Point, bool CompletedFault)> waits = [];
                var waited = false;
                if (_climbing.Add(tail))
                {
                    try
                    {
                        waited = Frame(tail, overlappingWork: false, out _, out _, out waits, admittedPass, outers);
                    }
                    finally
                    {
                        _climbing.Remove(tail);
                    }
                }
                Steps?.Add(PassStepEvidence.Of("climb", anchor.ChildId, tail, waited, ("entry-is-async-call", true), ("body-waits", true),
                                               ("fault-follows", faultFollows), ("entry-task-waited", waited)));
                if (!waited)
                    return [];
                above.AddRange(waits.Select(wait => (wait.Point, wait.CompletedFault && faultFollows)));
            }

            foreach (var (calling, awaitPoint, awaitFault) in awaiting)
            {
                WorkCancellation.Check();
                IReadOnlyList<(PointKey Point, bool CompletedFault)> climbed = [];
                if (_climbing.Add(UP + calling.Id))
                {
                    try
                    {
                        climbed = Climb(anchor, spawn, calling, awaitPoint, awaitFault && faultFollows, admittedPass, outers);
                    }
                    finally
                    {
                        _climbing.Remove(UP + calling.Id);
                    }
                }
                Steps?.Add(PassStepEvidence.Of("climb", anchor.ChildId, calling.Id, climbed.Count != 0, ("entry-awaited-at-once", true), ("body-waits", true),
                                               ("fault-follows", faultFollows), ("caller-task-waited", climbed.Count != 0)));
                if (climbed.Count == 0)
                    return [];
                above.AddRange(climbed);
            }

            return above;
        }

        /// <summary>The joins that end an admitted pass where the delegate of another admitted call suspends: the library awaits each run's
        /// task before the next run, so the <c>await</c> stands for itself for that run — the next run of the delegate, the next call
        /// the delegate makes — and the work needs no wait of the call's own task there. What the execution does after the call returns
        /// comes before the delegate's work is done unless the call's task is waited for, which each call's pass says
        /// (<see cref="OuterPass"/>): the wait at once (<see cref="Flow.WaitAtOnce"/>) with the exits it has by the join reading — the one
        /// <see cref="HappensBefore.ThrowsAfterCompletion(string, string, SummaryJoin, bool)"/> gives every wait, an <c>await</c> that
        /// suspends its own body climbing on from it (<see cref="Climb"/>) —, or the return of a member that has no task. A call whose
        /// task is not waited for at once has a pass with no join, whose work completes nothing for what comes after it.</summary>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="point">The <c>await</c> of the delegate.</param>
        /// <param name="completedFault">Whether a faulted outcome of the <c>await</c> still follows the work's completion.</param>
        /// <param name="runners">The admitted calls that run the delegate.</param>
        /// <param name="outers">Receives the pass of each call, or null for work that is not an admitted pass.</param>
        private IReadOnlyList<(PointKey Point, bool CompletedFault)> ClimbRuns(SpawnAnchor anchor, PassStart spawn, PointKey point, bool completedFault,
                                                                              IReadOnlyList<ExecutionModel.AdmittedCall> runners, List<OuterPass>? outers)
        {
            WorkCancellation.Check();
            var passes = outers ?? [];
            foreach (var runner in runners)
            {
                WorkCancellation.Check();
                if (!AddOuterPass(anchor, spawn, runner, passes))
                {
                    Steps?.Add(PassStepEvidence.Of("climb", anchor.ChildId, anchor.ExecutionId, false, ("library-call-found", false)));
                    return [];
                }
            }

            return [(point, completedFault)];
        }

        /// <summary>Adds the pass of an admitted call, as its caller waits for the call's task: the join of that wait, the climb from it
        /// where it suspends the caller's own prefix, or the return of the call.</summary>
        /// <param name="anchor">The start of the pass of the work.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="runner">The admitted call.</param>
        /// <param name="outers">Receives the pass, and those the climb meets.</param>
        private bool AddOuterPass(SpawnAnchor anchor, PassStart spawn, ExecutionModel.AdmittedCall runner, List<OuterPass> outers)
        {
            if (WaitOfCall(anchor.ExecutionId, runner) is not { } outer)
                return false;
            IReadOnlyList<(PointKey Point, bool CompletedFault)> joins = outer.Done || outer.Wait is not { } wait ? []
                : outer.Suspends ? Climb(anchor, spawn, outer.Caller, wait.Point, wait.CompletedFault, admittedPass: true, outers) : [wait];
            outers.Add(new OuterPass(outer.After, joins, outer.Done));
            return true;
        }

        /// <summary>Whether the pass of an admitted call that runs the delegate holding the work completes the work before the
        /// destinations: the delegate waits for it at once, the library awaits each run's task, and the call's own task is waited for
        /// where the destinations need it (<see cref="OutersAllow"/>), in a parent that does not overlap itself.</summary>
        /// <param name="anchor">The start of the pass of the work.</param>
        /// <param name="runner">The admitted call.</param>
        /// <param name="destinations">The points the work completes before.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        private bool RunPassAllows(SpawnAnchor anchor, ExecutionModel.AdmittedCall runner, IReadOnlyList<PointKey> destinations, string? creator)
        {
            WorkCancellation.Check();
            List<OuterPass> outers = [];
            return AddOuterPass(anchor, new PassStart(IrSpawnKind.AsyncCall, anchor.OperationId), runner, outers) &&
                   OutersAllow(anchor, outers, destinations, out var normal, out var all) && ParentAllows(anchor.ExecutionId, normal, all, creator, []);
        }

        /// <summary>How the task of an admitted call is waited for by the body making it.</summary>
        /// <param name="Caller">The instance making the call.</param>
        /// <param name="After">The point after the call.</param>
        /// <param name="Done">Whether the call returns only when its runs are done: a member that returns no task.</param>
        /// <param name="Wait">The wait at once for the call's task, with whether a faulted outcome of it still follows the work's
        /// completion, or null where the task is not waited for at once.</param>
        /// <param name="Suspends">Whether the wait is an <c>await</c> that suspends the caller's own prefix.</param>
        private sealed record CallWait(MethodInstance Caller, PointKey After, bool Done, (PointKey Point, bool CompletedFault)? Wait, bool Suspends);

        /// <summary>How the body making an admitted call waits for the call's own task: the wait at once (<see cref="Flow.WaitAtOnce"/>)
        /// with the exits it has by the join reading, which <see cref="HappensBefore.ThrowsAfterCompletion(string, string, SummaryJoin, bool)"/>
        /// gives every wait, or the return of a member that has no task. Null when the call or its instance cannot be placed.</summary>
        /// <param name="execution">The execution making the call.</param>
        /// <param name="runner">The admitted call.</param>
        private CallWait? WaitOfCall(string execution, ExecutionModel.AdmittedCall runner)
        {
            if (!order._heap.Instances.TryGetValue(runner.Instance, out var caller) ||
                !order._scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ||
                body.Blocks.SelectMany(block => block.Operations).FirstOrDefault(operation => operation.Id == runner.Operation) is not IrCallOperation call)
                return null;
            var after = new PointKey(caller.Id, call.Id, true);
            var wait = order.PlainFlow(execution).WaitAtOnce(body, call, runner.Task is not null);
            if (wait is not null && wait.Id == call.Id)
                return new CallWait(caller, after, true, null, false);
            if (wait is null || caller.Summary.Joins.FirstOrDefault(join => join.OperationId == wait.Id) is not { } waited)
                return new CallWait(caller, after, false, null, false);
            var suspends = wait is IrAwaitOperation &&
                           (order._visits.GetValueOrDefault(execution) ?? []).Any(visit => visit.InstanceId == caller.Id && visit.Segment == BodySegment.Prefix &&
                                                                                           order._segments.Runs(caller.BodyId, visit.Segment, wait.Id));
            return new CallWait(caller, after, false, (new PointKey(caller.Id, wait.Id, false), order.ThrowsAfterCompletion(execution, caller.Id, waited, proven: true)), suspends);
        }

        /// <summary>Whether what a pass's work is complete for stands after the passes of the admitted calls whose delegates the work is
        /// in: the destinations are reached from the point after each call only through the join of its task, and where the task is
        /// not waited for at once, are not reached from it at all — a loop that makes the call again reaches them, the code after the
        /// call does too. A call that returns when its runs are done completes the work for whatever follows.</summary>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="outers">The passes of the admitted calls.</param>
        /// <param name="destinations">The points the work completes before.</param>
        /// <param name="normal">Receives whether every normal return of the execution follows the join of each call's task.</param>
        /// <param name="all">Receives whether every exit of the execution, an exception that no handler catches included, follows it.</param>
        private bool OutersAllow(SpawnAnchor anchor, IReadOnlyList<OuterPass> outers, IReadOnlyList<PointKey> destinations, out bool normal, out bool all)
        {
            (normal, all) = (true, true);
            var allowed = true;
            foreach (var outer in outers)
            {
                WorkCancellation.Check();
                if (outer.Done)
                    continue;
                if (outer.Joins.Count == 0)
                {
                    var plain = order.PlainFlow(anchor.ExecutionId);
                    (normal, all) = (false, false);
                    allowed &= !destinations.Any(destination => plain.Reaches(outer.After, destination, overlappingRuns: true));
                    continue;
                }
                var flow = PassFlow(anchor, [.. outer.Joins]);
                var cuts = outer.Joins.Select(join => join.Point).ToArray();
                allowed &= destinations.All(destination => flow.BeforeDestination(outer.After, destination, cuts, overlappingRuns: true));
                normal &= flow.BeforeReturn(outer.After, cuts, exceptionalExits: false);
                all &= flow.BeforeReturn(outer.After, cuts, exceptionalExits: true);
            }

            return allowed;
        }

        /// <summary>The executions an async call at a place starts, in the execution making the call: the tail of the body it enters.</summary>
        /// <param name="execution">The execution making the call.</param>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="operation">The call operation.</param>
        private string[] AsyncCallsAt(string execution, string caller, int operation) =>
            (order._anchors.GetValueOrDefault(execution) ?? [])
                .Where(candidate => candidate.Kind == SpawnAnchorKind.AsyncReturn && candidate.CallerInstance == caller && candidate.OperationId == operation)
                .Select(candidate => candidate.ChildId).Distinct().ToArray();

        /// <summary>The await at which a body waits at once for the task of one call it makes, as a suspension of its own prefix: the
        /// call is followed by that await with nothing between that may throw (<see cref="Flow.WaitAtOnce"/>). The body it enters has its
        /// tail joined to the caller's, the work of the callee is complete where the await resumes, and the caller gives its own task to
        /// its callers there.</summary>
        /// <param name="execution">The execution making the call.</param>
        /// <param name="callerInstance">The instance making the call.</param>
        /// <param name="operation">The call operation.</param>
        private (PointKey Point, bool CompletedFault)? AwaitedAtOnce(string execution, string callerInstance, int operation)
        {
            if (!order._heap.Instances.TryGetValue(callerInstance, out var caller) ||
                !order._scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ||
                body.Blocks.SelectMany(block => block.Operations).FirstOrDefault(candidate => candidate.Id == operation) is not IrCallOperation call ||
                order.PlainFlow(execution).WaitAtOnce(body, call, returnsTask: true) is not IrAwaitOperation wait ||
                !(order._visits.GetValueOrDefault(execution) ?? []).Any(visit => visit.InstanceId == caller.Id && visit.Segment == BodySegment.Prefix &&
                                                                                 order._segments.Runs(caller.BodyId, visit.Segment, wait.Id)) ||
                caller.Summary.Joins.FirstOrDefault(join => join.OperationId == wait.Id) is not { } join)
                return null;
            return (new PointKey(caller.Id, wait.Id, false), order.ThrowsAfterCompletion(execution, caller.Id, join, proven: true));
        }

        /// <summary>The places that enter a chain of bodies: the calls of the bodies given, and, for a call its caller awaits at once in
        /// a prefix, whose body's tail is joined to the caller's, the calls of that caller in turn. Whatever else calls the body — an async
        /// call, a synchronous entry — is a place of its own.</summary>
        /// <param name="execution">The execution making the calls.</param>
        /// <param name="entries">The calls of the body, each by its caller instance, its operation and the instance it enters.</param>
        private List<(string Caller, int Site, string Body)> EntryPoints(string execution, List<(string Caller, int Site, string Body)> entries)
        {
            var places = new List<(string Caller, int Site, string Body)>();
            var seen = new HashSet<(string Caller, int Site, string Body)>();
            var pending = new Queue<(string Caller, int Site, string Body)>(entries);
            while (pending.TryDequeue(out var entry))
            {
                WorkCancellation.Check();
                if (!seen.Add(entry))
                    continue;
                var through = AsyncCallsAt(execution, entry.Caller, entry.Site).Length == 0 && !order._segments.IsAsyncSpawn(entry.Caller, entry.Site, entry.Body) &&
                              (order._visits.GetValueOrDefault(execution) ?? []).Any(visit => visit.InstanceId == entry.Body && visit.Segment == BodySegment.Prefix) &&
                              AwaitedAtOnce(execution, entry.Caller, entry.Site) is not null;
                if (!through)
                {
                    places.Add(entry);
                    continue;
                }
                foreach (var edge in order._heap.Edges.Where(edge => edge.CalleeInstance == entry.Caller))
                {
                    WorkCancellation.Check();
                    pending.Enqueue((edge.CallerInstance, edge.OperationId, entry.Caller));
                }
            }

            return places;
        }

        /// <summary>The waits of one visit of a timer's parent for every callback of this pass's timer: the await of its
        /// <c>DisposeAsync()</c>, whose fault follows the callbacks' completion, and each <c>WaitOne()</c> that on every path follows its
        /// <c>Dispose(handle)</c>, whose fault does not, as the happens-before graph reads a timer's waits.</summary>
        /// <param name="execution">The callback execution.</param>
        /// <param name="parent">The execution creating the timer.</param>
        /// <param name="visit">The visit of the parent.</param>
        /// <param name="instance">The instance of the visit.</param>
        /// <param name="anchor">The timer's creation.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="joins">Receives the waits.</param>
        /// <param name="suspended">Receives the awaits that suspend the body of a prefix visit, which end the pass only for the caller that
        /// waits the body's task (<see cref="Climb"/>).</param>
        /// <param name="rejected">Receives the disposals not proven to be of this pass's timer, or null.</param>
        private void TimerJoins(string execution, string parent, ExecutionVisit visit, MethodInstance instance, SpawnAnchor anchor, PassStart spawn,
                                List<(PointKey Point, bool CompletedFault)> joins,
                                List<(PointKey Point, bool CompletedFault, MethodInstance Instance)> suspended, List<PassJoinEvidence>? rejected)
        {
            foreach (var timer in instance.Summary.Timers)
            {
                WorkCancellation.Check();
                if (timer.Action is not (IrTimerAction.DisposeAsync or IrTimerAction.DisposeWaitHandle) ||
                    !order._segments.Runs(instance.BodyId, visit.Segment, timer.OperationId))
                    continue;
                if (order.ProveHandleSource(parent, instance, timer.Timer, timer.OperationId) != execution)
                {
                    rejected?.Add(new PassJoinEvidence(instance.Id, timer.OperationId, false, "source"));
                    continue;
                }
                if (!Fresh(timer.Timer, instance, anchor, spawn))
                {
                    rejected?.Add(new PassJoinEvidence(instance.Id, timer.OperationId, false, "not-this-pass"));
                    continue;
                }
                var awaited = timer.Action == IrTimerAction.DisposeAsync;
                var points = awaited
                    ? order.DisposeAwaits(visit, instance, timer).Select(operation => new PointKey(instance.Id, operation, false)).ToList()
                    : order.HandleWaitPoints(parent, instance, timer);
                foreach (var point in points)
                {
                    WorkCancellation.Check();
                    if (awaited && visit.Segment == BodySegment.Prefix)
                        suspended.Add((point, awaited, instance));
                    else if (!joins.Contains((point, awaited)))
                        joins.Add((point, awaited));
                }
            }
        }

        /// <summary>Where a pass starts its work in the parent's flow: the spawn or async call, or for a <c>Parallel</c> call, the call
        /// itself, whose return completes the work.</summary>
        /// <param name="anchor">The start.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        private PointKey StartPoint(SpawnAnchor anchor, PassStart spawn) =>
            spawn.ReturnCompletes ? new PointKey(anchor.CallerInstance, spawn.CallOperationId, false) : order.AnchorPoint(anchor);

        /// <summary>The library model record of the call a join point makes, for a diagnostic trace: the joining call of a summary join, or
        /// the call at the point itself; null for an await or a call no model describes.</summary>
        /// <param name="point">The join point.</param>
        private ModelRecordEvidence? JoinModel(PointKey point)
        {
            if (!order._heap.Instances.TryGetValue(point.Instance, out var instance) ||
                !order._scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                return null;
            var call = instance.Summary.Joins.FirstOrDefault(join => join.OperationId == point.Operation)?.CallOperationId ?? point.Operation;
            return body.Blocks.SelectMany(block => block.Operations).FirstOrDefault(operation => operation.Id == call) is IrCallOperation { Library: { } library }
                ? ModelRecordEvidence.Of("join", instance.Id, call, library, null)
                : null;
        }

        /// <summary>The start of an execution the proof reads passes of: a <c>Task.Run</c>, <c>StartNew</c> or <c>Thread.Start</c> spawn,
        /// or an async call's return, and with overlapping work a <c>Parallel.For</c> or <c>Parallel.ForEach</c> spawn or a
        /// <c>System.Threading.Timer</c>'s creation, of an execution that is neither recursive nor a composite task.</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="overlappingWork">Whether work whose units overlap within one pass is read.</param>
        /// <param name="anchor">Receives the start.</param>
        /// <param name="spawn">Receives the kind of the start and the call making it.</param>
        private bool Start(string execution, bool overlappingWork, out SpawnAnchor anchor, out PassStart spawn)
        {
            spawn = null!;
            if (!order._anchorOfChild.TryGetValue(execution, out anchor!) || order._executions[execution].IsRecursive || order.IsComposite(execution))
                return false;
            var origin = anchor;
            if (anchor.Kind == SpawnAnchorKind.Spawn &&
                order._spawnSites.TryGetValue((anchor.CallerInstance, anchor.OperationId), out var site) &&
                (site.Kind is IrSpawnKind.TaskRun or IrSpawnKind.StartNew or IrSpawnKind.ThreadStart ||
                 overlappingWork && site.Kind is IrSpawnKind.ParallelFor or IrSpawnKind.ParallelForEach))
                spawn = new PassStart(site.Kind, site.CallOperationId);
            else if (anchor.Kind == SpawnAnchorKind.AsyncReturn && order._heap.AsyncSpawns.Any(site =>
                         site.CallerInstance == origin.CallerInstance && site.OperationId == origin.OperationId && site.Kind == IrSpawnKind.AsyncCall))
                spawn = new PassStart(IrSpawnKind.AsyncCall, anchor.OperationId);
            else if (overlappingWork && anchor.Kind == SpawnAnchorKind.Timer && order._heap.TimerCallbacks.Any(site =>
                         site.CallerInstance == origin.CallerInstance && site.OperationId == origin.OperationId && site.Action == IrTimerAction.Create))
                spawn = new PassStart(null, anchor.OperationId);
            return spawn is not null;
        }

        /// <summary>Whether a handle comes from the pass's own start: the spawn's or async call's result, a new thread or timer the parent
        /// makes on every path to the next start, or a value every call or parameter it comes from carries that start's handle in.</summary>
        /// <param name="handle">The handle value.</param>
        /// <param name="instance">The instance expressing it.</param>
        /// <param name="anchor">The pass's start.</param>
        /// <param name="spawn">The kind of the start and the call making it.</param>
        private bool Fresh(SummaryValue handle, MethodInstance instance, SpawnAnchor anchor, PassStart spawn)
        {
            WorkCancellation.Check();
            return Fresh(handle, instance, anchor, spawn, []);
        }

        /// <summary>Whether a handle comes from the pass's own start, with the calls and parameters already followed.</summary>
        /// <param name="handle">The handle value.</param>
        /// <param name="instance">The instance expressing it.</param>
        /// <param name="anchor">The pass's start.</param>
        /// <param name="spawn">The kind of the start and the call making it.</param>
        /// <param name="seen">The calls and parameters on the chain followed.</param>
        private bool Fresh(SummaryValue handle, MethodInstance instance, SpawnAnchor anchor, PassStart spawn,
                           HashSet<(string Instance, int Operation)> seen)
        {
            WorkCancellation.Check();
            if (handle.Values.Count == 0 || handle.Completions.Count != 0 || handle.CompletedCalls.Count != 0 ||
                handle.UnknownSources.Any(source => source is not (UnknownSource.SourceCall or UnknownSource.Parameter)))
                return false;
            return handle.Values.All(value => value switch
            {
                AllocationValue allocation => instance.Id == anchor.CallerInstance && allocation.Site.BodyId == instance.BodyId &&
                    (allocation.Site.OperationId == spawn.CallOperationId || allocation.Site.OperationId == anchor.OperationId ||
                     (spawn.Kind == IrSpawnKind.ThreadStart || anchor.Kind == SpawnAnchorKind.Timer) && order.PlainFlow(anchor.ExecutionId).BeforeDestination(
                         order.AnchorPoint(anchor), order.AnchorPoint(anchor), [new PointKey(instance.Id, allocation.Site.OperationId, false)],
                         overlappingRuns: false)),
                CallResultValue result => FromCall(instance, result.OperationId),
                ParameterValue parameter => FromParameter(instance, parameter.Ordinal),
                _ => false
            }) && handle.SourceCalls.All(call => FromCall(instance, call));

            bool FromCall(MethodInstance caller, int operation)
            {
                if (caller.Id == anchor.CallerInstance && operation == spawn.CallOperationId)
                    return true;
                if (order._heap.UnresolvedCallTargets.Contains((caller.Id, operation)) || !seen.Add((caller.Id, operation)))
                    return false;
                try
                {
                    var targets = order._heap.Edges.Where(edge => edge.CallerInstance == caller.Id && edge.OperationId == operation)
                        .Select(edge => order._heap.Instances.GetValueOrDefault(edge.CalleeInstance)).ToArray();
                    return targets.Length != 0 && targets.All(target => target is not null && target.Summary.Returns.Count != 0 &&
                        target.Summary.Returns.All(returned => Fresh(new SummaryValue(returned.Values, returned.UnknownSources)
                        {
                            SourceCalls = returned.SourceCalls,
                            Completions = returned.Completions,
                            CompletedCalls = returned.CompletedCalls
                        }, target, anchor, spawn, seen)));
                }
                finally
                {
                    seen.Remove((caller.Id, operation));
                }
            }

            bool FromParameter(MethodInstance callee, int ordinal)
            {
                if (!seen.Add((callee.Id, -ordinal - 2)))
                    return false;
                try
                {
                    var bindings = order._heap.Edges.Where(edge => edge.CalleeInstance == callee.Id).ToArray();
                    return bindings.Length != 0 && bindings.All(edge =>
                        order._heap.Instances.TryGetValue(edge.CallerInstance, out var caller) &&
                        caller.Summary.Calls.FirstOrDefault(call => call.OperationId == edge.OperationId)?.Arguments
                            .FirstOrDefault(argument => argument.ParameterOrdinal == ordinal) is { } argument &&
                        Fresh(new SummaryValue(argument.Values, argument.UnknownSources)
                        {
                            SourceCalls = argument.SourceCalls,
                            Completions = argument.Completions,
                            CompletedCalls = argument.CompletedCalls
                        }, caller, anchor, spawn, seen));
                }
                finally
                {
                    seen.Remove((callee.Id, -ordinal - 2));
                }
            }
        }
    }
}
