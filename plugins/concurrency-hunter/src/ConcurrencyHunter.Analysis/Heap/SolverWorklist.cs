using System.Diagnostics;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Heap;

internal sealed record SolverWorklistOptions(bool Audit = false, bool VerifyConvergence = false);

public static partial class WholeProgram
{
    private sealed partial class Solver
    {
        private readonly SolverWorklistOptions _worklistOptions;
        private readonly Dictionary<StateKey, long> _writes = [];
        private readonly Dictionary<string, ProcessingHistory> _history = new(StringComparer.Ordinal);
        private HashSet<StateKey>? _reads;
        private long _writeClock;
        private int _suspendReads;
        private string? _auditing;
        private RebuiltState _rebuilding = new();
        private static readonly StateKey REACH_KEY = new StateKey("_reachIndex").All;
        // What the solve spent its passes on, for HeapSolveProfile: none of it is a counter of the solve's metrics.
        private long _skips;
        private long _dirtyCheckTicks;
        private long _processTicks;
        private long _callTicks;
        private long _passRestTicks;
        private long _calls;
        private long _changingCalls;
        private long _skippedCalls;
        // The last processing of each call of each instance, by the call's place in its summary: the worklist one level finer.
        private readonly Dictionary<string, CallHistory?[]> _callHistories = new(StringComparer.Ordinal);
        // Whether Process may skip a clean call: only in a processing the worklist decided, never in an audit or a full pass.
        private bool _skippingCalls;

        private sealed record ProcessingHistory(long Started, HashSet<StateKey> Reads, RebuiltState Contribution,
                                                Dictionary<RegionRef, List<RegionRef>>? ReachSnapshot);

        /// <summary>The last processing of one call of an instance: what it read and added, which a processing of the instance that
        /// finds the call clean takes again instead of processing it.</summary>
        /// <param name="Started">The write clock when the call's processing started; a read key written after it makes the call dirty.</param>
        /// <param name="Reads">The state keys the call read.</param>
        /// <param name="ReadsReach">Whether <paramref name="Reads"/> holds the reach index's key.</param>
        /// <param name="Contribution">What the call added to the rebuilt outputs; null for nothing.</param>
        /// <param name="ReferenceLookups">The reference lookups the call counted, which a skip counts again.</param>
        /// <param name="ReachSnapshot">The reach index the call read, if it read one.</param>
        private sealed record CallHistory(long Started, StateKey[] Reads, bool ReadsReach, RebuiltState? Contribution, int ReferenceLookups,
                                          Dictionary<RegionRef, List<RegionRef>>? ReachSnapshot);

        private void ReadState(StateKey key)
        {
            if (_suspendReads != 0)
                return;
            _reads?.Add(key);
        }

        private void WroteState(StateKey key)
        {
            if (_auditing is not null)
                throw new InvalidOperationException($"Worklist audit: {_auditing} wrote {key} during a clean processing.");
            if (Querying)
                throw new InvalidOperationException($"A query of the solved heap wrote {key}: the heap is frozen after its solve.");
            var version = ++_writeClock;
            _writes[key] = version;
            // bounded: walks the parents of one state key, as many as the key's nesting depth; cleanup after a cancellation writes through here.
            for (var parent = key; parent is not null; parent = parent.Parent)
                _writes[parent.All] = version;
        }

        private bool Dirty(ProcessingHistory history) =>
            _writes.GetValueOrDefault(StateKey.Wildcard) > history.Started ||
            history.Reads.Any(key => _writes.GetValueOrDefault(key) > history.Started);

        private void ProcessOrSkip(InstanceState instance)
        {
            var checking = Stopwatch.GetTimestamp();
            var clean = _history.TryGetValue(instance.Id, out var previous) && !Dirty(previous);
            if (clean && previous!.Reads.Contains(REACH_KEY))
            {
                // A clean first reader still builds this pass's snapshot at its original turn.
                BuildReachIndex();
                clean = SameReach(previous.ReachSnapshot, _reachIndex);
            }
            _dirtyCheckTicks += Stopwatch.GetTimestamp() - checking;
            if (clean)
            {
                _skips++;
                if (_worklistOptions.Audit)
                    AuditSkip(instance, previous!);
                return;
            }

            var processing = Stopwatch.GetTimestamp();
            var started = _writeClock;
            var contribution = new RebuiltState();
            _rebuilding = contribution;
            _reads = [];
            var before = _changes;
            _skippingCalls = true;
            try
            {
                Process(instance);
                _history[instance.Id] = new(started, _reads, contribution,
                                            _reads.Contains(REACH_KEY) ? _reachIndex : null);
            }
            finally
            {
                _reads = null;
                _skippingCalls = false;
                _processTicks += Stopwatch.GetTimestamp() - processing;
            }
            if (_changes != before)
                CountRound(instance);
        }

