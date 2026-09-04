namespace CacheDetective.Graph;

using Caching;

public sealed class CacheGraph
{
    private readonly List<(GraphOrigin Origin, CacheKey Key)> _cacheKeySites = [];
    private readonly List<(GraphOrigin Origin, CacheKey Key)> _cacheKeyObservations = [];
    private readonly List<(GraphOrigin Origin, Table Table)> _tableSites = [];
    private readonly List<(GraphOrigin Origin, StoredProcedure Procedure)> _procedureSites = [];
    private readonly List<(GraphOrigin Origin, Trigger Trigger)> _triggerSites = [];
    private readonly List<(GraphOrigin Origin, View View)> _viewSites = [];
    private readonly List<Handler> _handlerSites = [];
    private readonly List<(GraphOrigin Origin, GraphEdge Edge)> _edgeSites = [];
    private readonly List<Unresolved> _unresolved = [];
    private readonly List<UnresolvedOrigin> _unresolvedOrigins = [];
    private readonly List<(string Solution, CacheOperation Operation)> _cacheOperations = [];
    private readonly Dictionary<string, int> _derivedUnresolvedIds = new(StringComparer.Ordinal);
    private int _nextUnresolvedId = 1;

    public IReadOnlyList<CacheKey> CacheKeys => BuildCacheKeys();

    public IReadOnlyList<Table> Tables => BuildTables();

    public IReadOnlyList<StoredProcedure> StoredProcedures => BuildProcedures();

    public IReadOnlyList<Trigger> Triggers => BuildTriggers();

    public IReadOnlyList<View> Views => BuildViews();

    public IReadOnlyList<Handler> Handlers => BuildHandlers();

    public IReadOnlyList<GraphEdge> Edges => BuildEdges();

    public IReadOnlyList<Unresolved> Unresolved => _unresolved;

    public IReadOnlyList<CacheOperation> CacheOperations => BuildCacheOperations();

    public CacheKey AddCacheKey(string solution, CacheKey key) =>
        AddCacheKey(GraphOrigin.ForSolution(solution), key);

    public CacheKey AddCacheKeyObservation(string solution, CacheKey key) =>
        AddCacheKeyObservation(GraphOrigin.ForSolution(solution), key);

    public Table AddTable(string solution, Table table) =>
        AddTable(GraphOrigin.ForSolution(solution), table);

    /// <summary>Records a stored procedure the catalogue answered for, edge or no edge. A procedure that
    /// references nothing statically — one whose body is all dynamic SQL — has no edges at all, and
    /// without this the graph could not tell it from a procedure the catalogue does not hold.</summary>
    public StoredProcedure AddStoredProcedure(string database, StoredProcedure procedure)
    {
        _procedureSites.Add((GraphOrigin.ForDatabase(database), procedure));
        return FindProcedure(procedure.Name);
    }

    public Handler AddHandler(Handler handler)
    {
        _handlerSites.Add(handler);
        return FindHandler(handler.Solution, handler.Symbol);
    }

    public GraphEdge AddEdge(GraphEdge edge)
    {
        var origin = OriginOf(edge.From);
        AddVertex(origin, edge.From);

        switch (edge)
        {
            case Caches { To: CacheKey key }:
                AddCacheKey(origin, key);
                break;
            case Reads { To: CacheKey key }:
                AddCacheKeyObservation(origin, key);
                break;
            case Invalidates { To: CacheKey key }:
                AddCacheKeyObservation(origin, key);
                break;
            default:
                AddVertex(origin, edge.To);
                break;
        }

        _edgeSites.Add((origin, edge));
        return BuildEdge(edge);
    }

    public CacheOperation AddCacheOperation(CacheOperation operation)
    {
        _cacheOperations.Add((operation.Handler.Solution, operation));
        return operation;
    }

    public Unresolved AddUnresolved(UnresolvedKind kind, string solution, string file, int line, string snippet, string reason) =>
        AddUnresolved(kind, solution, new Evidence(file, line), snippet, reason);

