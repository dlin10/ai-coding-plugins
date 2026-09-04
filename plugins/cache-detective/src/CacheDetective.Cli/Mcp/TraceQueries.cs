using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CacheDetective.Caching;
using CacheDetective.Configuration;
using CacheDetective.Serialization;
using CacheDetective.Graph;

namespace CacheDetective.Mcp;

internal static class TraceQueries
{
    /// <summary>Raised from 1 for phase 2: the export now carries procedure, view and trigger nodes and
    /// the <c>fires</c> edge, so a graph captured under phase 1 is no longer directly comparable.</summary>
    private const int GRAPH_EXPORT_VERSION = 2;

    internal static TraceKeyResult TraceKey(CacheGraph graph, string template, string? store, PageArguments page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        var matches = graph.CacheKeys.Where(key => key.Template == template && (store is null || key.Store == store)).ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException(store is null
                                                    ? $"Cache key template '{template}' was not found."
                                                    : $"Cache key template '{template}' was not found in store '{store}'.");
        }

        if (matches.Length > 1)
        {
            var stores = string.Join(", ", matches.Select(key => key.Store).Order(StringComparer.Ordinal));
            throw new InvalidOperationException($"Cache key template '{template}' exists in multiple stores ({stores}); specify store.");
        }

        var key = matches[0];
        return Bounded(page, (effectivePage, notice) => BuildKeyTrace(graph, key, effectivePage, notice), TypeInfo<TraceKeyResult>());
    }

    internal static TraceTableResult TraceTable(CacheGraph graph, string name, PageArguments page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var table = graph.Tables.SingleOrDefault(candidate => candidate.Name == name) ?? throw new InvalidOperationException($"Table '{name}' was not found.");
        return Bounded(page, (effectivePage, notice) => BuildTableTrace(graph, table, effectivePage, notice), TypeInfo<TraceTableResult>());
    }

    internal static GraphExport ExportGraph(CacheGraph graph, WorkspaceConfiguration? configuration, string? filter)
    {
        var nodes = graph.CacheKeys.Select(KeyNode)
                         .Concat(graph.Tables.Select(TableNode))
                         .Concat(graph.Handlers.Select(HandlerNode))
                         .Concat(graph.StoredProcedures.Select(ProcedureNode))
                         .Concat(graph.Views.Select(ViewNode))
                         .Concat(graph.Triggers.Select(TriggerNode))
                         .OrderBy(node => node.Id, StringComparer.Ordinal)
                         .ToArray();

        var edges = graph.Edges.Select(ExportEdge)
                         .OrderBy(edge => edge.From, StringComparer.Ordinal)
                         .ThenBy(edge => edge.To, StringComparer.Ordinal)
                         .ThenBy(edge => edge.Type, StringComparer.Ordinal)
                         .ToArray();

        var unresolved = graph.Unresolved.Select(item => new UnresolvedExport($"u:{item.Id}",
                                                                              Kind(item.Kind),
                                                                              item.Solution,
                                                                              item.File,
                                                                              item.Line,
                                                                              item.Site.Database,
                                                                              item.Site.ObjectName,
                                                                              item.Snippet,
                                                                              item.Reason))
                              .ToArray();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var selectedIds = nodes.Where(node => Matches(node, filter)).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            var selectedEdges = edges.Where(edge => Matches(edge, filter) || selectedIds.Contains(edge.From) || selectedIds.Contains(edge.To)).ToArray();
            foreach (var edge in selectedEdges)
            {
                selectedIds.Add(edge.From);
                selectedIds.Add(edge.To);
            }

            nodes = nodes.Where(node => selectedIds.Contains(node.Id)).ToArray();
            edges = selectedEdges;
            unresolved = unresolved.Where(item => Matches(item, filter)).ToArray();
        }

        return new GraphExport(GRAPH_EXPORT_VERSION,
                               new GraphWorkspaceExport(configuration?.Solutions ?? [], configuration?.Databases, configuration?.Services),
                               nodes,
                               edges,
                               unresolved,
                               []);
    }

    private static TraceKeyResult BuildKeyTrace(CacheGraph graph, CacheKey key, PageArguments page, string? notice)
    {
        var cachedBy = graph.Edges.OfType<Caches>()
                            .Where(edge => SameKey(edge.To, key))
                            .Select(HandlerTrace)
                            .OrderBy(handler => handler.Solution, StringComparer.Ordinal)
                            .ThenBy(handler => handler.Symbol, StringComparer.Ordinal)
                            .ToArray();
        var dependencies = graph.DependsOn(key)
                                .GroupBy(dependency => NodeId(dependency.Target), StringComparer.Ordinal)
                                .Select(group => group.OrderBy(item => item.Confidence).ThenBy(item => item.Path.Count).First())
                                .Select(DependencyTrace)
                                .OrderBy(dependency => dependency.Id, StringComparer.Ordinal)
                                .ToArray();
        var invalidatedBy = graph.Edges.OfType<Invalidates>()
                                 .Where(edge => CacheKeyCovering.Covers(edge, key, key.TagsAny))
                                 .Select(edge => InvalidationTrace(edge, key))
                                 .OrderBy(invalidation => invalidation.Handler.Solution, StringComparer.Ordinal)
                                 .ThenBy(invalidation => invalidation.Handler.Symbol, StringComparer.Ordinal)
                                 .ToArray();

        return new TraceKeyResult(key.Template,
                                  key.Store,
                                  key.TtlSeconds,
                                  key.Role,
                                  Page(cachedBy, page),
                                  Page(dependencies, page),
                                  Page(invalidatedBy, page),
                                  notice);
    }

    private static TraceTableResult BuildTableTrace(CacheGraph graph, Table table, PageArguments page, string? notice)
    {
        var readBy = graph.Edges.OfType<Reads>()
                          .Where(edge => edge.To is Table candidate && candidate.Name == table.Name)
                          .Select(AccessTrace)
                          .OrderBy(access => access.Type, StringComparer.Ordinal)
                          .ThenBy(access => access.Name, StringComparer.Ordinal)
                          .ToArray();
        var writtenBy = graph.Edges.OfType<Writes>()
                             .Where(edge => ((Table)edge.To).Name == table.Name)
                             .Select(AccessTrace)
                             .OrderBy(access => access.Type, StringComparer.Ordinal)
                             .ThenBy(access => access.Name, StringComparer.Ordinal)
                             .ToArray();
        var triggers = graph.Edges.OfType<Fires>()
                            .Where(edge => ((Table)edge.From).Name == table.Name)
                            .Select(edge => TriggerTrace((Trigger)edge.To))
                            .OrderBy(trigger => trigger.Name, StringComparer.Ordinal)
                            .ToArray();
        var dependentKeys = graph.CacheKeys
                                 .SelectMany(key => graph.DependsOn(key)
                                                         .Where(dependency => dependency.Target is Table candidate && candidate.Name == table.Name)
                                                         .Select(dependency => (Key: key, Dependency: dependency)))
                                 .GroupBy(item => (item.Key.Template, item.Key.Store))
                                 .Select(group => group.OrderBy(item => item.Dependency.Confidence).ThenBy(item => item.Dependency.Path.Count).First())
                                 .Select(item => new DependentKeyTrace(item.Key.Template,
                                                                       item.Key.Store,
                                                                       ConfidenceName(item.Dependency.Confidence),
                                                                       item.Dependency.Path.Select(ExportEdge).ToArray()))
                                 .OrderBy(key => key.Template, StringComparer.Ordinal)
                                 .ThenBy(key => key.Store, StringComparer.Ordinal)
                                 .ToArray();

        return new TraceTableResult(table.Name,
                                    table.Database,
                                    Page(readBy, page),
                                    Page(writtenBy, page),
                                    Page(triggers, page),
                                    Page(dependentKeys, page),
                                    notice);
    }

    internal static T Bounded<T>(PageArguments requested, Func<PageArguments, string?, T> build, JsonTypeInfo<T> typeInfo)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requested.Page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requested.PageSize);
        for (var pageSize = requested.PageSize; pageSize > 0; pageSize--)
        {
            var notice = pageSize == requested.PageSize
                             ? null
                             : $"Page size was reduced from {requested.PageSize} to {pageSize} to stay under " +
                               $"the {ResponseEnvelope.MaximumSerializedBytes}-byte response limit.";
            var result = build(new PageArguments { Page = requested.Page, PageSize = pageSize }, notice);
            if (JsonSerializer.SerializeToUtf8Bytes(result, typeInfo).Length <= ResponseEnvelope.MaximumSerializedBytes)
            {
                return result;
            }
        }

        throw new InvalidOperationException($"The trace response cannot fit within {ResponseEnvelope.MaximumSerializedBytes} bytes.");
    }

    internal static ListEnvelope<T> Page<T>(IReadOnlyList<T> items, PageArguments page) =>
        ResponseEnvelope.Create(items, page, TypeInfo<ListEnvelope<T>>());

    private static HandlerEdgeTrace HandlerTrace(GraphEdge edge)
    {
        var handler = (Handler)edge.From;
        return new HandlerEdgeTrace(handler.Solution,
                                    handler.Symbol,
                                    handler.Kind,
                                    handler.File,
                                    handler.Line,
                                    ConfidenceName(edge.Confidence),
                                    Evidence(edge.Evidence));
    }

    /// <summary>One line of a table's readers or writers. A handler names a file and a line; a procedure,
    /// trigger or view names a database object instead — the provenance the catalogue half carries.</summary>
    private static TableAccessTrace AccessTrace(GraphEdge edge)
    {
        var confidence = ConfidenceName(edge.Confidence);
        var evidence = Evidence(edge.Evidence);
        return edge.From switch
               {
                   Handler handler => new TableAccessTrace("handler",
                                                           handler.Symbol,
                                                           handler.Solution,
                                                           handler.Kind,
                                                           handler.File,
                                                           handler.Line,
                                                           null,
                                                           confidence,
                                                           evidence),
                   StoredProcedure procedure => new TableAccessTrace("procedure",
                                                                     procedure.Name,
                                                                     null,
                                                                     null,
                                                                     null,
                                                                     null,
                                                                     procedure.Database,
                                                                     confidence,
                                                                     evidence),
                   Trigger trigger => new TableAccessTrace("trigger", trigger.Name, null, null, null, null, trigger.Database, confidence, evidence),
                   View view => new TableAccessTrace("view", view.Name, null, null, null, null, view.Database, confidence, evidence),
                   _ => throw new ArgumentOutOfRangeException(nameof(edge))
               };
    }

    private static TriggerTrace TriggerTrace(Trigger trigger) =>
        new(trigger.Name, trigger.Table, trigger.Database, trigger.Events.Select(EventName).Order(StringComparer.Ordinal).ToArray());

    private static DependencyTrace DependencyTrace(KeyDependency dependency)
    {
        var target = dependency.Target;
        return target switch
               {
                   Table table => new DependencyTrace("Table",
                                                      NodeId(table),
                                                      table.Name,
                                                      null,
                                                      ConfidenceName(dependency.Confidence),
                                                      dependency.Path.Select(ExportEdge).ToArray()),
                   CacheKey key => new DependencyTrace("CacheKey",
                                                       NodeId(key),
                                                       key.Template,
                                                       key.Store,
                                                       ConfidenceName(dependency.Confidence),
                                                       dependency.Path.Select(ExportEdge).ToArray()),
                   _ => throw new ArgumentOutOfRangeException(nameof(dependency))
               };
    }

    private static InvalidationTrace InvalidationTrace(Invalidates edge, CacheKey key) => new(HandlerTrace(edge),
                                                                                              SemanticName(edge.Semantic),
                                                                                              ((CacheKey)edge.To).Template,
                                                                                              ((CacheKey)edge.To).Store,
                                                                                              CacheKeyCovering.Covers(edge, key, key.TagsAll));

    private static GraphNodeExport KeyNode(CacheKey key) => new(NodeId(key),
                                                                "CacheKey",
                                                                key.Template,
                                                                null,
                                                                key.Store,
                                                                key.TtlSeconds,
                                                                key.TagsAll.Order(StringComparer.Ordinal).ToArray(),
                                                                key.TagsAny.Order(StringComparer.Ordinal).ToArray(),
                                                                key.Role,
                                                                null,
                                                                null,
                                                                null,
                                                                null,
                                                                null,
                                                                null,
                                                                null,
                                                                null);

    private static GraphNodeExport TableNode(Table table) =>
        new(NodeId(table), "Table", null, table.Name, null, null, null, null, null, table.Database, null, null, null, null, null, null, null);

    private static GraphNodeExport HandlerNode(Handler handler) => new(NodeId(handler),
                                                                       "Handler",
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       handler.Solution,
                                                                       handler.Symbol,
                                                                       handler.Kind,
                                                                       handler.File,
                                                                       handler.Line,
                                                                       null,
                                                                       null);

    private static GraphNodeExport ProcedureNode(StoredProcedure procedure) => new(NodeId(procedure),
                                                                                   "StoredProcedure",
                                                                                   null,
                                                                                   procedure.Name,
                                                                                   null,
                                                                                   null,
                                                                                   null,
                                                                                   null,
                                                                                   null,
                                                                                   procedure.Database,
                                                                                   null,
                                                                                   null,
                                                                                   null,
                                                                                   null,
                                                                                   null,
                                                                                   null,
                                                                                   null);

    private static GraphNodeExport ViewNode(View view) =>
        new(NodeId(view), "View", null, view.Name, null, null, null, null, null, view.Database, null, null, null, null, null, null, null);

    private static GraphNodeExport TriggerNode(Trigger trigger) => new(NodeId(trigger),
                                                                       "Trigger",
                                                                       null,
                                                                       trigger.Name,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       trigger.Database,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       null,
                                                                       trigger.Table,
                                                                       trigger.Events.Select(EventName).Order(StringComparer.Ordinal).ToArray());

    private static GraphEdgeExport ExportEdge(GraphEdge edge) => new(NodeId(edge.From),
                                                                     NodeId(edge.To),
                                                                     EdgeType(edge),
                                                                     ConfidenceName(edge.Confidence),
                                                                     Evidence(edge.Evidence),
                                                                     edge is Invalidates invalidates ? SemanticName(invalidates.Semantic) : null,
                                                                     edge is Caches caches ? caches.IsConditionalSet : null);

    internal static string NodeId(GraphVertex vertex) => vertex switch
                                                         {
                                                             CacheKey key => $"key:{key.Store}/{key.Template}",
                                                             Table table => $"table:{table.Name}",
                                                             Handler handler => $"handler:{handler.Solution}/{handler.Symbol}",
                                                             StoredProcedure procedure => $"procedure:{procedure.Name}",
                                                             View view => $"view:{view.Name}",
                                                             Trigger trigger => $"trigger:{trigger.Name}",
                                                             _ => throw new ArgumentOutOfRangeException(nameof(vertex))
                                                         };

    internal static string EdgeType(GraphEdge edge) => edge switch
                                                       {
                                                           Reads => "reads",
                                                           Writes => "writes",
                                                           Caches => "caches",
                                                           Invalidates => "invalidates",
                                                           Calls => "calls",
                                                           Fires => "fires",
                                                           _ => throw new ArgumentOutOfRangeException(nameof(edge))
                                                       };

    private static string EventName(WriteEvent writeEvent) => writeEvent.ToString().ToLowerInvariant();

    internal static string ConfidenceName(Confidence confidence) => confidence.ToString().ToLowerInvariant();

    private static string SemanticName(CacheSemantic semantic) => semantic switch
                                                                  {
                                                                      CacheSemantic.Get => "get",
                                                                      CacheSemantic.Set => "set",
                                                                      CacheSemantic.Remove => "remove",
                                                                      CacheSemantic.RemoveByTag => "remove_by_tag",
                                                                      CacheSemantic.RemoveByPrefix => "remove_by_prefix",
                                                                      CacheSemantic.Increment => "increment",
                                                                      CacheSemantic.Expire => "expire",
                                                                      CacheSemantic.Lock => "lock",
                                                                      _ => throw new ArgumentOutOfRangeException(nameof(semantic))
                                                                  };

    private static string Kind(UnresolvedKind kind) => kind switch
                                                       {
                                                           UnresolvedKind.Key => "key",
                                                           UnresolvedKind.Sql => "sql",
                                                           UnresolvedKind.Call => "call",
                                                           UnresolvedKind.CacheApi => "cache_api",
                                                           UnresolvedKind.Role => "role",
                                                           _ => throw new ArgumentOutOfRangeException(nameof(kind))
                                                       };

    private static string[] Evidence(IEnumerable<Evidence> evidence) => evidence.Select(item => item.Describe()).ToArray();

    private static bool SameKey(GraphVertex candidate, CacheKey key) =>
        candidate is CacheKey candidateKey && candidateKey.Template == key.Template && candidateKey.Store == key.Store;

    private static bool Matches(GraphNodeExport node, string filter) =>
        Contains(node.Id, filter) || Contains(node.Type, filter) || Contains(node.Template, filter) || Contains(node.Name, filter) ||
        Contains(node.Store, filter) || Contains(node.Solution, filter) || Contains(node.Symbol, filter);

    private static bool Matches(GraphEdgeExport edge, string filter) =>
        Contains(edge.From, filter) || Contains(edge.To, filter) || Contains(edge.Type, filter) || Contains(edge.Semantic, filter);

    private static bool Matches(UnresolvedExport unresolved, string filter) =>
        Contains(unresolved.Id, filter) || Contains(unresolved.Kind, filter) || Contains(unresolved.Solution, filter) || Contains(unresolved.File, filter) ||
        Contains(unresolved.ObjectName, filter) || Contains(unresolved.Snippet, filter) || Contains(unresolved.Reason, filter);

    private static bool Contains(string? value, string filter) => value?.Contains(filter, StringComparison.OrdinalIgnoreCase) is true;

    internal static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)(CacheDetectiveJsonContext.Default.GetTypeInfo(typeof(T)) ??
                                                                       throw new InvalidOperationException($"No generated JSON metadata exists for {typeof(T).Name}."));
}

