using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>A monitor as the must-hold analysis tracks it: <see cref="Key"/> identifies the lock object, and
/// <see cref="SingleObjectId"/> is set only when the object is provably one object per process. An object whose
/// identity is unknown is keyed by its value within the body, so its own release matches its acquisition.</summary>
internal sealed record LockObject(string Key, string Display, string? SingleObjectId, bool IdentityUnknown = false)
{
    /// <summary>The mechanism the acquisition took: two mechanisms on one object are independent, so a release matches only an
    /// acquisition of its own kind (TD-083).</summary>
    public IrSynchronizationPrimitive Primitive { get; init; }

    /// <summary>The mode the acquisition takes the lock in; every acquisition of one object need not share it.</summary>
    public IrLockMode Mode { get; init; }

    /// <summary>Which acquisition this is, across the whole program. Two accesses under one acquisition are under one lock
    /// section, which is what protection across a whole read-to-write span needs (R2); empty where nothing tracks it.</summary>
    public string Site { get; init; } = "";

    /// <summary>Whether a call carried this entry out of the callee that made it. Such an entry is protection only when the exit
    /// is carried back the same way on every path, since nothing else proves the scope ever closes (ADR 0009).</summary>
    public bool IsLifted { get; init; }
    public bool IsIteratorCarry { get; init; }

    /// <summary>Whether the analysis could name the object at all. A context that names none says nothing about which object a
    /// call works on, so it is no evidence either way about what the call carries out of it.</summary>
    public bool NamesObject { get; init; }
}

internal enum LockEffectKind
{
    None,
    Acquire,
    Release,
    IteratorState
}

/// <summary>What an operation does to held monitors; a null <see cref="Lock"/> is an object that does not trace to one key.</summary>
internal readonly record struct LockEffect(LockEffectKind Kind, LockObject? Lock)
{
    internal static LockEffect None => new(LockEffectKind.None, null);

    /// <summary>How many permits an exit gives back, as <see cref="IrReleaseOperation.Permits"/> reads it; an entry takes one.</summary>
    public int? Permits { get; init; } = 1;

    /// <summary>The callee's holding a lifted entry stands for: the acquisition it makes keeps whether that holding crosses a
    /// suspension and whether its permits pair, on every level it is lifted through (ADR 0009, TD-083).</summary>
    public HeldLock? Carried { get; init; }

    public IReadOnlyDictionary<string, HeldLock>? IteratorLocks { get; init; }
    public IReadOnlySet<string>? IteratorKeys { get; init; }
    public string? IteratorSite { get; init; }

    /// <summary>The locks an iterator's body lets go without holding them itself, which are its enumerator's. Nothing proves which
    /// step of the enumeration runs that exit, so every step lets go of them — the disposal alone for an exit no element can follow
    /// (ADR 0011).</summary>
    public IReadOnlyList<LockObject>? IteratorReleases { get; init; }

    /// <summary>The locks an iterator's body lets go without holding them on some path: a handler around the enumeration may be
    /// reached after any of them, so it holds none of them (ADR 0011).</summary>
    public IReadOnlyList<LockObject>? IteratorMayReleases { get; init; }

    /// <summary>The locks a call or a step of an enumeration may let go on some path without the body that does it ever taking
    /// them, which makes them its caller's or its enumerator's: a must-state holds none of them afterwards, and a handler around
    /// the operation none either, since the body may throw after letting go (R8, ADR 0009).</summary>
    public IReadOnlyList<LockObject>? MayReleases { get; init; }
}

internal readonly record struct HeldLock(int Depth, LockObject Lock, int AcquisitionId)
{
    /// <summary>Whether the region this acquisition guards contains a suspension point: a continuation may resume on another
    /// thread, which breaks every primitive owned by the thread that took it (TD-083).</summary>
    public bool CrossesSuspension { get; init; }

    /// <summary>Whether every path out of the region that holds this acquisition releases it, proven over the control flow of the
    /// <c>finally</c> that guards it rather than from where a release stands (R3).</summary>
    public bool IsReleasedOnAllPaths { get; init; }

    /// <summary>Whether every exit the body performs on this object gives back exactly what an entry takes. A
    /// <c>SemaphoreSlim.Release(n)</c> gives back more permits than the wait took, and an exit that pairs with no entry of this
    /// body gives back one nobody took: either way the permits outlive the section and the next holders overlap (R3).</summary>
    public bool IsPaired { get; init; } = true;

    /// <summary>The acquisitions this one is nested inside, innermost first, so that leaving an inner section restores the section
    /// around it — both its mode and which acquisition it is. <see cref="LockObject.Mode"/> and <see cref="LockObject.Site"/> of
    /// <see cref="Lock"/> are what is held now: one object taken again is held in the new acquisition's mode, and stands in the new
    /// section, until that acquisition is left (TD-083). A string and not a list, because the fixpoint compares states by value;
    /// the separators are the ASCII unit and record separators, which no name, region id or operation id contains.</summary>
    public string OuterSections { get; init; } = "";
}

