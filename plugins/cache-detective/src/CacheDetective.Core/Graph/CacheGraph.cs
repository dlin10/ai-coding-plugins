namespace CacheDetective.Graph;

using Caching;
using CacheDetective.Events;

public sealed class CacheGraph
{
    private readonly List<(GraphOrigin Origin, CacheKey Key)> _cacheKeySites = [];
    private readonly List<(GraphOrigin Origin, CacheKey Key)> _cacheKeyObservations = [];
    private readonly List<(GraphOrigin Origin, Table Table)> _tableSites = [];
    private readonly List<(GraphOrigin Origin, StoredProcedure Procedure)> _procedureSites = [];
    private readonly List<(GraphOrigin Origin, Trigger Trigger)> _triggerSites = [];
    private readonly List<(GraphOrigin Origin, View View)> _viewSites = [];
    private readonly List<(GraphOrigin Origin, Event Event)> _eventSites = [];
    private readonly List<(GraphOrigin Origin, ExternalSource Source)> _externalSites = [];
    private readonly List<(GraphOrigin Origin, string Name)> _indexedDatabases = [];
    private readonly List<Handler> _handlerSites = [];
    private readonly List<(GraphOrigin Origin, GraphEdge Edge)> _edgeSites = [];
    private readonly List<Unresolved> _unresolved = [];
    private readonly List<UnresolvedOrigin> _unresolvedOrigins = [];
    private readonly List<(string Solution, CacheOperation Operation)> _cacheOperations = [];
    private readonly List<(string Solution, PendingCacheOperation Operation)> _pendingCacheOperations = [];
    private readonly List<Annotation> _annotations = [];
    private readonly List<CacheOperation> _annotationCacheOperations = [];
    private readonly List<(ExternalSource Source, Handler Target, int AnnotationId)> _servesAnnotations = [];
    private readonly Dictionary<int, ExternalSource> _externalUnresolved = [];
    private readonly Dictionary<int, EventSiteRole> _eventSiteRoles = [];
    private readonly Dictionary<int, (string Template, string Store, IReadOnlyList<int> Blockers)> _roleBlockers = [];
    private readonly Dictionary<(string Template, string Store), string> _roleOverrides = [];
    private readonly Dictionary<int, int> _suppressedDerived = [];
    private readonly HashSet<int> _externallySuppressedDerived = [];
    private Dictionary<string, string> _serviceMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _derivedUnresolvedIds = new(StringComparer.Ordinal);
    private int _nextUnresolvedId = 1;
    private int _nextAnnotationId = 1;
    private int _version;
    private int _storedEdgesVersion = -1;
    private int _edgesVersion = -1;
    private int _eventHopsVersion = -1;
    private int _serviceJoinsVersion = -1;
    private int _procedureGapsVersion = -1;
    private int _eventGapsVersion = -1;
    private int _verticesVersion = -1;
    private IReadOnlyList<GraphEdge> _storedEdges = [];
    private IReadOnlyList<GraphEdge> _edges = [];
    private IReadOnlyList<EventHop> _eventHops = [];
    private IReadOnlyDictionary<Publishes, IReadOnlyList<EventHop>> _hopsByPublish = new Dictionary<Publishes, IReadOnlyList<EventHop>>();
    private ServiceJoinResult? _serviceJoins;
    private IReadOnlyList<ProcedureGap> _procedureGaps = [];
    private IReadOnlyList<EventGap> _eventGaps = [];
    private IReadOnlyList<CacheKey> _cacheKeys = [];
    private IReadOnlyList<Table> _tables = [];
    private IReadOnlyList<StoredProcedure> _procedures = [];
    private IReadOnlyList<Trigger> _triggers = [];
    private IReadOnlyList<View> _views = [];
    private IReadOnlyList<Event> _events = [];
    private IReadOnlyList<ExternalSource> _externalSources = [];
    private IReadOnlyList<string> _indexedDatabaseNames = [];
    private IReadOnlyList<Handler> _handlers = [];
    private IReadOnlyList<CacheOperation> _builtCacheOperations = [];
    private IReadOnlyList<PendingCacheOperation> _builtPendingCacheOperations = [];
    private readonly Dictionary<(string Template, string Store), (int Version, IReadOnlyList<KeyDependency> Dependencies)> _dependencies = [];