        /// <summary>What the solve spent its passes on, up to now: the instances and calls it skipped and the time its worklist took.</summary>
        /// <param name="started">The timestamp the solve started at.</param>
        private HeapSolveProfile Profile(long started) =>
            new(_skips, _calls, _changingCalls, _skippedCalls, Stopwatch.GetElapsedTime(0, _dirtyCheckTicks),
                Stopwatch.GetElapsedTime(0, _processTicks), Stopwatch.GetElapsedTime(0, _callTicks), Stopwatch.GetElapsedTime(0, _passRestTicks),
                Stopwatch.GetElapsedTime(started));

        /// <summary>Processes an instance's calls in their order. In a processing the worklist decided, a call none of whose reads was
        /// written since its last processing started is skipped: it would write nothing, so its reads, its contribution and its
        /// reference lookups are taken again at its turn, which leaves the instance's reads, the order of every rebuilt output and the
        /// counters as processing it would. Any other call is processed and its history kept.</summary>
        /// <param name="instance">The instance being processed.</param>
        private void ProcessCalls(InstanceState instance)
        {
            var calls = instance.Summary.Calls;
            CallHistory?[]? histories = null;
            if (_skippingCalls && !_callHistories.TryGetValue(instance.Id, out histories))
                _callHistories.Add(instance.Id, histories = new CallHistory?[calls.Count]);
            for (var index = 0; index < calls.Count; index++)
            {
                CheckCancellation();
                if (histories is null)
                    CountedCall(instance, calls[index]);
                else if (histories[index] is { } previous && CleanCall(previous))
                {
                    SkipCall(previous);
                    if (_worklistOptions.Audit)
                        AuditCallSkip(instance, calls[index], previous);
                }
                else
                    histories[index] = RecordedCall(instance, calls[index]);
            }
        }

        /// <summary>Processes one call, counting it for the solve's profile and whether it added a solver fact.</summary>
        /// <param name="instance">The calling instance.</param>
        /// <param name="call">The call.</param>
        private void CountedCall(InstanceState instance, CallTransfer call)
        {
            var changes = _changes;
            Call(instance, call);
            _calls++;
            if (_changes != changes)
                _changingCalls++;
        }

        /// <summary>Processes one call with reads and a contribution of its own, adds both to the instance's, and gives its history.</summary>
        /// <param name="instance">The calling instance.</param>
        /// <param name="call">The call.</param>
        private CallHistory RecordedCall(InstanceState instance, CallTransfer call)
        {
            var reads = _reads!;
            var rebuilding = _rebuilding;
            var started = _writeClock;
            var lookups = _counters[HeapCounters.REFERENCE_LOOKUPS];
            var callReads = new HashSet<StateKey>();
            var contribution = new RebuiltState();
            _reads = callReads;
            _rebuilding = contribution;
            try
            {
                CountedCall(instance, call);
            }
            finally
            {
                _reads = reads;
                _rebuilding = rebuilding;
            }
            reads.UnionWith(callReads);
            rebuilding.Union(contribution);
            var readsReach = callReads.Contains(REACH_KEY);
            return new(started, [.. callReads], readsReach, contribution.IsEmpty ? null : contribution,
                       _counters[HeapCounters.REFERENCE_LOOKUPS] - lookups, readsReach ? _reachIndex : null);
        }