/// <summary>What a body holds: before each of its operations, and where it ends on every path.</summary>
internal sealed record MustHeldState(IReadOnlyDictionary<int, IReadOnlyDictionary<string, HeldLock>> Operations,
                                     IReadOnlyDictionary<string, HeldLock> AtExit);

/// <summary>
/// Forward must-dataflow over one body: a map from lock key to acquisition depth, joined by the minimum. Normal
/// branches through <c>finally</c> regions carry their own entering state through each region to their own
/// destination; unwinding into a <c>finally</c> and entry to a catch or filter handler start from the minimum over
/// every point of the protected region, and unwinding never reaches a normal destination.
/// </summary>
internal static class MustHeldLocks
{
    /// <summary>The one exception whose handler still owns the mutex the guarded region waited on.</summary>
    private const string ABANDONED_MUTEX = "System.Threading.AbandonedMutexException";

    /// <summary>The separators of <see cref="HeldLock.OuterSections"/>: between the two fields of one section, and between
    /// sections.</summary>
    private const char FIELD = '';
    private const char RECORD = '';

    /// <summary>
    /// What two states both hold, weakened to what is true on either of them: the shallower nesting, a mode that excludes no more
    /// than either of them, a holding that crosses a suspension if either does and is released on every path only if both are, and
    /// a site that names every acquisition this may be. Whoever joins states of one body's several entries owes them the same
    /// weakening the paths inside a body get: keeping the first state's properties would carry a mode, a section or a proof of
    /// release that only one of the entries has (TD-083).
    /// </summary>
    internal static Dictionary<string, HeldLock>? Join(Dictionary<string, HeldLock>? left, Dictionary<string, HeldLock>? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;

        var joined = new Dictionary<string, HeldLock>(StringComparer.Ordinal);
        foreach (var (key, held) in left)
        {
            if (!right.TryGetValue(key, out var other))
                continue;
            var mode = held.Lock.Mode == other.Lock.Mode ? held.Lock.Mode : IrLockMode.Read;
            joined[key] = new HeldLock(Math.Min(held.Depth, other.Depth),
                                       held.Lock with { Mode = mode, Site = Sites(held.Lock.Site, other.Lock.Site) },
                                       Math.Min(held.AcquisitionId, other.AcquisitionId))
            {
                OuterSections = string.Equals(held.OuterSections, other.OuterSections, StringComparison.Ordinal) ? held.OuterSections : "",
                CrossesSuspension = held.CrossesSuspension || other.CrossesSuspension,
                IsReleasedOnAllPaths = held.IsReleasedOnAllPaths && other.IsReleasedOnAllPaths,
                IsPaired = held.IsPaired && other.IsPaired
            };
        }

        return joined;
    }

