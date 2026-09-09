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
    private readonly List<(GraphOrigin Origin, HeuristicWriteSite Site)> _heuristicWriteSites = [];
    private readonly List<Unresolved> _unresolved = [];
    private readonly List<(string Solution, CacheOperation Operation)> _cacheOperations = [];
    private int _parsedSqlSites;
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
    private int _adjacencyVersion = -1;
    private int _verticesVersion = -1;
    private bool _vertexListsDirty = true;
    private readonly Guid _instance = Guid.NewGuid();
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
    private IReadOnlyList<HeuristicWriteSite> _builtHeuristicWriteSites = [];
    private IReadOnlyList<CacheOperation> _builtCacheOperations = [];
    private IReadOnlyList<PendingCacheOperation> _builtPendingCacheOperations = [];
    private readonly Dictionary<(string Template, string Store), (int Version, KeyDependencyWalk Walk)> _dependencies = [];
    private CacheGraphAdjacency? _adjacency;

    // The vertex indexes. Every private Add* updates these in place, and every Find* reads them: the
    // lists below are built from the indexes, never scanned to answer a lookup. A private Add* must not
    // read anything derived from them, because the public Add* it serves returns the vertex it just
    // indexed and would otherwise pay for a materialisation per added site.
    private readonly Dictionary<(string Template, string Store), KeyAccumulator> _cacheKeyIndex = [];
    private readonly Dictionary<string, NamedVertex<Table>> _tableIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NamedVertex<StoredProcedure>> _procedureIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NamedVertex<Trigger>> _triggerIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NamedVertex<View>> _viewIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Event> _eventIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, string Method, string Template, string? ClientName, string Owner), ExternalSource> _externalIndex = [];
    private readonly Dictionary<(string Solution, string Symbol), HandlerAccumulator> _handlerIndex = [];
    private readonly HashSet<(string Solution, string Symbol)> _handlerSiteKeys = [];
    private readonly List<string> _databaseNames = [];
    private readonly HashSet<string> _databaseNameSet = new(StringComparer.Ordinal);

    // Unresolved rows by id, by originating handler, and the handler origin of a row. Without these,
    // GetUnresolvedForHandlers scans every origin and every row for every key it is asked about, which is
    // quadratic in the number of handlers however good the rest of the indexes are.
    private readonly Dictionary<int, Unresolved> _unresolvedById = [];
    private readonly Dictionary<int, UnresolvedOrigin> _unresolvedOriginById = [];
    private readonly Dictionary<(string Solution, string Symbol), List<int>> _unresolvedByHandler = [];

    // The role, kept out of the site records and stamped with the solution that classified it. Two
    // solutions may declare one key and classify it differently, and neither of them is wrong; the graph
    // reports the disagreement rather than picking a winner. See docs/adr/0005.
    private readonly Dictionary<(string Template, string Store), Dictionary<string, string>> _roles = [];
    private readonly Dictionary<(string Template, string Store), int> _roleConflicts = [];

    public IReadOnlyList<CacheKey> CacheKeys { get { EnsureVertices(); return _cacheKeys; } }
    public IReadOnlyList<Table> Tables { get { EnsureVertices(); return _tables; } }
    public IReadOnlyList<StoredProcedure> StoredProcedures { get { EnsureVertices(); return _procedures; } }
    public IReadOnlyList<Trigger> Triggers { get { EnsureVertices(); return _triggers; } }
    public IReadOnlyList<View> Views { get { EnsureVertices(); return _views; } }
    public IReadOnlyList<Event> Events { get { EnsureVertices(); return _events; } }
    public IReadOnlyList<ExternalSource> ExternalSources { get { EnsureVertices(); return _externalSources; } }
    public IReadOnlyList<string> IndexedDatabases { get { EnsureVertices(); return _indexedDatabaseNames; } }
    public IReadOnlyList<Handler> Handlers { get { EnsureVertices(); return _handlers; } }
    public IReadOnlyList<HeuristicWriteSite> HeuristicWriteSites { get { EnsureVertices(); return _builtHeuristicWriteSites; } }
    public IReadOnlyList<GraphEdge> Edges => GetEdges();
    public IReadOnlyDictionary<string, string> ServiceMap => _serviceMap;
    public IReadOnlyList<GraphEdge> StoredEdges => GetStoredEdges();
    public IReadOnlyList<Unresolved> Unresolved => _unresolved;
    public IReadOnlyList<CacheOperation> CacheOperations { get { EnsureVertices(); return _builtCacheOperations; } }
    public int ParsedSqlSites => _parsedSqlSites;
    public IReadOnlyList<PendingCacheOperation> PendingCacheOperations { get { EnsureVertices(); return _builtPendingCacheOperations; } }
    public IReadOnlyList<Annotation> Annotations => _annotations;
    internal int Version => _version;

    /// <summary>How many times the public vertex lists have been materialised from the indexes. A caller
    /// that reads the same graph twice without changing it must not move this.</summary>
    public int VertexRebuilds { get; private set; }

    /// <summary>
    /// How many entries of the owning handlers' lists a batch removal has looked at. Removing ids one at a
    /// time scanned the owner's whole list per id, which is quadratic when many unresolved calls belong to
    /// one handler; sweeping each owner once makes this grow with the rows and not with their square. It
    /// is counted rather than timed so that the scale test measures the work and not the machine.
    /// </summary>
    public long UnresolvedOwnerScans { get; private set; }

    /// <summary>
    /// How many unresolved rows the lookups have looked at. This is the work the by-handler index exists
    /// to bound: without it every question about one handler scans every row of every origin, which is
    /// quadratic in a graph whose handlers and rows grow together. Counting BFS visits instead proved
    /// nothing here — those are linear whatever the lookup does.
    /// </summary>
    public long UnresolvedRowScans { get; private set; }

    /// <summary>Names this graph and its current state in one string: equal tokens mean the same graph at
    /// the same version, and nothing else.</summary>
    public string VersionToken => $"{_instance:N}-{_version}";

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
        var added = AddProcedure(GraphOrigin.ForDatabase(database), procedure);
        Touch();
        return added;
    }

    public View AddView(string database, View view)
    {
        var added = AddView(GraphOrigin.ForDatabase(database), view);
        Touch();
        return added;
    }

    public void AddIndexedDatabase(string name)
    {
        AddIndexedDatabaseSite(GraphOrigin.ForDatabase(name), name);
        Touch();
    }

    public void SetServiceMap(IReadOnlyDictionary<string, string> map)
    {
        _serviceMap = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        Touch();
    }

    public Handler AddHandler(Handler handler)
    {
        var added = AddHandlerSite(handler);
        Touch();
        return added;
    }

    /// <summary>A site the write heuristic saw, recorded as it was seen: one entry per mutation, before
    /// the mutations of one entity are merged into a single <see cref="Writes"/> edge and before the
    /// facts are propagated to the callers of the method that holds them.</summary>
    public void AddHeuristicWriteSite(HeuristicWriteSite site)
    {
        _heuristicWriteSites.Add((GraphOrigin.ForSolution(site.Handler.Solution), site));
        Touch();
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

    /// <summary>Records one SQL call site whose text was fully reduced and parsed.</summary>
    public void AddParsedSqlSite()
    {
        _parsedSqlSites++;
        Touch();
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
            Record(unresolved);
        Touch();

        return unresolved;
    }

    public Unresolved AddUnresolved(UnresolvedKind kind, Handler handler, string file, int line, string snippet, string reason) =>
        AddUnresolved(kind, handler, new Evidence(file, line), snippet, reason);

    public Unresolved AddUnresolved(UnresolvedKind kind, Handler handler, Evidence site, string snippet, string reason)
    {
        var unresolved = AddUnresolved(kind, handler.Solution, site, snippet, reason);
        if (_unresolvedById.ContainsKey(unresolved.Id))
            RecordOrigin(new UnresolvedOrigin(unresolved.Id, handler.Solution, handler.Symbol));
        Touch();
        return unresolved;
    }

    public Unresolved AddUnresolvedExternal(UnresolvedKind kind, Handler handler, Evidence site, string snippet,
                                            string reason, ExternalSource source)
    {
        var unresolved = AddUnresolved(kind, handler, site, snippet, reason);
        if (_unresolvedById.ContainsKey(unresolved.Id))
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
        if (_unresolvedOriginById.TryGetValue(unresolvedId, out var origin) && HasHandler(origin.Solution, origin.HandlerSymbol))
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
                                    _unresolvedById.TryGetValue(pair.Key, out var item) && item.Kind == UnresolvedKind.Role)
                     .Select(pair => (pair.Key, pair.Value.Template, pair.Value.Store))
                     .ToArray();

    public void RemoveUnresolved(int id) => RemoveUnresolved(new HashSet<int> { id });

    /// <summary>Removes a whole set of rows in one pass over each list, because re-indexing removes every
    /// row of a solution at once and a scan per row is quadratic in the rows.</summary>
    private void RemoveUnresolved(IReadOnlySet<int> ids)
    {
        if (ids.Count == 0)
            return;

        _unresolved.RemoveAll(item => ids.Contains(item.Id));
        _pendingCacheOperations.RemoveAll(item => ids.Contains(item.Operation.UnresolvedId));

        // The owning handlers are collected first and their lists swept once each. Removing ids one at a
        // time scanned the owner's list per id, which is Θ(U²) when U unresolved calls belong to one
        // handler — exactly the shape a large solution produces.
        var owners = new HashSet<(string Solution, string Symbol)>();
        foreach (var id in ids)
        {
            _unresolvedById.Remove(id);
            _externalUnresolved.Remove(id);
            _eventSiteRoles.Remove(id);
            _roleBlockers.Remove(id);
            if (_unresolvedOriginById.Remove(id, out var origin))
                owners.Add((origin.Solution, origin.HandlerSymbol));
        }

        foreach (var owner in owners)
        {
            if (!_unresolvedByHandler.TryGetValue(owner, out var owned))
                continue;

            UnresolvedOwnerScans += owned.Count;
            owned.RemoveAll(ids.Contains);
        }

        foreach (var pair in _roleBlockers.ToArray())
        {
            if (!pair.Value.Blockers.Any(ids.Contains))
                continue;

            var blockers = pair.Value.Blockers.Where(blocker => !ids.Contains(blocker)).ToArray();
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
        Invalidate(template, store);
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
        var kindSet = kinds.ToHashSet();
        var seen = new HashSet<int>();
        var found = new List<Unresolved>();
        foreach (var handler in handlers)
        {
            if (!_unresolvedByHandler.TryGetValue((handler.Solution, handler.Symbol), out var owned))
                continue;

            foreach (var id in owned)
            {
                UnresolvedRowScans++;
                if (seen.Add(id) && _unresolvedById.TryGetValue(id, out var item) && kindSet.Contains(item.Kind))
                    found.Add(item);
            }
        }

        // Ids rise with insertion, so this is the order the rows were added in — the order a scan of the
        // list would have produced, which is what the caller that reports the first blocker relies on.
        found.Sort((left, right) => left.Id.CompareTo(right.Id));
        return found;
    }

    /// <summary>
    /// The unresolved rows the database indexer recorded against named catalogue objects. Those rows carry
    /// no solution and no owning handler — their site is the object's qualified name — so the handler
    /// lookup above cannot see them, and a walk that only asked it treated a dependency reached through a
    /// procedure full of dynamic SQL as if the chain were fully known.
    /// </summary>
    internal IReadOnlyList<Unresolved> GetUnresolvedForDatabaseObjects(IEnumerable<string> qualifiedNames,
                                                                       params UnresolvedKind[] kinds)
    {
        var wanted = qualifiedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return [];

        var kindSet = kinds.ToHashSet();
        var found = _unresolvedById.Values
                                   .Where(item => item.Solution is null && kindSet.Contains(item.Kind) &&
                                                  (item.Site.ObjectName ?? item.Site.File) is { } name && wanted.Contains(name))
                                   .ToList();
        found.Sort((left, right) => left.Id.CompareTo(right.Id));
        return found;
    }

    /// <summary>Records the role this graph's own solutions classified the key with. The classification
    /// is made from the whole graph, so every solution that declares the key gets the same answer here;
    /// two <em>different</em> answers can only arrive through <see cref="Replace"/>, one solution at a
    /// time, and are reported rather than silently resolved.</summary>
    /// <summary>
    /// Records a classification. The role is held per declaring solution, so that two solutions declaring
    /// one key may disagree and the graph reports the disagreement rather than picking a winner (see
    /// <c>docs/adr/0005</c>).
    /// <para><paramref name="solution"/> names the one solution whose opinion is being written. A
    /// re-classification after a replace must pass it: writing the answer into every declaring solution's
    /// slot made the outcome depend on the order the solutions were indexed in, so replacing A then B and
    /// replacing B then A settled the same conflict two different ways.</para>
    /// </summary>
    internal void SetCacheKeyRole(string template, string store, string role, string? solution = null)
    {
        var id = (template, store);
        if (!_roles.TryGetValue(id, out var bySolution))
        {
            bySolution = new Dictionary<string, string>(StringComparer.Ordinal);
            _roles[id] = bySolution;
        }

        var declaring = DeclaringSolutions(template, store);
        foreach (var declared in solution is null ? declaring : declaring.Where(name => name == solution))
            bySolution[declared] = role;

        SyncRoleConflict(template, store);
        Invalidate(template, store);
    }

    public void ReplaceSolution(string solution, CacheGraph replacement) =>
        Replace(origin => origin.Solution == solution, replacement, solution);
    public void ReplaceDatabase(string database, CacheGraph replacement) =>
        Replace(origin => origin.Database == database, replacement, null);

    private void Replace(Func<GraphOrigin, bool> belongs, CacheGraph replacement, string? solution)
    {
        if (ReferenceEquals(this, replacement))
            return;

        RemoveUnresolved(_unresolved.Where(item => belongs(OriginOf(item))).Select(item => item.Id).ToHashSet());

        // Everything derived about the contribution that is leaving goes with it, before anything of the
        // incoming one is carried over. An unblocking marker says "this solution's own graph could not
        // classify this key, and the rest of the workspace answered for it" — a statement about the
        // contribution being replaced, and untrue of the one arriving, which classified itself afresh.
        // Left behind, it made the reclassification below overwrite the incoming solution's own answer
        // with one taken from the merged graph, so re-indexing a solution and building the same solutions
        // from scratch disagreed.
        _unblocked.RemoveWhere(item => belongs(GraphOrigin.ForSolution(item.Solution)));

        // The keys this replacement touches, in both compositions: the ones the outgoing contribution had
        // a site for and the ones the incoming one brings. Both halves are needed. A key leaving takes its
        // handler's reads with it, so a key another solution declares may stop reaching data; a key
        // arriving may make one reach it for the first time.
        var touchedKeys = _cacheKeySites.Where(site => belongs(site.Origin))
                                        .Concat(replacement._cacheKeySites.Where(site => belongs(site.Origin)))
                                        .Select(site => (site.Key.Template, site.Key.Store))
                                        .ToHashSet();

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
        _heuristicWriteSites.RemoveAll(site => belongs(site.Origin));
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
        _heuristicWriteSites.AddRange(replacement._heuristicWriteSites.Where(site => belongs(site.Origin)));
        _cacheOperations.AddRange(replacement._cacheOperations.Where(site => belongs(GraphOrigin.ForSolution(site.Solution))));

        // The order is fixed: the lists are mutated wholesale, the version is moved on, then every index
        // is rebuilt from them, then the roles are carried over — and only after that may anything read a
        // vertex or classify a key. The accumulators merge irreversibly, so nothing short of a full
        // rebuild is correct here.
        //
        // The version moves on the moment the lists change, because every derived structure is keyed on
        // the version and not on the lists: GetEdges hands back its cached list while _edgesVersion still
        // matches. Anyone who read Edges since the last change — CurrentCounts does, after every
        // index_solution — leaves that cache holding the graph as it was before this replacement.
        Touch();
        RebuildIndexes();
        TransferRoles(belongs, replacement);

        var remapped = new Dictionary<int, int>();
        foreach (var item in replacement._unresolved.Where(item => belongs(OriginOf(item))))
        {
            if (MatchesAnnotation(item))
                continue;

            var added = AddUnresolved(item.Kind, item.Solution, item.Site, item.Snippet, item.Reason);
            if (_unresolvedById.ContainsKey(added.Id))
                remapped[item.Id] = added.Id;
        }

        foreach (var origin in replacement._unresolvedOriginById.Values)
        {
            if (remapped.TryGetValue(origin.UnresolvedId, out var id))
                RecordOrigin(new UnresolvedOrigin(id, origin.Solution, origin.HandlerSymbol));
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

        foreach (var item in replacement._roleBlockers)
        {
            if (!remapped.TryGetValue(item.Key, out var roleId))
                continue;

            var blockers = item.Value.Blockers.Where(remapped.ContainsKey).Select(blocker => remapped[blocker]).ToArray();
            _roleBlockers[roleId] = (item.Value.Template, item.Value.Store, blockers);
        }

        Reclassify(touchedKeys, solution);
        Touch();
    }

    /// <summary>
    /// Settles the roles a replacement can have changed. Two sets are reclassified, and both are needed.
    /// <para>The first is every contribution that was once blocked and was unblocked by the rest of the
    /// workspace, on a key this replacement touches — in its outgoing composition or its incoming one.
    /// Such a role does not survive the solution that resolved it leaving: with "one" and "two" merged,
    /// "two"'s own contribution classifies as <c>cache</c> because it can reach "one"'s read and its
    /// blocker row is dropped — and then removing "one" left <c>cache</c> standing with nothing to support
    /// it, because no blocked row was left to notice.
    /// <para>Only the contributions that were derived this way are recomputed, and never the ones a
    /// solution formed on its own graph. Reclassifying every declaring solution over the merged graph made
    /// two solutions that genuinely disagree agree, which is the disagreement <c>docs/adr/0005</c> exists
    /// to report rather than resolve.</para></para>
    /// <para>The second is every key some solution still has blocked. A solution indexed earlier may have
    /// been blocked by an unresolved call that the solution arriving now resolves, and it need not declare
    /// a key of this replacement to be affected — so A then B and B then A settled the same key two
    /// different ways.</para>
    /// <para>Neither set is the whole graph: the first is bounded by the replaced solution's own keys and
    /// their declaring solutions, the second by the rows that are actually blocked.</para>
    /// </summary>
    private void Reclassify(IReadOnlySet<(string Template, string Store)> touchedKeys, string? solution)
    {
        var blocked = _roleBlockers.Select(row => (Id: row.Key, row.Value.Template, row.Value.Store,
                                                   Solution: _unresolvedById.TryGetValue(row.Key, out var item) ? item.Solution : null))
                                   .ToArray();
        if (touchedKeys.Count == 0 && blocked.Length == 0)
            return;

        // Touch again, because the remap loop above may have added no rows and so moved the version not at
        // all. The index below reads the derived edge list, which is handed back from cache while the
        // version is unchanged.
        Touch();
        var classifier = new CacheRoleClassifier();
        var index = new CacheRoleIndex(this);
        var keys = CacheKeys.ToDictionary(key => (key.Template, key.Store));

        var removed = new HashSet<int>();
        foreach (var row in blocked)
        {
            if (!keys.TryGetValue((row.Template, row.Store), out var key))
                continue;

            // The row's own solution owns the opinion being rewritten, whichever replacement is running.
            var owner = row.Solution ?? solution;
            if (classifier.ClassifyKey(this, key, index, owner).Role is not ("cache" or "store"))
                continue;

            removed.Add(row.Id);

            // What this contribution turned out to be is not its own conclusion: it was blocked, and the
            // rest of the workspace answered for it. That has to be remembered, because the answer goes
            // away when whatever supplied it does.
            if (owner is not null)
                _unblocked.Add((row.Template, row.Store, owner));
        }

        foreach (var derived in _unblocked.Where(item => touchedKeys.Contains((item.Template, item.Store))).ToArray())
        {
            if (!keys.TryGetValue((derived.Template, derived.Store), out var key))
            {
                _unblocked.Remove(derived);
                continue;
            }

            var classification = classifier.ClassifyKey(this, key, index, derived.Solution);
            if (classification.Blockers.Count > 0)
                _rewritten.Add((key, derived.Solution, classification.Blockers));
        }

        RemoveUnresolved(removed);
        foreach (var (key, declaring, blockers) in _rewritten)
        {
            AddRoleRow(key, declaring, blockers);
            _unblocked.Remove((key.Template, key.Store, declaring));
        }
        _rewritten.Clear();
    }

    /// <summary>Contributions whose <c>unknown</c> was settled by another solution rather than by their
    /// own graph. They are the ones a later replacement has to ask again, and the only ones: an opinion a
    /// solution formed on its own graph is its own and is carried over, not recomputed.</summary>
    private readonly HashSet<(string Template, string Store, string Solution)> _unblocked = [];

    /// <summary>Scratch for <see cref="Reclassify"/>: the rows to write once the stale ones are gone. It
    /// is a field only so that the two passes over the blocked rows can run before anything is added, and
    /// it is cleared before it is handed back.</summary>
    private readonly List<(CacheKey Key, string Solution, IReadOnlyList<Unresolved> Blockers)> _rewritten = [];

    /// <summary>The <c>role</c> row for a key one solution could not classify, worded exactly as the
    /// classifier's own first pass words it.</summary>
    private void AddRoleRow(CacheKey key, string solution, IReadOnlyList<Unresolved> blockers)
    {
        var reasons = string.Join("; ", blockers.Select(item => $"{item.Kind.ToString().ToLowerInvariant()}: {item.Reason}")
                                                .Distinct(StringComparer.Ordinal));
        var role = AddUnresolved(UnresolvedKind.Role, solution, blockers[0].Site, key.Template,
                                 $"Role classification was blocked by incomplete analysis: {reasons}");
        AddRoleBlockers(role.Id, key.Template, key.Store, blockers.Select(item => item.Id).ToArray());
    }

    /// <summary>Carries the replaced scope's own classifications over, dropping the contribution of every
    /// solution the scope covers first. Removing one solution's opinion is all that is undone, so two
    /// solutions that disagreed become one that decides again.</summary>
    private void TransferRoles(Func<GraphOrigin, bool> belongs, CacheGraph replacement)
    {
        var touched = new HashSet<(string Template, string Store)>();
        foreach (var (id, bySolution) in _roles)
        {
            foreach (var solution in bySolution.Keys.Where(solution => belongs(GraphOrigin.ForSolution(solution))).ToArray())
            {
                bySolution.Remove(solution);
                touched.Add(id);
            }
        }

        foreach (var (id, bySolution) in replacement._roles)
        {
            foreach (var (solution, role) in bySolution.Where(pair => belongs(GraphOrigin.ForSolution(pair.Key))))
            {
                if (!_roles.TryGetValue(id, out var existing))
                {
                    existing = new Dictionary<string, string>(StringComparer.Ordinal);
                    _roles[id] = existing;
                }
                existing[solution] = role;
                touched.Add(id);
            }
        }

        // A key that no longer has a site anywhere has no role either.
        foreach (var id in _roles.Keys.Where(id => !_cacheKeyIndex.ContainsKey(id)).ToArray())
        {
            _roles.Remove(id);
            touched.Add(id);
        }

        foreach (var id in touched)
            SyncRoleConflict(id.Template, id.Store);
    }

    /// <summary>The role a key carries when nothing overrides it: the one every solution that classified
    /// it agrees on, or <c>unknown</c> plus an <c>unresolved</c> row of kind <c>role</c> when they do not.
    /// The rule reads the whole map and so does not depend on the order the solutions arrived in.</summary>
    private string? ClassifiedRole(string template, string store)
    {
        if (!_roles.TryGetValue((template, store), out var bySolution) || bySolution.Count == 0)
            return null;

        var first = bySolution.Values.First();
        return bySolution.Values.All(role => string.Equals(role, first, StringComparison.Ordinal)) ? first : "unknown";
    }

    private void SyncRoleConflict(string template, string store)
    {
        var id = (template, store);
        var diverged = ClassifiedRole(template, store) == "unknown" && _roles.TryGetValue(id, out var bySolution) &&
                       bySolution.Values.Distinct(StringComparer.Ordinal).Count() > 1;

        if (!diverged)
        {
            if (_roleConflicts.Remove(id, out var existing))
                Forget(existing);
            return;
        }

        if (_roleConflicts.ContainsKey(id))
            return;

        var solutions = _roles[id].OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                  .Select(pair => $"{pair.Key}: {pair.Value}");
        var row = new Unresolved(_nextUnresolvedId++, UnresolvedKind.Role, null,
                                 Evidence.InDatabase($"{template}@{store}"), template,
                                 $"Solutions disagree about the role of this key: {string.Join("; ", solutions)}");
        Record(row);
        _roleConflicts[id] = row.Id;
    }

    /// <summary>Every solution with a site for this key. A key declared by a database or an annotation has
    /// no solution of its own and is stamped with the origin it does have, so that re-indexing that
    /// origin takes its opinion back with it.</summary>
    private IReadOnlyList<string> DeclaringSolutions(string template, string store) =>
        _cacheKeyIndex.TryGetValue((template, store), out var accumulator) ? accumulator.Solutions : [];

    private static string SolutionOf(GraphOrigin origin) => origin.Solution ?? origin.Database ?? "(annotation)";

    private CacheKey AddCacheKey(GraphOrigin origin, CacheKey key)
    {
        _cacheKeySites.Add((origin, key));
        var accumulator = Accumulator(key);
        accumulator.Set.Add(key);
        accumulator.Declare(SolutionOf(origin));
        Invalidate(key.Template, key.Store);
        return FindCacheKey(key.Template, key.Store);
    }

    private CacheKey AddCacheKeyObservation(GraphOrigin origin, CacheKey key)
    {
        _cacheKeyObservations.Add((origin, key));
        var accumulator = Accumulator(key);
        accumulator.Observed.Add(key);
        accumulator.Declare(SolutionOf(origin));
        Invalidate(key.Template, key.Store);
        return FindCacheKey(key.Template, key.Store);
    }

    private Table AddTable(GraphOrigin origin, Table table)
    {
        _tableSites.Add((origin, table));
        Index(_tableIndex, table.Name, table, table.Database);
        return FindTable(table.Name);
    }

    private StoredProcedure AddProcedure(GraphOrigin origin, StoredProcedure procedure)
    {
        _procedureSites.Add((origin, procedure));
        Index(_procedureIndex, procedure.Name, procedure, procedure.Database);
        return FindProcedure(procedure.Name);
    }

    private Trigger AddTrigger(GraphOrigin origin, Trigger trigger)
    {
        _triggerSites.Add((origin, trigger));
        Index(_triggerIndex, trigger.Name, trigger, trigger.Database);
        return FindTrigger(trigger.Name);
    }

    private View AddView(GraphOrigin origin, View view)
    {
        _viewSites.Add((origin, view));
        Index(_viewIndex, view.Name, view, view.Database);
        return FindView(view.Name);
    }

    private Event AddEvent(GraphOrigin origin, Event @event)
    {
        _eventSites.Add((origin, @event));
        _eventIndex.TryAdd(@event.FullName, @event);
        return FindEvent(@event.FullName);
    }

    private ExternalSource AddExternal(GraphOrigin origin, ExternalSource source)
    {
        _externalSites.Add((origin, source));
        _externalIndex.TryAdd(ExternalId(source), source);
        return FindExternal(source);
    }

    private Handler AddHandlerSite(Handler handler)
    {
        var id = (handler.Solution, handler.Symbol);
        _handlerSites.Add(handler);
        _handlerSiteKeys.Add(id);
        if (!_handlerIndex.TryGetValue(id, out var accumulator))
        {
            accumulator = new HandlerAccumulator();
            _handlerIndex[id] = accumulator;
        }
        accumulator.Add(handler);
        return accumulator.Build();
    }

    private void AddIndexedDatabaseSite(GraphOrigin origin, string name)
    {
        _indexedDatabases.Add((origin, name));
        if (_databaseNameSet.Add(name))
            _databaseNames.Add(name);
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
                AddProcedure(origin, procedure);
                break;
            case Trigger trigger:
                AddTrigger(origin, trigger);
                break;
            case View view:
                AddView(origin, view);
                break;
            case Event @event:
                AddEvent(origin, @event);
                break;
            case ExternalSource source:
                AddExternal(origin, source);
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

    /// <summary>Rebuilds every vertex index from the site lists. Only <see cref="Replace"/> needs this:
    /// the accumulators merge TTLs and tags irreversibly, so a bulk removal cannot be undone in place.</summary>
    private void RebuildIndexes()
    {
        _cacheKeyIndex.Clear();
        _tableIndex.Clear();
        _procedureIndex.Clear();
        _triggerIndex.Clear();
        _viewIndex.Clear();
        _eventIndex.Clear();
        _externalIndex.Clear();
        _handlerIndex.Clear();
        _handlerSiteKeys.Clear();
        _databaseNames.Clear();
        _databaseNameSet.Clear();

        foreach (var site in _cacheKeySites)
        {
            var accumulator = Accumulator(site.Key);
            accumulator.Set.Add(site.Key);
            accumulator.Declare(SolutionOf(site.Origin));
        }
        foreach (var site in _cacheKeyObservations)
        {
            var accumulator = Accumulator(site.Key);
            accumulator.Observed.Add(site.Key);
            accumulator.Declare(SolutionOf(site.Origin));
        }
        foreach (var site in _tableSites)
            Index(_tableIndex, site.Table.Name, site.Table, site.Table.Database);
        foreach (var site in _procedureSites)
            Index(_procedureIndex, site.Procedure.Name, site.Procedure, site.Procedure.Database);
        foreach (var site in _triggerSites)
            Index(_triggerIndex, site.Trigger.Name, site.Trigger, site.Trigger.Database);
        foreach (var site in _viewSites)
            Index(_viewIndex, site.View.Name, site.View, site.View.Database);
        foreach (var site in _eventSites)
            _eventIndex.TryAdd(site.Event.FullName, site.Event);
        foreach (var site in _externalSites)
            _externalIndex.TryAdd(ExternalId(site.Source), site.Source);
        foreach (var handler in _handlerSites)
        {
            var id = (handler.Solution, handler.Symbol);
            _handlerSiteKeys.Add(id);
            if (!_handlerIndex.TryGetValue(id, out var accumulator))
            {
                accumulator = new HandlerAccumulator();
                _handlerIndex[id] = accumulator;
            }
            accumulator.Add(handler);
        }
        foreach (var site in _indexedDatabases)
        {
            if (_databaseNameSet.Add(site.Name))
                _databaseNames.Add(site.Name);
        }

        _vertexListsDirty = true;
    }

    private KeyAccumulator Accumulator(CacheKey key)
    {
        var id = (key.Template, key.Store);
        if (!_cacheKeyIndex.TryGetValue(id, out var accumulator))
        {
            accumulator = new KeyAccumulator();
            _cacheKeyIndex[id] = accumulator;
        }
        return accumulator;
    }

    private static void Index<T>(Dictionary<string, NamedVertex<T>> index, string name, T vertex, string? database)
    {
        if (!index.TryGetValue(name, out var existing))
            index[name] = new NamedVertex<T>(vertex, database);
        else if (existing.Database is null && database is not null)
            index[name] = new NamedVertex<T>(vertex, database);
    }

    private static (string, string, string, string?, string) ExternalId(ExternalSource source) =>
        (source.Kind, source.Method, source.Template, source.ClientName, source.Owner);

    private void Invalidate(string template, string store)
    {
        if (_cacheKeyIndex.TryGetValue((template, store), out var accumulator))
            accumulator.Built = null;
        _vertexListsDirty = true;
    }

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

    private CacheKey FindCacheKey(string template, string store)
    {
        if (!_cacheKeyIndex.TryGetValue((template, store), out var accumulator))
            throw new InvalidOperationException($"The graph holds no cache key '{template}' in '{store}'.");

        return accumulator.Built ??= Build(template, store, accumulator);
    }

    private CacheKey Build(string template, string store, KeyAccumulator accumulator)
    {
        var facts = accumulator.Effective;
        var role = _roleOverrides.TryGetValue((template, store), out var overrideRole)
                       ? overrideRole
                       : ClassifiedRole(template, store) ?? facts.Role;
        return new CacheKey(template, store, facts.Ttl, facts.TagsAll ?? [], facts.TagsAny, role) { TtlAgreed = facts.TtlAgreed };
    }

    private Table FindTable(string name) => Find(_tableIndex, name, "table");
    private StoredProcedure FindProcedure(string name) => Find(_procedureIndex, name, "stored procedure");
    private Trigger FindTrigger(string name) => Find(_triggerIndex, name, "trigger");
    private View FindView(string name) => Find(_viewIndex, name, "view");

    private static T Find<T>(Dictionary<string, NamedVertex<T>> index, string name, string what) =>
        index.TryGetValue(name, out var found) ? found.Vertex
            : throw new InvalidOperationException($"The graph holds no {what} '{name}'.");

    private Event FindEvent(string fullName) =>
        _eventIndex.TryGetValue(fullName, out var found) ? found
            : throw new InvalidOperationException($"The graph holds no event '{fullName}'.");

    private ExternalSource FindExternal(ExternalSource source) =>
        _externalIndex.TryGetValue(ExternalId(source), out var found) ? found
            : throw new InvalidOperationException($"The graph holds no external source '{source.Kind}:{source.Owner}:{source.Method} {source.Template}'.");

    private ExternalSource CanonicalExternal(ExternalSource source) => FindExternal(source);

    private Handler FindHandler(string solution, string symbol) =>
        _handlerIndex.TryGetValue((solution, symbol), out var found) ? found.Build()
            : throw new InvalidOperationException($"The graph holds no handler '{symbol}' in '{solution}'.");

    private bool HasHandler(Handler handler) => HasHandler(handler.Solution, handler.Symbol);
    private bool HasHandler(string solution, string symbol) => _handlerSiteKeys.Contains((solution, symbol));

    private bool HasLiveHandler(GraphEdge edge) =>
        (edge.From is not Handler from || HasHandler(from)) && (edge.To is not Handler to || HasHandler(to));

    private bool MatchesAnnotation(Unresolved unresolved) =>
        _annotations.Any(annotation => annotation.Kind == unresolved.Kind && annotation.Solution == unresolved.Solution &&
                                       annotation.Site.Describe() == unresolved.Site.Describe() && annotation.Snippet == unresolved.Snippet);

    private void Record(Unresolved unresolved)
    {
        _unresolved.Add(unresolved);
        _unresolvedById[unresolved.Id] = unresolved;
    }

    private void Forget(int id)
    {
        _unresolved.RemoveAll(item => item.Id == id);
        _unresolvedById.Remove(id);
    }

    private void RecordOrigin(UnresolvedOrigin origin)
    {
        _unresolvedOriginById[origin.UnresolvedId] = origin;
        var id = (origin.Solution, origin.HandlerSymbol);
        if (!_unresolvedByHandler.TryGetValue(id, out var owned))
        {
            owned = [];
            _unresolvedByHandler[id] = owned;
        }
        owned.Add(origin.UnresolvedId);
    }

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

    /// <summary>The edges grouped by the vertex a walk leaves, shared by every key's closure rather than
    /// rebuilt per key: the grouping costs one pass over the edges and saves each walk a pass per visit.
    /// </summary>
    internal CacheGraphAdjacency GetAdjacency()
    {
        if (_adjacencyVersion != _version || _adjacency is null)
        {
            _adjacency = CacheGraphAdjacency.Build(this);
            _adjacencyVersion = _version;
        }
        return _adjacency;
    }

    internal KeyDependencyWalk GetDependencies(CacheKey key)
    {
        var id = (key.Template, key.Store);
        if (!_dependencies.TryGetValue(id, out var cached) || cached.Version != _version)
        {
            cached = (_version, CacheGraphDependencies.Build(this, key));
            _dependencies[id] = cached;
        }
        return cached.Walk;
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
        if (_verticesVersion == _version && !_vertexListsDirty) return;
        _cacheKeys = _cacheKeyIndex.Select(pair => pair.Value.Built ??= Build(pair.Key.Template, pair.Key.Store, pair.Value)).ToArray();
        _tables = _tableIndex.Values.Select(item => item.Vertex).ToArray();
        _procedures = _procedureIndex.Values.Select(item => item.Vertex).ToArray();
        _triggers = _triggerIndex.Values.Select(item => item.Vertex).ToArray();
        _views = _viewIndex.Values.Select(item => item.Vertex).ToArray();
        _events = _eventIndex.Values.ToArray();
        _externalSources = _externalIndex.Values.ToArray();
        _indexedDatabaseNames = _databaseNames.ToArray();
        _handlers = _handlerIndex.Values.Select(accumulator => accumulator.Build()).ToArray();
        _builtHeuristicWriteSites = _heuristicWriteSites.Select(site => site.Site).ToArray();
        _builtCacheOperations = BuildCacheOperations();
        _builtPendingCacheOperations = BuildPendingCacheOperations();
        _verticesVersion = _version;
        _vertexListsDirty = false;
        VertexRebuilds++;
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

    private void Touch()
    {
        _version++;
        _vertexListsDirty = true;
    }

    private static Confidence Weaken(Confidence first, Confidence second, Confidence minimum = Confidence.Confirmed) =>
        (Confidence)Math.Max((int)minimum, Math.Max((int)first, (int)second));

    private sealed record UnresolvedOrigin(int UnresolvedId, string Solution, string HandlerSymbol);

    private sealed record NamedVertex<T>(T Vertex, string? Database);

    /// <summary>What one key's sites say. Set sites win outright when there are any, exactly as reading a
    /// key back tells you what the writer wrote rather than what a reader guessed.</summary>
    private sealed class KeyAccumulator
    {
        private readonly HashSet<string> _seenSolutions = new(StringComparer.Ordinal);
        private readonly List<string> _solutions = [];

        internal KeyFacts Set { get; } = new();
        internal KeyFacts Observed { get; } = new();
        internal CacheKey? Built { get; set; }
        internal KeyFacts Effective => Set.Count > 0 ? Set : Observed;

        /// <summary>Every origin that declared this key, in the order they first did. A role is stamped
        /// with each of them, so re-indexing one takes only its own opinion back.</summary>
        internal IReadOnlyList<string> Solutions => _solutions;

        internal void Declare(string solution)
        {
            if (_seenSolutions.Add(solution))
                _solutions.Add(solution);
        }
    }

    private sealed class KeyFacts
    {
        private bool _seen;
        private TimeSpan? _first;
        private TimeSpan? _longest;
        private bool _anyNull;

        internal int Count { get; private set; }
        internal bool TtlAgreed { get; private set; } = true;
        internal HashSet<string>? TagsAll { get; private set; }
        internal HashSet<string> TagsAny { get; } = new(StringComparer.Ordinal);
        internal string? Role { get; private set; }

        /// <summary>The longest TTL the sites named, and <c>null</c> for good once any site named none: a
        /// key one writer sets without an expiry does not expire.</summary>
        internal TimeSpan? Ttl => _anyNull ? null : _longest;

        internal void Add(CacheKey key)
        {
            Count++;
            if (!_seen)
            {
                _seen = true;
                _first = key.Ttl;
            }
            else if (_first != key.Ttl)
            {
                TtlAgreed = false;
            }

            if (key.Ttl is null) _anyNull = true;
            else if (_longest is null || key.Ttl > _longest) _longest = key.Ttl;

            if (TagsAll is null) TagsAll = new HashSet<string>(key.TagsAny, StringComparer.Ordinal);
            else TagsAll.IntersectWith(key.TagsAny);
            TagsAny.UnionWith(key.TagsAny);
            Role ??= key.Role;
        }
    }

    private sealed class HandlerAccumulator
    {
        private readonly HashSet<HandlerRoute> _seenRoutes = [];
        private readonly List<HandlerRoute> _routes = [];
        private Handler? _first;
        private string? _project;
        private Handler? _built;

        internal void Add(Handler handler)
        {
            _first ??= handler;
            if (string.IsNullOrEmpty(_project) && !string.IsNullOrEmpty(handler.Project))
                _project = handler.Project;
            foreach (var route in handler.Routes)
            {
                if (_seenRoutes.Add(route))
                    _routes.Add(route);
            }
            _built = null;
        }

        internal Handler Build() => _built ??= _first! with { Project = _project, Routes = _routes.ToArray() };
    }
}
