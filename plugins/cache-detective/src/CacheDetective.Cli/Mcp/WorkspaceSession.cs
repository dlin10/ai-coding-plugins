using CacheDetective.Configuration;
using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Serialization;
using CacheDetective.Database;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Workspaces;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace CacheDetective.Mcp;

/// <summary>Opens a connection to one configured database and reads its catalogue. Substituted in tests,
/// which have no server; the live path is exercised by the integration tests.</summary>
internal delegate Task<DatabaseIndexResult> CatalogueSource(DatabaseConfiguration database, string name,
                                                            CancellationToken cancellationToken);

internal sealed class WorkspaceSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly MsBuildSolutionLoader _loader = new();
    private readonly FindingCatalog _findingCatalog = new();
    private readonly Dictionary<string, DateTimeOffset> _indexedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CacheRecognizer> _declaredCacheRecognizers = [];
    private readonly List<EventRecognizer> _declaredEventRecognizers = [];
    private string? _repositoryRoot;
    private WorkspaceConfiguration? _configuration;

    internal CacheGraph Graph { get; private set; } = new();

    internal async Task<AnnotateResult> AnnotateAsync(string unresolvedId, JsonElement resolution, string? note,
                                                       CancellationToken cancellationToken = default)
    {
        if (!unresolvedId.StartsWith("u:", StringComparison.Ordinal) || !int.TryParse(unresolvedId[2..], out var id))
            throw new InvalidOperationException($"Unresolved '{unresolvedId}' is not in this session.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var procedureGap = ProcedureGaps.Derive(Graph).SingleOrDefault(item => item.Unresolved.Id == id);
            var eventGap = EventGaps.Derive(Graph).SingleOrDefault(item => item.Unresolved.Id == id);
            var serviceGap = ServiceJoins.Derive(Graph).Gaps.SingleOrDefault(item => item.Unresolved.Id == id);
            var unresolved = Graph.Unresolved.SingleOrDefault(item => item.Id == id) ?? procedureGap?.Unresolved ?? eventGap?.Unresolved ?? serviceGap?.Unresolved ??
                             throw new InvalidOperationException($"Unresolved '{unresolvedId}' is not in this session.");
            var beforeKeys = KeySnapshots();
            var beforeFindings = FindingSnapshots();
            var blockedRoles = Graph.RoleRowsBlockedBy(id);
            var annotationId = Graph.NextAnnotationId();
            var reindexed = unresolved.Kind switch
            {
                UnresolvedKind.CacheApi => await DeclareCacheRecognizerAsync(unresolved, resolution, annotationId, cancellationToken).ConfigureAwait(false),
                UnresolvedKind.EventApi => await DeclareEventRecognizerAsync(unresolved, resolution, annotationId, cancellationToken).ConfigureAwait(false),
                _ => null
            };
            if (reindexed is null)
                ApplyAnnotation(unresolved, resolution, annotationId, eventGap, serviceGap);
            if (procedureGap is not null || eventGap is not null || serviceGap is not null)
                Graph.SuppressDerivedUnresolved(id, annotationId, IsExternalResolution(resolution));
            else
                Graph.RemoveUnresolved(id);
            ReclassifyBlockedRoles(blockedRoles);
            Graph.AddAnnotation(new Annotation(annotationId, id, unresolved.Kind, unresolved.Solution, unresolved.Site,
                                               unresolved.Snippet, resolution.GetRawText(), note));
            var keys = Changes(beforeKeys, KeySnapshots(), (key, change) => new AffectedKey(key, change));
            var afterFindings = FindingSnapshots();
            var findings = Changes(beforeFindings, afterFindings, (finding, change) => new AffectedFinding(finding,
                FindingRule(afterFindings.TryGetValue(finding, out var after) ? after : beforeFindings[finding]), change));
            return LimitResult(new AnnotateResult(unresolvedId, annotationId, FindingQueries.KindName(unresolved.Kind), reindexed,
                                                  keys.Count, keys, findings.Count, findings, false, null));
        }
        finally { _gate.Release(); }
    }

    private void ApplyAnnotation(Unresolved unresolved, JsonElement resolution, int annotationId, EventGap? eventGap, ServiceJoinGap? serviceGap)
    {
        if (unresolved.Kind == UnresolvedKind.Key && TrySingleString(resolution, "template", out var template) &&
            Graph.TryGetPendingCacheOperation(unresolved.Id, out var pending))
        {
            var key = new CacheKey(template, pending.Store, pending.Ttl, pending.Tags, null);
            var operation = new CacheOperation(pending.Handler, key, pending.Semantic, pending.IsConditionalSet, pending.Evidence);
            switch (pending.Semantic)
            {
                case CacheSemantic.Get:
                    Graph.AddAnnotationEdge(new Reads(pending.Handler, key, Confidence.Likely, pending.Evidence) { AnnotationId = annotationId });
                    break;
                case CacheSemantic.Set:
                    Graph.AddAnnotationEdge(new Caches(pending.Handler, key, Confidence.Likely, pending.Evidence, pending.IsConditionalSet) { AnnotationId = annotationId });
                    break;
                case CacheSemantic.Remove or CacheSemantic.RemoveByTag or CacheSemantic.RemoveByPrefix:
                    Graph.AddAnnotationEdge(new Invalidates(pending.Handler, key, Confidence.Likely, pending.Evidence, pending.Semantic) { AnnotationId = annotationId });
                    break;
            }
            Graph.AddAnnotationCacheOperation(operation);
            var classification = new CacheRoleClassifier().ClassifyKey(Graph, key);
            Graph.SetCacheKeyRoleOverride(key.Template, key.Store, classification.Role);
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Sql && TrySql(resolution, out var reads, out var writes, out var procedures))
        {
            if (!Graph.TryGetUnresolvedHandler(unresolved.Id, out var sqlHandler))
                throw new ArgumentException("this sql item has no handler");
            foreach (var table in reads)
                Graph.AddAnnotationEdge(new Reads(sqlHandler, new Table(TableName(table)), Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            foreach (var table in writes)
                Graph.AddAnnotationEdge(new Writes(sqlHandler, new Table(TableName(table)), Confidence.Likely, [unresolved.Site], [WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete]) { AnnotationId = annotationId });
            foreach (var procedure in procedures)
                Graph.AddAnnotationEdge(new Calls(sqlHandler, new StoredProcedure(TableName(procedure)), Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Role && TryRole(resolution, out var role, out var store))
        {
            var candidates = Graph.CacheKeys.Where(key => key.Template == unresolved.Snippet && (store is null || key.Store == store)).ToArray();
            if (candidates.Length != 1)
                throw new ArgumentException("resolution for kind 'role' must be { role: cache|store, store?: string }; specify store when the template is ambiguous.");
            Graph.SetCacheKeyRoleOverride(candidates[0].Template, candidates[0].Store, role);
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Call && IsExternalResolution(resolution)) return;
        if (unresolved.Kind == UnresolvedKind.Call && IsTargetResolution(resolution, out var target))
        {
            var value = target.GetString()!;
            var handler = FindHandler(value);
            if (Graph.TryGetExternalSource(unresolved.Id, out var source)) Graph.AddServesAnnotation(source, handler, annotationId);
            else if (serviceGap is not null) Graph.AddServesAnnotation(serviceGap.Source, handler, annotationId);
            else if (Graph.TryGetUnresolvedHandler(unresolved.Id, out var from)) Graph.AddAnnotationEdge(new Calls(from, handler, Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Event && TryStrings(resolution, "handlers", out var handlerIds) && resolution.EnumerateObject().Count() == 1 &&
            resolution.TryGetProperty("handlers", out _) && eventGap is not null)
        {
            var handlers = handlerIds.Select(FindHandler).ToArray();
            foreach (var handler in handlers)
            {
                Graph.AddAnnotationEdge(new Consumes((Event)eventGap.Publish.To, handler, Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            }
            return;
        }
        if (unresolved.Kind == UnresolvedKind.Event && IsExternalResolution(resolution) && eventGap is not null) return;
        if (unresolved.Kind == UnresolvedKind.Event && TryStrings(resolution, "events", out var eventNames) && resolution.EnumerateObject().Count() == 1 &&
            resolution.TryGetProperty("events", out _) &&
            Graph.TryGetUnresolvedHandler(unresolved.Id, out var eventHandler) && Graph.TryGetEventSiteRole(unresolved.Id, out var eventRole))
        {
            if (eventNames.Length == 0)
                throw new ArgumentException($"resolution for kind '{FindingQueries.KindName(unresolved.Kind)}' must be {ResolutionSchema(unresolved.Kind)}");
            foreach (var name in eventNames)
                Graph.AddAnnotationEdge(eventRole == EventSiteRole.Publish
                    ? new Publishes(eventHandler, new Event(name), Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId }
                    : new Consumes(new Event(name), eventHandler, Confidence.Likely, [unresolved.Site]) { AnnotationId = annotationId });
            return;
        }
        throw new ArgumentException($"resolution for kind '{FindingQueries.KindName(unresolved.Kind)}' must be {ResolutionSchema(unresolved.Kind)}");
    }

    private void ReclassifyBlockedRoles(IReadOnlyList<(int roleUnresolvedId, string template, string store)> blockedRoles)
    {
        foreach (var (roleId, template, store) in blockedRoles)
        {
            var key = Graph.CacheKeys.SingleOrDefault(candidate => candidate.Template == template && candidate.Store == store);
            if (key is null) continue;
            var classification = new CacheRoleClassifier().ClassifyKey(Graph, key);
            if (classification.Role is "cache" or "store")
            {
                Graph.SetCacheKeyRoleOverride(template, store, classification.Role);
                Graph.RemoveUnresolved(roleId);
            }
        }
    }

    private async Task<string> DeclareCacheRecognizerAsync(Unresolved unresolved, JsonElement resolution, int annotationId,
                                                            CancellationToken cancellationToken)
    {
        if (resolution.ValueKind != JsonValueKind.Object || !TryString(resolution, "store", out var store) ||
            !resolution.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !resolution.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array ||
            resolution.EnumerateObject().Any(member => member.Name is not ("type" or "methods" or "store")))
        {
            throw new ArgumentException("resolution for kind 'cache_api' must be { type, methods: [{ name, semantic, key_arg, ttl_arg?, tags_arg? }], store }");
        }

        var parsed = new List<CacheMethodRecognizer>();
        foreach (var method in methods.EnumerateArray())
        {
            if (method.ValueKind != JsonValueKind.Object || method.EnumerateObject().Any(member => member.Name is not ("name" or "semantic" or "key_arg" or "ttl_arg" or "tags_arg")) ||
                !TryString(method, "name", out var name) || !TryString(method, "semantic", out var semanticName) ||
                !TryInt(method, "key_arg", out var keyArgument) || !TryOptionalInt(method, "ttl_arg", out var ttlArgument) ||
                !TryOptionalInt(method, "tags_arg", out var tagsArgument) || !TrySemantic(semanticName, out var semantic))
            {
                throw new ArgumentException("resolution for kind 'cache_api' must be { type, methods: [{ name, semantic, key_arg, ttl_arg?, tags_arg? }], store }");
            }
            parsed.Add(new CacheMethodRecognizer(name, semantic, keyArgument, ttlArgument, tagsArgument));
        }
        if (parsed.Count == 0)
            throw new ArgumentException("resolution for kind 'cache_api' must be { type, methods: [{ name, semantic, key_arg, ttl_arg?, tags_arg? }], store }");
        var recognizer = new CacheRecognizer(type.GetString()!, store, parsed, Confidence.Likely, annotationId);
        _declaredCacheRecognizers.Add(recognizer);
        try { return await ReindexAnnotatedSolutionAsync(unresolved.Solution, cancellationToken).ConfigureAwait(false); }
        catch { _declaredCacheRecognizers.Remove(recognizer); throw; }
    }

    private async Task<string> DeclareEventRecognizerAsync(Unresolved unresolved, JsonElement resolution, int annotationId,
                                                            CancellationToken cancellationToken)
    {
        EventRecognizerConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<EventRecognizerConfiguration>(resolution.GetRawText()) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ArgumentException("resolution for kind 'event_api' must be { publisher, consumer, methods?, event_argument?, arity?, handle?, handler_kind? }");
        }

        if (string.IsNullOrWhiteSpace(configuration.Publisher) && (configuration.Publishers is null || configuration.Publishers.Length == 0))
        {
            var publisher = EventApiType(unresolved.Reason);
            if (publisher is null)
                throw new ArgumentException("resolution for kind 'event_api' must be { publisher, consumer, methods?, event_argument?, arity?, handle?, handler_kind? }");
            configuration = new EventRecognizerConfiguration
            {
                Name = configuration.Name,
                Publisher = publisher,
                Methods = configuration.Methods,
                EventArgument = configuration.EventArgument,
                Consumer = configuration.Consumer,
                Arity = configuration.Arity,
                Handle = configuration.Handle,
                HandlerKind = configuration.HandlerKind
            };
        }
        try
        {
            var recognizer = configuration.ToRecognizer(Confidence.Likely, annotationId);
            _declaredEventRecognizers.Add(recognizer);
            try { return await ReindexAnnotatedSolutionAsync(unresolved.Solution, cancellationToken).ConfigureAwait(false); }
            catch { _declaredEventRecognizers.Remove(recognizer); throw; }
        }
        catch (InvalidDataException error)
        {
            throw new ArgumentException("resolution for kind 'event_api' must be { publisher, consumer, methods?, event_argument?, arity?, handle?, handler_kind? }", error);
        }
    }

    private async Task<string> ReindexAnnotatedSolutionAsync(string? solution, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(solution))
            throw new ArgumentException("this item has no solution to reindex");
        var result = await IndexSolutionCoreAsync(solution, null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error ?? $"Could not reindex '{solution}'.");
        return result.Path;
    }

    private static bool TryString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        return value.TryGetProperty(property, out var member) && member.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result = member.GetString()!);
    }

    private static bool IsExternalResolution(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
                                                               value.TryGetProperty("external", out var external) && external.ValueKind == JsonValueKind.True;

    private static bool IsTargetResolution(JsonElement value, out JsonElement target)
    {
        target = default;
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
               value.TryGetProperty("target", out target) && target.ValueKind == JsonValueKind.String;
    }

    private static bool TryInt(JsonElement value, string property, out int result)
    {
        result = default;
        return value.TryGetProperty(property, out var member) && member.TryGetInt32(out result);
    }

    private static bool TryOptionalInt(JsonElement value, string property, out int? result)
    {
        result = null;
        return !value.TryGetProperty(property, out var member) || (member.TryGetInt32(out var number) && (result = number) is not null);
    }

    private static bool TrySemantic(string value, out CacheSemantic semantic) => value switch
    {
        "get" => Set(CacheSemantic.Get, out semantic),
        "set" => Set(CacheSemantic.Set, out semantic),
        "remove" => Set(CacheSemantic.Remove, out semantic),
        "remove_by_tag" => Set(CacheSemantic.RemoveByTag, out semantic),
        "remove_by_prefix" => Set(CacheSemantic.RemoveByPrefix, out semantic),
        "increment" => Set(CacheSemantic.Increment, out semantic),
        "expire" => Set(CacheSemantic.Expire, out semantic),
        "lock" => Set(CacheSemantic.Lock, out semantic),
        _ => Set(default, out semantic, false)
    };

    private static bool Set(CacheSemantic value, out CacheSemantic semantic, bool success = true)
    {
        semantic = value;
        return success;
    }

    private static string? EventApiType(string reason)
    {
        const string prefix = "Unknown event bus type ";
        return reason.StartsWith(prefix, StringComparison.Ordinal) ? reason[prefix.Length..].TrimEnd('.') : null;
    }

    private static string ResolutionSchema(UnresolvedKind kind) => kind switch
    {
        UnresolvedKind.Key => "{ template: string }",
        UnresolvedKind.Sql => "{ reads?: string[], writes?: string[], procs?: string[] }",
        UnresolvedKind.Call => "{ target: handler:<Solution>/<Symbol> } or { external: true }",
        UnresolvedKind.Event => "{ handlers: string[] } or { events: string[] } or { external: true }",
        UnresolvedKind.Role => "{ role: cache|store, store?: string }",
        UnresolvedKind.CacheApi => "{ type, methods: [{ name, semantic, key_arg, ttl_arg?, tags_arg? }], store }",
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

    private Handler FindHandler(string id)
    {
        var handlers = Graph.Handlers.Select(item => (Handler: item, Id: $"handler:{item.Solution}/{item.Symbol}")).ToArray();
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

    private Dictionary<string, string> KeySnapshots() => Graph.CacheKeys.ToDictionary(key => $"key:{key.Store}/{key.Template}", key =>
        string.Join('|', key.Store, key.Template, key.Role, key.TtlSeconds, string.Join(',', key.TagsAll.Order()), string.Join(',', key.TagsAny.Order()),
                    string.Join(';', Graph.DependsOn(key).OrderBy(dependency => TraceQueries.NodeId(dependency.Target), StringComparer.Ordinal)
                                             .ThenBy(dependency => EdgeDescription(dependency.Path), StringComparer.Ordinal)
                                             .Select(dependency => $"{TraceQueries.NodeId(dependency.Target)}:{dependency.Confidence}:{EdgeDescription(dependency.Path)}"))));

    private Dictionary<string, string> FindingSnapshots() => _findingCatalog.GetAll(Graph, _configuration?.Budgets)
        .ToDictionary(snapshot => snapshot.Item.Id, snapshot => string.Join('|', snapshot.Item.Rule,
            JsonSerializer.Serialize(snapshot.Item), string.Join(',', snapshot.SearchedProjects), EdgeDescription(snapshot.Chain), EdgeDescription(snapshot.EventChain)));

    private static List<T> Changes<T>(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after,
                                      Func<string, string, T> create) => before.Keys.Union(after.Keys, StringComparer.Ordinal)
        .Select(id => (Id: id, Change: !after.ContainsKey(id) ? "removed" : !before.ContainsKey(id) ? "added" : before[id] == after[id] ? null : "changed"))
        .Where(item => item.Change is not null).OrderBy(item => item.Change == "removed" ? 0 : item.Change == "added" ? 1 : 2)
        .ThenBy(item => item.Id, StringComparer.Ordinal).Select(item => create(item.Id, item.Change!)).ToList();

    private static string FindingRule(string snapshot) => snapshot[..snapshot.IndexOf('|')];

    private static string EdgeDescription(IEnumerable<GraphEdge> edges) => string.Join(';', edges.Select(edge =>
        string.Join('|', TraceQueries.EdgeType(edge), TraceQueries.NodeId(edge.From), TraceQueries.NodeId(edge.To), edge.Confidence,
                    string.Join(',', edge.Evidence.Select(site => site.Describe())), edge.AnnotationId, edge.Reason)));

    private static AnnotateResult LimitResult(AnnotateResult result)
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

    internal async Task<WorkspaceInitResult> InitializeAsync(string root, IReadOnlyList<string>? solutions, IReadOnlyDictionary<string, double>? budgets,
                                                             CancellationToken cancellationToken = default,
                                                             IReadOnlyDictionary<string, string>? services = null,
                                                             EventRecognizerConfiguration[]? events = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var repositoryRoot = Path.GetFullPath(root);
            var configurationPath = WorkspaceConfigurationStore.GetPath(repositoryRoot);
            var exists = File.Exists(configurationPath);
            var hasOverrides = solutions is not null || budgets is not null || services is not null || events is not null;
            if (!exists && solutions is null)
            {
                throw new InvalidOperationException($"No workspace configuration exists at '{configurationPath}', and no solutions were supplied.");
            }

            var existing = exists
                               ? await WorkspaceConfigurationStore.ReadAsync(repositoryRoot,
                                                                             cancellationToken)
                                                                  .ConfigureAwait(false)
                               : null;
            var configuration = hasOverrides
                                    ? Merge(existing,
                                            repositoryRoot,
                                            solutions,
                                            budgets, services, events)
                                    : existing!;
            var written = hasOverrides && await WorkspaceConfigurationStore.WriteAsync(repositoryRoot,
                                                                                       configuration,
                                                                                       cancellationToken)
                                                                           .ConfigureAwait(false);

            if (!string.Equals(_repositoryRoot,
                               repositoryRoot,
                               StringComparison.OrdinalIgnoreCase))
            {
                Graph = new CacheGraph();
                _indexedAt.Clear();
                _findingCatalog.Reset();
                _declaredCacheRecognizers.Clear();
                _declaredEventRecognizers.Clear();
            }

            _repositoryRoot = repositoryRoot;
            _configuration = configuration;
            Graph.SetServiceMap(configuration.Services ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return new WorkspaceInitResult(configuration,
                                           written);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<WorkspaceStatusResult> GetStatusAsync(PageArguments? page = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return BuildStatus(page);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IndexSolutionResult> IndexSolutionAsync(string path, PageArguments? diagnosticsPage = null,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await IndexSolutionCoreAsync(path, diagnosticsPage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IndexSolutionResult> IndexSolutionCoreAsync(string path, PageArguments? diagnosticsPage,
                                                                    CancellationToken cancellationToken)
    {
        if (_repositoryRoot is null || _configuration is null)
        {
            return new IndexSolutionResult(path,
                                           false,
                                           null,
                                           CurrentCounts(),
                                           PageDiagnostics([], diagnosticsPage),
                                           "workspace_init must be called before index_solution.");
        }

        var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
                                            ? path
                                            : Path.Combine(_repositoryRoot, path));
        var solutionName = NormalizePath(Path.GetRelativePath(_repositoryRoot, fullPath));
        MsBuildLoadResult? loaded = null;
        try
        {
            loaded = await _loader.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var configuredEvents = (_configuration.Events ?? []).Select(configuration => configuration.ToRecognizer(Confidence.Confirmed, null));
            var indexer = new CallGraphIndexer(new IndexerOptions(CacheRecognizers.All.Concat(_declaredCacheRecognizers).ToArray(),
                                                                   EventRecognizers.All.Concat(configuredEvents).Concat(_declaredEventRecognizers).ToArray()));
            var replacement = await indexer.IndexAsync(loaded.Solution, solutionName, cancellationToken).ConfigureAwait(false);
            Graph.ReplaceSolution(solutionName, replacement);
            var indexedAt = DateTimeOffset.UtcNow;
            _indexedAt[solutionName] = indexedAt;
            return new IndexSolutionResult(solutionName, true, indexedAt, CurrentCounts(), PageDiagnostics(loaded.Diagnostics, diagnosticsPage), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new IndexSolutionResult(solutionName, false, null, CurrentCounts(),
                                           PageDiagnostics(loaded?.Diagnostics ?? [], diagnosticsPage), error.Message);
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    internal Task<IndexDatabaseResult> IndexDatabaseAsync(string name, CancellationToken cancellationToken = default) =>
        IndexDatabaseAsync(name,
                           ReadCatalogueAsync,
                           cancellationToken);

    /// <summary>Re-indexing replaces the database's half of the graph through
    /// <see cref="CacheGraph.ReplaceDatabase"/>, exactly as <see cref="IndexSolutionAsync"/> replaces a
    /// solution's. Without it a second call would double every catalogue edge, inflate the counts, and
    /// make the unguarded-write rule report each finding twice.</summary>
    internal async Task<IndexDatabaseResult> IndexDatabaseAsync(string name, CatalogueSource source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_repositoryRoot is null || _configuration is null)
            {
                return Failed(name,
                              "workspace_init must be called before index_database.");
            }

            var configured = FindDatabase(_configuration,
                                          name,
                                          out var error);
            if (configured is null)
            {
                return Failed(name,
                              error!);
            }

            var database = configured.Name ?? name;
            try
            {
                var indexed = await source(configured,
                                           database,
                                           cancellationToken)
                                 .ConfigureAwait(false);
                Graph.ReplaceDatabase(database,
                                      indexed.Graph);
                return new IndexDatabaseResult(database,
                                               true,
                                               DateTimeOffset.UtcNow,
                                               new DatabaseCounts(indexed.Graph.StoredProcedures.Count,
                                                                  indexed.Graph.Triggers.Count,
                                                                  indexed.Graph.Views.Count,
                                                                  indexed.Graph.Edges.Count,
                                                                  indexed.Graph.Unresolved.Count),
                                               CurrentCounts(),
                                               indexed.UnresolvableObjects,
                                               null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure)
            {
                return Failed(database,
                              failure.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        IndexDatabaseResult Failed(string database, string message) =>
            new(database,
                false,
                null,
                new DatabaseCounts(0,
                                   0,
                                   0,
                                   0,
                                   0),
                CurrentCounts(),
                [],
                message);
    }

    private static async Task<DatabaseIndexResult> ReadCatalogueAsync(DatabaseConfiguration database, string name, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ReadOnlyIntent.Apply(database.ResolveConnectionString()));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await new DatabaseIndexer().IndexAsync(connection,
                                                      name,
                                                      cancellationToken)
                                          .ConfigureAwait(false);
    }

    private static DatabaseConfiguration? FindDatabase(WorkspaceConfiguration configuration, string name, out string? error)
    {
        var databases = configuration.Databases ?? [];
        if (databases.Length == 0)
        {
            error = "No database is configured. Add one to the 'databases' array of " + ".cache-detective/workspace.json, as { \"name\": \"shop\", " +
                    "\"connection\": \"env:CD_SHOP_CONN\" }.";
            return null;
        }

        // Matched on the configured name only. A nameless record is refused when the configuration is
        // read, and must not be matched against whatever name the caller happened to type — that would
        // stamp the caller's string onto every vertex read from the catalogue.
        var match = databases.FirstOrDefault(database => string.Equals(database.Name,
                                                                       name,
                                                                       StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var names = string.Join(", ",
                                    databases.Select(database => database.Name));
            error = $"No database named '{name}' is configured. Configured: {names}.";
            return null;
        }

        error = null;
        return match;
    }

    internal IReadOnlyList<UnguardedWriteFinding> GetUnguardedWriteFindings() => new UnguardedWriteRule().Evaluate(Graph,
     _configuration?.Budgets);

    internal async Task<T> ReadGraphAsync<T>(Func<CacheGraph, WorkspaceConfiguration?, T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(Graph,
                        _configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<T> ReadFindingsAsync<T>(Func<CacheGraph, WorkspaceConfiguration?, string?, FindingCatalog, T> read,
                                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(Graph,
                        _configuration,
                        _repositoryRoot,
                        _findingCatalog);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkspaceStatusResult BuildStatus(PageArguments? page)
    {
        if (_configuration is null)
        {
            return new WorkspaceStatusResult(PageSolutions([],
                                                           page),
                                             CurrentCounts());
        }

        var solutions = _configuration.Solutions.Select(solution =>
                                       {
                                           var normalized = NormalizeSolution(_repositoryRoot!,
                                                                              solution);
                                           return _indexedAt.TryGetValue(normalized,
                                                                         out var indexedAt)
                                                      ? new SolutionStatus(solution,
                                                                           true,
                                                                           indexedAt)
                                                      : new SolutionStatus(solution,
                                                                           false,
                                                                           null);
                                       })
                                      .ToArray();
        return new WorkspaceStatusResult(PageSolutions(solutions,
                                                       page),
                                         CurrentCounts());
    }

    private WorkspaceCounts CurrentCounts()
    {
        var invalidations = new OrphanInvalidationRule().Evaluate(Graph);
        var findings = GetUnguardedWriteFindings().Count + new ExternalNoTtlRule().Evaluate(Graph).Count +
                       new StaleParentKeyRule().Evaluate(Graph).Count + invalidations.Orphans.Count + invalidations.PatternMismatches.Count;
        var vertices = Graph.CacheKeys.Count + Graph.Tables.Count + Graph.Handlers.Count + Graph.StoredProcedures.Count + Graph.Triggers.Count +
                       Graph.Views.Count + Graph.Events.Count + Graph.ExternalSources.Count;
        return new WorkspaceCounts(vertices,
                                   Graph.Edges.Count,
                                   findings,
                                   Graph.Unresolved.Count,
                                   Graph.StoredProcedures.Count,
                                   Graph.Triggers.Count,
                                   Graph.Views.Count,
                                   Graph.Events.Count,
                                   Graph.ExternalSources.Count,
                                   Graph.Annotations.Count);
    }

    private static WorkspaceConfiguration Merge(WorkspaceConfiguration? existing, string repositoryRoot, IReadOnlyList<string>? solutions,
                                                IReadOnlyDictionary<string, double>? budgets,
                                                IReadOnlyDictionary<string, string>? services,
                                                EventRecognizerConfiguration[]? events)
    {
        var mergedSolutions = (existing?.Solutions ?? []).Concat(solutions ?? [])
                                                         .Select(solution => NormalizeSolution(repositoryRoot,
                                                                                               solution))
                                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                                         .ToArray();
        if (mergedSolutions.Length == 0)
        {
            throw new InvalidOperationException("At least one solution must be supplied.");
        }

        var mergedBudgets = new Dictionary<string, double>(existing?.Budgets ?? [],
                                                           StringComparer.Ordinal);
        foreach (var (table, seconds) in budgets ?? new Dictionary<string, double>())
        {
            mergedBudgets[table] = seconds;
        }

        return new WorkspaceConfiguration
        {
            Version = WorkspaceConfiguration.CurrentVersion,
            Root = existing?.Root ?? repositoryRoot,
            Solutions = mergedSolutions,
            Budgets = mergedBudgets,
            Databases = existing?.Databases,
            Services = services is null ? existing?.Services : new Dictionary<string, string>(services, StringComparer.OrdinalIgnoreCase),
            Events = events ?? existing?.Events,
            Verify = existing?.Verify,
            Sensitive = existing?.Sensitive
        };
    }

    private static string NormalizeSolution(string repositoryRoot, string solution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solution);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(solution)
                                            ? solution
                                            : Path.Combine(repositoryRoot,
                                                           solution));
        var relative = Path.GetRelativePath(repositoryRoot,
                                            fullPath);
        return NormalizePath(relative);
    }

    private static string NormalizePath(string path) => path.Replace('\\',
                                                                     '/');

    private static ListEnvelope<SolutionStatus> PageSolutions(IReadOnlyList<SolutionStatus> solutions, PageArguments? page) =>
        ResponseEnvelope.Create(solutions,
                                page,
                                CacheDetectiveJsonContext.Default.ListEnvelopeSolutionStatus);

    private static ListEnvelope<WorkspaceDiagnosticResult> PageDiagnostics(IReadOnlyList<Microsoft.CodeAnalysis.WorkspaceDiagnostic> diagnostics,
                                                                           PageArguments? page)
    {
        var mapped = diagnostics.Select(diagnostic => new WorkspaceDiagnosticResult(diagnostic.Kind.ToString(),
                                                                                    diagnostic.Message))
                                .ToArray();
        return ResponseEnvelope.Create(mapped,
                                       page,
                                       CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult);
    }
}

internal sealed record WorkspaceInitResult(WorkspaceConfiguration Configuration, bool Written);
internal sealed record SolutionStatus(string Path, bool Indexed, DateTimeOffset? IndexedAt);
internal sealed record WorkspaceCounts(int Vertices, int Edges, int Findings, int Unresolved,
                                       int Procedures, int Triggers, int Views, int Events, int ExternalSources, int Annotations);
internal sealed record DatabaseCounts(int Procedures, int Triggers, int Views, int Edges, int Unresolved);
internal sealed record IndexDatabaseResult(string Database, bool Succeeded, DateTimeOffset? IndexedAt,
                                           DatabaseCounts Added, WorkspaceCounts Counts,
                                           IReadOnlyList<string> UnresolvableObjects, string? Error);
internal sealed record WorkspaceStatusResult(ListEnvelope<SolutionStatus> Solutions, WorkspaceCounts Counts);
internal sealed record WorkspaceDiagnosticResult(string Kind, string Message);
internal sealed record IndexSolutionResult(string Path, bool Succeeded, DateTimeOffset? IndexedAt,
                                           WorkspaceCounts Counts,
                                           ListEnvelope<WorkspaceDiagnosticResult> Diagnostics,
                                           string? Error);