    public Unresolved AddUnresolved(UnresolvedKind kind, string? solution, Evidence site, string snippet, string reason)
    {
        var unresolved = new Unresolved(_nextUnresolvedId++, kind, solution, site, snippet, reason);
        _unresolved.Add(unresolved);
        return unresolved;
    }

    public Unresolved AddUnresolved(UnresolvedKind kind, Handler handler, string file, int line, string snippet, string reason) =>
        AddUnresolved(kind, handler, new Evidence(file, line), snippet, reason);

    public Unresolved AddUnresolved(UnresolvedKind kind, Handler handler, Evidence site, string snippet, string reason)
    {
        var unresolved = AddUnresolved(kind, handler.Solution, site, snippet, reason);
        _unresolvedOrigins.Add(new UnresolvedOrigin(unresolved.Id, handler.Solution, handler.Symbol));
        return unresolved;
    }

    /// <summary>Reserves an id for a row that is derived on query rather than stored. The id comes from
    /// the same session sequence the stored rows draw on, so the two can never collide, and it is
    /// remembered against the row's identity, so a derived row keeps its id for as long as the session
    /// lasts even as later indexing adds stored rows.</summary>
    internal int GetDerivedUnresolvedId(string identity)
    {
        if (!_derivedUnresolvedIds.TryGetValue(identity, out var id))
        {
            id = _nextUnresolvedId++;
            _derivedUnresolvedIds.Add(identity, id);
        }

        return id;
    }

    internal IReadOnlyList<Unresolved> GetUnresolvedForHandlers(IEnumerable<Handler> handlers, params UnresolvedKind[] kinds)
    {
        var handlerIds = handlers.Select(handler => (handler.Solution, handler.Symbol)).ToHashSet();
        var kindSet = kinds.ToHashSet();
        var unresolvedIds = _unresolvedOrigins.Where(origin => handlerIds.Contains((origin.Solution, origin.HandlerSymbol)))
                                              .Select(origin => origin.UnresolvedId)
                                              .ToHashSet();
        return _unresolved.Where(item => unresolvedIds.Contains(item.Id) && kindSet.Contains(item.Kind)).ToArray();
    }

    internal void SetCacheKeyRole(string template, string store, string role)
    {
        for (var index = 0; index < _cacheKeySites.Count; index++)
        {
            var site = _cacheKeySites[index];
            if (site.Key.Template == template && site.Key.Store == store)
                _cacheKeySites[index] = (site.Origin, WithRole(site.Key, role));
        }

        for (var index = 0; index < _cacheKeyObservations.Count; index++)
        {
            var site = _cacheKeyObservations[index];
            if (site.Key.Template == template && site.Key.Store == store)
                _cacheKeyObservations[index] = (site.Origin, WithRole(site.Key, role));
        }
    }

    public void ReplaceSolution(string solution, CacheGraph replacement) =>
        Replace(origin => origin.Solution == solution, replacement);

    public void ReplaceDatabase(string database, CacheGraph replacement) =>
        Replace(origin => origin.Database == database, replacement);

