namespace CacheDetective.Graph;

using CacheDetective.Caching;

public sealed class CacheGraph
{
    private readonly List<(string Solution, CacheKey Key)> _cacheKeySites = [];
    private readonly List<(string Solution, CacheKey Key)> _cacheKeyObservations = [];
    private readonly List<(string Solution, Table Table)> _tableSites = [];
    private readonly List<Handler> _handlerSites = [];
    private readonly List<(string Solution, GraphEdge Edge)> _edgeSites = [];
    private readonly List<Unresolved> _unresolved = [];
    private readonly List<UnresolvedOrigin> _unresolvedOrigins = [];
    private readonly List<(string Solution, CacheOperation Operation)> _cacheOperations = [];
    private int _nextUnresolvedId = 1;

    public IReadOnlyList<CacheKey> CacheKeys => BuildCacheKeys();

    public IReadOnlyList<Table> Tables => BuildTables();

    public IReadOnlyList<Handler> Handlers => BuildHandlers();

    public IReadOnlyList<GraphEdge> Edges => BuildEdges();

    public IReadOnlyList<Unresolved> Unresolved => _unresolved;

    public IReadOnlyList<CacheOperation> CacheOperations => BuildCacheOperations();

    public CacheKey AddCacheKey(string solution, CacheKey key)
    {
        _cacheKeySites.Add((solution, key));
        return FindCacheKey(key.Template, key.Store);
    }

    public CacheKey AddCacheKeyObservation(string solution, CacheKey key)
    {
        _cacheKeyObservations.Add((solution, key));
        return FindCacheKey(key.Template, key.Store);
    }

    public Table AddTable(string solution, Table table)
    {
        _tableSites.Add((solution, table));
        return FindTable(table.Name);
    }

    public Handler AddHandler(Handler handler)
    {
        _handlerSites.Add(handler);
        return FindHandler(handler.Solution, handler.Symbol);
    }

    public GraphEdge AddEdge(GraphEdge edge)
    {
        var solution = ((Handler)edge.From).Solution;
        AddHandler((Handler)edge.From);

        switch (edge)
        {
            case Caches caches:
                AddCacheKey(solution, (CacheKey)caches.To);
                break;
            case Reads { To: CacheKey key }:
                AddCacheKeyObservation(solution, key);
                break;
            case Invalidates { To: CacheKey key }:
                AddCacheKeyObservation(solution, key);
                break;
            case GraphEdge { To: Table table }:
                AddTable(solution, table);
                break;
            case Calls { To: Handler handler }:
                AddHandler(handler);
                break;
        }

        _edgeSites.Add((solution, edge));
        return BuildEdge(edge);
    }

    public CacheOperation AddCacheOperation(CacheOperation operation)
    {
        _cacheOperations.Add((operation.Handler.Solution, operation));
        return operation;
    }

    public Unresolved AddUnresolved(UnresolvedKind kind, string solution, string file, int line,
                                    string snippet, string reason)
    {
        var unresolved = new Unresolved(_nextUnresolvedId++, kind, solution, file, line, snippet, reason);
        _unresolved.Add(unresolved);
        return unresolved;
    }

    public Unresolved AddUnresolved(UnresolvedKind kind, Handler handler, string file, int line,
                                    string snippet, string reason)
    {
        var unresolved = AddUnresolved(kind, handler.Solution, file, line, snippet, reason);
        _unresolvedOrigins.Add(new UnresolvedOrigin(unresolved.Id, handler.Solution, handler.Symbol));
        return unresolved;
    }

    internal IReadOnlyList<Unresolved> GetUnresolvedForHandlers(IEnumerable<Handler> handlers,
                                                                 params UnresolvedKind[] kinds)
    {
        var handlerIds = handlers.Select(handler => (handler.Solution, handler.Symbol)).ToHashSet();
        var kindSet = kinds.ToHashSet();
        var unresolvedIds = _unresolvedOrigins
            .Where(origin => handlerIds.Contains((origin.Solution, origin.HandlerSymbol)))
            .Select(origin => origin.UnresolvedId)
            .ToHashSet();
        return _unresolved.Where(item => unresolvedIds.Contains(item.Id) && kindSet.Contains(item.Kind))
            .ToArray();
    }

    internal void SetCacheKeyRole(string template, string store, string role)
    {
        for (var index = 0; index < _cacheKeySites.Count; index++)
        {
            var site = _cacheKeySites[index];
            if (site.Key.Template == template && site.Key.Store == store)
                _cacheKeySites[index] = (site.Solution, WithRole(site.Key, role));
        }

        for (var index = 0; index < _cacheKeyObservations.Count; index++)
        {
            var site = _cacheKeyObservations[index];
            if (site.Key.Template == template && site.Key.Store == store)
                _cacheKeyObservations[index] = (site.Solution, WithRole(site.Key, role));
        }
    }

