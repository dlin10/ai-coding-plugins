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
    /// between them; a sequential-awaited call's continuation of one run completes before the next run's prefix where the call's task
    /// is awaited at once. The iterations of one <c>Parallel.For</c> or
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

        private readonly Dictionary<(string SourceExecution, string DestinationExecution, PointKey Source, PointKey Destination, string? Creator), bool> _proofs = [];
        private readonly Dictionary<string, Flow> _flows = new(StringComparer.Ordinal);

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

        /// <summary>Whether a continuation of an admitted sequential-awaited call's delegate is ordered with another run of the same
        /// delegate there. One call of the library runs the delegate one run after another and completes each run's task before the next
        /// run starts and before its own task completes. Runs of different calls are ordered only when the call's task is awaited at once
        /// in the prefix of a body entered only through one async call whose own pass completes before its next start.</summary>
        /// <param name="continuation">The access in a continuation of the call's delegate.</param>
        /// <param name="other">The access in the same continuation or in the delegate's own prefix run by that call.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        private bool SequentialRuns(Access continuation, Access other, string? creator)
        {
            // A non-async target the admission runs as one async call of its body anchors that call's continuation in its own instance.
            if (!order._anchorOfChild.TryGetValue(continuation.ExecutionId, out var anchor) || anchor.Kind != SpawnAnchorKind.AsyncReturn ||
                order._awaitedCalls.FirstOrDefault(call => call.Execution == anchor.ExecutionId && call.Continuations.Contains(continuation.ExecutionId))
                    is not { } admitted ||
                !order._heap.Instances.TryGetValue(admitted.Instance, out var instance) ||
                !order._scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                return false;
            // One continuation execution can stand for the call in several instances of its body; each would need its own proof.
            if (order._anchors.Values.SelectMany(anchors => anchors).Count(candidate => candidate.ChildId == continuation.ExecutionId) != 1)
                return false;
            // The admission leaves the one delegate it read as the call's every async target, at the call or at the async call a
            // non-async target runs as; other delegates the call runs, such as callbacks a holder keeps, are executions of their own and
            // do not decide this one's order.
            var sites = admitted.Continuations.Select(child => order._anchorOfChild.GetValueOrDefault(child))
                                .OfType<SpawnAnchor>().Select(site => (site.CallerInstance, site.OperationId)).ToHashSet();
            bool OnlyFrom(string callee, string caller, int operation) =>
                order._heap.Edges.Where(edge => edge.CalleeInstance == callee)
                     .All(edge => edge.CallerInstance == caller && edge.OperationId == operation);
            if (other.ExecutionId != continuation.ExecutionId &&
                !(other.ExecutionId == anchor.ExecutionId &&
                  order._heap.AsyncSpawns.Any(site => sites.Contains((site.CallerInstance, site.OperationId)) && site.Callees.Contains(other.InstanceId) &&
                                                      OnlyFrom(other.InstanceId, site.CallerInstance, site.OperationId))))
                return false;

            // The call's task is awaited at once: nothing that may throw stands between, so the prefix ends there whenever the call returns.
            var block = body.Blocks.FirstOrDefault(candidate => candidate.Operations.Any(operation => operation.Id == admitted.Operation));
            if (block?.Operations.FirstOrDefault(operation => operation.Id == admitted.Operation) is not IrCallOperation { ResultValue: int task } call ||
                !order._segments.Runs(instance.BodyId, BodySegment.Prefix, call.Id) || order._segments.Runs(instance.BodyId, BodySegment.Tail, call.Id))
                return false;
            var awaited = false;
            foreach (var operation in block.Operations.SkipWhile(operation => operation.Id != call.Id).Skip(1))
            {
                WorkCancellation.Check();
                if (operation is IrAwaitOperation { AwaitableValue: var value } && value == task)
                {
                    awaited = true;
                    break;
                }
                if (Flow.MayThrow(operation))
                    return false;
            }
            if (!awaited)
                return false;

            // The body runs only as the async call of one site; each pass of that call completes before its next start.
            var entries = order._heap.Edges.Where(edge => edge.CalleeInstance == instance.Id).Select(edge => (edge.CallerInstance, edge.OperationId))
                               .Distinct().ToArray();
            if (entries is not [var (caller, site)])
                return false;
            var outer = (order._anchors.GetValueOrDefault(anchor.ExecutionId) ?? []).Where(candidate => candidate.Kind == SpawnAnchorKind.AsyncReturn &&
                candidate.CallerInstance == caller && candidate.OperationId == site).Select(candidate => candidate.ChildId).Distinct().ToArray();
            // An entry awaited at once runs the whole body in the execution making the call, which the admission has run once and make
            // the call once: the entry ends before that execution goes on, and nothing starts it again.
            var inline = outer.Length == 0 && !order._segments.IsAsyncSpawn(caller, site, instance.Id) &&
                         (order._visits.GetValueOrDefault(anchor.ExecutionId) ?? []).Any(visit => visit.InstanceId == instance.Id &&
                                                                                                 visit.Segment == BodySegment.Whole);
            var proven = inline || outer is [var asyncCall] && CompleteBeforeNext(asyncCall, creator);
            Steps?.Add(PassStepEvidence.Of("sequential-runs", continuation.ExecutionId, other.ExecutionId, proven,
                ("awaited-at-once", true), ("one-async-entry", inline || outer.Length == 1), ("entry-completes-before-next", proven)));
            if (proven && call.Library is { } library)
                Models?.Add(ModelRecordEvidence.Of("sequential-runs", instance.Id, call.Id, library, RunRelation.SequentialAwaited));
            return proven;
        }

        /// <summary>Whether each pass of an execution's start completes the work before the next pass starts it again, in a parent that
        /// does not overlap itself.</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        private bool CompleteBeforeNext(string execution, string? creator)
        {
            WorkCancellation.Check();
            return Frame(execution, overlappingWork: false, out var anchor, out var spawn, out var joins) &&
                   CompleteBeforeNext(execution, creator, anchor, spawn, joins);
        }

        /// <summary>Whether each pass of an execution's start completes the work before the next pass, with the pass's joins given.</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="joins">The pass's joins, with whether a faulted outcome of each still follows the work's completion.</param>
        private bool CompleteBeforeNext(string execution, string? creator, SpawnAnchor anchor, PassStart spawn,
                                        List<(PointKey Point, bool CompletedFault)> joins)
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
            if (!joins.Contains((point, completedFault)))
                joins.Add((point, completedFault));
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
                // wait for the parent completes it on a fault only when every exit of the parent waited for it as well — and for the work
                // below it, which a join whose fault does not follow that work's completion has not.
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
        /// comes after that join, as <see cref="FlowOf"/> reads proven joins — and so does that of a site calling such a join where the
        /// hoisting of <see cref="AtCallSites"/> says the call throws only after it: a path asking about the pass's work stops there.</summary>
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
        private bool Frame(string execution, bool overlappingWork, out SpawnAnchor anchor, out PassStart spawn,
                           out List<(PointKey Point, bool CompletedFault)> joins)
        {
            joins = [];
            WorkCancellation.Check();
            if (!Start(execution, overlappingWork, out anchor, out spawn))
            {
                Trace?.Add(new PassFrameEvidence(execution, null, null, null, null, -1, [], []));
                return false;
            }
            var parent = order._executions[anchor.ExecutionId];
            var rejected = Trace is null ? null : new List<PassJoinEvidence>();
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
                    TimerJoins(execution, parent.Id, visit, instance, anchor, spawn, joins, rejected);
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
                    if (!joins.Contains((point, completedFault)))
                        joins.Add((point, completedFault));
                }
            }
            Trace?.Add(new PassFrameEvidence(execution, anchor.ExecutionId, anchor.Kind.ToString(), spawn.Kind?.ToString(), anchor.CallerInstance,
                anchor.OperationId, joins.Select(join => new PassJoinEvidence(join.Point.Instance, join.Point.Operation, join.CompletedFault, null,
                    JoinModel(join.Point))).ToArray(),
                rejected?.ToArray() ?? []));
            return joins.Count != 0;
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
        /// <param name="rejected">Receives the disposals not proven to be of this pass's timer, or null.</param>
        private void TimerJoins(string execution, string parent, ExecutionVisit visit, MethodInstance instance, SpawnAnchor anchor, PassStart spawn,
                                List<(PointKey Point, bool CompletedFault)> joins, List<PassJoinEvidence>? rejected)
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
                    if (!joins.Contains((point, awaited)))
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