internal sealed record HandlerEdgeTrace(string Solution, string Symbol, string Kind, string File, int Line,
                                        string Confidence, IReadOnlyList<string> Evidence);
internal sealed record DependencyTrace(string Type, string Id, string Name, string? Store, string Confidence,
                                       IReadOnlyList<GraphEdgeExport> Path);
internal sealed record InvalidationTrace(HandlerEdgeTrace Handler, string Semantic, string Template,
                                         string Store, bool CoversAll);
internal sealed record DependentKeyTrace(string Template, string Store, string Confidence,
                                         IReadOnlyList<GraphEdgeExport> Path);
internal sealed record TraceKeyResult(string Template, string Store, double? Ttl, string? Role,
                                      ListEnvelope<HandlerEdgeTrace> CachedBy,
                                      ListEnvelope<DependencyTrace> Dependencies,
                                      ListEnvelope<InvalidationTrace> InvalidatedBy,
                                      string? Notice);
internal sealed record TableAccessTrace(string Type, string Name, string? Solution, string? Kind,
                                        string? File, int? Line, string? Database, string Confidence,
                                        IReadOnlyList<string> Evidence);
internal sealed record TriggerTrace(string Name, string Table, string? Database,
                                    IReadOnlyList<string> Events);
internal sealed record TraceTableResult(string Name, string? Database,
                                        ListEnvelope<TableAccessTrace> ReadBy,
                                        ListEnvelope<TableAccessTrace> WrittenBy,
                                        ListEnvelope<TriggerTrace> Triggers,
                                        ListEnvelope<DependentKeyTrace> DependentKeys,
                                        string? Notice);
