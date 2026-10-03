using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Heap;

internal sealed record SolverWorklistOptions(bool FullPasses = false, bool Audit = false, bool VerifyConvergence = false);

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

        private sealed record ProcessingHistory(long Started, HashSet<StateKey> Reads, RebuiltState Contribution,
                                                Dictionary<string, List<string>>? ReachSnapshot);

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
            var version = ++_writeClock;
            _writes[key] = version;
            for (var parent = key; parent is not null; parent = parent.Parent)
                _writes[parent.All] = version;
        }

        private bool Dirty(ProcessingHistory history) =>
            _writes.GetValueOrDefault(StateKey.Wildcard) > history.Started ||
            history.Reads.Any(key => _writes.GetValueOrDefault(key) > history.Started);

        private void ProcessOrSkip(InstanceState instance)
        {
            var clean = _history.TryGetValue(instance.Id, out var previous) && !Dirty(previous);
            if (clean && previous!.Reads.Contains(REACH_KEY))
            {
                // A clean first reader still builds this pass's snapshot at its original turn.
                BuildReachIndex();
                clean = SameReach(previous.ReachSnapshot, _reachIndex);
            }
            if (clean && !_worklistOptions.FullPasses)
            {
                if (_worklistOptions.Audit)
                    AuditSkip(instance, previous!);
                return;
            }

            var started = _writeClock;
            var contribution = new RebuiltState();
            _rebuilding = contribution;
            _reads = [];
            var before = _changes;
            try
            {
                Process(instance);
                _history[instance.Id] = new(started, _reads, contribution,
                                            _reads.Contains(REACH_KEY) ? _reachIndex : null);
            }
            finally
            {
                _reads = null;
            }
            if (_changes != before)
                CountRound(instance);
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
                if (_history.TryGetValue(id, out var history))
                    _rebuilding.Union(history.Contribution);
        }

        private void BuildReachIndex()
        {
            if (_reachIndex is not null)
                return;
            _suspendReads++;
            try
            {
                _reachIndex = _fields.Where(pair => pair.Value.Count != 0)
                                     .GroupBy(pair => pair.Key.Region, StringComparer.Ordinal)
                                     .ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value)
                                                                                  .Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            }
            finally
            {
                _suspendReads--;
            }
        }

        private static bool SameReach(Dictionary<string, List<string>>? first, Dictionary<string, List<string>>? second) =>
            ReferenceEquals(first, second) || first is not null && second is not null && first.Count == second.Count &&
            first.All(pair => second.TryGetValue(pair.Key, out var values) && pair.Value.SequenceEqual(values));

        private void VerifyConvergence()
        {
            var before = _changes;
            var counters = new Dictionary<string, int>(_counters, StringComparer.Ordinal);
            _reachIndex = null;
            foreach (var id in _instanceOrder.ToArray())
            {
                var contribution = new RebuiltState();
                _rebuilding = contribution;
                Process(_instances[id]);
                _history[id] = _history[id] with { Contribution = contribution };
            }
            AssembleContributions();
            EscapeCapturedHolders();
            foreach (var state in _registrations.Values.ToArray())
                ConstructFactory(state);
            foreach (var (target, region) in _sinks.ToArray())
                Add(target, Object(region));
            if (_changes != before)
                throw new InvalidOperationException("The full pass after convergence added a solver fact.");
            foreach (var pair in counters)
                _counters[pair.Key] = pair.Value;
        }

        private sealed class RebuiltState
        {
            internal TrackedSet<(string BodyId, int OperationId)> NoReceiver { get; } = [];
            internal TrackedSet<(string Instance, int Operation)> UnresolvedDispatches { get; } = [];
            internal TrackedMap<(string Instance, int Operation), TrackedSet<(string Region, string? DeclaringTypeKey)>> UnresolvedReceivers { get; } = [];
            internal TrackedMap<(string Instance, int Operation), TrackedSet<IrFactoryInput>> UnresolvedFactoryInputs { get; } = [];
            internal TrackedMap<(string Instance, int Operation), TrackedSet<string>> UnresolvedFateInputs { get; } = [];
            internal TrackedMap<string, (TrackedSet<(string Caller, int Operation)> Sites, TrackedSet<string> Callees)> Handoffs { get; } = new(StringComparer.Ordinal);
            internal TrackedSet<(string Caller, int Operation, string Region, string Callee)> StartupDelegates { get; } = [];
            internal TrackedSet<(string Instance, int Operation, string Region)> IteratorEnumerations { get; } = [];

            internal void Union(RebuiltState other)
            {
                NoReceiver.UnionWith(other.NoReceiver);
                UnresolvedDispatches.UnionWith(other.UnresolvedDispatches);
                Merge(UnresolvedReceivers, other.UnresolvedReceivers);
                Merge(UnresolvedFactoryInputs, other.UnresolvedFactoryInputs);
                Merge(UnresolvedFateInputs, other.UnresolvedFateInputs);
                StartupDelegates.UnionWith(other.StartupDelegates);
                IteratorEnumerations.UnionWith(other.IteratorEnumerations);
                foreach (var pair in other.Handoffs)
                {
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
                    if (!target.TryGetValue(pair.Key, out var values))
                        target.Add(pair.Key, values = new(pair.Value.Comparer));
                    values.UnionWith(pair.Value);
                }
            }

            internal bool Same(RebuiltState other) =>
                NoReceiver.SequenceEqual(other.NoReceiver) && UnresolvedDispatches.SequenceEqual(other.UnresolvedDispatches) &&
                SameMap(UnresolvedReceivers, other.UnresolvedReceivers) && SameMap(UnresolvedFactoryInputs, other.UnresolvedFactoryInputs) &&
                SameMap(UnresolvedFateInputs, other.UnresolvedFateInputs) && StartupDelegates.SequenceEqual(other.StartupDelegates) &&
                IteratorEnumerations.SequenceEqual(other.IteratorEnumerations) && Handoffs.Keys.SequenceEqual(other.Handoffs.Keys) &&
                Handoffs.All(pair => pair.Value.Sites.SequenceEqual(other.Handoffs[pair.Key].Sites) &&
                                     pair.Value.Callees.SequenceEqual(other.Handoffs[pair.Key].Callees));

            private static bool SameMap<K, V>(TrackedMap<K, TrackedSet<V>> first, TrackedMap<K, TrackedSet<V>> second) where K : notnull =>
                first.Keys.SequenceEqual(second.Keys) && first.All(pair => pair.Value.SequenceEqual(second[pair.Key]));
        }
    }
}
