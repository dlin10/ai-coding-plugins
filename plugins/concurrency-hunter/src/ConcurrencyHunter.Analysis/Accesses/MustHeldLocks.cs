using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>A monitor as the must-hold analysis tracks it: <see cref="Key"/> identifies the lock object, and
/// <see cref="SingleObjectId"/> is set only when the object is provably one object per process. An object whose
/// identity is unknown is keyed by its value within the body, so its own release matches its acquisition.</summary>
internal sealed record LockObject(string Key, string Display, string? SingleObjectId, bool IdentityUnknown = false);

internal enum LockEffectKind
{
    None,
    Acquire,
    Release
}

/// <summary>What an operation does to held monitors; a null <see cref="Lock"/> is an object that does not trace to one key.</summary>
internal readonly record struct LockEffect(LockEffectKind Kind, LockObject? Lock)
{
    internal static LockEffect None => new(LockEffectKind.None, null);
}

internal readonly record struct HeldLock(int Depth, LockObject Lock, int AcquisitionId);

/// <summary>
/// Forward must-dataflow over one body: a map from lock key to acquisition depth, joined by the minimum. Normal
/// branches through <c>finally</c> regions carry their own entering state through each region to their own
/// destination; unwinding into a <c>finally</c> and entry to a catch or filter handler start from the minimum over
/// every point of the protected region, and unwinding never reaches a normal destination.
/// </summary>
internal static class MustHeldLocks
{
    internal static IReadOnlyDictionary<int, IReadOnlyDictionary<string, HeldLock>> Compute(IrBody body, Func<IrOperation, LockEffect> effectOf) =>
        new Solver(body, effectOf).Run();

    private sealed class Solver(IrBody body, Func<IrOperation, LockEffect> effectOf)
    {
        private static readonly Dictionary<string, HeldLock> Empty = new(StringComparer.Ordinal);
        private readonly Dictionary<int, IrRegion> _regions = body.Regions.ToDictionary(region => region.Id);

        internal IReadOnlyDictionary<int, IReadOnlyDictionary<string, HeldLock>> Run()
        {
            var result = new Dictionary<int, IReadOnlyDictionary<string, HeldLock>>();
            if (body.Blocks.Count == 0)
                return result;

            var entries = Solve(0, body.Blocks.Count - 1, 0, Empty);
            for (var ordinal = 0; ordinal < body.Blocks.Count; ordinal++)
            {
                if (entries[ordinal] is { } entry)
                    Transfer(body.Blocks[ordinal], entry, (operation, state) => result[operation.Id] = state);
            }

            return result;
        }

        /// <summary>Entry states of blocks <paramref name="first"/>..<paramref name="last"/> when control enters at
        /// <paramref name="entry"/> with <paramref name="entryState"/>; null is a block no path reaches.</summary>
        private Dictionary<string, HeldLock>?[] Solve(int first, int last, int entry, Dictionary<string, HeldLock> entryState)
        {
            var entries = new Dictionary<string, HeldLock>?[last - first + 1];
            entries[entry - first] = entryState;
            while (true)
            {
                var incoming = new Dictionary<string, HeldLock>?[entries.Length];
                incoming[entry - first] = entryState;
                for (var ordinal = first; ordinal <= last; ordinal++)
                {
                    if (entries[ordinal - first] is not { } state)
                        continue;

                    var block = body.Blocks[ordinal];
                    var exit = Transfer(block, state, null);
                    Propagate(block.ConditionalBranch, exit, first, last, incoming);
                    Propagate(block.FallThroughBranch, exit, first, last, incoming);
                }

                foreach (var handler in body.Regions.Where(region => region.Kind is IrRegionKind.Catch or IrRegionKind.Filter or IrRegionKind.Finally))
                {
                    if (handler.FirstBlockOrdinal < first || handler.FirstBlockOrdinal > last || handler.FirstBlockOrdinal == entry ||
                        Protected(handler) is not { } protectedRegion ||
                        protectedRegion.FirstBlockOrdinal < first || protectedRegion.LastBlockOrdinal > last)
                    {
                        continue;
                    }

                    var index = handler.FirstBlockOrdinal - first;
                    incoming[index] = Join(incoming[index], MinimumOver(protectedRegion, entries, first));
                }

                if (Same(incoming, entries))
                    return entries;
                entries = incoming;
            }
        }

