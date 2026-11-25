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

        /// <summary>The answers of <see cref="Ordered"/> by question, at most the graph's <see cref="HappensBefore.ProofAnswerLimit"/>.</summary>
        private readonly Dictionary<(string SourceExecution, string DestinationExecution, PointKey Source, PointKey Destination, string? Creator), bool> _proofs = [];
        private readonly Dictionary<string, Flow> _flows = new(StringComparer.Ordinal);
        private GapOrigins? _handleOrigins;

        /// <summary>The prefix of a caller's id in <see cref="_climbing"/>, which tells it from a tail's.</summary>
        private const string UP = "up:";

        /// <summary>The tails whose waits and the callers whose awaits <see cref="Climb"/> is reading, to stop a chain of entries that
        /// comes back to itself.</summary>
        private readonly HashSet<string> _climbing = new(StringComparer.Ordinal);

        // The waits the climb passed, by the start of the work they complete: one start's waits never explain another's exits (ADR 0035).
        // They explain exceptional edges, but are not cuts for a caller that still has to wait for the returned task. A weaker proof of a
        // fault must survive a stronger reading of the same wait's form. They are the disjunct's own: forgotten before each disjunct of a
        // question is proven, so that no question reads the waits another's climbs passed, such as an outer caller's await an admitted pass
        // adds (#486), and no disjunct those of one tried before it (#490).
        private readonly Dictionary<SpawnAnchor, Dictionary<PointKey, bool>> _completions = [];

        /// <summary>Forgets the waits of the climb, so that the disjunct of a question that follows reads only the waits its own climbs
        /// pass (#490).</summary>
        /// <returns>True, so that a disjunct reads <c>Anew() &amp;&amp; disjunct</c>.</returns>
        private bool Anew()
        {
            _completions.Clear();
            return true;
        }

        /// <summary>Keeps a completion the climb passed, with the weakest proof of its exceptional exit.</summary>
        /// <param name="start">The start of the work being completed.</param>
        /// <param name="point">The wait.</param>
        /// <param name="completedFault">Whether a faulted outcome still follows this work's completion.</param>
        private void Completion(SpawnAnchor start, PointKey point, bool completedFault)
        {
            if (!_completions.TryGetValue(start, out var waits))
                _completions.Add(start, waits = []);
            waits[point] = (!waits.TryGetValue(point, out var known) || known) && completedFault;
        }

        /// <summary>Where a diagnostic request collects every start the proof considers; null for the analysis itself.</summary>
        internal List<PassFrameEvidence>? Trace { get; init; }

        /// <summary>Where a diagnostic request collects every transition check the proof makes; null for the analysis itself.</summary>
        internal List<PassStepEvidence>? Steps { get; init; }

        /// <summary>Where a diagnostic request collects the library model records a check that held read; null for the analysis itself.</summary>
        internal List<ModelRecordEvidence>? Models { get; init; }

        /// <summary>Whether the completion of the queried passes orders two accesses: of one execution, its passes do not overlap each
        /// other; of two, the work of one completes before the other on every pass. An answer is kept for the same question asked again,
        /// and at the graph's limit every kept answer is forgotten at once (#486): pairing asks about every pair of variants of two sides,
        /// so a question is mostly about a pair of executions no question named before, and keeping each answer grew without bound on
        /// OrchardCore. A question asked after that is proven anew and gets the answer it got, because an answer is a function of the
        /// graph and the question alone: each of its disjuncts reads only the waits its own climbs pass (<see cref="_completions"/>,
        /// forgotten before every disjunct, <see cref="Anew"/>); the joins of a parent are recorded by a walk whose answers no start
        /// changes (#475); a pass's flow is kept by everything it is built from; and the climb's guards are empty at the top of a question.
        /// So the order of questions decides no answer, and a diagnostic request's fresh proof answers what the analysis answered. Nor does
        /// the order of the question's own accesses (#490): the disjuncts are the same either way round, and a disjunct that fails leaves
        /// no wait for the next to read, even where both climb through one outer start.</summary>
        /// <param name="first">One access.</param>
        /// <param name="second">The other access.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        internal bool Ordered(Access first, Access second, string? creator)
        {
            var source = new PointKey(first.InstanceId, first.OperationId, false);
            var destination = new PointKey(second.InstanceId, second.OperationId, false);
            var key = (first.ExecutionId, second.ExecutionId, source, destination, creator);
            order.Counters.PassProofs++;
            if (_proofs.TryGetValue(key, out var proven))
            {
                order.Counters.PassProofHits++;
                return proven;
            }

            proven = first.ExecutionId == second.ExecutionId
                ? Anew() && CompleteBeforeNext(first.ExecutionId, creator)
                : Anew() && CompleteBeforeAccess(first, second, creator) || Anew() && CompleteBeforeAccess(second, first, creator);
            proven = proven || Anew() && SequentialRuns(first, second, creator) || Anew() && SequentialRuns(second, first, creator);
            if (_proofs.Count >= order.ProofAnswerLimit)
            {
                _proofs.Clear();
                order.Counters.PassProofClears++;
            }
            _proofs.Add(key, proven);
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
        /// each suspension of the bodies on the way up is reached only through the wait below (<see cref="TailFollows"/>). Each admitted
        /// call is proven with the start of the continuation that is its own (<see cref="StartOfCall"/>), and the proof holds when it holds
        /// for every call (ADR 0035).</summary>
        /// <param name="continuation">The access in a continuation of the call's delegate.</param>
        /// <param name="other">The access in the same continuation, in the synchronous part of the delegate run by that call, or, for a
        /// call admitted per pass, in the execution making the call outside the call's delegates or in the tail of a body on the way up
        /// from the body making it.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        private bool SequentialRuns(Access continuation, Access other, string? creator)
        {
            var starts = order.StartsOf(continuation.ExecutionId);
            if (starts.Count == 0 || starts.Any(start => start.Kind != SpawnAnchorKind.AsyncReturn) ||
                starts.Select(start => start.ExecutionId).Distinct(StringComparer.Ordinal).ToArray() is not [var parent])
                return false;
            // One continuation execution can stand for the call in several instances of its body, each admitted as a call of its own:
            // each needs its own proof with its own start, and the entries of all of them are one set.
            var calls = order._awaitedCalls.Where(call => call.Execution == parent && call.Continuations.Contains(continuation.ExecutionId)).ToArray();
            var own = calls.Select(call => StartOfCall(call, continuation.ExecutionId)).ToArray();
            if (calls.Length == 0 || calls.Length != starts.Count || own.Any(start => start is null) || own.Distinct().Count() != calls.Length)
                return false;
            // The admission leaves the one delegate it read as the call's every async target, at the call or at the async call a
            // non-async target runs as; other delegates the call runs, such as callbacks a holder keeps, are executions of their own and
            // do not decide this one's order.
            var children = calls.SelectMany(call => call.Continuations).ToHashSet(StringComparer.Ordinal);
            var sites = children.SelectMany(order.StartsOf).Select(site => (site.CallerInstance, site.OperationId)).ToHashSet();
            var occurrence = other.ExecutionId == parent ? OccurrenceOf(parent, calls, sites, other.InstanceId) : (Occurrence?)null;
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
                         order.StartsOf(other.ExecutionId).Any(candidate => candidate.ExecutionId == parent && candidate.Kind == SpawnAnchorKind.AsyncReturn);
            if (other.ExecutionId != continuation.ExecutionId && !inDelegate && !inParent && !inTail)
                return false;

            var target = new PointKey(other.InstanceId, other.OperationId, false);
            var entries = new List<(string Caller, int Site, string Body, SpawnAnchor Start)>();
            var models = new List<ModelRecordEvidence>();
            bool nextCall = true, allowed = true;
            foreach (var (admitted, anchor) in calls.Zip(own, (call, start) => (call, start!)))
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
                    // The call's own runs: no work below them whose fault could be lost on the way up.
                    var flow = PassFlow(anchor, waits, faultCompletion: true);
                    PointKey[] cuts = [point];
                    // Every path from the call back to the call passes the wait, and from the call to what the parent does after it.
                    next = flow.BeforeNext(after, cuts) && (!inParent || flow.BeforeDestination(after, target, cuts, overlappingRuns: true));
                    (normal, exceptional) = (flow.BeforeReturn(after, cuts, exceptionalExits: false), flow.BeforeReturn(after, cuts, exceptionalExits: true));
                }
                nextCall &= next;
                allowed &= next && ParentAllows(anchor.ExecutionId, normal, exceptional, creator, []);
                if (!allowed)
                    break;
                // The tail runs after a suspension of its body, which the body reaches only through the wait for the work. The wait is
                // for the call's own runs, with no work below them whose fault could be lost on the way up.
                if (inTail && !TailFollows(anchor, instance, after, waits, other.ExecutionId, faultCompletion: true, []))
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
                                          .Select(edge => (edge.CallerInstance, edge.OperationId, instance.Id, anchor)));
                }
                if (call.Library is { } library)
                    models.Add(ModelRecordEvidence.Of("sequential-runs", instance.Id, call.Id, library, RunRelation.SequentialAwaited));
            }

            // The body is the root of its execution, which runs once, or waits for the work without suspending, or each of its entries
            // is synchronous or an async call whose pass completes before the next pass of the entry and before the next entry starts:
            // a suspension of the body gives its task to the caller, and the work is complete only where the caller waits for that task,
            // up every level of the chain: a caller that awaits the body at once suspends in turn, and its own callers are the entries.
            entries = EntryPoints(parent, entries);
            var entryStarts = entries.Select(entry => new PointKey(entry.Caller, entry.Site, false)).Distinct().ToArray();
            // The tail is the rest of a call this pass makes, which the next pass may start before the tail is done unless it completes first.
            var entriesComplete = allowed && (!inTail || CompleteBeforeNext(other.ExecutionId, creator, admittedPass: true));
            foreach (var (caller, site, bodyInstance, start) in entries)
            {
                WorkCancellation.Check();
                if (!entriesComplete)
                    break;
                // The delegate of an admitted call is entered by the call, whose own pass says where the work is complete.
                if (order._awaitedCalls.FirstOrDefault(call => call.Instance == caller && call.Operation == site) is { } runner)
                {
                    entriesComplete = RunPassAllows(start, runner, inParent ? [.. entryStarts, target] : entryStarts, creator);
                    continue;
                }
                var outer = AsyncCallsAt(parent, caller, site);
                // A synchronous entry runs the whole body in the execution making the call before that execution goes on.
                var inline = outer.Length == 0 && order.Forwarders.Returned(parent, bodyInstance) is null &&
                             !order._segments.IsAsyncSpawn(caller, site, bodyInstance) &&
                             (order._visits.GetValueOrDefault(parent) ?? []).Any(visit => visit.InstanceId == bodyInstance &&
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

        /// <summary>The start of a continuation that is an admitted call's own: the anchor at the call itself, or, for a non-async target
        /// the call runs as one async call of its body, the anchor of that inner call in the target's instance — the task the target
        /// forwards, as the admission found it (<see cref="ForwardedTask"/>). Null when no such start is one of the continuation's, or more
        /// than one target forwards one.</summary>
        /// <param name="call">The admitted call.</param>
        /// <param name="continuation">The continuation execution.</param>
        private SpawnAnchor? StartOfCall(ExecutionModel.AdmittedCall call, string continuation)
        {
            var starts = order.StartsOf(continuation);
            if (starts.FirstOrDefault(start => start.CallerInstance == call.Instance && start.OperationId == call.Operation) is { } own)
                return own;
            var forwarded = order._heap.Edges.Where(edge => edge.CallerInstance == call.Instance && edge.OperationId == call.Operation)
                                 .Select(edge => order.Forwarders.Returned(call.Execution!, edge.CalleeInstance))
                                 .Where(start => start is not null && starts.Contains(start))
                                 .Distinct()
                                 .ToArray();
            return forwarded is [var one] ? one : null;
        }

        /// <summary>Whether the code a body runs after a suspension is reached only after the work of an admitted call is complete: the
        /// body is the one making the call or one above it, entered at an async call whose task the body below is waited by, and each of
        /// its suspensions is reached from the call, or from the call of the body below, only through the wait for that call's task —
        /// the call's own wait in the body making it, a join of the tail below in a body above. A body entered whole runs inside its
        /// caller before the caller goes on, so the caller's suspensions follow the same wait; one a caller awaits at once in its prefix
        /// has its tail joined to the caller's, which suspends at that await; one that an admitted call runs as its delegate is waited
        /// for by the call's own wait (<see cref="WaitOfCall"/>). A call whose work is done when it returns has no wait to pass. The tail of
        /// a body above is joined where its caller waits for the task of the one start at that caller and call (<see cref="AsyncStartsAt"/>).
        /// A caller's wait — a fresh join, an <c>await</c> at once, an admitted call's — stands for the body's waits only where every normal
        /// return of the body passes them, and its fault follows the work only where every exception of the body does too: a body that
        /// may return or throw around its waits hands its caller a task complete, or faulted, before the work is (#503).</summary>
        /// <param name="anchor">The start of the continuation of the call's delegate.</param>
        /// <param name="body">The body at this level of the chain of async entries.</param>
        /// <param name="start">The point after the call this level makes: the admitted call, or the async call of the body below.</param>
        /// <param name="waits">The waits at this level for the work of the call or the task of the body below, with whether a faulted
        /// outcome of each still follows the work's completion; none for a call whose work is done when it returns.</param>
        /// <param name="tail">The execution running the code after a suspension of a body in the chain.</param>
        /// <param name="faultCompletion">Whether a fault of the work below still follows its completion (<see cref="PassFlow"/>): where
        /// it does not, a suspension reached through a wait's exceptional exit is reached around that wait.</param>
        /// <param name="seen">The bodies already asked on the way down to this one, which the chain does not come back to.</param>
        private bool TailFollows(SpawnAnchor anchor, MethodInstance body, PointKey start, List<(PointKey Point, bool CompletedFault)> waits, string tail,
                                 bool faultCompletion, HashSet<string> seen)
        {
            WorkCancellation.Check();
            if (!seen.Add(body.Id))
                return false;
            // What the body's exits guarantee a caller whose wait for its task stands for the waits here: a normal return around them
            // hands the caller a task complete before the work is, and an exception around them a fault the work has not completed
            // before (#503). A call whose work is done when it returns guarantees both.
            bool normal = true, faultFollows = faultCompletion;
            if (waits.Count != 0)
            {
                var flow = PassFlow(anchor, waits, faultCompletion);
                var cuts = waits.Select(wait => wait.Point).ToArray();
                foreach (var suspension in order.PrefixAwaits(body).Select(operation => new PointKey(body.Id, operation, false))
                                                .Where(point => !cuts.Contains(point)))
                {
                    WorkCancellation.Check();
                    if (!flow.BeforeDestination(start, suspension, cuts, overlappingRuns: false))
                        return false;
                }
                normal = flow.BeforeReturn(start, cuts, exceptionalExits: false);
                faultFollows = faultCompletion && flow.BeforeReturn(start, cuts, exceptionalExits: true);
            }

            var calls = (order._steps.GetValueOrDefault(anchor.ExecutionId) ?? []).Where(step => step.Callee == body.Id).ToArray();
            var entries = calls.Where(step => TaskEntry(anchor.ExecutionId, step)).Select(step => (step.Caller, step.OperationId)).Distinct().ToArray();
            var tails = entries.Select(entry => (Entry: entry, Starts: AsyncStartsAt(anchor.ExecutionId, entry.Caller, entry.OperationId)))
                               .Where(item => item.Starts.Length != 0).ToDictionary(item => item.Entry, item => item.Starts);
            if (tails.Values.Any(starts => starts.Any(start => start.ChildId == tail)))
                return true;
            // The delegate of an admitted call runs in the call, whose own task its caller waits for, and the work is complete for the
            // caller's tail where that wait resumes; a call that returns when its runs are done has no wait to pass.
            foreach (var (callerId, operation) in entries)
            {
                WorkCancellation.Check();
                if (!normal || order._awaitedCalls.FirstOrDefault(call => call.Instance == callerId && call.Operation == operation) is not { } runner ||
                    WaitOfCall(anchor.ExecutionId, runner) is not { } outer || !outer.Done && outer.Wait is null)
                    continue;
                if (TailFollows(anchor, outer.Caller, outer.After, outer.Wait is { } wait ? [wait] : [], tail, faultFollows, [.. seen]))
                    return true;
            }
            // A body that a caller awaits at once in its prefix has its tail joined to the caller's: the caller suspends at that await,
            // so its own suspensions are reached only through it, and its tail follows what the body's does.
            foreach (var (callerId, operation) in entries.Where(entry => normal && !tails.ContainsKey(entry)))
            {
                WorkCancellation.Check();
                if (AwaitedAtOnce(anchor.ExecutionId, callerId, operation) is { } awaited &&
                    order._heap.Instances.TryGetValue(callerId, out var awaiting) &&
                    TailFollows(anchor, awaiting, new PointKey(callerId, operation, true), [awaited], tail, faultFollows, [.. seen]))
                    return true;
            }
            // A body entered whole runs inside its caller's visit before the caller goes on, so the caller reaches its own suspensions
            // only through the same wait.
            foreach (var caller in calls.Where(step => step.CalleeSegment == BodySegment.Whole && !TaskEntry(anchor.ExecutionId, step)).Select(step => step.Caller).Distinct(StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                if (order._heap.Instances.TryGetValue(caller, out var inline) && TailFollows(anchor, inline, start, waits, tail, faultCompletion, [.. seen]))
                    return true;
            }
            foreach (var ((caller, operation), starts) in tails)
            {
                WorkCancellation.Check();
                if (!normal || !order._heap.Instances.TryGetValue(caller, out var above))
                    continue;
                var aboveWaits = new List<(PointKey Point, bool CompletedFault)>();
                foreach (var child in starts)
                {
                    WorkCancellation.Check();
                    if (!Joins(child, overlappingWork: false, rejected: null, out _, out var joins, out var suspended))
                    {
                        aboveWaits.Clear();
                        break;
                    }
                    aboveWaits.AddRange(joins.Concat(suspended.Select(join => (join.Point, join.CompletedFault))).Where(join => join.Point.Instance == caller));
                }
                if (aboveWaits.Count != 0 && TailFollows(anchor, above, new PointKey(caller, operation, true), aboveWaits.Distinct().ToList(), tail, faultFollows, [.. seen]))
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
        /// start, in a parent that does not overlap itself; none given is any pass completing it. It holds only when it holds for every
        /// start of the execution, each with its own frame (ADR 0035).</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="destinations">The points, each of them before an operation.</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call (<see cref="Frame"/>).</param>
        private bool CompleteBefore(string execution, IReadOnlyList<PointKey> destinations, bool admittedPass = false)
        {
            WorkCancellation.Check();
            var starts = order.StartsOf(execution);
            foreach (var anchor in starts)
            {
                WorkCancellation.Check();
                List<OuterPass>? outers = admittedPass ? [] : null;
                // The work of the execution itself, not work below it climbed from: its own fault is all the waits read.
                if (!Frame(anchor, overlappingWork: false, out var spawn, out var joins, faultCompletion: true, admittedPass, outers))
                    return false;
                var flow = PassFlow(anchor, joins, faultCompletion: true);
                var start = StartPoint(anchor, spawn);
                var cuts = joins.Select(join => join.Point).ToArray();
                if (!destinations.All(destination => flow.BeforeDestination(start, destination, cuts, overlappingRuns: true)) ||
                    outers is not null && !OutersAllow(anchor, outers, destinations, out _, out _))
                    return false;
            }
            return starts.Count != 0;
        }

        /// <summary>Whether each pass of an execution's start completes the work before the next pass starts it again, in a parent that
        /// does not overlap itself. It holds only when it holds for every start of the execution, each with its own frame (ADR 0035).</summary>
        /// <param name="execution">The started execution.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call (<see cref="Frame"/>).</param>
        private bool CompleteBeforeNext(string execution, string? creator, bool admittedPass = false)
        {
            WorkCancellation.Check();
            var starts = order.StartsOf(execution);
            foreach (var anchor in starts)
            {
                WorkCancellation.Check();
                List<OuterPass>? outers = admittedPass ? [] : null;
                // The question's own work, against its next pass: no work below it on the way up.
                if (!Frame(anchor, overlappingWork: false, out var spawn, out var joins, faultCompletion: true, admittedPass, outers) ||
                    !CompleteBeforeNext(execution, creator, anchor, spawn, joins, outers))
                    return false;
            }
            return starts.Count != 0;
        }

        /// <summary>Whether one pass of an execution's start completes the work before the next pass — any start of the execution
        /// (<see cref="NextPasses"/>) — with the pass's joins given.</summary>
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
            // The question's own work, as its frame was made.
            var flow = PassFlow(anchor, joins, faultCompletion: true);
            var start = StartPoint(anchor, spawn);
            var cuts = joins.Select(join => join.Point).ToArray();
            if (!BeforeNextPass(flow, anchor, spawn, start, cuts, overlappingRuns: true))
            {
                Steps?.Add(PassStepEvidence.Of("complete-before-next", execution, anchor.ExecutionId, false, ("before-next", false)));
                return false;
            }
            var normal = flow.BeforeReturn(start, cuts, exceptionalExits: false);
            var all = flow.BeforeReturn(start, cuts, exceptionalExits: true);
            if (outers is not null)
            {
                // The call that runs the delegate may be passed again before its own task is waited for.
                if (!OutersAllow(anchor, outers, NextPasses(anchor, spawn), out var outerNormal, out var outerAll))
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

        /// <summary>The next passes of a pass's work: every start of its execution, before its call, since another start of the same
        /// execution starts the same work again (ADR 0035).</summary>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="spawn">How the pass starts its work, which every start of the execution shares with its site.</param>
        private PointKey[] NextPasses(SpawnAnchor anchor, PassStart spawn) =>
            order.StartsOf(anchor.ChildId).Select(next => StartPoint(next, spawn) with { AfterCall = false }).Distinct().ToArray();

        /// <summary>Whether every path from a pass's start to any next pass (<see cref="NextPasses"/>) passes one of the pass's cuts.</summary>
        /// <param name="flow">The flow of the pass's parent.</param>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="start">The pass's start in the parent's flow.</param>
        /// <param name="cuts">The points that complete the pass's work.</param>
        /// <param name="overlappingRuns">Whether the runs a call around the start may begin before this one completes count.</param>
        private bool BeforeNextPass(Flow flow, SpawnAnchor anchor, PassStart spawn, PointKey start, PointKey[] cuts, bool overlappingRuns) =>
            NextPasses(anchor, spawn).All(next => flow.BeforeDestination(start, next, cuts, overlappingRuns));

        /// <summary>Whether a join of the parent on one handle that may carry the pass's own would, were it that handle, complete the
        /// work of one execution before its next pass. Its fault follows the work's completion as a frame's join on that handle would:
        /// the form, the environment and the scheduler of the task the handle carries decide it. Every start of the execution is read
        /// with its own frame (ADR 0035): the join is added to the frame of each start whose own handle it carries, the work is ordered
        /// only when every start completes it before the next pass, and a join that carries no start's handle orders nothing. Like a
        /// question of <see cref="Ordered"/>, it reads only the waits its own climbs pass (#486).</summary>
        /// <param name="instanceId">The instance making the join.</param>
        /// <param name="operation">The join operation.</param>
        /// <param name="first">One access of the work.</param>
        /// <param name="second">The other access of the same work.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        internal bool JoinWouldOrder(string instanceId, int operation, Access first, Access second, string? creator)
        {
            _completions.Clear();
            if (first.ExecutionId != second.ExecutionId || !order._heap.Instances.TryGetValue(instanceId, out var instance) ||
                instance.Summary.Joins.FirstOrDefault(join => join.OperationId == operation) is not { HandlesKnown: true, Handles.Count: 1 } join)
                return false;
            var point = new PointKey(instanceId, operation, false);
            var starts = new List<(SpawnAnchor Anchor, PassStart Spawn, (MethodInstance Instance, SummaryValue Value)? Handle)>();
            foreach (var anchor in order.StartsOf(first.ExecutionId))
            {
                WorkCancellation.Check();
                if (!Start(anchor, overlappingWork: false, out var spawn) ||
                    !(order._visits.GetValueOrDefault(anchor.ExecutionId) ?? []).Any(visit => visit.InstanceId == instanceId &&
                        order._segments.Runs(instance.BodyId, visit.Segment, operation)))
                    return false;
                starts.Add((anchor, spawn, CarriedHandle(join.Handles[0], instance, anchor, spawn, operation, [])));
            }
            if (!starts.Any(start => start.Handle is not null))
                return false;
            foreach (var (anchor, spawn, handle) in starts)
            {
                WorkCancellation.Check();
                // The pass's own work, whose join the question is about: no work below it on the way up.
                Frame(anchor, overlappingWork: false, out _, out var joins, faultCompletion: true);
                if (handle is ({ } carrier, { } carried))
                {
                    var completedFault = order.ThrowsAfterCompletion(anchor.ExecutionId, join, carrier.Id, [carried], proven: true);
                    var suspends = join.Kind == SummaryJoinKind.Await &&
                                   (order._visits.GetValueOrDefault(anchor.ExecutionId) ?? []).Any(visit => visit.InstanceId == instanceId &&
                                       visit.Segment == BodySegment.Prefix && order._segments.Runs(instance.BodyId, visit.Segment, operation));
                    // The climb from the carried join is of the same pass's own work, with no work below it either.
                    foreach (var above in suspends ? Climb(anchor, spawn, instance, point, completedFault, faultCompletion: true, admittedPass: false)
                                                   : [(point, completedFault)])
                    {
                        WorkCancellation.Check();
                        if (!joins.Contains(above))
                            joins.Add(above);
                    }
                }
                if (!CompleteBeforeNext(first.ExecutionId, creator, anchor, spawn, joins))
                    return false;
            }
            return true;
        }

        /// <summary>The pass's own handle a value may carry, with the instance expressing it: the value itself, an argument of a call the
        /// analysis cannot see into, or a return of a callee it comes from, through the heap's value flows; null when it can carry none.</summary>
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
            var calls = new List<(MethodInstance Instance, int Operation)>();
            var origins = value.Producers with
            {
                Calls = value.SourceCalls.Concat(value.Producers.Calls).Concat(value.Values.OfType<CallResultValue>().Select(result => result.OperationId)).ToHashSet()
            };
            (_handleOrigins ??= new GapOrigins(order._heap, [])).Walk(instance, origins, (current, call) =>
            {
                calls.Add((current, call));
                return ValueOrigin.None;
            });
            foreach (var (current, call) in calls)
            {
                WorkCancellation.Check();
                if (!seen.Add((current.Id, call)))
                    continue;
                if (current.Summary.OpaqueCalls.FirstOrDefault(candidate => candidate.OperationId == call) is { IsKnown: false } opaque)
                {
                    foreach (var argument in opaque.Arguments)
                    {
                        WorkCancellation.Check();
                        if (CarriedHandle(new SummaryValue(argument.Values, argument.UnknownSources)
                            {
                                SourceCalls = argument.SourceCalls, Completions = argument.Completions,
                                CompletedCalls = argument.CompletedCalls, Producers = argument.Producers
                            }, current, anchor, spawn, operation, seen) is { } carried)
                            return carried;
                    }
                }
                foreach (var edge in order._heap.Edges.Where(edge => edge.CallerInstance == current.Id && edge.OperationId == call))
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
        /// whose units overlap within one pass — iterations of one <c>Parallel</c> call, callbacks of one timer — completes here too. It
        /// holds only when it holds for every start of the work's execution, each with its own frame, and, up the parents, for every start
        /// of each parent the climb passes (ADR 0035).</summary>
        /// <param name="source">The access in the work.</param>
        /// <param name="destination">The access the work completes before.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        private bool CompleteBeforeAccess(Access source, Access destination, string? creator)
        {
            WorkCancellation.Check();
            // The source's own work, where the question starts: no work below it yet.
            return CompleteBeforeAccess(source.ExecutionId, destination, creator, faultCompletion: true, new HashSet<string>(StringComparer.Ordinal));
        }

        /// <summary>Whether the work of one execution completes before the destination on every pass of every start of it.</summary>
        /// <param name="current">The execution whose work is completed.</param>
        /// <param name="destination">The access the work completes before.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        /// <param name="faultCompletion">Whether a fault of the work below still follows its completion.</param>
        /// <param name="seen">The executions already asked on this chain.</param>
        private bool CompleteBeforeAccess(string current, Access destination, string? creator, bool faultCompletion, HashSet<string> seen)
        {
            WorkCancellation.Check();
            var starts = order.StartsOf(current);
            if (!seen.Add(current) || starts.Count == 0)
                return false;
            foreach (var anchor in starts)
            {
                WorkCancellation.Check();
                if (!Frame(anchor, overlappingWork: true, out var spawn, out var joins, faultCompletion) ||
                    !CompleteBeforeAccess(anchor, spawn, joins, destination, creator, faultCompletion, seen))
                    return false;
            }
            return true;
        }

        /// <summary>Whether the work of one start completes before the destination on every pass of that start, with its frame given.</summary>
        /// <param name="anchor">The start.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="joins">The start's joins, with whether a faulted outcome of each still follows the work's completion.</param>
        /// <param name="destination">The access the work completes before.</param>
        /// <param name="creator">The creator whose run the query reads, or null.</param>
        /// <param name="faultCompletion">Whether a fault of the work below still follows its completion.</param>
        /// <param name="seen">The executions already asked on this chain.</param>
        private bool CompleteBeforeAccess(SpawnAnchor anchor, PassStart spawn, List<(PointKey Point, bool CompletedFault)> joins, Access destination,
                                          string? creator, bool faultCompletion, HashSet<string> seen)
        {
            WorkCancellation.Check();
            var current = anchor.ChildId;
            if (!faultCompletion)
                joins = joins.Select(join => (join.Point, false)).ToList();
            var start = StartPoint(anchor, spawn);
            var flow = PassFlow(anchor, joins, faultCompletion);
            var cuts = joins.Select(join => join.Point).ToArray();
            if (!BeforeNextPass(flow, anchor, spawn, start, cuts, overlappingRuns: false))
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
            // The caller may resume in a separate async tail of the same execution. The waits passed on the climb prove which
            // suspensions of its body follow this work; a fault that can leave the work running proves no such completion.
            if (TailCompletes(anchor, start, destination.ExecutionId, faultCompletion) &&
                ParentAllows(anchor.ExecutionId, flow.BeforeReturn(start, cuts, exceptionalExits: false),
                             flow.BeforeReturn(start, cuts, exceptionalExits: true), creator, []))
                return true;
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
            return CompleteBeforeAccess(anchor.ExecutionId, destination, creator, faultCompletion, [.. seen]);
        }

        /// <summary>Whether a tail resumes only after a completion of this pass's work. Where a fault of the work below does not follow
        /// its completion, the waits the climb passed stop no path on their exceptional exits, so a tail entered after such a fault —
        /// a suspension in a <c>catch</c>, or after one — does not follow the work (#498).</summary>
        /// <param name="anchor">The pass's start.</param>
        /// <param name="start">Its point in the parent's flow.</param>
        /// <param name="tail">The resumed execution or work it starts.</param>
        /// <param name="faultCompletion">Whether a fault of the work below still follows its completion (<see cref="PassFlow"/>).</param>
        private bool TailCompletes(SpawnAnchor anchor, PointKey start, string tail, bool faultCompletion)
        {
            var tails = new HashSet<string>(StringComparer.Ordinal);
            for (string? current = tail; current is not null && current != anchor.ExecutionId && tails.Add(current);
                 current = order._executions.GetValueOrDefault(current)?.ParentId)
            {
                WorkCancellation.Check();
            }
            foreach (var group in (_completions.GetValueOrDefault(anchor) ?? []).Where(wait => wait.Value).GroupBy(wait => wait.Key.Instance))
            {
                WorkCancellation.Check();
                var waits = group.Select(wait => (Point: wait.Key, CompletedFault: wait.Value)).ToList();
                if (order._heap.Instances.TryGetValue(group.Key, out var body) &&
                    PassFlow(anchor, waits, faultCompletion).BeforeReturn(start, waits.Select(wait => wait.Point).ToArray(), exceptionalExits: true) &&
                    tails.Any(candidate => TailFollows(anchor, body, start, waits, candidate, faultCompletion, [])))
                    return true;
            }
            return false;
        }

        /// <summary>Whether the parent of a pass does not overlap itself, so its passes run one after another: it runs at most once and
        /// is serialized, runs once per run of the creator, or is serialized and waited for the work on every exit; otherwise it is work
        /// of a pass in turn, which waited for the work on every normal return and whose own pass the same holds for — for every start of
        /// the parent, each with its own frame, its next pass any start of it (ADR 0035).</summary>
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
            var starts = order.StartsOf(parent);
            if (!normalCompletion || starts.Count == 0)
            {
                Steps?.Add(PassStepEvidence.Of("parent-allows", parent, null, false, ("runs-once", false), ("serialized-after-every-exit", false),
                    ("normal-return", normalCompletion)));
                return false;
            }
            foreach (var anchor in starts)
            {
                WorkCancellation.Check();
                // The parent's waits for the work complete it on a fault only where every exit of the parent waited for it.
                if (!Frame(anchor, overlappingWork: false, out var spawn, out var joins, allCompletion))
                {
                    Steps?.Add(PassStepEvidence.Of("parent-allows", parent, null, false, ("runs-once", false), ("serialized-after-every-exit", false),
                        ("normal-return", normalCompletion)));
                    return false;
                }
                if (!allCompletion)
                    joins = joins.Select(join => (join.Point, false)).ToList();
                var flow = PassFlow(anchor, joins, allCompletion);
                var start = StartPoint(anchor, spawn);
                var cuts = joins.Select(join => join.Point).ToArray();
                if (!BeforeNextPass(flow, anchor, spawn, start, cuts, overlappingRuns: true))
                {
                    Steps?.Add(PassStepEvidence.Of("parent-allows", parent, anchor.ExecutionId, false, ("runs-once", false),
                        ("serialized-after-every-exit", false), ("normal-return", true), ("own-pass-before-next", false)));
                    return false;
                }
                var normal = flow.BeforeReturn(start, cuts, exceptionalExits: false);
                // A join whose fault does not follow the work's completion leaves it running on that exit, however the joins sit.
                var all = allCompletion && flow.BeforeReturn(start, cuts, exceptionalExits: true);
                // Each start climbs its own chain of parents.
                var allowed = ParentAllows(anchor.ExecutionId, normal, all, creator, starts.Count == 1 ? seen : [.. seen]);
                Steps?.Add(PassStepEvidence.Of("parent-allows", parent, anchor.ExecutionId, allowed, ("runs-once", false),
                    ("serialized-after-every-exit", false), ("normal-return", true), ("own-pass-before-next", true), ("parent-allows", allowed)));
                if (!allowed)
                    return false;
            }
            return true;
        }

        /// <summary>The flow of a pass's parent, in which the exception of a join of the pass that throws only after the work completed
        /// comes after that join, as <see cref="FlowOf"/> reads proven joins, an <c>await</c> among them — and so does that of a site
        /// calling such a join where the hoisting of <see cref="AtCallSites"/> says the call throws only after it: a path asking about
        /// the pass's work stops there. The intermediate waits the climb passed keep that reading of their exceptions without cutting
        /// the caller's paths. The exception of an <c>await</c> that is no join of the pass is a step of the exits as it is. Only the waits
        /// recorded for this start are read: another start's waits classify none of its edges (ADR 0035). Where a fault of the work below
        /// does not follow its completion, no wait's exception does, so none of the waits — those given and those recorded alike — stops a
        /// path asking about the work on its exceptional exit (#498).</summary>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="joins">The pass's joins, with whether a faulted outcome of each still follows the work's completion.</param>
        /// <param name="faultCompletion">Whether a fault of the work below still follows its completion; false reads every wait, given or
        /// recorded for the start by the climb, as one whose fault does not. Every call says which: true where the proof starts from its
        /// question's own work, the inherited value where it climbs from work below.</param>
        private Flow PassFlow(SpawnAnchor anchor, List<(PointKey Point, bool CompletedFault)> joins, bool faultCompletion)
        {
            var local = joins.Concat((_completions.GetValueOrDefault(anchor) ?? []).Select(wait => (Point: wait.Key, CompletedFault: wait.Value)))
                             .Select(wait => (wait.Point, CompletedFault: faultCompletion && wait.CompletedFault)).ToArray();
            var key = anchor.ChildId + "|" + string.Join(";", local.Select(join => $"{join.Point}:{join.CompletedFault}").Order(StringComparer.Ordinal));
            if (_flows.TryGetValue(key, out var flow))
                return flow;
            var waits = local.GroupBy(join => join.Point)
                             .Select(group => new Candidate(anchor.ExecutionId, group.Key, [anchor.ChildId], group.All(join => join.CompletedFault))).ToArray();
            var throwsAfter = order.ThrowingAfter(waits.Concat(order.Hoisted(waits, [anchor.ExecutionId])).Where(wait => wait.ThrowsAfter)
                                                       .Select(wait => (wait.Point, wait.Targets)).ToArray());
            _flows.Add(key, flow = order.NewFlow(anchor.ExecutionId, throwsAfter));
            return flow;
        }

        /// <summary>The joins of a start's parent that wait for this pass of that start: a join on one handle, proven to name the
        /// execution and to come from the pass's own start; for a timer, an awaited <c>DisposeAsync()</c> or a <c>WaitOne()</c> after
        /// <c>Dispose(handle)</c> of such a timer; for a <c>Parallel</c> call, the call's own return. The frame is the selected start's
        /// own: its joins, its climb and the completions it records are never another start's (ADR 0035).</summary>
        /// <param name="anchor">The start selected, one of the execution's (<see cref="HappensBefore.StartsOf"/>).</param>
        /// <param name="overlappingWork">Whether work whose units overlap within one pass — a <c>Parallel</c> call's iterations, a
        /// timer's callbacks — has a frame.</param>
        /// <param name="spawn">Receives how the pass starts its work.</param>
        /// <param name="joins">Receives the pass's joins, with whether a faulted outcome of each still follows the work's completion.</param>
        /// <param name="faultCompletion">Whether a fault of the work below still follows its completion: where it does not, the climb lifts
        /// no wait of a caller above a body whose normal return a fault of the pass can reach (<see cref="Climb"/>, #498).</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call. Where its work is inside a delegate another admitted
        /// call runs, the delegate's <c>await</c> ends it for that call's next run, but for what follows the call's return only where the
        /// call's own task is waited at once (<see cref="ClimbRuns"/>); other work keeps the <c>await</c> as its join.</param>
        /// <param name="outers">Receives, for an admitted pass, the passes of the admitted calls whose delegates the work is in, each
        /// completing the work for what follows its call (<see cref="OutersAllow"/>); null for other work.</param>
        private bool Frame(SpawnAnchor anchor, bool overlappingWork, out PassStart spawn, out List<(PointKey Point, bool CompletedFault)> joins,
                           bool faultCompletion, bool admittedPass = false, List<OuterPass>? outers = null)
        {
            WorkCancellation.Check();
            var execution = anchor.ChildId;
            var rejected = Trace is null ? null : new List<PassJoinEvidence>();
            if (!Joins(anchor, overlappingWork, rejected, out spawn, out joins, out var suspended))
            {
                Trace?.Add(new PassFrameEvidence(execution, null, null, null, null, -1, [], []));
                return false;
            }
            foreach (var wait in joins.Concat(suspended.Select(wait => (wait.Point, wait.CompletedFault))))
            {
                WorkCancellation.Check();
                Completion(anchor, wait.Point, wait.CompletedFault);
            }
            foreach (var (point, completedFault, instance) in suspended)
            {
                WorkCancellation.Check();
                foreach (var above in Climb(anchor, spawn, instance, point, completedFault, faultCompletion, admittedPass, outers))
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

        /// <summary>The joins of the parent that wait for a pass of one start, as the parent's visits hold them: those that stop the paths
        /// of the parent, and, apart, the <c>await</c>s that suspend the body of a prefix visit, which stop only the paths that stay in that
        /// body (<see cref="Climb"/>).</summary>
        /// <param name="anchor">The start selected.</param>
        /// <param name="overlappingWork">Whether work whose units overlap within one pass has a frame.</param>
        /// <param name="rejected">Receives the joins of the parent the start cannot be proven to be the handle of, or null.</param>
        /// <param name="spawn">Receives how the pass starts its work.</param>
        /// <param name="joins">Receives the joins that stop the parent's paths, with whether a faulted outcome of each still follows the
        /// work's completion.</param>
        /// <param name="suspended">Receives the awaits that suspend the body of a prefix visit.</param>
        private bool Joins(SpawnAnchor anchor, bool overlappingWork, List<PassJoinEvidence>? rejected, out PassStart spawn,
                           out List<(PointKey Point, bool CompletedFault)> joins,
                           out List<(PointKey Point, bool CompletedFault, MethodInstance Instance)> suspended)
        {
            joins = [];
            suspended = [];
            WorkCancellation.Check();
            order.Counters.PassJoins++;
            var execution = anchor.ChildId;
            if (!Start(anchor, overlappingWork, out spawn))
                return false;
            var parent = order._executions[anchor.ExecutionId];
            // A fault out of the call follows the iterations' completion, as the happens-before graph reads the call's own join.
            if (spawn.ReturnCompletes)
            {
                joins.Add((StartPoint(anchor, spawn), true));
                return true;
            }
            if (anchor.Kind == SpawnAnchorKind.Timer)
            {
                foreach (var visit in order._visits.GetValueOrDefault(parent.Id) ?? [])
                {
                    WorkCancellation.Check();
                    if (order._heap.Instances.TryGetValue(visit.InstanceId, out var instance))
                        TimerJoins(execution, parent.Id, visit, instance, anchor, spawn, joins, suspended, rejected);
                }
                return true;
            }
            // A diagnostic request reads every join of the parent, the rejected ones too; the analysis reads the record of the joins whose
            // handle names this start's execution once a start of the parent made it.
            var candidates = rejected is null && _parentJoins.TryGetValue(parent.Id, out var recorded)
                ? recorded.GetValueOrDefault(execution) ?? []
                : ParentJoins(parent.Id);
            foreach (var candidate in candidates)
            {
                WorkCancellation.Check();
                var (instance, join, completedFault) = (candidate.Instance, candidate.Join, candidate.CompletedFault);
                if (!candidate.OneHandle)
                {
                    rejected?.Add(new PassJoinEvidence(instance.Id, join.OperationId, completedFault, "handles"));
                    continue;
                }
                if (candidate.Source != execution)
                {
                    rejected?.Add(new PassJoinEvidence(instance.Id, join.OperationId, completedFault, "source"));
                    continue;
                }
                if (!Fresh(join.Handles[0], instance, anchor, spawn))
                {
                    rejected?.Add(new PassJoinEvidence(instance.Id, join.OperationId, completedFault, "not-this-pass"));
                    continue;
                }
                var point = new PointKey(instance.Id, join.OperationId, false);
                if (candidate.Suspends)
                    suspended.Add((point, completedFault, instance));
                else if (!joins.Contains((point, completedFault)))
                    joins.Add((point, completedFault));
            }
            return true;
        }

        /// <summary>A join of a visit of a parent, as every start of that parent reads it: nothing of it depends on the start.</summary>
        /// <param name="Instance">The instance making the join.</param>
        /// <param name="Join">The join.</param>
        /// <param name="Suspends">Whether it is an <c>await</c> in the prefix of its body, which suspends the body (<see cref="Climb"/>).</param>
        /// <param name="CompletedFault">Whether a faulted outcome of it still follows the completion of the work its one handle names; only
        /// the form and the environment decide it, since that handle is proven.</param>
        /// <param name="OneHandle">Whether its handles are known and are one.</param>
        /// <param name="Source">The execution its one handle names with proven identity (<see cref="HappensBefore.ProveHandleSource"/>), or
        /// null.</param>
        private sealed record ParentJoin(MethodInstance Instance, SummaryJoin Join, bool Suspends, bool CompletedFault, bool OneHandle, string? Source);

        /// <summary>The joins of each parent's visits whose one handle names a start's execution, by parent and by that execution, in the
        /// order of <see cref="ParentJoins"/>, recorded when a start of the parent first read them all. What a start reads of a join
        /// beyond them depends on the start alone: whether the handle is that start's own (<see cref="Fresh"/>).</summary>
        private readonly Dictionary<string, Dictionary<string, ParentJoin[]>> _parentJoins = new(StringComparer.Ordinal);

        /// <summary>The joins of a parent's visits as they stand — the visits in order, the joins of each in its summary's order, a join
        /// once for each visit whose segment runs it — with the fault reading and the proof of the one handle's source of each. It yields
        /// each as it is proven, so a start reading it does what it does with one join before the next is proven, and the proofs and
        /// what a start does with them come in the order they come in when every start proves every join itself. Read whole, it records
        /// the proven ones (<see cref="_parentJoins"/>). Neither the fault reading nor the proof depends on the start, its pair or the
        /// creator whose run the query reads; asked again on the graph, with nothing of a handle proof in progress, as a pass's proof always
        /// asks, each answers what it answered, and it reaches no cache the first answer left empty.</summary>
        /// <param name="parent">The parent execution.</param>
        private IEnumerable<ParentJoin> ParentJoins(string parent)
        {
            var proven = new Dictionary<string, List<ParentJoin>>(StringComparer.Ordinal);
            foreach (var visit in order._visits.GetValueOrDefault(parent) ?? [])
            {
                WorkCancellation.Check();
                if (!order._heap.Instances.TryGetValue(visit.InstanceId, out var instance))
                    continue;
                foreach (var join in instance.Summary.Joins)
                {
                    WorkCancellation.Check();
                    if (!order._segments.Runs(instance.BodyId, visit.Segment, join.OperationId))
                        continue;
                    // The one handle of a frame's join is proven below, so only the form and the environment decide its fault.
                    var completedFault = order.ThrowsAfterCompletion(parent, instance.Id, join, proven: true);
                    var oneHandle = join.HandlesKnown && join.Handles.Count == 1;
                    var source = oneHandle ? order.ProveHandleSource(parent, instance, join.Handles[0], join.OperationId) : null;
                    var candidate = new ParentJoin(instance, join, join.Kind == SummaryJoinKind.Await && visit.Segment == BodySegment.Prefix,
                                                   completedFault, oneHandle, source);
                    if (source is not null)
                        Get(proven, source).Add(candidate);
                    yield return candidate;
                }
            }
            if (_parentJoins.TryAdd(parent, proven.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal)))
            {
                order.Counters.ParentJoins++;
                order.Counters.ParentJoinEntries += proven.Values.Sum(list => list.Count);
            }
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
        /// may leave around it. An entry's async call whose execution has several starts is waited only when every start whose call
        /// reaches the suspending instance (<see cref="CallReaches"/>) is, each with its own frame (ADR 0035). Where a fault of the work
        /// below does not follow its completion, a path through the <c>await</c>'s exceptional exit has not waited for that work, so a body
        /// that returns normally on such a path — catching the fault — does not leave its prefix only through the <c>await</c>, and
        /// nothing above it stands for the work (#498). The frame of the body's tail reads the same guarantee for the body's task: where
        /// the body may leave by exception around the <c>await</c>, a caller that catches the task's fault and returns normally has not
        /// waited for the work either.</summary>
        /// <param name="anchor">The start of the pass.</param>
        /// <param name="spawn">How the pass starts its work.</param>
        /// <param name="instance">The instance whose body holds the <c>await</c>.</param>
        /// <param name="point">The <c>await</c>.</param>
        /// <param name="completedFault">Whether a faulted outcome of the <c>await</c> still follows the work's completion.</param>
        /// <param name="faultCompletion">Whether a fault of the work below the pass still follows its completion (<see cref="PassFlow"/>).</param>
        /// <param name="admittedPass">Whether the pass is that of an admitted call (<see cref="Frame"/>).</param>
        /// <param name="outers">Receives, for an admitted pass, the passes of the admitted calls the climb meets (<see cref="ClimbRuns"/>);
        /// null for other work.</param>
        private IReadOnlyList<(PointKey Point, bool CompletedFault)> Climb(SpawnAnchor anchor, PassStart spawn, MethodInstance instance,
                                                                           PointKey point, bool completedFault, bool faultCompletion,
                                                                           bool admittedPass, List<OuterPass>? outers = null)
        {
            WorkCancellation.Check();
            Completion(anchor, point, completedFault);
            var entries = (order._steps.GetValueOrDefault(anchor.ExecutionId) ?? [])
                          .Where(step => step.Callee == instance.Id && TaskEntry(anchor.ExecutionId, step))
                          .Select(step => (step.Caller, step.OperationId)).Distinct().ToArray();
            if (entries.Length == 0)
                return [(point, completedFault)];
            var runners = entries.Select(entry => order._awaitedCalls.FirstOrDefault(call => call.Instance == entry.Caller && call.Operation == entry.OperationId))
                                 .ToArray();
            if (runners.Any(runner => runner is not null))
                return !admittedPass ? [(point, completedFault)] : runners.All(runner => runner is not null) ? ClimbRuns(anchor, spawn, point, completedFault, runners!, outers) : [];

            var tails = new List<SpawnAnchor>();
            var awaiting = new List<(MethodInstance Caller, PointKey Point, bool CompletedFault)>();
            foreach (var (caller, operation) in entries)
            {
                WorkCancellation.Check();
                var started = AsyncStartsAt(anchor.ExecutionId, caller, operation);
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

            var flow = PassFlow(anchor, [(point, completedFault)], faultCompletion);
            var start = StartPoint(anchor, spawn);
            var bodyWaits = flow.BeforeReturn(start, [point], exceptionalExits: false);
            if (!bodyWaits)
            {
                Steps?.Add(PassStepEvidence.Of("climb", anchor.ChildId, anchor.ExecutionId, false, ("entry-is-async-call", true), ("body-waits", false)));
                return [];
            }
            var faultFollows = completedFault && flow.BeforeReturn(start, [point], exceptionalExits: true);
            var above = new List<(PointKey Point, bool CompletedFault)>();
            foreach (var tail in tails.Select(start => start.ChildId).Distinct(StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                // Every start of the tail whose call reaches the suspending instance hands that instance's task on, each a pass of its own.
                var through = order.StartsOf(tail).Where(candidate => CallReaches(candidate, instance.Id))
                                   .Concat(tails.Where(candidate => candidate.ChildId == tail)).Distinct().ToArray();
                var frames = new List<(SpawnAnchor Start, List<(PointKey Point, bool CompletedFault)> Waits)>();
                var waited = false;
                if (_climbing.Add(tail))
                {
                    try
                    {
                        waited = true;
                        foreach (var tailStart in through)
                        {
                            WorkCancellation.Check();
                            // The tail's task is the work below for the frame, and its fault follows the work's completion only where the
                            // body's does: a body that may throw around the wait hands its caller a task whose fault the work has not
                            // completed before.
                            if (!Frame(tailStart, overlappingWork: false, out _, out var waits, faultCompletion && faultFollows, admittedPass, outers))
                            {
                                waited = false;
                                break;
                            }
                            frames.Add((tailStart, waits));
                        }
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
                foreach (var (tailStart, waits) in frames)
                {
                    WorkCancellation.Check();
                    foreach (var completion in (_completions.GetValueOrDefault(tailStart) ?? []).ToArray())
                    {
                        WorkCancellation.Check();
                        Completion(anchor, completion.Key, completion.Value && faultFollows);
                    }
                    above.AddRange(waits.Select(wait => (wait.Point, wait.CompletedFault && faultFollows)));
                }
            }

            foreach (var (calling, awaitPoint, awaitFault) in awaiting)
            {
                WorkCancellation.Check();
                IReadOnlyList<(PointKey Point, bool CompletedFault)> climbed = [];
                if (_climbing.Add(UP + calling.Id))
                {
                    try
                    {
                        climbed = Climb(anchor, spawn, calling, awaitPoint, awaitFault && faultFollows, faultCompletion, admittedPass, outers);
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
                // The admitted call's own task: no work below it on the way up.
                : outer.Suspends ? Climb(anchor, spawn, outer.Caller, wait.Point, wait.CompletedFault, faultCompletion: true, admittedPass: true, outers) : [wait];
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
            var wait = order.PlainFlow(execution).WaitAtOnce(body, call,
                runner.Task is not null || order.Forwarders.Call(execution, caller.Id, call.Id) is not null);
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
                // The admitted call's pass for what follows the call: the question's own work, nothing below it.
                var flow = PassFlow(anchor, [.. outer.Joins], faultCompletion: true);
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
            AsyncStartsAt(execution, caller, operation).Select(start => start.ChildId).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>The starts an async call at a place makes, in the execution making the call: the anchors of its own return, or, for a
        /// call of a member that forwards the task of an async call, that call's anchor.</summary>
        /// <param name="execution">The execution making the call.</param>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="operation">The call operation.</param>
        private SpawnAnchor[] AsyncStartsAt(string execution, string caller, int operation)
        {
            var direct = (order._anchors.GetValueOrDefault(execution) ?? [])
                .Where(candidate => candidate.Kind == SpawnAnchorKind.AsyncReturn && candidate.CallerInstance == caller && candidate.OperationId == operation)
                .Distinct().ToArray();
            return direct.Length != 0 ? direct : order.Forwarders.Call(execution, caller, operation) is { } forwarded ? [forwarded] : [];
        }

        /// <summary>Whether the call a start makes reaches an instance: its heap edges lead to the instance, or to instances whose calls in
        /// the start's parent lead to it.</summary>
        /// <param name="start">The start.</param>
        /// <param name="instance">The instance.</param>
        private bool CallReaches(SpawnAnchor start, string instance)
        {
            WorkCancellation.Check();
            return Reachable(start.ExecutionId, order.Calls.Callees(start.CallerInstance, start.OperationId), _ => true).Contains(instance);
        }

        /// <summary>Whether a step enters a body whose work is complete where its caller waits for its task.</summary>
        /// <param name="execution">The execution running the step.</param>
        /// <param name="step">The entry step.</param>
        private bool TaskEntry(string execution, ExecutionStep step) =>
            step.CalleeSegment == BodySegment.Prefix || order.Forwarders.Returned(execution, step.Callee) is not null;

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
        /// call, a synchronous entry — is a place of its own. Each place keeps the start of the admitted call whose body it enters.</summary>
        /// <param name="execution">The execution making the calls.</param>
        /// <param name="entries">The calls of the body, each by its caller instance, its operation, the instance it enters and the start of
        /// the admitted call made in that body.</param>
        private List<(string Caller, int Site, string Body, SpawnAnchor Start)> EntryPoints(string execution,
                                                                                            List<(string Caller, int Site, string Body, SpawnAnchor Start)> entries)
        {
            var places = new List<(string Caller, int Site, string Body, SpawnAnchor Start)>();
            var seen = new HashSet<(string Caller, int Site, string Body, SpawnAnchor Start)>();
            var pending = new Queue<(string Caller, int Site, string Body, SpawnAnchor Start)>(entries);
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
                    pending.Enqueue((edge.CallerInstance, edge.OperationId, entry.Caller, entry.Start));
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

        /// <summary>Whether a start the caller selected is one the proof reads passes of: a start of its execution
        /// (<see cref="HappensBefore.StartsOf"/>) that is a <c>Task.Run</c>, <c>StartNew</c> or <c>Thread.Start</c> spawn, or an async
        /// call's return, and with overlapping work a <c>Parallel.For</c> or <c>Parallel.ForEach</c> spawn or a
        /// <c>System.Threading.Timer</c>'s creation, of an execution that is neither recursive nor a composite task. Only the selected start
        /// is read: a caller asks for each start of an execution with several, never for one in place of the others (ADR 0035).</summary>
        /// <param name="anchor">The start selected.</param>
        /// <param name="overlappingWork">Whether work whose units overlap within one pass is read.</param>
        /// <param name="spawn">Receives the kind of the start and the call making it.</param>
        private bool Start(SpawnAnchor anchor, bool overlappingWork, out PassStart spawn)
        {
            spawn = null!;
            var execution = anchor.ChildId;
            if (!order.StartsOf(execution).Contains(anchor) || order._executions[execution].IsRecursive || order.IsComposite(execution))
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
                if (order.Forwarders.Call(anchor.ExecutionId, caller.Id, operation) is { } forwarded && forwarded == anchor)
                    return true;
                if (order._heap.UnresolvedCallTargets.Contains((caller.Id, operation)) || !seen.Add((caller.Id, operation)))
                    return false;
                try
                {
                    var targets = order.Calls.Callees(caller.Id, operation).Select(callee => order._heap.Instances.GetValueOrDefault(callee)).ToArray();
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
                    var bindings = order.Calls.Into(callee.Id);
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
