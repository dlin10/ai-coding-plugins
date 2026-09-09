using CacheDetective.Caching;
using CacheDetective.Configuration;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Serialization;
using System.Text.Json;

namespace CacheDetective.Mcp;

/// <summary>Applying one agent's resolution to a graph: reading the resolution against the schema its kind
/// allows, adding the edges it names, and describing what the graph looked like before and after so the
/// answer can say which keys and findings the annotation moved.
/// <para>Every edge an annotation creates is <see cref="Confidence.Likely"/>. An agent naming a key or a
/// target is telling us what it believes, not what the compiler saw, and a graph that recorded the two the
/// same way would let a belief silently earn the coverage a fact earns.</para>
/// <para>Nothing here holds the graph. Each member is given the graph it is to read or write, so the session
/// keeps its gate and no collaborator can capture a graph that a re-index has since replaced.</para>
/// </summary>
internal static class AnnotationApplication
{
    internal static void ApplyAnnotation(CacheGraph graph, Unresolved unresolved, JsonElement resolution, int annotationId,
                                         EventGap? eventGap, ServiceJoinGap? serviceGap)
    {
        if (unresolved.Kind == UnresolvedKind.Key && TrySingleString(resolution, "template", out var template) &&
            graph.TryGetPendingCacheOperation(unresolved.Id, out var pending))
        {
            var key = new CacheKey(template, pending.Store, pending.Ttl, pending.Tags, null);
            var operation = new CacheOperation(pending.Handler, key, pending.Semantic, pending.IsConditionalSet, pending.Evidence);
            switch (pending.Semantic)
            {
                case CacheSemantic.Get:
                    graph.AddAnnotationEdge(new Reads(pending.Handler, key, Confidence.Likely, pending.Evidence) { AnnotationId = annotationId });
                    break;
                case CacheSemantic.Set:
                    graph.AddAnnotationEdge(new Caches(pending.Handler, key, Confidence.Likely, pending.Evidence, pending.IsConditionalSet) { AnnotationId = annotationId });
                    break;
                case CacheSemantic.Remove or CacheSemantic.RemoveByTag or CacheSemantic.RemoveByPrefix:
                    // The annotation says what the key is. It does not say the site had no choice, so the
                    // modality the fold recorded travels on the pending operation and is honoured here:
                    // naming the unnameable member of a mixed removal must not create the certainty the
                    // fold refused.
                    graph.AddAnnotationEdge(new Invalidates(pending.Handler, key, Confidence.Likely, pending.Evidence, pending.Semantic)
                    {
                        AnnotationId = annotationId,
                        Modality = pending.Modality,
                        Reason = pending.Modality == InvalidationModality.May
                            ? "This removal may fire with this key: the annotation named the key, but the site's fold " +
                              "left it a choice, so it is not certain to remove this value and does not count as coverage."
                            : null
                    });
                    break;
            }
            graph.AddAnnotationCacheOperation(operation);
            var classification = new CacheRoleClassifier().ClassifyKey(graph, key);
            graph.SetCacheKeyRoleOverride(key.Template, key.Store, classification.Role);
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Sql && TrySql(resolution, out var reads, out var writes, out var procedures))
        {
            if (!graph.TryGetUnresolvedHandler(unresolved.Id, out var sqlHandler))
                throw new ArgumentException("this sql item has no handler");
            foreach (var table in reads)
                graph.AddAnnotationEdge(new Reads(sqlHandler, new Table(TableName(table)), Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            foreach (var table in writes)
                graph.AddAnnotationEdge(new Writes(sqlHandler, new Table(TableName(table)), Confidence.Likely, [unresolved.Site], [WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete]) { AnnotationId = annotationId });
            foreach (var procedure in procedures)
                graph.AddAnnotationEdge(new Calls(sqlHandler, new StoredProcedure(TableName(procedure)), Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Role && TryRole(resolution, out var role, out var store))
        {
            var candidates = graph.CacheKeys.Where(key => key.Template == unresolved.Snippet && (store is null || key.Store == store)).ToArray();
            if (candidates.Length != 1)
                throw new ArgumentException("resolution for kind 'role' must be { role: cache|store, store?: string }; specify store when the template is ambiguous.");
            graph.SetCacheKeyRoleOverride(candidates[0].Template, candidates[0].Store, role);
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Call && IsExternalResolution(resolution)) return;
        if (unresolved.Kind == UnresolvedKind.Call && IsTargetResolution(resolution, out var target))
        {
            var value = target.GetString()!;
            var handler = FindHandler(graph, value);
            if (graph.TryGetExternalSource(unresolved.Id, out var source)) graph.AddServesAnnotation(source, handler, annotationId);
            else if (serviceGap is not null) graph.AddServesAnnotation(serviceGap.Source, handler, annotationId);
            else if (graph.TryGetUnresolvedHandler(unresolved.Id, out var from)) graph.AddAnnotationEdge(new Calls(from, handler, Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Event && TryStrings(resolution, "handlers", out var handlerIds) && resolution.EnumerateObject().Count() == 1 &&
            resolution.TryGetProperty("handlers", out _) && eventGap is not null)
        {
            var handlers = handlerIds.Select(handlerId => FindHandler(graph, handlerId)).ToArray();
            foreach (var handler in handlers)
            {
                graph.AddAnnotationEdge(new Consumes((Event)eventGap.Publish.To, handler, Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            }
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Event && IsExternalResolution(resolution) && eventGap is not null) return;
        if (unresolved.Kind == UnresolvedKind.Event && TryStrings(resolution, "events", out var eventNames) && resolution.EnumerateObject().Count() == 1 &&
            resolution.TryGetProperty("events", out _) &&
            graph.TryGetUnresolvedHandler(unresolved.Id, out var eventHandler) && graph.TryGetEventSiteRole(unresolved.Id, out var eventRole))
        {
            if (eventNames.Length == 0)
                throw new ArgumentException($"resolution for kind '{FindingQueries.KindName(unresolved.Kind)}' must be {ResolutionSchema(unresolved.Kind)}");
            foreach (var name in eventNames)
                graph.AddAnnotationEdge(eventRole == EventSiteRole.Publish
                    ? new Publishes(eventHandler, new Event(name), Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId }
                    : new Consumes(new Event(name), eventHandler, Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            return;
        }
        throw new ArgumentException($"resolution for kind '{FindingQueries.KindName(unresolved.Kind)}' must be {ResolutionSchema(unresolved.Kind)}");
    }

    internal static void ReclassifyBlockedRoles(CacheGraph graph, IReadOnlyList<(int roleUnresolvedId, string template, string store)> blockedRoles)
    {
        foreach (var (roleId, template, store) in blockedRoles)
        {
            var key = graph.CacheKeys.SingleOrDefault(candidate => candidate.Template == template && candidate.Store == store);
            if (key is null) continue;
            var classification = new CacheRoleClassifier().ClassifyKey(graph, key);
            if (classification.Role is "cache" or "store")
            {
                graph.SetCacheKeyRoleOverride(template, store, classification.Role);
                graph.RemoveUnresolved(roleId);
            }
        }
    }

    internal static bool IsExternalResolution(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
                                                                value.TryGetProperty("external", out var external) && external.ValueKind == JsonValueKind.True;

    private static bool IsTargetResolution(JsonElement value, out JsonElement target)
    {
        target = default;
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
               value.TryGetProperty("target", out target) && target.ValueKind == JsonValueKind.String;
    }

    internal static string? EventApiType(string reason)
    {
        const string prefix = "Unknown event bus type ";
        return reason.StartsWith(prefix, StringComparison.Ordinal) ? reason[prefix.Length..].TrimEnd('.') : null;
    }

    internal static string ResolutionSchema(UnresolvedKind kind) => kind switch
    {
        UnresolvedKind.Key => "{ template: string }",
        UnresolvedKind.Sql => "{ reads?: string[], writes?: string[], procs?: string[] }",
        UnresolvedKind.Call => "{ target: handler:<Solution>/<Symbol> } or { external: true }",
        UnresolvedKind.Event => "{ handlers: string[] } or { events: string[] } or { external: true }",
        UnresolvedKind.Role => "{ role: cache|store, store?: string }",
        UnresolvedKind.CacheApi => "{ type, methods: [{ name, semantic, key_arg, ttl_arg?, tags_arg? }], store, " +
                                   "key_object?: { type, template_arg, factories: [{ type, methods, key_arg, args_arg }] } }",
        UnresolvedKind.EventApi => "{ publisher, consumer, methods?, event_argument?, arity?, handle?, handler_kind? }",
        _ => "{}"
    };

    private static bool TrySingleString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
               value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(result = member.GetString()!);
    }

    private static bool TrySql(JsonElement value, out string[] reads, out string[] writes, out string[] procedures)
    {
        reads = []; writes = []; procedures = [];
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(member => member.Name is not ("reads" or "writes" or "procs"))) return false;
        return TryStrings(value, "reads", out reads) && TryStrings(value, "writes", out writes) && TryStrings(value, "procs", out procedures) &&
               reads.Length + writes.Length + procedures.Length > 0;
    }

    private static bool TryStrings(JsonElement value, string property, out string[] result)
    {
        result = [];
        if (!value.TryGetProperty(property, out var member)) return true;
        if (member.ValueKind != JsonValueKind.Array || member.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) return false;
        result = member.EnumerateArray().Select(item => item.GetString()!).ToArray();
        return true;
    }

    private static bool TryRole(JsonElement value, out string role, out string? store)
    {
        role = string.Empty; store = null;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(member => member.Name is not ("role" or "store")) ||
            !value.TryGetProperty("role", out var roleValue) || roleValue.ValueKind != JsonValueKind.String) return false;
        role = roleValue.GetString()!;
        if (role is not ("cache" or "store")) return false;
        if (!value.TryGetProperty("store", out var storeValue)) return true;
        if (storeValue.ValueKind != JsonValueKind.String) return false;
        store = storeValue.GetString();
        return !string.IsNullOrWhiteSpace(store);
    }

    private static Handler FindHandler(CacheGraph graph, string id)
    {
        var handlers = graph.Handlers.Select(item => (Handler: item, Id: $"handler:{item.Solution}/{item.Symbol}")).ToArray();
        var handler = handlers.SingleOrDefault(item => item.Id == id).Handler;
        if (handler is not null)
            return handler;

        var candidates = handlers.OrderBy(item => EditDistance(id, item.Id)).ThenBy(item => item.Id, StringComparer.Ordinal)
                                 .Take(5).Select(item => item.Id).ToArray();
        throw new ArgumentException($"Unknown handler '{id}'." +
                                    (candidates.Length == 0 ? string.Empty : $" Nearest handlers: {string.Join(", ", candidates)}."));
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var current = new int[right.Length + 1];
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                current[rightIndex] = Math.Min(Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                                               previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1));
            previous = current;
        }

        return previous[right.Length];
    }

    private static string TableName(string name) => name.Contains('.') ? name : $"dbo.{name}";

    internal static Dictionary<string, string> KeySnapshots(CacheGraph graph) => graph.CacheKeys.ToDictionary(key => $"key:{key.Store}/{key.Template}", key =>
        string.Join('|', key.Store, key.Template, key.Role, key.TtlSeconds, string.Join(',', key.TagsAll.Order()), string.Join(',', key.TagsAny.Order()),
                    string.Join(';', graph.DependsOn(key).OrderBy(dependency => TraceQueries.NodeId(dependency.Target), StringComparer.Ordinal)
                                             .ThenBy(dependency => EdgeDescription(dependency.Path), StringComparer.Ordinal)
                                             .Select(dependency => $"{TraceQueries.NodeId(dependency.Target)}:{dependency.Confidence}:{EdgeDescription(dependency.Path)}"))));

    internal static Dictionary<string, string> FindingSnapshots(CacheGraph graph, FindingCatalog findingCatalog,
                                                                WorkspaceConfiguration? configuration) =>
        findingCatalog.GetAll(graph, configuration?.Budgets)
        .ToDictionary(snapshot => snapshot.Item.Id, snapshot => string.Join('|', snapshot.Item.Rule,
            JsonSerializer.Serialize(snapshot.Item), string.Join(',', snapshot.SearchedProjects), EdgeDescription(snapshot.Chain), EdgeDescription(snapshot.EventChain)));

    internal static List<T> Changes<T>(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after,
                                       Func<string, string, T> create) => before.Keys.Union(after.Keys, StringComparer.Ordinal)
        .Select(id => (Id: id, Change: !after.ContainsKey(id) ? "removed" : !before.ContainsKey(id) ? "added" : before[id] == after[id] ? null : "changed"))
        .Where(item => item.Change is not null).OrderBy(item => item.Change == "removed" ? 0 : item.Change == "added" ? 1 : 2)
        .ThenBy(item => item.Id, StringComparer.Ordinal).Select(item => create(item.Id, item.Change!)).ToList();

    internal static string FindingRule(string snapshot) => snapshot[..snapshot.IndexOf('|')];

    private static string EdgeDescription(IEnumerable<GraphEdge> edges) => string.Join(';', edges.Select(edge =>
        string.Join('|', TraceQueries.EdgeType(edge), TraceQueries.NodeId(edge.From), TraceQueries.NodeId(edge.To), edge.Confidence,
                    string.Join(',', edge.Evidence.Select(site => site.Describe())), edge.AnnotationId, edge.Reason)));

    internal static AnnotateResult LimitResult(AnnotateResult result)
    {
        var total = result.AffectedKeys.Count + result.AffectedFindings.Count;
        var keys = result.AffectedKeys.ToList();
        var findings = result.AffectedFindings.ToList();
        while (JsonSerializer.SerializeToUtf8Bytes(result, CacheDetectiveJsonContext.Default.AnnotateResult).Length > ResponseEnvelope.MaximumSerializedBytes && (keys.Count > 0 || findings.Count > 0))
        {
            if (keys.Count > 0) keys.RemoveAt(keys.Count - 1); else findings.RemoveAt(findings.Count - 1);
            var omitted = total - keys.Count - findings.Count;
            result = result with { AffectedKeys = keys.ToArray(), AffectedFindings = findings.ToArray(), Truncated = true, Notice = $"{omitted} affected items omitted to fit the response limit." };
        }
        return result;
    }
}
