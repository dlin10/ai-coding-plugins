using System.Collections;
using System.Runtime.CompilerServices;
using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Accesses;

/// <summary>
/// Records each access of a scope once with the executions that make it so (ADR 0038). Two accesses are one when they agree
/// on everything but what <see cref="AccessVariant"/> holds and the execution: the site, instance, resource and operation, the held
/// protections, conditions, construction-locality, freshness, iteration indexing, selector term, ownership, library-internality, gap
/// checks and labels. Every execution of it keeps its id, its variant and its place in the order the executions made their accesses, so
/// pairing can still answer each pair of executions as it did, in the order it did.
/// </summary>
internal sealed class AccessFolding
{
    private readonly Dictionary<Access, int> _index = new(AccessKey.INSTANCE);
    private readonly List<Access> _firsts = [];
    private readonly List<List<AccessExecution>> _executions = [];
    private readonly SequencePool<string> _paths = new();
    private readonly SequencePool<SpawnSiteLocation> _spawns = new();
    private readonly SequencePool<ReadSource> _reads = new();
    private readonly Dictionary<AccessRoot, AccessRoot> _roots = [];
    private readonly Dictionary<AccessVariant, AccessVariant> _variants = new(VariantKey.INSTANCE);

    /// <summary>The pooled copy of each list and root one execution's accesses share, by the object met: the execution's own, so it is
    /// cleared after each.</summary>
    private readonly Dictionary<object, object> _met = new(ReferenceEqualityComparer.Instance);

    private int? _count;

    /// <summary>How many distinct accesses the scope has so far.</summary>
    internal int Count => _count ?? _firsts.Count;

    /// <summary>How many executions they have: every access the executions made.</summary>
    internal int ExecutionCount { get; private set; }

    /// <summary>Adds the accesses one execution made, in the order it made them.</summary>
    /// <param name="accesses">The execution's accesses.</param>
    internal void Add(IReadOnlyList<Access> accesses)
    {
        WorkCancellation.Check();
        foreach (var access in accesses)
        {
            WorkCancellation.Check();
            var execution = new AccessExecution(access.ExecutionId, Variant(access), ExecutionCount++);
            if (_index.TryGetValue(access, out var at))
            {
                _executions[at].Add(execution);
                continue;
            }

            _index.Add(access, _firsts.Count);
            _firsts.Add(access);
            _executions.Add([execution]);
        }

        _met.Clear();
    }

    /// <summary>The scope's accesses, each with its executions, in the order their first executions made them; an access's own
    /// execution, root and path are its first execution's. The folding keeps only its counts after.</summary>
    internal IReadOnlyList<Access> Accesses()
    {
        var accesses = new Access[_firsts.Count];
        for (var at = 0; at < accesses.Length; at++)
        {
            WorkCancellation.Check();
            var executions = _executions[at].ToArray();
            accesses[at] = _firsts[at].View(executions[0]) with { Executions = executions };
            _executions[at] = [];
        }

        _count = _firsts.Count;
        _index.Clear();
        _firsts.Clear();
        _executions.Clear();
        _variants.Clear();
        _roots.Clear();
        return accesses;
    }

    private AccessVariant Variant(Access access)
    {
        var variant = new AccessVariant(Root(access.Root), Root(access.PathRoot), Pooled(access.CallPath, _paths), Pooled(access.SpawnSites, _spawns),
                                        access.CodeFlow, Pooled(access.ReadSources, _reads));
        if (_variants.TryGetValue(variant, out var known))
            return known;
        _variants.Add(variant, variant);
        return variant;
    }

    private AccessRoot Root(AccessRoot root)
    {
        if (_met.TryGetValue(root, out var met))
            return (AccessRoot)met;
        if (!_roots.TryGetValue(root, out var pooled))
            _roots.Add(root, pooled = root);
        _met.Add(root, pooled);
        return pooled;
    }

    private IReadOnlyList<T> Pooled<T>(IReadOnlyList<T> values, SequencePool<T> pool) where T : notnull
    {
        if (values.Count == 0)
            return [];
        if (_met.TryGetValue(values, out var met))
            return (IReadOnlyList<T>)met;
        var pooled = pool.Get(values);
        _met.Add(values, pooled);
        return pooled;
    }