        /// <summary>Whether a call is clean: no key it read was written since its last processing started, and a reach index it read is
        /// the one this pass builds at its turn.</summary>
        /// <param name="previous">The call's last processing.</param>
        private bool CleanCall(CallHistory previous)
        {
            if (_writes.GetValueOrDefault(StateKey.Wildcard) > previous.Started ||
                previous.Reads.Any(key => _writes.GetValueOrDefault(key) > previous.Started))
                return false;
            if (!previous.ReadsReach)
                return true;
            // A clean first reader still builds this pass's snapshot at its original turn.
            BuildReachIndex();
            return SameReach(previous.ReachSnapshot, _reachIndex);
        }

        /// <summary>Takes a clean call's last processing again in place of processing it.</summary>
        /// <param name="previous">The call's last processing.</param>
        private void SkipCall(CallHistory previous)
        {
            _reads!.UnionWith(previous.Reads);
            if (previous.Contribution is { } contribution)
                _rebuilding.Union(contribution);
            _counters[HeapCounters.REFERENCE_LOOKUPS] += previous.ReferenceLookups;
            _skippedCalls++;
        }

        /// <summary>Processes a skipped call anyway and checks that it reads nothing its history lacks, adds no solver fact, adds what
        /// its history holds in the same order and counts as many reference lookups; then puts back what the check changed.</summary>
        /// <param name="instance">The calling instance.</param>
        /// <param name="call">The skipped call.</param>
        /// <param name="previous">The call's last processing.</param>
        private void AuditCallSkip(InstanceState instance, CallTransfer call, CallHistory previous)
        {
            var reads = _reads;
            var rebuilding = _rebuilding;
            var callReads = new HashSet<StateKey>();
            var contribution = new RebuiltState();
            _reads = callReads;
            _rebuilding = contribution;
            _auditing = $"{instance.Id} call {call.OperationId}";
            var before = _changes;
            var counters = new Dictionary<string, int>(_counters, StringComparer.Ordinal);
            var lookups = _counters[HeapCounters.REFERENCE_LOOKUPS];
            try
            {
                Call(instance, call);
                var recorded = previous.Reads.ToHashSet();
                var missed = callReads.FirstOrDefault(key => !recorded.Contains(key));
                if (missed is not null)
                    throw new InvalidOperationException($"Worklist audit: {_auditing} read unrecorded key {missed}.");
                if (_changes != before)
                    throw new InvalidOperationException($"Worklist audit: {_auditing} changed _changes during a clean processing.");
                if (!(previous.Contribution ?? new RebuiltState()).Same(contribution))
                    throw new InvalidOperationException($"Worklist audit: {_auditing} changed its rebuilt contribution.");
                if (_counters[HeapCounters.REFERENCE_LOOKUPS] - lookups != previous.ReferenceLookups)
                    throw new InvalidOperationException($"Worklist audit: {_auditing} counted other reference lookups.");
            }
            finally
            {
                _reads = reads;
                _rebuilding = rebuilding;
                _auditing = null;
                foreach (var pair in counters)
                    _counters[pair.Key] = pair.Value;
            }
        }

        private void AuditSkip(InstanceState instance, ProcessingHistory previous)
        {
            var contribution = new RebuiltState();
            _rebuilding = contribution;
            _reads = [];
            _auditing = instance.Id;
            var before = _changes;
            var counters = new Dictionary<string, int>(_counters, StringComparer.Ordinal);
            try
            {
                Process(instance);
                var missed = _reads.FirstOrDefault(key => !previous.Reads.Contains(key));
                if (missed is not null)
                    throw new InvalidOperationException($"Worklist audit: {instance.Id} read unrecorded key {missed}.");
                if (_changes != before)
                    throw new InvalidOperationException($"Worklist audit: {instance.Id} changed _changes during a clean processing.");
                if (!previous.Contribution.Same(contribution))
                    throw new InvalidOperationException($"Worklist audit: {instance.Id} changed its rebuilt contribution.");
            }
            finally
            {
                _reads = null;
                _auditing = null;
                foreach (var pair in counters)
                    _counters[pair.Key] = pair.Value;
            }
        }

        private void AssembleContributions()
        {
            _rebuilding = new();
            foreach (var id in _instanceOrder)
            {
                CheckCancellation();
                if (_history.TryGetValue(id, out var history))
                    _rebuilding.Union(history.Contribution);
            }
        }

