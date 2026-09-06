using CacheDetective.Graph;

namespace CacheDetective.Caching;

/// <summary>
/// Everything classifying a key needs, gathered in one pass over the graph. Built once and read for every
/// key: without it each key rescans the operations for its store signal, regroups every <c>Calls</c> edge
/// to walk the call graph, and rescans every edge to ask whether anything it reaches touches data — three
/// full scans per key, which is quadratic in a graph whose keys and handlers grow together.
/// </summary>
public sealed class CacheRoleIndex
{
    private readonly Dictionary<(string Solution, string Symbol), List<GraphEdge>> _edgesByFrom = [];
    private readonly Dictionary<(string Solution, string Symbol), Handler[]> _calls = [];
    private readonly HashSet<(string Solution, string Symbol)> _dataAccess = [];
    private readonly Dictionary<(string Solution, string Symbol), Dictionary<(string Solution, string Symbol), Handler>> _reachable = [];
    private readonly Dictionary<(string Template, string Store), List<CacheOperation>> _operationsByKey = [];
    private readonly Dictionary<(string Template, string Store), List<Handler>> _cachingHandlersByKey = [];
    private readonly HashSet<(string Template, string Store)> _conditionalSetKeys = [];

    // The two answers a key actually wants from a handler's reachable set, computed for every handler at
    // once rather than per handler. Memoising the full reachable set per handler was still Θ(N²) in both
    // time and memory when N callers share a helper: the graph is linear, but each caller's own set
    // contains everything below the helper, so N sets of size N are built and kept.
    private readonly HashSet<(string Solution, string Symbol)> _reachesData = [];
    private readonly HashSet<(string Solution, string Symbol)> _reachesBlockers = [];
    private readonly Dictionary<(string Solution, string Symbol), Handler> _handlersById = [];
    private readonly Dictionary<(string Solution, string Symbol), IReadOnlyList<Unresolved>> _blockers = [];
    private bool _reachabilityBuilt;
    private readonly CacheGraph _graph;

    public CacheRoleIndex(CacheGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        foreach (var operation in graph.CacheOperations)
        {
            var id = (operation.Key.Template, operation.Key.Store);
            if (!_operationsByKey.TryGetValue(id, out var operations))
            {
                operations = [];
                _operationsByKey[id] = operations;
            }
            operations.Add(operation);
        }

        foreach (var edge in graph.Edges)
        {
            if (edge is Caches { To: CacheKey cached } caches && edge.From is Handler caching)
            {
                var id = (cached.Template, cached.Store);
                if (!_cachingHandlersByKey.TryGetValue(id, out var handlers))
                {
                    handlers = [];
                    _cachingHandlersByKey[id] = handlers;
                }
                handlers.Add(caching);
                if (caches.IsConditionalSet)
                    _conditionalSetKeys.Add(id);
            }

            if (edge.From is not Handler from)
                continue;

            var source = (from.Solution, from.Symbol);
            _handlersById[source] = from;
            if (!_edgesByFrom.TryGetValue(source, out var outgoing))
            {
                outgoing = [];
                _edgesByFrom[source] = outgoing;
            }
            outgoing.Add(edge);
        }

        foreach (var (source, outgoing) in _edgesByFrom)
        {
            _calls[source] = outgoing.OfType<Calls>().Select(edge => edge.To).OfType<Handler>().ToArray();
            foreach (var target in _calls[source])
                _handlersById[(target.Solution, target.Symbol)] = target;
            // Calling a stored procedure is reaching for data whether or not the database is indexed,
            // which is why the role survives a later index_database.
            if (outgoing.Any(edge => edge is Reads { To: Table or ExternalSource } or Calls { To: StoredProcedure }))
                _dataAccess.Add(source);
        }
    }

    internal IReadOnlyList<CacheOperation> Operations(CacheKey key) =>
        _operationsByKey.TryGetValue((key.Template, key.Store), out var operations) ? operations : [];

    internal IReadOnlyList<Handler> CachingHandlers(CacheKey key) =>
        _cachingHandlersByKey.TryGetValue((key.Template, key.Store), out var handlers) ? handlers : [];

    internal bool HasConditionalSet(CacheKey key) => _conditionalSetKeys.Contains((key.Template, key.Store));

    internal bool TouchesData((string Solution, string Symbol) handler) => _dataAccess.Contains(handler);