internal sealed record GraphWorkspaceExport(IReadOnlyList<string> Solutions,
                                            IReadOnlyList<DatabaseConfiguration>? Databases,
                                            JsonElement? Services);
internal sealed record GraphNodeExport(string Id, string Type, string? Template, string? Name, string? Store,
                                       double? Ttl, IReadOnlyList<string>? Tags,
                                       IReadOnlyList<string>? TagsAny, string? Role, string? Database,
                                       string? Solution, string? Symbol, string? Kind, string? File, int? Line,
                                       string? Table, IReadOnlyList<string>? Events);
internal sealed record GraphEdgeExport(string From, string To, string Type, string Confidence,
                                       IReadOnlyList<string> Evidence, string? Semantic, bool? Conditional);
internal sealed record UnresolvedExport(string Id, string Kind, string? Solution, string? File, int? Line,
                                        string? Database, string? ObjectName,
                                        string Snippet, string Reason);
internal sealed record GraphAnnotationExport(string UnresolvedId, string Kind, JsonElement Resolution,
                                              string? Note);
internal sealed record GraphExport(int Version, GraphWorkspaceExport Workspace,
                                   IReadOnlyList<GraphNodeExport> Nodes,
                                   IReadOnlyList<GraphEdgeExport> Edges,
                                   IReadOnlyList<UnresolvedExport> Unresolved,
                                   IReadOnlyList<GraphAnnotationExport> Annotations);