    public IReadOnlyList<CacheKey> CacheKeys { get { EnsureVertices(); return _cacheKeys; } }
    public IReadOnlyList<Table> Tables { get { EnsureVertices(); return _tables; } }
    public IReadOnlyList<StoredProcedure> StoredProcedures { get { EnsureVertices(); return _procedures; } }
    public IReadOnlyList<Trigger> Triggers { get { EnsureVertices(); return _triggers; } }
    public IReadOnlyList<View> Views { get { EnsureVertices(); return _views; } }
    public IReadOnlyList<Event> Events { get { EnsureVertices(); return _events; } }
    public IReadOnlyList<ExternalSource> ExternalSources { get { EnsureVertices(); return _externalSources; } }
    public IReadOnlyList<string> IndexedDatabases { get { EnsureVertices(); return _indexedDatabaseNames; } }
    public IReadOnlyList<Handler> Handlers { get { EnsureVertices(); return _handlers; } }
    public IReadOnlyList<GraphEdge> Edges => GetEdges();
    public IReadOnlyDictionary<string, string> ServiceMap => _serviceMap;
    public IReadOnlyList<GraphEdge> StoredEdges => GetStoredEdges();
    public IReadOnlyList<Unresolved> Unresolved => _unresolved;
    public IReadOnlyList<CacheOperation> CacheOperations { get { EnsureVertices(); return _builtCacheOperations; } }
    public IReadOnlyList<PendingCacheOperation> PendingCacheOperations { get { EnsureVertices(); return _builtPendingCacheOperations; } }
    public IReadOnlyList<Annotation> Annotations => _annotations;
    internal int Version => _version;

    public IReadOnlyList<(ExternalSource Source, Handler Target, int AnnotationId)> ServesAnnotations =>
        _servesAnnotations.Where(item => HasHandler(item.Target))
                          .Select(item => (CanonicalExternal(item.Source), FindHandler(item.Target.Solution, item.Target.Symbol), item.AnnotationId))
                          .ToArray();

    public CacheKey AddCacheKey(string solution, CacheKey key)
    {
        var added = AddCacheKey(GraphOrigin.ForSolution(solution), key);
        Touch();
        return added;
    }

    public CacheKey AddCacheKeyObservation(string solution, CacheKey key)
    {
        var added = AddCacheKeyObservation(GraphOrigin.ForSolution(solution), key);
        Touch();
        return added;
    }

    public Table AddTable(string solution, Table table)
    {
        var added = AddTable(GraphOrigin.ForSolution(solution), table);
        Touch();
        return added;
    }

    public StoredProcedure AddStoredProcedure(string database, StoredProcedure procedure)
    {
        AddIndexedDatabase(database);
        _procedureSites.Add((GraphOrigin.ForDatabase(database), procedure));
        Touch();
        return FindProcedure(procedure.Name);
    }

    public View AddView(string database, View view)
    {
        _viewSites.Add((GraphOrigin.ForDatabase(database), view));
        Touch();
        return FindView(view.Name);
    }

    public void AddIndexedDatabase(string name)
    {
        _indexedDatabases.Add((GraphOrigin.ForDatabase(name), name));
        Touch();
    }

    public void SetServiceMap(IReadOnlyDictionary<string, string> map)
    {
        _serviceMap = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        Touch();
    }

    public Handler AddHandler(Handler handler)
    {
        _handlerSites.Add(handler);
        Touch();
        return FindHandler(handler.Solution, handler.Symbol);
    }

    public GraphEdge AddEdge(GraphEdge edge)
    {
        var origin = OriginOf(edge);
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
        Touch();
        return BuildEdge(edge);
    }

    public GraphEdge AddAnnotationEdge(GraphEdge edge)
    {
        var origin = GraphOrigin.ForAnnotation();
        if (edge.From is not Handler)
            AddAnnotationVertex(origin, edge.From);

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
                if (edge.To is not Handler)
                    AddAnnotationVertex(origin, edge.To);
                break;
        }