    /// <summary>
    /// How many handlers the reachability walks have visited since the index was built. It is the work the
    /// classification actually does, counted rather than timed: a scale test that divides one wall-clock
    /// reading by another is measuring the JIT, the collector and whatever else the machine is doing, and
    /// this project had one flicker in the gate for exactly that reason.
    /// </summary>
    public long ReachabilityVisits { get; private set; }

    /// <summary>How many reachable sets have been computed. Memoisation is what keeps this at one per
    /// handler however many keys ask about it.</summary>
    public long ReachabilityWalks { get; private set; }

    /// <summary>Whether anything this handler reaches touches data. Answered for every handler in one
    /// linear pass, not once per handler that asks.</summary>
    internal bool ReachesData(Handler start)
    {
        EnsureReachability();
        return _reachesData.Contains((start.Solution, start.Symbol));
    }

    /// <summary>
    /// Whether anything this handler reaches carries an unresolved call or statement. It is the question a
    /// key actually asks — a key with no blockers anywhere is a store and never needs the list — so it is
    /// answered as a boolean from the same pass, and the list itself is gathered only for the keys that
    /// came out <c>unknown</c>. Memoising a list per handler was the other half of the Θ(N²) memory.
    /// </summary>
    internal bool ReachesBlockers(Handler start)
    {
        EnsureReachability();
        return _reachesBlockers.Contains((start.Solution, start.Symbol));
    }

    /// <summary>
    /// Both reachability answers for every handler at once, by walking the call graph <em>backwards</em>
    /// from the handlers that carry the thing being looked for. Each handler enters a queue at most once
    /// and each call edge is followed at most once — when the handler it points at is dequeued — so the
    /// pass is linear in the graph and needs no per-handler set to be built or kept.
    /// <para>Walking backwards is also what makes cycles a non-issue: a handler is added to the answer the
    /// first time it is reached and never revisited, so a call cycle terminates without any special case,
    /// no condensation, and no node held back from the memo until some root completes.</para>
    /// </summary>
    private void EnsureReachability()
    {
        if (_reachabilityBuilt)
            return;

        _reachabilityBuilt = true;
        ReachabilityWalks++;
        var callers = new Dictionary<(string Solution, string Symbol), List<(string Solution, string Symbol)>>();
        foreach (var (source, targets) in _calls)
        {
            foreach (var target in targets)
            {
                var id = (target.Solution, target.Symbol);
                if (!callers.TryGetValue(id, out var incoming))
                {
                    incoming = [];
                    callers[id] = incoming;
                }
                incoming.Add(source);
            }
        }

        Propagate(_reachesData, TouchesData, callers);
        Propagate(_reachesBlockers, OwnsBlockers, callers);
    }

    /// <summary>Seeds the answer with every handler that carries the thing itself, then hands it up to
    /// their callers until nothing new is added.</summary>
    private void Propagate(HashSet<(string Solution, string Symbol)> answer,
                           Func<(string Solution, string Symbol), bool> carries,
                           IReadOnlyDictionary<(string Solution, string Symbol), List<(string Solution, string Symbol)>> callers)
    {
        var pending = new Queue<(string Solution, string Symbol)>();
        foreach (var id in _handlersById.Keys)
        {
            ReachabilityVisits++;
            if (carries(id) && answer.Add(id))
                pending.Enqueue(id);
        }

        while (pending.TryDequeue(out var handler))
        {
            ReachabilityVisits++;
            if (!callers.TryGetValue(handler, out var incoming))
                continue;

            foreach (var caller in incoming)
            {
                if (answer.Add(caller))
                    pending.Enqueue(caller);
            }
        }
    }

    private bool OwnsBlockers((string Solution, string Symbol) handler) =>
        _handlersById.TryGetValue(handler, out var found) &&
        _graph.GetUnresolvedForHandlers([found], UnresolvedKind.Call, UnresolvedKind.Sql).Count > 0;

    /// <summary>The unresolved rows on everything this handler reaches, memoised. Asked only for a key
    /// that <see cref="ReachesBlockers"/> already said is blocked, so the walk it needs is paid for by the
    /// keys that report it and not by every key in the graph.</summary>
    internal IReadOnlyList<Unresolved> BlockersFrom(Handler start)
    {
        var id = (start.Solution, start.Symbol);
        if (_blockers.TryGetValue(id, out var cached))
            return cached;

        var blockers = _graph.GetUnresolvedForHandlers(ReachableFrom(start).Values, UnresolvedKind.Call, UnresolvedKind.Sql);
        _blockers[id] = blockers;
        return blockers;
    }