    /// <summary>Two variants are one when they hold the same pooled root, path, steps and loads: the code flow is pooled by the
    /// collectors (issue 454), the rest by <see cref="AccessFolding"/>.</summary>
    private sealed class VariantKey : IEqualityComparer<AccessVariant>
    {
        internal static readonly VariantKey INSTANCE = new();

        public bool Equals(AccessVariant? x, AccessVariant? y) =>
            ReferenceEquals(x, y) || x is not null && y is not null && ReferenceEquals(x.Root, y.Root) && ReferenceEquals(x.PathRoot, y.PathRoot) &&
            ReferenceEquals(x.CallPath, y.CallPath) && ReferenceEquals(x.SpawnSites, y.SpawnSites) && ReferenceEquals(x.CodeFlow, y.CodeFlow) &&
            ReferenceEquals(x.ReadSources, y.ReadSources);

        // bounded: hashes the variant's six references once; RuntimeHelpers.GetHashCode is no call of this one.
        public int GetHashCode(AccessVariant obj) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Root), RuntimeHelpers.GetHashCode(obj.PathRoot), RuntimeHelpers.GetHashCode(obj.CallPath),
                             RuntimeHelpers.GetHashCode(obj.SpawnSites), RuntimeHelpers.GetHashCode(obj.CodeFlow), RuntimeHelpers.GetHashCode(obj.ReadSources));
    }

    /// <summary>What two accesses must agree on to be one: every field but the execution and <see cref="AccessVariant"/>'s, lists and
    /// maps by their contents.</summary>
    internal sealed class AccessKey : IEqualityComparer<Access>
    {
        internal static readonly AccessKey INSTANCE = new();

        public bool Equals(Access? x, Access? y)
        {
            WorkCancellation.Check();
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;
            return x.OperationId == y.OperationId && x.Operation == y.Operation && x.IsConstructionLocal == y.IsConstructionLocal &&
                   x.IsReferenceAccess == y.IsReferenceAccess && x.IsIterationIndexed == y.IsIterationIndexed && x.IsFresh == y.IsFresh &&
                   x.IsLibraryInternal == y.IsLibraryInternal && x.WritesWholeStruct == y.WritesWholeStruct &&
                   x.UnseenWriteOperation == y.UnseenWriteOperation && x.Ownership == y.Ownership &&
                   string.Equals(x.BodyId, y.BodyId, StringComparison.Ordinal) && string.Equals(x.InstanceId, y.InstanceId, StringComparison.Ordinal) &&
                   string.Equals(x.Symbol, y.Symbol, StringComparison.Ordinal) && x.Source == y.Source && SameResource(x.Resource, y.Resource) &&
                   Same(x.HeldProtection, y.HeldProtection) && Same(x.HeldProtectionIds, y.HeldProtectionIds) &&
                   SameHeld(x.HeldProtections, y.HeldProtections) && Same(x.BindingEvidence, y.BindingEvidence) &&
                   Same(x.Uncertainties, y.Uncertainties) && Same(x.OwnershipEvidence, y.OwnershipEvidence) && Same(x.Conditions, y.Conditions) &&
                   x.SelectorTerm == y.SelectorTerm && Same(x.GapChecks, y.GapChecks) && Same(x.DecidingCalls, y.DecidingCalls);
        }

        public int GetHashCode(Access obj) =>
            HashCode.Combine(obj.BodyId, obj.OperationId, obj.InstanceId, obj.Operation, obj.Resource.RegionId, obj.Resource.Member.Name,
                             obj.HeldProtectionIds.Count, obj.IsConstructionLocal);

        private static bool SameResource(AccessResource x, AccessResource y) =>
            ReferenceEquals(x, y) ||
            string.Equals(x.Assembly, y.Assembly, StringComparison.Ordinal) && string.Equals(x.Scope, y.Scope, StringComparison.Ordinal) &&
            string.Equals(x.Region, y.Region, StringComparison.Ordinal) && string.Equals(x.RegionId, y.RegionId, StringComparison.Ordinal) &&
            string.Equals(x.RegionKey, y.RegionKey, StringComparison.Ordinal) && string.Equals(x.CollectionId, y.CollectionId, StringComparison.Ordinal) &&
            x.IsWildcard == y.IsWildcard && x.Member == y.Member && x.Selector == y.Selector && Same(x.AccessPath, y.AccessPath) &&
            Same(x.StructPath, y.StructPath) && SameStorage(x.Storage, y.Storage) &&
            (ReferenceEquals(x.WildcardExclusions, y.WildcardExclusions) || x.WildcardExclusions.SetEquals(y.WildcardExclusions));

        private static bool SameStorage(StoragePath? x, StoragePath? y) =>
            ReferenceEquals(x, y) ||
            x is not null && y is not null && x.Cell == y.Cell && string.Equals(x.Region, y.Region, StringComparison.Ordinal) &&
            string.Equals(x.Slot, y.Slot, StringComparison.Ordinal) && Same(x.Fields, y.Fields);

        private static bool SameHeld(IReadOnlyDictionary<string, IReadOnlyList<HeldProtection>> x,
                                     IReadOnlyDictionary<string, IReadOnlyList<HeldProtection>> y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x.Count != y.Count)
                return false;
            foreach (var (id, held) in x)
            {
                WorkCancellation.Check();
                if (!y.TryGetValue(id, out var other) || !Same(held, other))
                    return false;
            }

            return true;
        }

        private static bool Same<T>(IReadOnlyList<T> x, IReadOnlyList<T> y) => ReferenceEquals(x, y) || x.Count == y.Count && x.SequenceEqual(y);
    }
}