        private void Propagate(IrBranch? branch, Dictionary<string, HeldLock> exit, int first, int last,
                               Dictionary<string, HeldLock>?[] incoming)
        {
            if (branch is null)
                return;

            Dictionary<string, HeldLock>? state = exit;
            foreach (var regionId in branch.FinallyRegions)
            {
                var finallyRegion = _regions[regionId];
                if (finallyRegion.FirstBlockOrdinal >= first && finallyRegion.FirstBlockOrdinal <= last)
                {
                    var index = finallyRegion.FirstBlockOrdinal - first;
                    incoming[index] = Join(incoming[index], state);
                }

                state = ThroughFinally(finallyRegion, state);
                if (state is null)
                    return;
            }

            if (branch.Destination is int destination && destination >= first && destination <= last)
                incoming[destination - first] = Join(incoming[destination - first], state);
        }

        private Dictionary<string, HeldLock>? ThroughFinally(IrRegion finallyRegion, Dictionary<string, HeldLock> state)
        {
            var entries = Solve(finallyRegion.FirstBlockOrdinal, finallyRegion.LastBlockOrdinal, finallyRegion.FirstBlockOrdinal, state);
            var lastEntry = entries[^1];
            return lastEntry is null ? null : Transfer(body.Blocks[finallyRegion.LastBlockOrdinal], lastEntry, null);
        }

        private IrRegion? Protected(IrRegion handler)
        {
            if (handler.Parent is not int parentId)
                return null;
            var parent = _regions[parentId];
            if (handler.Kind is IrRegionKind.Catch or IrRegionKind.Filter && parent.Kind == IrRegionKind.FilterAndHandler && parent.Parent is int outer)
                parent = _regions[outer];
            return body.Regions.FirstOrDefault(region => region.Parent == parent.Id && region.Kind == IrRegionKind.Try);
        }

        private Dictionary<string, HeldLock>? MinimumOver(IrRegion region, Dictionary<string, HeldLock>?[] entries, int first)
        {
            Dictionary<string, HeldLock>? minimum = null;
            for (var ordinal = region.FirstBlockOrdinal; ordinal <= region.LastBlockOrdinal; ordinal++)
            {
                if (entries[ordinal - first] is not { } entry)
                    continue;
                minimum = Join(minimum, entry);
                var exit = Transfer(body.Blocks[ordinal], entry, (_, state) => minimum = Join(minimum, Copy(state)));
                minimum = Join(minimum, exit);
            }

            return minimum;
        }

        private Dictionary<string, HeldLock> Transfer(IrBlock block, Dictionary<string, HeldLock> entry,
                                                      Action<IrOperation, IReadOnlyDictionary<string, HeldLock>>? record)
        {
            var state = new Dictionary<string, HeldLock>(entry, StringComparer.Ordinal);
            foreach (var operation in block.Operations)
            {
                record?.Invoke(operation, new Dictionary<string, HeldLock>(state, StringComparer.Ordinal));
                var effect = effectOf(operation);
                switch (effect.Kind)
                {
                    case LockEffectKind.Acquire when effect.Lock is { } acquired:
                        state[acquired.Key] = state.TryGetValue(acquired.Key, out var held)
                            ? held with { Depth = held.Depth + 1 }
                            : new HeldLock(1, acquired, operation.Id);
                        break;
                    case LockEffectKind.Release when effect.Lock is { } released:
                        if (state.TryGetValue(released.Key, out var current))
                        {
                            if (current.Depth <= 1)
                                state.Remove(released.Key);
                            else
                                state[released.Key] = current with { Depth = current.Depth - 1 };
                        }
                        else if (released.IdentityUnknown)
                        {
                            // An object of unknown identity that was not acquired as such may be any held monitor.
                            state.Clear();
                        }
                        break;
                    case LockEffectKind.Release:
                        state.Clear();
                        break;
                }
            }

            return state;
        }

        private static Dictionary<string, HeldLock> Copy(IReadOnlyDictionary<string, HeldLock> state) =>
            new(state, StringComparer.Ordinal);

        private static Dictionary<string, HeldLock>? Join(Dictionary<string, HeldLock>? left, Dictionary<string, HeldLock>? right)
        {
            if (left is null)
                return right;
            if (right is null)
                return left;

            var joined = new Dictionary<string, HeldLock>(StringComparer.Ordinal);
            foreach (var (key, held) in left)
            {
                if (right.TryGetValue(key, out var other))
                    joined[key] = new HeldLock(Math.Min(held.Depth, other.Depth), held.Lock, Math.Min(held.AcquisitionId, other.AcquisitionId));
            }

            return joined;
        }

        private static bool Same(Dictionary<string, HeldLock>?[] left, Dictionary<string, HeldLock>?[] right)
        {
            for (var index = 0; index < left.Length; index++)
            {
                var (first, second) = (left[index], right[index]);
                if (first is null || second is null)
                {
                    if (first != second)
                        return false;
                    continue;
                }

                if (first.Count != second.Count || first.Any(pair => !second.TryGetValue(pair.Key, out var other) || other != pair.Value))
                    return false;
            }

            return true;
        }
    }
}