    /// <summary>The handlers one handler can reach, memoised: a key with the same caching handler as
    /// another key does not walk the call graph twice.</summary>
    internal IReadOnlyDictionary<(string Solution, string Symbol), Handler> ReachableFrom(Handler start)
    {
        var id = (start.Solution, start.Symbol);
        if (_reachable.TryGetValue(id, out var cached))
            return cached;

        ReachabilityWalks++;
        var reachable = new Dictionary<(string Solution, string Symbol), Handler>();
        var pending = new Queue<Handler>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var handler))
        {
            ReachabilityVisits++;
            var handlerId = (handler.Solution, handler.Symbol);
            if (!reachable.TryAdd(handlerId, handler) || !_calls.TryGetValue(handlerId, out var targets))
                continue;

            foreach (var target in targets)
                pending.Enqueue(target);
        }

        _reachable[id] = reachable;
        return reachable;
    }
}

public sealed class CacheRoleClassifier
{
    private static readonly string[] STORE_PREFIXES =
    [
        "session:",
        "lock:",
        "idempotency:",
        "ratelimit:",
        "token:"
    ];

    public void Classify(CacheGraph graph, string solutionName) => Classify(graph, solutionName, new CacheRoleIndex(graph));

    /// <param name="index">The index to classify through, so that a caller measuring the work can read its
    /// counters afterwards.</param>
    public void Classify(CacheGraph graph, string solutionName, CacheRoleIndex index)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var keys = graph.CacheKeys.ToArray();
        foreach (var key in keys)
        {
            var classification = ClassifyKey(graph, key, index);
            if (classification.Blockers.Count == 0)
            {
                continue;
            }

            var blocker = classification.Blockers[0];
            var reasons =
                string.Join("; ", classification.Blockers.Select(item => $"{item.Kind.ToString().ToLowerInvariant()}: {item.Reason}")
                                                   .Distinct(StringComparer.Ordinal));
            var role = graph.AddUnresolved(UnresolvedKind.Role, solutionName, blocker.Site, key.Template,
                                           $"Role classification was blocked by incomplete analysis: {reasons}");
            graph.AddRoleBlockers(role.Id, key.Template, key.Store, classification.Blockers.Select(item => item.Id).ToArray());
        }
    }

    public (string Role, IReadOnlyList<Unresolved> Blockers) ClassifyKey(CacheGraph graph, CacheKey key) =>
        ClassifyKey(graph, key, new CacheRoleIndex(graph));

    /// <param name="solution">The solution whose opinion is being recorded, or <c>null</c> for every
    /// solution that declares the key. A re-classification after one solution was replaced names it, so
    /// that the answer does not depend on the order the solutions were indexed in.</param>
    public (string Role, IReadOnlyList<Unresolved> Blockers) ClassifyKey(CacheGraph graph, CacheKey key, CacheRoleIndex index,
                                                                          string? solution = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(index);
        if (HasStoreSignal(key, index))
        {
            graph.SetCacheKeyRole(key.Template, key.Store, "store", solution);
            return ("store", []);
        }

        // Each caching handler is asked its own memoised question, so a key costs the number of handlers
        // that cache it rather than the size of everything they can reach.
        var caching = index.CachingHandlers(key);
        if (caching.Any(index.ReachesData))
        {
            graph.SetCacheKeyRole(key.Template, key.Store, "cache", solution);
            return ("cache", []);
        }

        // Whether the key is blocked is a boolean the index already holds for every handler; the rows
        // themselves are gathered only once the answer is known to be unknown, so a graph of stores does
        // not build a blocker list per key to find every one of them empty.
        if (!caching.Any(index.ReachesBlockers))
        {
            graph.SetCacheKeyRole(key.Template, key.Store, "store", solution);
            return ("store", []);
        }

        var blockers = caching.SelectMany(index.BlockersFrom)
                              .DistinctBy(blocker => blocker.Id)
                              .OrderBy(blocker => blocker.Id)
                              .ToArray();
        graph.SetCacheKeyRole(key.Template, key.Store, "unknown", solution);
        return ("unknown", blockers);
    }

    private static bool HasStoreSignal(CacheKey key, CacheRoleIndex index)
    {
        if (STORE_PREFIXES.Any(prefix => key.Template.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (index.Operations(key).Any(operation => operation.Semantic is CacheSemantic.Increment or CacheSemantic.Expire or CacheSemantic.Lock ||
                                                   operation.IsConditionalSet))
        {
            return true;
        }

        return index.HasConditionalSet(key);
    }
}