    /// <summary>
    /// The site of a holding two paths reached by different acquisitions: every acquisition it may be, which is one token, because
    /// on any one run it is one of them and the same one for everything the joined state reaches. Two such holdings are the same
    /// section exactly when they may be the same acquisitions, so the token is the union and not an empty site — an empty site is
    /// an acquisition nothing tracks, and two of those are no evidence of one section (R2).
    /// </summary>
    private static string Sites(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal)
            ? left
            : string.Join('+', left.Split('+').Concat(right.Split('+')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>The locks held before each operation, starting from <paramref name="entry"/> (none when null).</summary>
    internal static MustHeldState Compute(IrBody body, Func<IrOperation, LockEffect> effectOf,
                                          IReadOnlyDictionary<string, HeldLock>? entry = null) =>
        new Solver(body, effectOf, entry).Run();

    /// <summary>Whether <paramref name="key"/> is released on every path out of <paramref name="body"/>, which is what a scope
    /// somebody else opened needs of the body that closes it (R3, ADR 0009).</summary>
    internal static bool ReleasesOnEveryPath(IrBody body, Func<IrOperation, LockEffect> effectOf, string key) =>
        Dual(body, effectOf, key).Run().AtExit.ContainsKey(key);

    /// <summary>
    /// Whether <paramref name="key"/> is held from one operation to another without being let go in between, which is what one
    /// section over a whole read-to-write span means (R2). The site of an acquisition cannot answer it on its own: one acquisition
    /// inside a loop opens a new section on every iteration, and a read of one iteration is not under the section the write of the
    /// next one runs in. The answer is no exactly where some path runs from the read through a release of the key to the write —
    /// the back edge of such a loop included, since it passes the release the iteration ends with.
    /// </summary>
    internal static bool HoldsBetween(IrBody body, Func<IrOperation, LockEffect> effectOf, string key, int from, int to)
    {
        var (positions, successors) = Flow(body);
        if (!positions.TryGetValue(from, out var start) || !positions.TryGetValue(to, out var end))
            return false;

        var forward = Reachable(successors, (start.Block, start.Index + 1));
        var backward = Reachable(Reversed(successors), end);
        foreach (var (block, index) in forward)
        {
            if ((block, index) == end || !backward.Contains((block, index)) || index >= body.Blocks[block].Operations.Count)
                continue;
            if (Releases(effectOf(body.Blocks[block].Operations[index]), key))
                return false;
        }

        return true;
    }

    /// <summary>Whether some path runs from the body's entry to <paramref name="to"/> without taking <paramref name="key"/> on the
    /// way: what makes an exit at <paramref name="to"/> possibly the exit of a lock somebody else took. An exit every path to which
    /// passes the body's own entry of that lock may be that entry's exit. An operation the body does not have may be reached any
    /// way.</summary>
    internal static bool ReachesWithoutEntry(IrBody body, Func<IrOperation, LockEffect> effectOf, string key, int to)
    {
        var (positions, successors) = Flow(body);
        if (body.Blocks.Count == 0 || !positions.TryGetValue(to, out var end))
            return true;

        var first = (Block: 0, Index: 0);
        var seen = new HashSet<(int Block, int Index)> { first };
        var queue = new Queue<(int Block, int Index)>([first]);
        while (queue.Count > 0)
        {
            var position = queue.Dequeue();
            if (position == end)
                return true;
            var operations = body.Blocks[position.Block].Operations;
            if (position.Index < operations.Count &&
                effectOf(operations[position.Index]) is { Kind: LockEffectKind.Acquire, Lock: { } entered } &&
                string.Equals(entered.Key, key, StringComparison.Ordinal))
                continue;
            foreach (var next in successors.GetValueOrDefault(position) ?? [])
            {
                if (seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        return false;
    }

    /// <summary>Whether some path from just after <paramref name="from"/> reaches an operation <paramref name="target"/> accepts;
    /// an operation the body does not have may reach anything.</summary>
    internal static bool Reaches(IrBody body, int from, Func<IrOperation, bool> target)
    {
        var (positions, successors) = Flow(body);
        if (!positions.TryGetValue(from, out var start))
            return true;

        return Reachable(successors, (start.Block, start.Index + 1))
            .Any(position => position.Index < body.Blocks[position.Block].Operations.Count &&
                             target(body.Blocks[position.Block].Operations[position.Index]));
    }

    /// <summary>Where each operation stands, and which position follows which over the normal control flow of the body.</summary>
    private static (Dictionary<int, (int Block, int Index)> Positions,
                    Dictionary<(int Block, int Index), List<(int Block, int Index)>> Successors) Flow(IrBody body)
    {
        var positions = new Dictionary<int, (int Block, int Index)>();
        for (var ordinal = 0; ordinal < body.Blocks.Count; ordinal++)
        {
            var operations = body.Blocks[ordinal].Operations;
            for (var index = 0; index < operations.Count; index++)
                positions[operations[index].Id] = (ordinal, index);
        }

        var regions = body.Regions.ToDictionary(region => region.Id);
        var successors = new Dictionary<(int Block, int Index), List<(int Block, int Index)>>();
        for (var ordinal = 0; ordinal < body.Blocks.Count; ordinal++)
        {
            var block = body.Blocks[ordinal];
            // The position after a block's last operation stands for its exit, so that a block with no operations still carries
            // control through.
            for (var index = 0; index < block.Operations.Count; index++)
                Link((ordinal, index), (ordinal, index + 1));

            foreach (var branch in new[] { block.ConditionalBranch, block.FallThroughBranch }.OfType<IrBranch>())
            {
                // A branch runs every `finally` it leaves, in order, and goes on to its destination from the end of the last of
                // them; the blocks of a `finally` end in no destination of their own, so nothing else would join the two.
                var leaving = (Block: ordinal, Index: block.Operations.Count);
                foreach (var regionId in branch.FinallyRegions.Where(regions.ContainsKey))
                {
                    var region = regions[regionId];
                    if (region.FirstBlockOrdinal < 0 || region.LastBlockOrdinal >= body.Blocks.Count)
                        continue;
                    Link(leaving, (region.FirstBlockOrdinal, 0));
                    leaving = (region.LastBlockOrdinal, body.Blocks[region.LastBlockOrdinal].Operations.Count);
                }

                if (branch.Destination is int destination && destination >= 0 && destination < body.Blocks.Count)
                    Link(leaving, (destination, 0));
            }
        }

        void Link((int Block, int Index) from, (int Block, int Index) to)
        {
            if (!successors.TryGetValue(from, out var next))
                successors[from] = next = [];
            next.Add(to);
        }

        return (positions, successors);
    }

    /// <summary>A release that may let go of <paramref name="key"/>: its own, one that traces to no object at all, and one of an
    /// object whose identity is unknown, which may be any held monitor.</summary>
    private static bool Releases(LockEffect effect, string key) =>
        effect.Kind == LockEffectKind.Release &&
        (effect.Lock is not { } released || released.IdentityUnknown || string.Equals(released.Key, key, StringComparison.Ordinal)) ||
        (effect.IteratorReleases ?? []).Any(released => released.IdentityUnknown || string.Equals(released.Key, key, StringComparison.Ordinal)) ||
        (effect.MayReleases ?? []).Any(released => !released.NamesObject || string.Equals(released.Key, key, StringComparison.Ordinal));

    private static HashSet<(int Block, int Index)> Reachable(IReadOnlyDictionary<(int Block, int Index), List<(int Block, int Index)>> edges,
                                                             (int Block, int Index) from)
    {
        var seen = new HashSet<(int Block, int Index)> { from };
        var queue = new Queue<(int Block, int Index)>([from]);
        while (queue.Count > 0)
        {
            foreach (var next in edges.GetValueOrDefault(queue.Dequeue()) ?? [])
            {
                if (seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        return seen;
    }

    private static Dictionary<(int Block, int Index), List<(int Block, int Index)>> Reversed(
        Dictionary<(int Block, int Index), List<(int Block, int Index)>> edges)
    {
        var reversed = new Dictionary<(int Block, int Index), List<(int Block, int Index)>>();
        foreach (var (position, next) in edges)
        {
            foreach (var target in next)
            {
                if (!reversed.TryGetValue(target, out var sources))
                    reversed[target] = sources = [];
                sources.Add(position);
            }
        }

        return reversed;
    }

    /// <summary>The solver that proves a release of one key, over the same body.</summary>
    private static Solver Dual(IrBody body, Func<IrOperation, LockEffect> effectOf, string key) =>
        new(body, operation => Dual(effectOf(operation), key), null, NullGuards(body, effectOf, key));

    /// <summary>
    /// The values a release of <paramref name="key"/> is performed on, by the lock it releases. Nothing is ever held on an object
    /// that is not there, so a path on which one of these values is null needs no release of it — which is how the disposal of a
    /// <c>using</c>, guarded by the null test its lowering writes, is a release on every path that could have taken anything.
    /// </summary>
    private static Dictionary<int, LockObject> NullGuards(IrBody body, Func<IrOperation, LockEffect> effectOf, string key)
    {
        var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
        // The value a release is performed on is rarely the value a test names: a `using` disposes a conversion of the value it
        // captured and tests the capture, so both are taken back to the value they are copies of.
        var copies = new Dictionary<int, int>();
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case IrAssignOperation assign:
                    copies[assign.TargetValue] = assign.SourceValue;
                    break;
                case IrConvertOperation convert:
                    copies[convert.ResultValue] = convert.OperandValue;
                    break;
            }
        }

        var subjects = new Dictionary<int, LockObject>();
        foreach (var operation in operations)
        {
            if (effectOf(operation) is not { Kind: LockEffectKind.Release, Lock: { } released } ||
                !string.Equals(released.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (operation switch
                {
                    IrReleaseOperation release => release.LockValue,
                    IrCallOperation { ReceiverValue: int receiver } => receiver,
                    _ => (int?)null
                } is int subject)
            {
                subjects[Origin(copies, subject)] = released;
            }
        }

        var guards = new Dictionary<int, LockObject>(subjects);
        foreach (var value in copies.Keys)
        {
            if (subjects.TryGetValue(Origin(copies, value), out var released))
                guards[value] = released;
        }

        return guards;
    }

    /// <summary>The value <paramref name="value"/> is a copy of, as far back as the body copies it.</summary>
    private static int Origin(IReadOnlyDictionary<int, int> copies, int value)
    {
        var seen = new HashSet<int>();
        while (seen.Add(value) && copies.TryGetValue(value, out var source))
            value = source;
        return value;
    }

    /// <summary>
    /// One lock's effects, turned around. Held-on-every-path is a must-fact and released-on-every-path is not its negation: a join
    /// of a path that released the lock with one that did not drops the key exactly as a release does, so an absence at the exit
    /// says only that the lock is not held everywhere. Asking the same must-analysis about the run where every release of this one
    /// key is an acquisition, every acquisition of it a release and nothing else has an effect, starting from holding nothing,
    /// answers the other question: the key is held at the exit only where every path reached it through a release.
    /// </summary>
    private static LockEffect Dual(LockEffect effect, string key) =>
        effect.Lock is not { } subject || !string.Equals(subject.Key, key, StringComparison.Ordinal)
            ? LockEffect.None
            : effect.Kind switch
            {
                LockEffectKind.Acquire => new LockEffect(LockEffectKind.Release, subject),
                LockEffectKind.Release => new LockEffect(LockEffectKind.Acquire, subject),
                _ => LockEffect.None
            };

    private sealed class Solver(IrBody body, Func<IrOperation, LockEffect> effectOf, IReadOnlyDictionary<string, HeldLock>? entry,
                                IReadOnlyDictionary<int, LockObject>? nullGuards = null)
    {
        private readonly Dictionary<string, HeldLock> Empty = new(entry ?? new Dictionary<string, HeldLock>(), StringComparer.Ordinal);
        private readonly Dictionary<int, IrRegion> _regions = body.Regions.ToDictionary(region => region.Id);

        internal MustHeldState Run()
        {
            var result = new Dictionary<int, IReadOnlyDictionary<string, HeldLock>>();
            if (body.Blocks.Count == 0)
                return new MustHeldState(result, new Dictionary<string, HeldLock>(StringComparer.Ordinal));

            var entries = Solve(0, body.Blocks.Count - 1, 0, Empty);
            for (var ordinal = 0; ordinal < body.Blocks.Count; ordinal++)
            {
                if (entries[ordinal] is { } entry)
                    Transfer(body.Blocks[ordinal], entry, (operation, state) => result[operation.Id] = state);
            }

            var released = ReleasedByFinally(entries);
            var unpaired = Unpaired(result);

            // What the body still holds where it ends on every path: an entry it opened for its caller (ADR 0009). It is marked like
            // every other state, so that the caller's acquisition keeps what was true of it here.
            var atExit = entries[^1] is { } exit
                ? Transfer(body.Blocks[^1], exit, null)
                : new Dictionary<string, HeldLock>(StringComparer.Ordinal);
            var marked = Mark(result, atExit, released, unpaired);
            return new MustHeldState(marked.States, marked.AtExit);
        }

        /// <summary>
        /// The objects this body does not take and give back one for one. A <c>SemaphoreSlim.Release(n)</c> gives back n permits
        /// where the wait took one, and a release that pairs with no acquisition of this body gives back a permit this body never
        /// took; after either of them the primitive admits more holders than the section assumed, so no holding of that object in
        /// this body excludes anybody (R3). Only a semaphore counts permits: every other primitive is taken and given back once,
        /// and its exit without an entry is a scope its caller opened, which ADR 0009 carries rather than counts.
        /// </summary>
        private HashSet<string> Unpaired(Dictionary<int, IReadOnlyDictionary<string, HeldLock>> states)
        {
            var unpaired = new HashSet<string>(StringComparer.Ordinal);
            foreach (var operation in body.Blocks.SelectMany(block => block.Operations))
            {
                if (effectOf(operation) is not { Kind: LockEffectKind.Release, Lock: { Primitive: IrSynchronizationPrimitive.SemaphoreSlim } released } effect ||
                    !states.TryGetValue(operation.Id, out var state))
                {
                    continue;
                }

                if (effect.Permits != 1 || !state.ContainsKey(released.Key))
                    unpaired.Add(released.Key);
            }

            return unpaired;
        }

        /// <summary>Two properties of a whole acquisition, marked into every state that holds it: whether its region holds over a
        /// suspension point, so that an <c>await</c> after the access counts as much as one before it, and whether a
        /// <c>finally</c> releases it, so that no path leaves the region holding it (TD-083).</summary>
        private (Dictionary<int, IReadOnlyDictionary<string, HeldLock>> States, Dictionary<string, HeldLock> AtExit) Mark(
            Dictionary<int, IReadOnlyDictionary<string, HeldLock>> states,
            Dictionary<string, HeldLock> atExit,
            HashSet<(string Key, int Acquisition)> released,
            HashSet<string> unpaired)
        {
            var operations = body.Blocks.SelectMany(block => block.Operations.Select(operation => (Block: block, Operation: operation))).ToArray();
            // Every suspension counts, not one kind of it: an iterator resumed after a `yield return` may run on another thread
            // exactly as a continuation does (R3).
            var suspensions = operations.Where(item => item.Operation is IIrSuspension).Select(item => item.Operation.Id).ToHashSet();
            var crossed = new HashSet<(string Key, int Acquisition)>();
            foreach (var (_, operation) in operations)
            {
                if (!states.TryGetValue(operation.Id, out var state) || !suspensions.Contains(operation.Id))
                    continue;
                foreach (var held in state.Values)
                {
                    // A synchronous foreach keeps the caller's header lock while its iterator is suspended.
                    // The iterator's own acquisition can resume on another thread; the caller's does not.
                    if (body.IsIterator && Empty.TryGetValue(held.Lock.Key, out var inherited) &&
                        inherited.Lock.Site == held.Lock.Site)
                        continue;
                    crossed.Add((held.Lock.Key, held.AcquisitionId));
                }
            }

            if (crossed.Count == 0 && released.Count == 0 && unpaired.Count == 0)
                return (states, atExit);

            foreach (var operationId in states.Keys.ToArray())
                states[operationId] = Marked(states[operationId]);

            return (states, Marked(atExit));

            Dictionary<string, HeldLock> Marked(IReadOnlyDictionary<string, HeldLock> state) =>
                state.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value with
                    {
                        CrossesSuspension = pair.Value.CrossesSuspension || crossed.Contains((pair.Value.Lock.Key, pair.Value.AcquisitionId)),
                        IsReleasedOnAllPaths = pair.Value.IsReleasedOnAllPaths || released.Contains((pair.Value.Lock.Key, pair.Value.AcquisitionId)),
                        IsPaired = pair.Value.IsPaired && !unpaired.Contains(pair.Value.Lock.Key)
                    },
                    StringComparer.Ordinal);
        }

        /// <summary>
        /// The acquisitions a <c>finally</c> releases on every path out of it, which is the only thing that makes a region's exit
        /// unconditional (R3). It is read from the control flow of the region and not from where a release stands: a release under
        /// an <c>if</c> inside the <c>finally</c>, or in one of two handlers, is a release on some paths, and the region is still
        /// left holding the lock on the others. Each acquisition the region enters holding is asked of the dual run over that
        /// region, which is where a release is proven on every path.
        /// </summary>
        private HashSet<(string Key, int Acquisition)> ReleasedByFinally(Dictionary<string, HeldLock>?[] entries)
        {
            var released = new HashSet<(string Key, int Acquisition)>();
            foreach (var region in body.Regions.Where(region => region.Kind == IrRegionKind.Finally))
            {
                if (region.FirstBlockOrdinal >= entries.Length || entries[region.FirstBlockOrdinal] is not { } entry)
                    continue;

                foreach (var (key, held) in entry)
                {
                    // A finally that never completes normally proves nothing about what it releases, so it is left out.
                    var dual = MustHeldLocks.Dual(body, effectOf, key);
                    if (dual.ThroughFinally(region, dual.Empty) is { } exit &&
                        exit.TryGetValue(key, out var proven) && proven.Depth >= held.Depth)
                    {
                        released.Add((key, held.AcquisitionId));
                    }
                }
            }

            return released;
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
                    var conditional = new Dictionary<int, HeldLock>();
                    var exit = Transfer(block, state, null, conditional);
                    Propagate(block.ConditionalBranch, Taking(exit, conditional, block.ConditionalBranch, taken: true), first, last, incoming);
                    Propagate(block.FallThroughBranch, Taking(exit, conditional, block.ConditionalBranch, taken: false), first, last, incoming);
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
                    incoming[index] = Join(incoming[index],
                                           Abandoned(handler, protectedRegion, MinimumOver(protectedRegion, entries, first), entries, first));
                }

                if (Same(incoming, entries))
                    return entries;
                entries = incoming;
            }
        }

        /// <summary>The state on one edge out of a block: a conditional entry is held only where its success flag is true, which
        /// is the conditional edge when the branch jumps if true and the fall-through otherwise (TD-083).</summary>
        private static Dictionary<string, HeldLock> Taking(Dictionary<string, HeldLock> exit, Dictionary<int, HeldLock> conditional,
                                                           IrBranch? conditionalBranch, bool taken)
        {
            if (conditional.Count == 0 || conditionalBranch is not { ConditionValue: int flag, JumpIfTrue: bool jumpIfTrue } ||
                !conditional.TryGetValue(flag, out var held) || jumpIfTrue != taken)
            {
                return exit;
            }

            var state = new Dictionary<string, HeldLock>(exit, StringComparer.Ordinal);
            state[held.Lock.Key] = state.TryGetValue(held.Lock.Key, out var outer) ? outer with { Depth = outer.Depth + 1 } : held;
            return state;
        }

        /// <summary>
        /// A <c>WaitOne</c> that throws <c>AbandonedMutexException</c> did take the mutex it waited on, and only that one: the
        /// waits after it never ran, and the waits before it are held or not by their own paths. Any wait of the guarded region
        /// may be the one that threw, so the handler holds what every one of those paths holds — the state before that wait, plus
        /// its own mutex, intersected over the waits (TD-083). Handing the handler every mutex the region enters would let a write
        /// under one of them look protected in a handler that never took it.
        /// </summary>
        private Dictionary<string, HeldLock>? Abandoned(IrRegion handler, IrRegion guarded, Dictionary<string, HeldLock>? minimum,
                                                        Dictionary<string, HeldLock>?[] entries, int first)
        {
            if (handler.Kind != IrRegionKind.Catch || handler.CatchType != ABANDONED_MUTEX)
                return minimum;

            Dictionary<string, HeldLock>? owned = null;
            for (var ordinal = guarded.FirstBlockOrdinal; ordinal <= guarded.LastBlockOrdinal; ordinal++)
            {
                var index = ordinal - first;
                if (index < 0 || index >= entries.Length || entries[index] is not { } blockEntry)
                    continue;

                Transfer(body.Blocks[ordinal], blockEntry, (operation, state) =>
                {
                    if (effectOf(operation) is not { Kind: LockEffectKind.Acquire, Lock: { Primitive: IrSynchronizationPrimitive.Mutex } mutex })
                        return;

                    var scenario = Copy(state);
                    if (!scenario.ContainsKey(mutex.Key))
                        scenario[mutex.Key] = new HeldLock(1, mutex, operation.Id) { IsReleasedOnAllPaths = true };
                    owned = Join(owned, scenario);
                });
            }

            return owned ?? minimum;
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
            if (lastEntry is null)
                return null;
            var exit = Transfer(body.Blocks[finallyRegion.LastBlockOrdinal], lastEntry, null);
            // A compiler generated foreach finally checks the enumerator for null before Dispose. A null enumerator
            // cannot reach the statement after the foreach, so the iterator's must state still holds there.
            foreach (var operation in body.Blocks.Skip(finallyRegion.FirstBlockOrdinal)
                                          .Take(finallyRegion.LastBlockOrdinal - finallyRegion.FirstBlockOrdinal + 1)
                                          .SelectMany(block => block.Operations))
            {
                if (operation is IrCallOperation { EnumerationRole: IrEnumerationRole.Dispose } &&
                    effectOf(operation) is { Kind: LockEffectKind.IteratorState } effect)
                    ApplyIteratorState(exit, effect);
            }
            return exit;
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
                var exit = Transfer(body.Blocks[ordinal], entry, (operation, state) =>
                {
                    minimum = Join(minimum, Copy(state));
                    // A step of an enumeration runs the iterator's body, which may throw after letting a lock go on a path that
                    // hands out no element: the handler is reached without it, whatever the loop itself still holds.
                    if (effectOf(operation) is { Kind: LockEffectKind.IteratorState, IteratorMayReleases: { Count: > 0 } released })
                        minimum = Join(minimum, Without(state, released));
                    // A call as much: its callee may let the caller's lock go and throw after.
                    else if (effectOf(operation) is { MayReleases: { Count: > 0 } mayRelease })
                        minimum = Join(minimum, LetGo(Copy(state), mayRelease));
                });
                minimum = Join(minimum, exit);
            }

            return minimum;
        }

        private Dictionary<string, HeldLock> Transfer(IrBlock block, Dictionary<string, HeldLock> entry,
                                                      Action<IrOperation, IReadOnlyDictionary<string, HeldLock>>? record,
                                                      Dictionary<int, HeldLock>? conditional = null)
        {
            var state = new Dictionary<string, HeldLock>(entry, StringComparer.Ordinal);
            foreach (var operation in block.Operations)
            {
                record?.Invoke(operation, new Dictionary<string, HeldLock>(state, StringComparer.Ordinal));
                // The success flag of a conditional entry travels with the value it is copied into.
                if (operation is IrAssignOperation assign && conditional?.TryGetValue(assign.SourceValue, out var carried) == true)
                    conditional[assign.TargetValue] = carried;

                // A path on which the object a release is performed on is null needs no release: it took nothing.
                if (conditional is not null && operation is IrCompareOperation { Comparison: IrComparisonKind.Null } test &&
                    test.Operator is null or IrComparisonOperator.Equal &&
                    nullGuards?.TryGetValue(test.LeftValue, out var absent) == true)
                {
                    conditional[test.ResultValue] = new HeldLock(1, absent, operation.Id);
                }

                var effect = effectOf(operation);
                if (effect.Kind == LockEffectKind.IteratorState)
                {
                    ApplyIteratorState(state, effect);
                    continue;
                }
                // What a call may let go of it lets go while it runs, before the entry it leaves open at its end.
                LetGo(state, effect.MayReleases);
                switch (effect.Kind)
                {
                    // A conditional entry holds nothing where it stands: only the branch on which its flag is true holds it.
                    case LockEffectKind.Acquire when effect.Lock is { } pending && operation is IrAcquireOperation { ConditionValue: int flag }:
                        if (conditional is not null)
                            conditional[flag] = Acquired(state, pending, operation.Id);
                        break;
                    case LockEffectKind.Acquire when effect.Lock is { } acquired:
                        state[acquired.Key] = Acquired(state, acquired, operation.Id, effect.Carried);
                        break;
                    case LockEffectKind.Release when effect.Lock is { } released:
                        if (state.TryGetValue(released.Key, out var current))
                        {
                            if (current.Depth <= 1)
                                state.Remove(released.Key);
                            else
                                state[released.Key] = Released(current);
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

        private static void ApplyIteratorState(Dictionary<string, HeldLock> state, LockEffect effect)
        {
            foreach (var key in effect.IteratorKeys ?? new HashSet<string>(StringComparer.Ordinal))
            {
                if (state.TryGetValue(key, out var previous) && previous.Lock.IsLifted &&
                    previous.Lock.Site.StartsWith(effect.IteratorSite ?? "", StringComparison.Ordinal))
                    state.Remove(key);
            }
            // As a release performed here would: an object not held as such may be any held monitor.
            foreach (var released in effect.IteratorReleases ?? [])
            {
                if (!state.Remove(released.Key) && released.IdentityUnknown)
                    state.Clear();
            }
            LetGo(state, effect.MayReleases);
            foreach (var (key, held) in effect.IteratorLocks ?? new Dictionary<string, HeldLock>())
            {
                if (!state.ContainsKey(key))
                    state[key] = held;
            }
        }

        /// <summary>Taking a lock already held: the mode and the site become the new acquisition's, and the section it was held in
        /// until now goes on the stack, so that leaving this acquisition restores it. A reader that takes the same lock for writing
        /// holds it for writing until it leaves, and claiming otherwise would call a write section compatible with somebody else's
        /// read. A lifted entry starts with what was true of the callee's holding it stands for; permits that do not pair are a
        /// fact about the object and not about one section of it, so taking it again keeps them unpaired (TD-083).</summary>
        private static HeldLock Acquired(Dictionary<string, HeldLock> state, LockObject acquired, int operationId,
                                         HeldLock? carried = null) =>
            state.TryGetValue(acquired.Key, out var held)
                ? held with
                {
                    Depth = held.Depth + 1,
                    Lock = acquired,
                    OuterSections = Push(held.Lock, held.OuterSections),
                    IsPaired = held.IsPaired && carried?.IsPaired != false
                }
                : new HeldLock(1, acquired, operationId)
                {
                    CrossesSuspension = carried?.CrossesSuspension == true,
                    IsPaired = carried?.IsPaired != false
                };

        /// <summary>The acquisition left behind when an inner one is released: the section around it, or, where the paths that
        /// joined here disagreed and nothing proves which section is left, the weakest mode and the section the release stands in —
        /// which is the same holding of the lock, since the depth only fell.</summary>
        private static HeldLock Released(HeldLock held)
        {
            var separator = held.OuterSections.IndexOf(RECORD);
            var outer = separator < 0 ? held.OuterSections : held.OuterSections[..separator];
            var rest = separator < 0 ? "" : held.OuterSections[(separator + 1)..];
            var field = outer.IndexOf(FIELD);
            var mode = field >= 0 && Enum.TryParse<IrLockMode>(outer[..field], out var parsed) ? parsed : IrLockMode.Read;
            var site = field >= 0 ? outer[(field + 1)..] : held.Lock.Site;
            return held with { Depth = held.Depth - 1, Lock = held.Lock with { Mode = mode, Site = site }, OuterSections = rest };
        }

        private static string Push(LockObject held, string outer) =>
            outer.Length == 0 ? $"{held.Mode}{FIELD}{held.Site}" : $"{held.Mode}{FIELD}{held.Site}{RECORD}{outer}";

        /// <summary>A state after the given exits, as a release performed here would leave it: an object not held as such may be
        /// any held monitor.</summary>
        private static Dictionary<string, HeldLock> Without(IReadOnlyDictionary<string, HeldLock> state, IReadOnlyList<LockObject> released)
        {
            var left = Copy(state);
            foreach (var exit in released)
            {
                if (!left.Remove(exit.Key) && exit.IdentityUnknown)
                    left.Clear();
            }
            return left;
        }

        /// <summary>A must-state after an operation that may let these locks go: none of them is held any longer, and an object
        /// nothing names may be any held lock. A named object nobody holds here is not one of this state's, so it takes nothing
        /// away — a semaphore a callee signals is not a reason to forget the monitor its caller holds.</summary>
        private static Dictionary<string, HeldLock> LetGo(Dictionary<string, HeldLock> state, IReadOnlyList<LockObject>? released)
        {
            foreach (var exit in released ?? [])
            {
                if (!state.Remove(exit.Key) && !exit.NamesObject)
                    state.Clear();
            }
            return state;
        }

        private static Dictionary<string, HeldLock> Copy(IReadOnlyDictionary<string, HeldLock> state) =>
            new(state, StringComparer.Ordinal);

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