    public void ReplaceSolution(string solution, CacheGraph replacement)
    {
        if (ReferenceEquals(this, replacement))
        {
            return;
        }

        _cacheKeySites.RemoveAll(site => site.Solution == solution);
        _cacheKeyObservations.RemoveAll(site => site.Solution == solution);
        _tableSites.RemoveAll(site => site.Solution == solution);
        _handlerSites.RemoveAll(handler => handler.Solution == solution);
        _edgeSites.RemoveAll(site => site.Solution == solution);
        _unresolved.RemoveAll(item => item.Solution == solution);
        _unresolvedOrigins.RemoveAll(origin => origin.Solution == solution);
        _cacheOperations.RemoveAll(site => site.Solution == solution);

        _cacheKeySites.AddRange(replacement._cacheKeySites.Where(site => site.Solution == solution));
        _cacheKeyObservations.AddRange(replacement._cacheKeyObservations.Where(site => site.Solution == solution));
        _tableSites.AddRange(replacement._tableSites.Where(site => site.Solution == solution));
        _handlerSites.AddRange(replacement._handlerSites.Where(handler => handler.Solution == solution));
        _edgeSites.AddRange(replacement._edgeSites.Where(site => site.Solution == solution));
        _cacheOperations.AddRange(replacement._cacheOperations.Where(site => site.Solution == solution));

        foreach (var item in replacement._unresolved.Where(item => item.Solution == solution))
        {
            var origin = replacement._unresolvedOrigins.FirstOrDefault(candidate =>
                candidate.UnresolvedId == item.Id);
            if (origin is null)
            {
                AddUnresolved(item.Kind, item.Solution, item.File, item.Line, item.Snippet, item.Reason);
            }
            else
            {
                var handler = replacement.FindHandler(origin.Solution, origin.HandlerSymbol);
                AddUnresolved(item.Kind, handler, item.File, item.Line, item.Snippet, item.Reason);
            }
        }
    }

    private IReadOnlyList<CacheKey> BuildCacheKeys() =>
        _cacheKeySites.Concat(_cacheKeyObservations)
            .GroupBy(site => (site.Key.Template, site.Key.Store))
            .Select(group =>
            {
                var setSites = _cacheKeySites.Where(site => site.Key.Template == group.Key.Template &&
                                                            site.Key.Store == group.Key.Store)
                                             .Select(site => site.Key)
                                             .ToArray();
                var sites = setSites.Length > 0 ? setSites : group.Select(site => site.Key).ToArray();
                var tagsAll = sites.Select(site => site.TagsAny.AsEnumerable())
                                   .Aggregate((left, right) => left.Intersect(right, StringComparer.Ordinal));
                var tagsAny = sites.SelectMany(site => site.TagsAny);
                var ttl = sites.Any(site => site.Ttl is null)
                    ? null
                    : sites.Max(site => site.Ttl);
                var role = sites.Select(site => site.Role).FirstOrDefault(role => role is not null);

                return new CacheKey(group.Key.Template, group.Key.Store, ttl, tagsAll, tagsAny, role);
            })
            .ToArray();

    private IReadOnlyList<CacheOperation> BuildCacheOperations() =>
        _cacheOperations.Select(site =>
        {
            var operation = site.Operation;
            return operation with
            {
                Handler = FindHandler(operation.Handler.Solution, operation.Handler.Symbol),
                Key = FindCacheKey(operation.Key.Template, operation.Key.Store)
            };
        }).ToArray();

    private IReadOnlyList<Table> BuildTables() =>
        _tableSites.GroupBy(site => site.Table.Name, StringComparer.Ordinal)
                   .Select(group => group.Select(site => site.Table)
                                         .FirstOrDefault(table => table.Database is not null) ?? group.First().Table)
                   .ToArray();

    private IReadOnlyList<Handler> BuildHandlers() =>
        _handlerSites.GroupBy(handler => (handler.Solution, handler.Symbol))
                     .Select(group => group.First())
                     .ToArray();

    private IReadOnlyList<GraphEdge> BuildEdges() => _edgeSites.Select(site => BuildEdge(site.Edge)).ToArray();

    private GraphEdge BuildEdge(GraphEdge edge)
    {
        var from = (Handler)edge.From;
        var canonicalFrom = FindHandler(from.Solution, from.Symbol);

        return edge switch
        {
            Reads { To: Table table } reads => new Reads(canonicalFrom, FindTable(table.Name), reads.Confidence,
                                                         reads.Evidence),
            Reads { To: CacheKey key } reads => new Reads(canonicalFrom,
                                                           FindCacheKey(key.Template, key.Store), reads.Confidence,
                                                           reads.Evidence),
            Writes writes => new Writes(canonicalFrom, FindTable(((Table)writes.To).Name), writes.Confidence,
                                         writes.Evidence),
            Caches caches => new Caches(canonicalFrom,
                                         FindCacheKey(((CacheKey)caches.To).Template, ((CacheKey)caches.To).Store),
                                         caches.Confidence, caches.Evidence, caches.IsConditionalSet),
            Invalidates invalidates => new Invalidates(
                canonicalFrom,
                FindCacheKey(((CacheKey)invalidates.To).Template, ((CacheKey)invalidates.To).Store),
                invalidates.Confidence, invalidates.Evidence, invalidates.Semantic),
            Calls calls => new Calls(canonicalFrom,
                                     FindHandler(((Handler)calls.To).Solution, ((Handler)calls.To).Symbol),
                                     calls.Confidence, calls.Evidence),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }

    private CacheKey FindCacheKey(string template, string store) =>
        BuildCacheKeys().Single(key => key.Template == template && key.Store == store);

    private Table FindTable(string name) => BuildTables().Single(table => table.Name == name);

    private Handler FindHandler(string solution, string symbol) =>
        BuildHandlers().Single(handler => handler.Solution == solution && handler.Symbol == symbol);

    private static CacheKey WithRole(CacheKey key, string role) =>
        new(key.Template, key.Store, key.Ttl, key.TagsAll, key.TagsAny, role);

    private sealed record UnresolvedOrigin(int UnresolvedId, string Solution, string HandlerSymbol);
}