        private void BuildReachIndex()
        {
            if (_reachIndex is not null)
                return;
            _suspendReads++;
            try
            {
                _reachIndex = _fields.Where(pair => pair.Value.Count != 0)
                                     .GroupBy(pair => pair.Key.Region)
                                     .ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value).Distinct().ToList());
            }
            finally
            {
                _suspendReads--;
            }
        }

        private static bool SameReach(Dictionary<RegionRef, List<RegionRef>>? first, Dictionary<RegionRef, List<RegionRef>>? second) =>
            ReferenceEquals(first, second) || first is not null && second is not null && first.Count == second.Count &&
            first.All(pair => second.TryGetValue(pair.Key, out var values) && pair.Value.SequenceEqual(values));

        private void VerifyConvergence()
        {
            var before = _changes;
            var counters = new Dictionary<string, int>(_counters, StringComparer.Ordinal);
            _reachIndex = null;
            foreach (var id in _instanceOrder.ToArray())
            {
                CheckCancellation();
                var contribution = new RebuiltState();
                _rebuilding = contribution;
                Process(_instances[id]);
                _history[id] = _history[id] with { Contribution = contribution };
            }
            AssembleContributions();
            EscapeCapturedHolders();
            foreach (var state in _registrations.Values.ToArray())
            {
                CheckCancellation();
                ConstructFactory(state);
            }
            foreach (var (target, region) in _sinks.ToArray())
            {
                CheckCancellation();
                Add(target, Object(region));
            }
            if (_changes != before)
                throw new InvalidOperationException("The full pass after convergence added a solver fact.");
            foreach (var pair in counters)
            {
                CheckCancellation();
                _counters[pair.Key] = pair.Value;
            }
        }

        /// <summary>What a processing of an instance, or of one of its calls, adds to the outputs every pass rebuilds. A collection is
        /// made the first time it is asked for: most calls add to none.</summary>
        private sealed class RebuiltState
        {
            private TrackedSet<(string BodyId, int OperationId)>? _noReceiver;
            private TrackedSet<(string Instance, int Operation)>? _unresolvedDispatches;
            private TrackedMap<(string Instance, int Operation), TrackedSet<(RegionRef Region, string? DeclaringTypeKey)>>? _unresolvedReceivers;
            private TrackedMap<(string Instance, int Operation), TrackedSet<IrFactoryInput>>? _unresolvedFactoryInputs;
            private TrackedMap<(string Instance, int Operation), TrackedSet<RegionRef>>? _unresolvedFateInputs;
            private TrackedMap<UnresolvedWorkSite, TrackedSet<(RegionRef Region, string? DeclaringTypeKey)>>? _unresolvedWorkReceivers;
            private TrackedMap<UnresolvedWorkSite, TrackedSet<RegionRef>>? _unresolvedWorkInputs;
            private TrackedMap<RegionRef, (TrackedSet<(string Caller, int Operation)> Sites, TrackedSet<string> Callees)>? _handoffs;
            private TrackedSet<(string Caller, int Operation, RegionRef Region, string Callee)>? _startupDelegates;
            private TrackedSet<(string Instance, int Operation, RegionRef Region)>? _iteratorEnumerations;
            private TrackedSet<DelegateSite>? _delegateSites;
            private TrackedSet<(DelegateSite Site, DelegateRun Run)>? _delegateRuns;
            private TrackedSet<DelegateSite>? _unseenDelegates;
            private TrackedSet<(DelegateSite Site, string Callee)>? _fateRuns;
            private TrackedSet<(string Caller, int Operation, string Callee)>? _repeatedRuns;
            private TrackedSet<(string Caller, int Operation, string Callee)>? _transferEdges;
            private TrackedSet<(string Caller, int Operation, string Callee, RunRelation Relation)>? _runRelations;
            private TrackedMap<(string Instance, int Operation), RegionRef>? _callTasks;

            internal TrackedSet<(string BodyId, int OperationId)> NoReceiver => _noReceiver ??= [];
            internal TrackedSet<(string Instance, int Operation)> UnresolvedDispatches => _unresolvedDispatches ??= [];
            internal TrackedMap<(string Instance, int Operation), TrackedSet<(RegionRef Region, string? DeclaringTypeKey)>> UnresolvedReceivers => _unresolvedReceivers ??= [];
            internal TrackedMap<(string Instance, int Operation), TrackedSet<IrFactoryInput>> UnresolvedFactoryInputs => _unresolvedFactoryInputs ??= [];
            internal TrackedMap<(string Instance, int Operation), TrackedSet<RegionRef>> UnresolvedFateInputs => _unresolvedFateInputs ??= [];
            internal TrackedMap<UnresolvedWorkSite, TrackedSet<(RegionRef Region, string? DeclaringTypeKey)>> UnresolvedWorkReceivers => _unresolvedWorkReceivers ??= [];
            internal TrackedMap<UnresolvedWorkSite, TrackedSet<RegionRef>> UnresolvedWorkInputs => _unresolvedWorkInputs ??= [];
            internal TrackedMap<RegionRef, (TrackedSet<(string Caller, int Operation)> Sites, TrackedSet<string> Callees)> Handoffs => _handoffs ??= [];
            internal TrackedSet<(string Caller, int Operation, RegionRef Region, string Callee)> StartupDelegates => _startupDelegates ??= [];
            internal TrackedSet<(string Instance, int Operation, RegionRef Region)> IteratorEnumerations => _iteratorEnumerations ??= [];
            internal TrackedSet<DelegateSite> DelegateSites => _delegateSites ??= [];
            internal TrackedSet<(DelegateSite Site, DelegateRun Run)> DelegateRuns => _delegateRuns ??= [];
            internal TrackedSet<DelegateSite> UnseenDelegates => _unseenDelegates ??= [];
            internal TrackedSet<(DelegateSite Site, string Callee)> FateRuns => _fateRuns ??= [];
            internal TrackedSet<(string Caller, int Operation, string Callee)> RepeatedRuns => _repeatedRuns ??= [];
            internal TrackedSet<(string Caller, int Operation, string Callee)> TransferEdges => _transferEdges ??= [];
            internal TrackedSet<(string Caller, int Operation, string Callee, RunRelation Relation)> RunRelations => _runRelations ??= [];
            internal TrackedMap<(string Instance, int Operation), RegionRef> CallTasks => _callTasks ??= [];

            /// <summary>Adds another contribution after this one's: each item keeps the place it first appears at, as one processing
            /// adding both in turn would leave it.</summary>
            /// <param name="other">The contribution added.</param>
            internal void Union(RebuiltState other)
            {
                if (other._noReceiver is { } noReceiver) NoReceiver.UnionWith(noReceiver);
                if (other._unresolvedDispatches is { } unresolvedDispatches) UnresolvedDispatches.UnionWith(unresolvedDispatches);
                if (other._unresolvedReceivers is { } unresolvedReceivers) Merge(UnresolvedReceivers, unresolvedReceivers);
                if (other._unresolvedFactoryInputs is { } unresolvedFactoryInputs) Merge(UnresolvedFactoryInputs, unresolvedFactoryInputs);
                if (other._unresolvedFateInputs is { } unresolvedFateInputs) Merge(UnresolvedFateInputs, unresolvedFateInputs);
                if (other._unresolvedWorkReceivers is { } unresolvedWorkReceivers) Merge(UnresolvedWorkReceivers, unresolvedWorkReceivers);
                if (other._unresolvedWorkInputs is { } unresolvedWorkInputs) Merge(UnresolvedWorkInputs, unresolvedWorkInputs);
                if (other._startupDelegates is { } startupDelegates) StartupDelegates.UnionWith(startupDelegates);
                if (other._iteratorEnumerations is { } iteratorEnumerations) IteratorEnumerations.UnionWith(iteratorEnumerations);
                if (other._delegateSites is { } delegateSites) DelegateSites.UnionWith(delegateSites);
                if (other._delegateRuns is { } delegateRuns) DelegateRuns.UnionWith(delegateRuns);
                if (other._unseenDelegates is { } unseenDelegates) UnseenDelegates.UnionWith(unseenDelegates);
                if (other._fateRuns is { } fateRuns) FateRuns.UnionWith(fateRuns);
                if (other._repeatedRuns is { } repeatedRuns) RepeatedRuns.UnionWith(repeatedRuns);
                if (other._transferEdges is { } transferEdges) TransferEdges.UnionWith(transferEdges);
                if (other._runRelations is { } runRelations) RunRelations.UnionWith(runRelations);
                foreach (var pair in other._callTasks ?? [])
                {
                    WorkCancellation.Check();
                    CallTasks[pair.Key] = pair.Value;
                }
                foreach (var pair in other._handoffs ?? [])
                {
                    WorkCancellation.Check();
                    if (!Handoffs.TryGetValue(pair.Key, out var handoff))
                        Handoffs.Add(pair.Key, handoff = ([], new(StringComparer.Ordinal)));
                    handoff.Sites.UnionWith(pair.Value.Sites);
                    handoff.Callees.UnionWith(pair.Value.Callees);
                }
            }

            private static void Merge<K, V>(TrackedMap<K, TrackedSet<V>> target, TrackedMap<K, TrackedSet<V>> source) where K : notnull
            {
                foreach (var pair in source)
                {
                    WorkCancellation.Check();
                    if (!target.TryGetValue(pair.Key, out var values))
                        target.Add(pair.Key, values = new(pair.Value.Comparer));
                    values.UnionWith(pair.Value);
                }
            }

            /// <summary>Whether another contribution holds the same items in the same order; a collection never asked for is an empty
            /// one.</summary>
            /// <param name="other">The other contribution.</param>
            internal bool Same(RebuiltState other) =>
                Items(_noReceiver).SequenceEqual(Items(other._noReceiver)) &&
                Items(_unresolvedDispatches).SequenceEqual(Items(other._unresolvedDispatches)) &&
                SameMap(_unresolvedReceivers, other._unresolvedReceivers) && SameMap(_unresolvedFactoryInputs, other._unresolvedFactoryInputs) &&
                SameMap(_unresolvedFateInputs, other._unresolvedFateInputs) && SameMap(_unresolvedWorkReceivers, other._unresolvedWorkReceivers) &&
                SameMap(_unresolvedWorkInputs, other._unresolvedWorkInputs) &&
                Items(_startupDelegates).SequenceEqual(Items(other._startupDelegates)) &&
                Items(_iteratorEnumerations).SequenceEqual(Items(other._iteratorEnumerations)) &&
                Items(_delegateSites).SequenceEqual(Items(other._delegateSites)) &&
                Items(_delegateRuns).SequenceEqual(Items(other._delegateRuns)) &&
                Items(_unseenDelegates).SequenceEqual(Items(other._unseenDelegates)) &&
                Items(_fateRuns).SequenceEqual(Items(other._fateRuns)) && Items(_repeatedRuns).SequenceEqual(Items(other._repeatedRuns)) &&
                Items(_transferEdges).SequenceEqual(Items(other._transferEdges)) &&
                Items(_runRelations).SequenceEqual(Items(other._runRelations)) && Items(_callTasks).SequenceEqual(Items(other._callTasks)) &&
                Items(_handoffs?.Keys).SequenceEqual(Items(other._handoffs?.Keys)) &&
                (_handoffs ?? []).All(pair => pair.Value.Sites.SequenceEqual(other._handoffs![pair.Key].Sites) &&
                                              pair.Value.Callees.SequenceEqual(other._handoffs[pair.Key].Callees));

            /// <summary>Whether no collection was ever asked for, so the contribution adds nothing.</summary>
            internal bool IsEmpty =>
                _noReceiver is null && _unresolvedDispatches is null && _unresolvedReceivers is null && _unresolvedFactoryInputs is null &&
                _unresolvedFateInputs is null && _unresolvedWorkReceivers is null && _unresolvedWorkInputs is null && _handoffs is null &&
                _startupDelegates is null && _iteratorEnumerations is null && _delegateSites is null && _delegateRuns is null &&
                _unseenDelegates is null && _fateRuns is null && _repeatedRuns is null && _transferEdges is null && _runRelations is null &&
                _callTasks is null;

            private static IEnumerable<T> Items<T>(IEnumerable<T>? items) => items ?? [];

            private static bool SameMap<K, V>(TrackedMap<K, TrackedSet<V>>? first, TrackedMap<K, TrackedSet<V>>? second) where K : notnull =>
                Items(first?.Keys).SequenceEqual(Items(second?.Keys)) && (first ?? []).All(pair => pair.Value.SequenceEqual(second![pair.Key]));
        }
    }
}