        _edgeSites.Add((origin, edge));
        Touch();
        return edge;
    }

    public CacheOperation AddCacheOperation(CacheOperation operation)
    {
        _cacheOperations.Add((operation.Handler.Solution, operation));
        Touch();
        return operation;
    }

    public void AddAnnotationCacheOperation(CacheOperation operation)
    {
        if (operation.Semantic == CacheSemantic.Set)
            AddCacheKey(GraphOrigin.ForAnnotation(), operation.Key);
        else
            AddCacheKeyObservation(GraphOrigin.ForAnnotation(), operation.Key);
        _annotationCacheOperations.Add(operation);
        Touch();
    }

    public void AddPendingCacheOperation(PendingCacheOperation operation)
    {
        _pendingCacheOperations.Add((operation.Handler.Solution, operation));
        Touch();
    }

    public bool TryGetPendingCacheOperation(int unresolvedId, out PendingCacheOperation operation)
    {
        var found = _pendingCacheOperations.FirstOrDefault(item => item.Operation.UnresolvedId == unresolvedId);
        if (found.Operation is not null)
        {
            operation = found.Operation with
            {
                Handler = HasHandler(found.Operation.Handler) ? FindHandler(found.Operation.Handler.Solution, found.Operation.Handler.Symbol) : found.Operation.Handler
            };
            return true;
        }

        operation = null!;
        return false;
    }

    public Unresolved AddUnresolved(UnresolvedKind kind, string solution, string file, int line, string snippet, string reason) =>
        AddUnresolved(kind, solution, new Evidence(file, line), snippet, reason);

    public Unresolved AddUnresolved(UnresolvedKind kind, string? solution, Evidence site, string snippet, string reason)
    {
        var unresolved = new Unresolved(_nextUnresolvedId++, kind, solution, site, snippet, reason);
        if (!MatchesAnnotation(unresolved))
            _unresolved.Add(unresolved);
        Touch();

        return unresolved;
    }

    public Unresolved AddUnresolved(UnresolvedKind kind, Handler handler, string file, int line, string snippet, string reason) =>
        AddUnresolved(kind, handler, new Evidence(file, line), snippet, reason);

    public Unresolved AddUnresolved(UnresolvedKind kind, Handler handler, Evidence site, string snippet, string reason)
    {
        var unresolved = AddUnresolved(kind, handler.Solution, site, snippet, reason);
        if (_unresolved.Any(item => item.Id == unresolved.Id))
            _unresolvedOrigins.Add(new UnresolvedOrigin(unresolved.Id, handler.Solution, handler.Symbol));
        Touch();
        return unresolved;
    }

    public Unresolved AddUnresolvedExternal(UnresolvedKind kind, Handler handler, Evidence site, string snippet,
                                            string reason, ExternalSource source)
    {
        var unresolved = AddUnresolved(kind, handler, site, snippet, reason);
        if (_unresolved.Any(item => item.Id == unresolved.Id))
            _externalUnresolved[unresolved.Id] = source;
        Touch();
        return unresolved;
    }

    public bool TryGetExternalSource(int unresolvedId, out ExternalSource source)
    {
        if (_externalUnresolved.TryGetValue(unresolvedId, out var candidate))
        {
            source = candidate;
            return true;
        }

        source = null!;
        return false;
    }

    public void MarkEventSite(int unresolvedId, EventSiteRole role)
    {
        _eventSiteRoles[unresolvedId] = role;
        Touch();
    }
    public bool TryGetEventSiteRole(int unresolvedId, out EventSiteRole role) => _eventSiteRoles.TryGetValue(unresolvedId, out role);

    public bool TryGetUnresolvedHandler(int unresolvedId, out Handler handler)
    {
        var origin = _unresolvedOrigins.FirstOrDefault(item => item.UnresolvedId == unresolvedId);
        if (origin is not null && HasHandler(origin.Solution, origin.HandlerSymbol))
        {
            handler = FindHandler(origin.Solution, origin.HandlerSymbol);
            return true;
        }

        handler = null!;
        return false;
    }

    public void AddRoleBlockers(int roleUnresolvedId, string template, string store, IReadOnlyList<int> blockerIds)
    {
        _roleBlockers[roleUnresolvedId] = (template, store, blockerIds.ToArray());
        Touch();
    }

    public IReadOnlyList<(int roleUnresolvedId, string template, string store)> RoleRowsBlockedBy(int unresolvedId) =>
        _roleBlockers.Where(pair => pair.Value.Blockers.Contains(unresolvedId) &&
                                    _unresolved.Any(item => item.Id == pair.Key && item.Kind == UnresolvedKind.Role))
                     .Select(pair => (pair.Key, pair.Value.Template, pair.Value.Store))
                     .ToArray();

    public void RemoveUnresolved(int id)
    {
        _unresolved.RemoveAll(item => item.Id == id);
        _unresolvedOrigins.RemoveAll(item => item.UnresolvedId == id);
        _externalUnresolved.Remove(id);
        _eventSiteRoles.Remove(id);
        _pendingCacheOperations.RemoveAll(item => item.Operation.UnresolvedId == id);
        _roleBlockers.Remove(id);

        foreach (var pair in _roleBlockers.ToArray())
        {
            var blockers = pair.Value.Blockers.Where(blocker => blocker != id).ToArray();
            _roleBlockers[pair.Key] = (pair.Value.Template, pair.Value.Store, blockers);
        }
        Touch();
    }

    public int NextAnnotationId() => _nextAnnotationId++;

    public void AddAnnotation(Annotation annotation)
    {
        _annotations.Add(annotation);
        if (annotation.Id >= _nextAnnotationId)
            _nextAnnotationId = annotation.Id + 1;
        Touch();
    }

    public bool TryGetAnnotation(int id, out Annotation annotation)
    {
        if (_annotations.FirstOrDefault(item => item.Id == id) is { } found)
        {
            annotation = found;
            return true;
        }

        annotation = null!;
        return false;
    }

    public void SetCacheKeyRoleOverride(string template, string store, string role)
    {
        _roleOverrides[(template, store)] = role;
        Touch();
    }

    public void AddServesAnnotation(ExternalSource source, Handler target, int annotationId)
    {
        AddAnnotationVertex(GraphOrigin.ForAnnotation(), source);
        _servesAnnotations.Add((source, target, annotationId));
        Touch();
    }

    public void SuppressDerivedUnresolved(int id, int annotationId, bool external = false)
    {
        _suppressedDerived[id] = annotationId;
        if (external) _externallySuppressedDerived.Add(id);
        Touch();
    }

    public bool IsDerivedUnresolvedSuppressed(int id, int annotationId) =>
        _suppressedDerived.TryGetValue(id, out var existing) && existing == annotationId;

    public bool IsEventGapSuppressed(int id) =>
        _externallySuppressedDerived.Contains(id) || _suppressedDerived.TryGetValue(id, out var annotationId) &&
        StoredEdges.Any(edge => edge.AnnotationId == annotationId);

    public bool IsServiceJoinGapSuppressed(int id) =>
        _externallySuppressedDerived.Contains(id) || _suppressedDerived.TryGetValue(id, out var annotationId) &&
        _servesAnnotations.Any(item => item.AnnotationId == annotationId && HasHandler(item.Target));

    public IReadOnlyList<EventHop> EventHops() => GetEventHops();

    internal IReadOnlyList<EventHop> GetEventHops(Publishes publish) =>
        GetEventHopIndex().TryGetValue(publish, out var hops) ? hops : [];

    public bool TryEventHop(Publishes publish, Consumes consume, out EventHop hop)
    {
        var publishEvent = publish.To as Event;
        var consumeEvent = consume.From as Event;
        if (publishEvent is null || consumeEvent is null)
        {
            hop = null!;
            return false;
        }

        if (publishEvent.FullName == consumeEvent.FullName)
        {
            hop = new EventHop(publish, consume, Weaken(publish.Confidence, consume.Confidence), null);
            return true;
        }

        if (publishEvent.Name == consumeEvent.Name &&
            publish.From is Handler publisher && consume.To is Handler consumer &&
            publisher.ServiceId() != consumer.ServiceId())
        {
            var reason = $"contract duplicated across services: {publishEvent.FullName} vs {consumeEvent.FullName}";
            hop = new EventHop(publish, consume, Weaken(publish.Confidence, consume.Confidence, Confidence.Likely), reason);
            return true;
        }

        hop = null!;
        return false;
    }

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
        Touch();
    }

    public void ReplaceSolution(string solution, CacheGraph replacement) => Replace(origin => origin.Solution == solution, replacement);
    public void ReplaceDatabase(string database, CacheGraph replacement) => Replace(origin => origin.Database == database, replacement);

    private void Replace(Func<GraphOrigin, bool> belongs, CacheGraph replacement)
    {
        if (ReferenceEquals(this, replacement))
            return;

        var replaced = _unresolved.Where(item => belongs(OriginOf(item))).Select(item => item.Id).ToHashSet();
        foreach (var id in replaced)
            RemoveUnresolved(id);

        _cacheKeySites.RemoveAll(site => belongs(site.Origin));
        _cacheKeyObservations.RemoveAll(site => belongs(site.Origin));
        _tableSites.RemoveAll(site => belongs(site.Origin));
        _procedureSites.RemoveAll(site => belongs(site.Origin));
        _triggerSites.RemoveAll(site => belongs(site.Origin));
        _viewSites.RemoveAll(site => belongs(site.Origin));
        _eventSites.RemoveAll(site => belongs(site.Origin));
        _externalSites.RemoveAll(site => belongs(site.Origin));
        _indexedDatabases.RemoveAll(site => belongs(site.Origin));
        _handlerSites.RemoveAll(handler => belongs(OriginOf(handler)));
        _edgeSites.RemoveAll(site => belongs(site.Origin));
        _cacheOperations.RemoveAll(site => belongs(GraphOrigin.ForSolution(site.Solution)));
        _pendingCacheOperations.RemoveAll(site => belongs(GraphOrigin.ForSolution(site.Solution)));

        _cacheKeySites.AddRange(replacement._cacheKeySites.Where(site => belongs(site.Origin)));
        _cacheKeyObservations.AddRange(replacement._cacheKeyObservations.Where(site => belongs(site.Origin)));
        _tableSites.AddRange(replacement._tableSites.Where(site => belongs(site.Origin)));
        _procedureSites.AddRange(replacement._procedureSites.Where(site => belongs(site.Origin)));
        _triggerSites.AddRange(replacement._triggerSites.Where(site => belongs(site.Origin)));
        _viewSites.AddRange(replacement._viewSites.Where(site => belongs(site.Origin)));
        _eventSites.AddRange(replacement._eventSites.Where(site => belongs(site.Origin)));
        _externalSites.AddRange(replacement._externalSites.Where(site => belongs(site.Origin)));
        _indexedDatabases.AddRange(replacement._indexedDatabases.Where(site => belongs(site.Origin)));
        _handlerSites.AddRange(replacement._handlerSites.Where(handler => belongs(OriginOf(handler))));
        _edgeSites.AddRange(replacement._edgeSites.Where(site => belongs(site.Origin)));
        _cacheOperations.AddRange(replacement._cacheOperations.Where(site => belongs(GraphOrigin.ForSolution(site.Solution))));

        var remapped = new Dictionary<int, int>();
        foreach (var item in replacement._unresolved.Where(item => belongs(OriginOf(item))))
        {
            if (MatchesAnnotation(item))
                continue;

            var added = AddUnresolved(item.Kind, item.Solution, item.Site, item.Snippet, item.Reason);
            if (_unresolved.Any(candidate => candidate.Id == added.Id))
                remapped[item.Id] = added.Id;
        }

        foreach (var origin in replacement._unresolvedOrigins)
        {
            if (remapped.TryGetValue(origin.UnresolvedId, out var id))
                _unresolvedOrigins.Add(new UnresolvedOrigin(id, origin.Solution, origin.HandlerSymbol));
        }

        foreach (var item in replacement._externalUnresolved)
        {
            if (remapped.TryGetValue(item.Key, out var id))
                _externalUnresolved[id] = item.Value;
        }

        foreach (var item in replacement._eventSiteRoles)
        {
            if (remapped.TryGetValue(item.Key, out var id))
                _eventSiteRoles[id] = item.Value;
        }

        foreach (var item in replacement._pendingCacheOperations.Where(item => belongs(GraphOrigin.ForSolution(item.Solution))))
        {
            if (remapped.ContainsKey(item.Operation.UnresolvedId))
                _pendingCacheOperations.Add((item.Solution, item.Operation with { UnresolvedId = remapped[item.Operation.UnresolvedId] }));
        }

        var addedRoleRows = new List<(int Id, string Template, string Store)>();
        foreach (var item in replacement._roleBlockers)
        {
            if (!remapped.TryGetValue(item.Key, out var roleId))
                continue;

            var blockers = item.Value.Blockers.Where(remapped.ContainsKey).Select(blocker => remapped[blocker]).ToArray();
            _roleBlockers[roleId] = (item.Value.Template, item.Value.Store, blockers);
            addedRoleRows.Add((roleId, item.Value.Template, item.Value.Store));
        }

        foreach (var role in addedRoleRows)
        {
            if (CacheKeys.FirstOrDefault(key => key.Template == role.Template && key.Store == role.Store) is not { } key)
                continue;

            var classification = new CacheRoleClassifier().ClassifyKey(this, key);
            if (classification.Role is "cache" or "store")
            {
                SetCacheKeyRole(role.Template, role.Store, classification.Role);
                RemoveUnresolved(role.Id);
            }
        }
        Touch();
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
        if (vertex is Handler handler)
            AddHandler(handler);
        else
        {
            if (!origin.Annotation && origin.Database is not null && vertex is StoredProcedure or Trigger or View)
                AddIndexedDatabase(origin.Database);
            AddAnnotationVertex(origin, vertex);
        }
    }

    private void AddAnnotationVertex(GraphOrigin origin, GraphVertex vertex)
    {
        switch (vertex)
        {
            case CacheKey key:
                AddCacheKey(origin, key);
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
            case Event @event:
                _eventSites.Add((origin, @event));
                break;
            case ExternalSource source:
                _externalSites.Add((origin, source));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(vertex));
        }
    }

    private static GraphOrigin OriginOf(GraphEdge edge) => edge switch
    {
        Consumes { To: Handler handler } => GraphOrigin.ForSolution(handler.Solution),
        _ when edge.From is Handler handler => GraphOrigin.ForSolution(handler.Solution),
        _ => OriginOf(edge.From)
    };

    private static GraphOrigin OriginOf(GraphVertex vertex) => vertex switch
    {
        Handler handler => GraphOrigin.ForSolution(handler.Solution),
        Table table => GraphOrigin.ForDatabase(table.Database),
        StoredProcedure procedure => GraphOrigin.ForDatabase(procedure.Database),
        Trigger trigger => GraphOrigin.ForDatabase(trigger.Database),
        View view => GraphOrigin.ForDatabase(view.Database),
        ExternalSource source => GraphOrigin.ForSolution(source.Owner),
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
                           var role = _roleOverrides.TryGetValue(group.Key, out var overrideRole)
                                          ? overrideRole
                                          : sites.Select(site => site.Role).FirstOrDefault(role => role is not null);

                           return new CacheKey(group.Key.Template, group.Key.Store, ttl, tagsAll, tagsAny, role);
                       })
                      .ToArray();

    private IReadOnlyList<CacheOperation> BuildCacheOperations() =>
        _cacheOperations.Where(site => HasHandler(site.Operation.Handler))
                        .Select(site => BuildCacheOperation(site.Operation))
                        .Concat(_annotationCacheOperations.Where(operation => HasHandler(operation.Handler))
                                                          .Select(BuildCacheOperation))
                        .ToArray();

    private CacheOperation BuildCacheOperation(CacheOperation operation) => operation with
    {
        Handler = FindHandler(operation.Handler.Solution, operation.Handler.Symbol),
        Key = FindCacheKey(operation.Key.Template, operation.Key.Store)
    };

    private IReadOnlyList<PendingCacheOperation> BuildPendingCacheOperations() =>
        _pendingCacheOperations.Where(site => HasHandler(site.Operation.Handler))
                               .Select(site => site.Operation with { Handler = FindHandler(site.Operation.Handler.Solution, site.Operation.Handler.Symbol) })
                               .ToArray();

    private IReadOnlyList<Table> BuildTables() =>
        _tableSites.GroupBy(site => site.Table.Name, StringComparer.Ordinal)
                   .Select(group => group.Select(site => site.Table).FirstOrDefault(table => table.Database is not null) ?? group.First().Table)
                   .ToArray();

    private IReadOnlyList<StoredProcedure> BuildProcedures() =>
        _procedureSites.GroupBy(site => site.Procedure.Name, StringComparer.Ordinal)
                       .Select(group => group.Select(site => site.Procedure).FirstOrDefault(procedure => procedure.Database is not null) ?? group.First().Procedure)
                       .ToArray();

    private IReadOnlyList<Trigger> BuildTriggers() =>
        _triggerSites.GroupBy(site => site.Trigger.Name, StringComparer.Ordinal)
                     .Select(group => group.Select(site => site.Trigger).FirstOrDefault(trigger => trigger.Database is not null) ?? group.First().Trigger)
                     .ToArray();

    private IReadOnlyList<View> BuildViews() =>
        _viewSites.GroupBy(site => site.View.Name, StringComparer.Ordinal)
                  .Select(group => group.Select(site => site.View).FirstOrDefault(view => view.Database is not null) ?? group.First().View)
                  .ToArray();

    private IReadOnlyList<Event> BuildEvents() =>
        _eventSites.GroupBy(site => site.Event.FullName, StringComparer.Ordinal).Select(group => group.First().Event).ToArray();

    private IReadOnlyList<ExternalSource> BuildExternalSources() =>
        _externalSites.GroupBy(site => (site.Source.Kind, site.Source.Method, site.Source.Template, site.Source.ClientName, site.Source.Owner))
                      .Select(group => group.First().Source)
                      .ToArray();

    private IReadOnlyList<Handler> BuildHandlers() =>
        _handlerSites.GroupBy(handler => (handler.Solution, handler.Symbol))
                     .Select(group =>
                     {
                         var first = group.First();
                         var project = group.Select(handler => handler.Project).FirstOrDefault(project => !string.IsNullOrEmpty(project));
                         var routes = group.SelectMany(handler => handler.Routes).Distinct().ToArray();
                         return first with { Project = project, Routes = routes };
                     })
                     .ToArray();

    private IReadOnlyList<GraphEdge> BuildEdges() =>
        _edgeSites.Where(site => !site.Origin.Annotation || HasLiveHandler(site.Edge)).Select(site => BuildEdge(site.Edge)).ToArray();

    private GraphEdge BuildEdge(GraphEdge edge) => edge with { From = Canonical(edge.From), To = Canonical(edge.To) };

    private GraphVertex Canonical(GraphVertex vertex) => vertex switch
    {
        CacheKey key => FindCacheKey(key.Template, key.Store),
        Table table => FindTable(table.Name),
        Handler handler => FindHandler(handler.Solution, handler.Symbol),
        StoredProcedure procedure => FindProcedure(procedure.Name),
        Trigger trigger => FindTrigger(trigger.Name),
        View view => FindView(view.Name),
        Event @event => FindEvent(@event.FullName),
        ExternalSource source => FindExternal(source),
        _ => throw new ArgumentOutOfRangeException(nameof(vertex))
    };

    private CacheKey FindCacheKey(string template, string store) => BuildCacheKeys().Single(key => key.Template == template && key.Store == store);
    private Table FindTable(string name) => BuildTables().Single(table => table.Name == name);
    private StoredProcedure FindProcedure(string name) => BuildProcedures().Single(procedure => procedure.Name == name);
    private Trigger FindTrigger(string name) => BuildTriggers().Single(trigger => trigger.Name == name);
    private View FindView(string name) => BuildViews().Single(view => view.Name == name);
    private Event FindEvent(string fullName) => BuildEvents().Single(@event => @event.FullName == fullName);
    private ExternalSource FindExternal(ExternalSource source) => BuildExternalSources().Single(candidate => candidate == source);
    private ExternalSource CanonicalExternal(ExternalSource source) => FindExternal(source);
    private Handler FindHandler(string solution, string symbol) => BuildHandlers().Single(handler => handler.Solution == solution && handler.Symbol == symbol);
    private bool HasHandler(Handler handler) => HasHandler(handler.Solution, handler.Symbol);
    private bool HasHandler(string solution, string symbol) => _handlerSites.Any(handler => handler.Solution == solution && handler.Symbol == symbol);

    private bool HasLiveHandler(GraphEdge edge) =>
        (edge.From is not Handler from || HasHandler(from)) && (edge.To is not Handler to || HasHandler(to));

    private bool MatchesAnnotation(Unresolved unresolved) =>
        _annotations.Any(annotation => annotation.Kind == unresolved.Kind && annotation.Solution == unresolved.Solution &&
                                       annotation.Site.Describe() == unresolved.Site.Describe() && annotation.Snippet == unresolved.Snippet);

    internal ServiceJoinResult GetServiceJoins()
    {
        if (_serviceJoinsVersion != _version)
        {
            _serviceJoins = ServiceJoins.Build(this);
            _serviceJoinsVersion = _version;
        }
        return _serviceJoins!;
    }

    internal IReadOnlyList<ProcedureGap> GetProcedureGaps()
    {
        if (_procedureGapsVersion != _version)
        {
            _procedureGaps = ProcedureGaps.Build(this);
            _procedureGapsVersion = _version;
        }
        return _procedureGaps;
    }

    internal IReadOnlyList<EventGap> GetEventGaps()
    {
        if (_eventGapsVersion != _version)
        {
            _eventGaps = EventGaps.Build(this);
            _eventGapsVersion = _version;
        }
        return _eventGaps;
    }

    internal IReadOnlyList<KeyDependency> GetDependencies(CacheKey key)
    {
        var id = (key.Template, key.Store);
        if (!_dependencies.TryGetValue(id, out var cached) || cached.Version != _version)
        {
            cached = (_version, CacheGraphDependencies.Build(this, key));
            _dependencies[id] = cached;
        }
        return cached.Dependencies;
    }

    private IReadOnlyList<GraphEdge> GetStoredEdges()
    {
        if (_storedEdgesVersion != _version)
        {
            _storedEdges = BuildEdges();
            _storedEdgesVersion = _version;
        }
        return _storedEdges;
    }

    private void EnsureVertices()
    {
        if (_verticesVersion == _version) return;
        _cacheKeys = BuildCacheKeys();
        _tables = BuildTables();
        _procedures = BuildProcedures();
        _triggers = BuildTriggers();
        _views = BuildViews();
        _events = BuildEvents();
        _externalSources = BuildExternalSources();
        _indexedDatabaseNames = _indexedDatabases.Select(site => site.Name).Distinct(StringComparer.Ordinal).ToArray();
        _handlers = BuildHandlers();
        _builtCacheOperations = BuildCacheOperations();
        _builtPendingCacheOperations = BuildPendingCacheOperations();
        _verticesVersion = _version;
    }

    private IReadOnlyList<GraphEdge> GetEdges()
    {
        if (_edgesVersion != _version)
        {
            _edges = [.. StoredEdges, .. GetServiceJoins().Serves];
            _edgesVersion = _version;
        }
        return _edges;
    }

    private IReadOnlyList<EventHop> GetEventHops()
    {
        if (_eventHopsVersion != _version)
        {
            var consumes = StoredEdges.OfType<Consumes>().ToArray();
            var index = new Dictionary<Publishes, IReadOnlyList<EventHop>>();
            var hops = new List<EventHop>();
            foreach (var publish in StoredEdges.OfType<Publishes>())
            {
                var matches = consumes.Select(consume => TryEventHop(publish, consume, out var hop) ? hop : null)
                                      .OfType<EventHop>().ToArray();
                index[publish] = matches;
                hops.AddRange(matches);
            }
            _hopsByPublish = index;
            _eventHops = hops;
            _eventHopsVersion = _version;
        }
        return _eventHops;
    }

    private IReadOnlyDictionary<Publishes, IReadOnlyList<EventHop>> GetEventHopIndex()
    {
        GetEventHops();
        return _hopsByPublish;
    }

    private void Touch() => _version++;

    private static Confidence Weaken(Confidence first, Confidence second, Confidence minimum = Confidence.Confirmed) =>
        (Confidence)Math.Max((int)minimum, Math.Max((int)first, (int)second));

    private static CacheKey WithRole(CacheKey key, string role) => new(key.Template, key.Store, key.Ttl, key.TagsAll, key.TagsAny, role);

    private sealed record UnresolvedOrigin(int UnresolvedId, string Solution, string HandlerSymbol);
}
