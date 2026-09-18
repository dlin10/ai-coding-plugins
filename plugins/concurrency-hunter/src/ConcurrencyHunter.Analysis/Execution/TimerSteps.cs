using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Execution;

/// <summary>How often a timer's callbacks run, broadest last: the order is the one a creation site with several contexts is
/// counted in.</summary>
internal enum TimerKind
{
    Disabled,
    OneShot,
    Periodic
}

/// <summary>A timer step at one operation of one instance.</summary>
internal sealed record TimerStep(string InstanceId, string BodyId, int OperationId);

/// <summary>
/// What the reachable timer steps may do to each timer region, by points-to: the due time and period a
/// <c>System.Threading.Timer</c> is created with and whether any <c>Change</c> may reach it; the <c>Start()</c> and
/// <c>Enabled = true</c> (or unknown) activations and the <c>AutoReset</c> assignments of a <c>System.Timers.Timer</c>.
/// </summary>
internal sealed class TimerSteps
{
    private readonly Dictionary<string, (bool DueInfinite, bool FiresOnce)> _creations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TimerStep>> _activations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(TimerStep Step, bool Off)>> _autoResets = new(StringComparer.Ordinal);
    private readonly HashSet<IrTimerAction> _unknownTargets = [];

    internal TimerSteps(HeapSolution heap)
    {
        foreach (var instance in heap.Instances.Values.OrderBy(instance => instance.Id, StringComparer.Ordinal))
        {
            foreach (var timer in instance.Summary.Timers)
            {
                var step = new TimerStep(instance.Id, instance.BodyId, timer.OperationId);
                var regions = timer.Timer.Values.SelectMany(value => heap.Resolve(instance.Id, value)).Distinct(StringComparer.Ordinal).ToArray();
                // A step reaches the timers points-to gives it, over-approximated: a null or a field read before its first write reaches no
                // timer, and a source call reaches the ones it returned, when the heap followed them there. Any other origin may be any timer.
                if (Unknown(heap, instance, timer, regions) && (timer.Action != IrTimerAction.SetEnabled || timer.Flag != IrTimerFlag.False))
                    _unknownTargets.Add(timer.Action);

                foreach (var region in regions)
                {
                    switch (timer.Action)
                    {
                        case IrTimerAction.Create:
                            var dueInfinite = timer.DueTime == IrTimerInterval.Infinite;
                            var firesOnce = timer.Period is IrTimerInterval.Infinite or IrTimerInterval.Zero;
                            _creations[region] = _creations.TryGetValue(region, out var known)
                                ? (known.DueInfinite && dueInfinite, known.FiresOnce && firesOnce)
                                : (dueInfinite, firesOnce);
                            break;
                        case IrTimerAction.Change:
                            _changed.Add(region);
                            break;
                        case IrTimerAction.Start:
                        case IrTimerAction.SetEnabled when timer.Flag != IrTimerFlag.False:
                            Add(_activations, region, step);
                            break;
                        case IrTimerAction.SetAutoReset:
                            Add(_autoResets, region, (step, timer.Flag == IrTimerFlag.False));
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Whether a step may reach a timer beside the ones it resolves to.</summary>
    private static bool Unknown(HeapSolution heap, MethodInstance instance, SummaryTimer timer, IReadOnlyList<string> regions) =>
        timer.Timer.UnknownSources.Any(source => source is not (UnknownSource.Null or UnknownSource.FieldBeforeWrite or UnknownSource.SourceCall)) ||
        timer.Timer.SourceCalls.Any(call => heap.UnfollowedCallResults.Contains((instance.Id, call)) ||
                                            heap.Resolve(instance.Id, new CallResultValue(call)).ToArray() is not { Length: > 0 } returned ||
                                            !returned.All(regions.Contains));

    /// <summary>A <c>System.Threading.Timer</c>: disabled when created with an infinite due time and no <c>Change</c> reaches it, one-shot
    /// when created with an infinite or zero period and no <c>Change</c> reaches it, periodic otherwise.</summary>
    internal TimerKind ThreadingKind(string region) =>
        !_creations.TryGetValue(region, out var creation) || _changed.Contains(region) || _unknownTargets.Contains(IrTimerAction.Change) ? TimerKind.Periodic
        : creation.DueInfinite ? TimerKind.Disabled
        : creation.FiresOnce ? TimerKind.OneShot
        : TimerKind.Periodic;

    /// <summary>The activations of a <c>System.Timers.Timer</c>: none means it is disabled.</summary>
    internal IReadOnlyList<TimerStep> Activations(string region) => _activations.GetValueOrDefault(region) ?? [];

    /// <summary>Whether an activation or an <c>AutoReset</c> assignment reaches a timer of unknown origin, so it may reach this one:
    /// such a timer is neither disabled nor one-shot.</summary>
    internal bool MayBeActivatedElsewhere => _unknownTargets.Contains(IrTimerAction.Start) || _unknownTargets.Contains(IrTimerAction.SetEnabled);

    internal bool MayBeResetElsewhere => _unknownTargets.Contains(IrTimerAction.SetAutoReset);

    /// <summary>The <c>AutoReset</c> assignments of a <c>System.Timers.Timer</c>, and whether each assigns <c>false</c>.</summary>
    internal IReadOnlyList<(TimerStep Step, bool Off)> AutoResets(string region) => _autoResets.GetValueOrDefault(region) ?? [];

    private static void Add<TValue>(Dictionary<string, List<TValue>> map, string key, TValue value)
    {
        if (!map.TryGetValue(key, out var list))
            map.Add(key, list = []);
        list.Add(value);
    }
}