    private void Replace(Func<GraphOrigin, bool> belongs, CacheGraph replacement)
    {
        if (ReferenceEquals(this, replacement))
        {
            return;
        }

        var replaced = _unresolved.Where(item => belongs(OriginOf(item))).Select(item => item.Id).ToHashSet();

        _cacheKeySites.RemoveAll(site => belongs(site.Origin));
        _cacheKeyObservations.RemoveAll(site => belongs(site.Origin));
        _tableSites.RemoveAll(site => belongs(site.Origin));
        _procedureSites.RemoveAll(site => belongs(site.Origin));
        _triggerSites.RemoveAll(site => belongs(site.Origin));
        _viewSites.RemoveAll(site => belongs(site.Origin));
        _handlerSites.RemoveAll(handler => belongs(OriginOf(handler)));
        _edgeSites.RemoveAll(site => belongs(site.Origin));
        _unresolved.RemoveAll(item => replaced.Contains(item.Id));
        _unresolvedOrigins.RemoveAll(origin => replaced.Contains(origin.UnresolvedId));
        _cacheOperations.RemoveAll(site => belongs(GraphOrigin.ForSolution(site.Solution)));

        _cacheKeySites.AddRange(replacement._cacheKeySites.Where(site => belongs(site.Origin)));
        _cacheKeyObservations.AddRange(replacement._cacheKeyObservations.Where(site => belongs(site.Origin)));
        _tableSites.AddRange(replacement._tableSites.Where(site => belongs(site.Origin)));
        _procedureSites.AddRange(replacement._procedureSites.Where(site => belongs(site.Origin)));
        _triggerSites.AddRange(replacement._triggerSites.Where(site => belongs(site.Origin)));
        _viewSites.AddRange(replacement._viewSites.Where(site => belongs(site.Origin)));
        _handlerSites.AddRange(replacement._handlerSites.Where(handler => belongs(OriginOf(handler))));
        _edgeSites.AddRange(replacement._edgeSites.Where(site => belongs(site.Origin)));
        _cacheOperations.AddRange(replacement._cacheOperations.Where(site => belongs(GraphOrigin.ForSolution(site.Solution))));

        foreach (var item in replacement._unresolved.Where(item => belongs(OriginOf(item))))
        {
            var origin = replacement._unresolvedOrigins.FirstOrDefault(candidate => candidate.UnresolvedId == item.Id);
            if (origin is null)
            {
                AddUnresolved(item.Kind, item.Solution, item.Site, item.Snippet, item.Reason);
            }
            else
            {
                var handler = replacement.FindHandler(origin.Solution, origin.HandlerSymbol);
                AddUnresolved(item.Kind, handler, item.Site, item.Snippet, item.Reason);
            }
        }
    }

    private CacheKey AddCacheKey(GraphOrigin origin, CacheKey key)
    {
        _cacheKeySites.Add((origin, key));
        return FindCacheKey(key.Template, key.Store);
    }

    private CacheKey AddCacheKeyObservation(GraphOrigin origin, CacheKey key)
    {
        _cacheKeyObservations.Add((origin, key));
        return FindCacheKey(key.Template, key.Store);
    }

    private Table AddTable(GraphOrigin origin, Table table)
    {
        _tableSites.Add((origin, table));
        return FindTable(table.Name);
    }