/// <summary>Every execution of the accesses of one or more scopes as an access of its own (<see cref="Access.View"/>), each scope's in the order its
/// executions made them (ADR 0038): what the collection was before accesses were recorded once. A view is made once, when first read; an
/// access built for one execution alone is its own view.</summary>
public sealed class AccessExecutionList : IReadOnlyList<Access>
{
    private readonly IReadOnlyList<Access>[] _scopes;
    private readonly int[] _starts;
    private (int Access, int Execution)[][]? _order;
    private Access?[][]? _views;

    /// <summary>The executions of each scope's accesses, scope after scope.</summary>
    /// <param name="scopes">Each scope's accesses: recorded once with their executions, or each built for one execution alone.</param>
    public AccessExecutionList(IEnumerable<IReadOnlyList<Access>> scopes)
    {
        _scopes = scopes.ToArray();
        _starts = new int[_scopes.Length];
        var count = 0;
        for (var at = 0; at < _scopes.Length; at++)
        {
            WorkCancellation.Check();
            _starts[at] = count;
            count += _scopes[at].Sum(access => access.ExecutionCount);
        }

        Count = count;
    }

    public int Count { get; }

    public Access this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            var scope = Array.FindLastIndex(_starts, start => start <= index);
            return View(scope, index - _starts[scope]);
        }
    }

    public IEnumerator<Access> GetEnumerator()
    {
        for (var scope = 0; scope < _scopes.Length; scope++)
        {
            WorkCancellation.Check();
            var count = (scope + 1 < _starts.Length ? _starts[scope + 1] : Count) - _starts[scope];
            for (var at = 0; at < count; at++)
            {
                WorkCancellation.Check();
                yield return View(scope, at);
            }
        }
    }

    // bounded: forwards once to the generic enumerator.
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // bounded: one view, made once; checks nothing, so a work checks as often however much of the list was read before it.
    private Access View(int scope, int at)
    {
        _order ??= _scopes.Select(Order).ToArray();
        _views ??= _scopes.Select((_, index) => new Access?[_order[index].Length]).ToArray();
        if (_views[scope][at] is { } view)
            return view;
        var (access, execution) = _order[scope][at];
        var folded = _scopes[scope][access];
        return _views[scope][at] = folded.Executions.Count == 0 ? folded : folded.View(folded.Executions[execution]);
    }

    /// <summary>A scope's executions of accesses in collection order: by their positions, an access without executions where it stands
    /// in the list.</summary>
    /// <param name="accesses">The scope's accesses.</param>
    private static (int Access, int Execution)[] Order(IReadOnlyList<Access> accesses)
    {
        var order = new List<(int Position, int Access, int Execution)>();
        // bounded: one pass, made once when the list is first read; it checks nothing, as View says.
        for (var index = 0; index < accesses.Count; index++)
        {
            var access = accesses[index];
            if (access.Executions.Count == 0)
                order.Add((index, index, 0));
            // bounded: the access's executions, in the same one pass.
            for (var at = 0; at < access.Executions.Count; at++)
            {
                order.Add((access.Executions[at].Position, index, at));
            }
        }

        return order.OrderBy(item => item.Position).Select(item => (item.Access, item.Execution)).ToArray();
    }
}