    private void AddVertex(GraphOrigin origin, GraphVertex vertex)
    {
        switch (vertex)
        {
            case Handler handler:
                AddHandler(handler);
                break;
            case Table table:
                AddTable(origin, table);
                break;
            case StoredProcedure procedure:
                _procedureSites.Add((origin, procedure));
                break;
            case Trigger trigger:
                _triggerSites.Add((origin, trigger));
                break;
            case View view:
                _viewSites.Add((origin, view));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vertex));
        }
    }

    private static GraphOrigin OriginOf(GraphVertex vertex) => vertex switch
                                                               {
                                                                   Handler handler => GraphOrigin.ForSolution(handler.Solution),
                                                                   Table table => GraphOrigin.ForDatabase(table.Database),
                                                                   StoredProcedure procedure => GraphOrigin.ForDatabase(procedure.Database),
                                                                   Trigger trigger => GraphOrigin.ForDatabase(trigger.Database),
                                                                   View view => GraphOrigin.ForDatabase(view.Database),
                                                                   _ => throw new ArgumentOutOfRangeException(nameof(vertex))
                                                               };

    private static GraphOrigin OriginOf(Unresolved unresolved) => unresolved.Solution is null
                                                                      ? GraphOrigin.ForDatabase(unresolved.Site.Database)
                                                                      : GraphOrigin.ForSolution(unresolved.Solution);

    private IReadOnlyList<CacheKey> BuildCacheKeys() =>
        _cacheKeySites.Concat(_cacheKeyObservations)
                      .GroupBy(site => (site.Key.Template, site.Key.Store))
                      .Select(group =>
                       {
                           var setSites = _cacheKeySites.Where(site => site.Key.Template == group.Key.Template && site.Key.Store == group.Key.Store)
                                                        .Select(site => site.Key)
                                                        .ToArray();
                           var sites = setSites.Length > 0 ? setSites : group.Select(site => site.Key).ToArray();
                           var tagsAll = sites.Select(site => site.TagsAny.AsEnumerable())
                                              .Aggregate((left, right) => left.Intersect(right, StringComparer.Ordinal));
                           var tagsAny = sites.SelectMany(site => site.TagsAny);
                           var ttl = sites.Any(site => site.Ttl is null) ? null : sites.Max(site => site.Ttl);
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
                         })
                        .ToArray();

    private IReadOnlyList<Table> BuildTables() =>
        _tableSites.GroupBy(site => site.Table.Name, StringComparer.Ordinal)
                   .Select(group => group.Select(site => site.Table).FirstOrDefault(table => table.Database is not null) ?? group.First().Table)
                   .ToArray();

    private IReadOnlyList<StoredProcedure> BuildProcedures() =>
        _procedureSites.GroupBy(site => site.Procedure.Name, StringComparer.Ordinal)
                       .Select(group => group.Select(site => site.Procedure).FirstOrDefault(procedure => procedure.Database is not null) ??
                                        group.First().Procedure)
                       .ToArray();

    private IReadOnlyList<Trigger> BuildTriggers() =>
        _triggerSites.GroupBy(site => site.Trigger.Name, StringComparer.Ordinal)
                     .Select(group => group.Select(site => site.Trigger).FirstOrDefault(trigger => trigger.Database is not null) ?? group.First().Trigger)
                     .ToArray();

    private IReadOnlyList<View> BuildViews() =>
        _viewSites.GroupBy(site => site.View.Name, StringComparer.Ordinal)
                  .Select(group => group.Select(site => site.View).FirstOrDefault(view => view.Database is not null) ?? group.First().View)
                  .ToArray();

    private IReadOnlyList<Handler> BuildHandlers() =>
        _handlerSites.GroupBy(handler => (handler.Solution, handler.Symbol)).Select(group => group.First()).ToArray();

    private IReadOnlyList<GraphEdge> BuildEdges() => _edgeSites.Select(site => BuildEdge(site.Edge)).ToArray();

    private GraphEdge BuildEdge(GraphEdge edge) =>
        edge with { From = Canonical(edge.From), To = Canonical(edge.To) };

    private GraphVertex Canonical(GraphVertex vertex) => vertex switch
                                                         {
                                                             CacheKey key => FindCacheKey(key.Template, key.Store),
                                                             Table table => FindTable(table.Name),
                                                             Handler handler => FindHandler(handler.Solution, handler.Symbol),
                                                             StoredProcedure procedure => FindProcedure(procedure.Name),
                                                             Trigger trigger => FindTrigger(trigger.Name),
                                                             View view => FindView(view.Name),
                                                             _ => throw new ArgumentOutOfRangeException(nameof(vertex))
                                                         };

    private CacheKey FindCacheKey(string template, string store) =>
        BuildCacheKeys().Single(key => key.Template == template && key.Store == store);

    private Table FindTable(string name) => BuildTables().Single(table => table.Name == name);

    private StoredProcedure FindProcedure(string name) =>
        BuildProcedures().Single(procedure => procedure.Name == name);

    private Trigger FindTrigger(string name) => BuildTriggers().Single(trigger => trigger.Name == name);

    private View FindView(string name) => BuildViews().Single(view => view.Name == name);

    private Handler FindHandler(string solution, string symbol) =>
        BuildHandlers().Single(handler => handler.Solution == solution && handler.Symbol == symbol);

    private static CacheKey WithRole(CacheKey key, string role) =>
        new(key.Template, key.Store, key.Ttl, key.TagsAll, key.TagsAny, role);

    private sealed record UnresolvedOrigin(int UnresolvedId, string Solution, string HandlerSymbol);
}
