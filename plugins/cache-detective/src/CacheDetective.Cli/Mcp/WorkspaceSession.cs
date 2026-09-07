using CacheDetective.Configuration;
using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Serialization;
using CacheDetective.Database;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Rules;
using CacheDetective.Verification;
using CacheDetective.Workspaces;
using Microsoft.Data.SqlClient;
using StackExchange.Redis;
using System.Data.Common;
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
    private readonly Dictionary<string, RememberedIndex> _lastIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Workspace, string Finding, string Version), VerificationRun> _verifications = [];
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
                ConfiguredArity = configuration.ConfiguredArity,
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
        result = 0;
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
                // The verification snapshots were readings of another repository's cache and database.
                _verifications.Clear();
                // And the remembered indexes were another repository's. They are keyed by the solution's
                // name alone, so a solution of the same relative name in the new workspace would have been
                // answered for page two out of the old workspace's diagnostics.
                _lastIndex.Clear();
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
            // Paging through the diagnostics of an index is reading, not indexing. Loading the solution
            // again to answer for page two would cost minutes and could answer differently.
            if ((diagnosticsPage?.Page ?? PageArguments.DefaultPage) > PageArguments.DefaultPage &&
                _repositoryRoot is not null && _lastIndex.TryGetValue(SolutionNameOf(path), out var remembered))
            {
                return Recall(SolutionNameOf(path), remembered, diagnosticsPage);
            }

            return await IndexSolutionCoreAsync(path, diagnosticsPage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One page of a remembered index. The whole diagnostic list — the real ones and any missing-project
    /// names that did not fit beside them — is assembled first and paged exactly once, with the weight of
    /// the result around it reserved.
    /// <para>Paging the diagnostics first and then appending to <em>that page</em> was two bugs at once:
    /// the pages of one index stopped agreeing with each other, because each page re-partitioned a
    /// different mixture, and diagnostics fell out of every page. The envelope also filled its own 8 KB
    /// with no room left for the wrapper, so the result went over the limit as soon as the diagnostics
    /// were numerous enough to fill a page.</para>
    /// </summary>
    internal IndexSolutionResult Recall(string solutionName, RememberedIndex remembered, PageArguments? diagnosticsPage)
    {
        var missing = remembered.MissingProjects ?? [];
        var skipped = remembered.SkippedProjects ?? [];
        var empty = remembered.EmptyProjects ?? [];

        // Every candidate shell carries the hidden counts the final one will carry, so that trimming a list
        // cannot make the result grow again by widening a number beside it.
        IndexSolutionResult Shell(string? error, IReadOnlyList<string> shownMissing, IReadOnlyList<string> shownSkipped,
                                  IReadOnlyList<string> shownEmpty) =>
            new(solutionName, remembered.Succeeded, remembered.IndexedAt, CurrentCounts(), Empty(),
                error, remembered.LoadComplete, remembered.ProjectsExpected,
                remembered.ProjectsLoaded, shownMissing, missing.Count - shownMissing.Count, shownSkipped, shownEmpty,
                skipped.Count - shownSkipped.Count, empty.Count - shownEmpty.Count);

        // The error is settled first, against a shell with every list empty. It is the one part that cannot
        // be carried into the diagnostics, so if it does not fit here nothing the lists do can help.
        var fittedError = FittedError(remembered.Error, error => Shell(error, [], [], []));

        // In order, each list fitted against the shell the ones before it already settled. missingProjects
        // goes first because it is the one that says the scan was partial.
        var fittedMissing = Fitted(missing, kept => Shell(fittedError, kept, [], []));
        var fittedSkipped = Fitted(skipped, kept => Shell(fittedError, fittedMissing, kept, []));
        var fittedEmpty = Fitted(empty, kept => Shell(fittedError, fittedMissing, fittedSkipped, kept));
        var counted = Shell(fittedError, fittedMissing, fittedSkipped, fittedEmpty);

        var diagnostics = remembered.Diagnostics
                                    .Concat(Carried(remembered.Diagnostics,
                                                    ("missingProjects", missing.Skip(fittedMissing.Count).ToArray()),
                                                    ("skippedProjects", skipped.Skip(fittedSkipped.Count).ToArray()),
                                                    ("emptyProjects", empty.Skip(fittedEmpty.Count).ToArray())))
                                    .ToArray();
        return counted with { Diagnostics = PageDiagnostics(diagnostics, diagnosticsPage, Weight(counted)) };
    }

    /// <summary>
    /// The longest prefix of a list of project names the shell can show. Decided from the shell alone, so
    /// every page of one index shows the same names and reports the same hidden count.
    /// </summary>
    private static IReadOnlyList<string> Fitted(IReadOnlyList<string> names,
                                                Func<IReadOnlyList<string>, IndexSolutionResult> shell)
    {
        if (Weight(shell(names)) <= MaximumShellBytes)
            return names;

        for (var kept = names.Count / 2; kept > 0; kept /= 2)
        {
            var candidate = names.Take(kept).ToArray();
            if (Weight(shell(candidate)) <= MaximumShellBytes)
                return candidate;
        }

        return [];
    }

    /// <summary>
    /// The one budget everything else is derived from: the most the result around the diagnostics may
    /// weigh. What is left of <see cref="ResponseEnvelope.MaximumSerializedBytes"/> after it is what a page
    /// of diagnostics has to live in, and <see cref="DiagnosticFragmentBytes"/> is cut from that same
    /// remainder — so a fragment always fits beside a shell.
    /// <para>The two used to be set apart from each other: the shell was allowed to keep all but 2048
    /// bytes while a fragment could be 4096, so with enough project names no fragment fitted at all and
    /// every page of diagnostics came back empty.</para>
    /// </summary>
    internal const int MaximumShellBytes = ResponseEnvelope.MaximumSerializedBytes / 2;

    /// <summary>
    /// What the shell may weigh once the error is in it and before any list is. The rest of the budget is
    /// what the three project lists are fitted into, so an error can never crowd them out entirely — and,
    /// more to the point, can never leave the shell heavier than the whole budget with every list already
    /// empty, which is the state <see cref="Fitted"/> has no answer for.
    /// </summary>
    private const int MAXIMUM_ERRORED_SHELL_BYTES = MaximumShellBytes / 2;

    private const string ERROR_TRUNCATION_MARKER = " … (error truncated)";

    /// <summary>
    /// The error, cut to what the shell can carry. The measure is the serialized weight of the shell around
    /// it, not the error's character count: 1024 characters of Cyrillic escape to roughly 6144 bytes of
    /// JSON, so a count that looked modest left the shell heavier than its whole budget, the lists were
    /// emptied for nothing, and no diagnostic fragment could fit beside it.
    /// <para>It is the one part of the shell that is not a list and so cannot be carried into the
    /// diagnostics. Cutting is the only thing that can be done with it, and the cut never lands between the
    /// halves of a surrogate pair.</para>
    /// </summary>
    private static string? FittedError(string? error, Func<string?, IndexSolutionResult> shell) =>
        ResponseText.Fitted(error, candidate => Weight(shell(candidate)), MAXIMUM_ERRORED_SHELL_BYTES,
                            ERROR_TRUNCATION_MARKER);

    /// <summary>
    /// The names that did not fit, as diagnostics of their own — one per list, each labelled with the list
    /// it continues. Their ids continue after the last real diagnostic's, because SKILL.md tells the agent
    /// to rejoin a long message by id and part: sharing <c>d:1</c> with a real diagnostic would splice two
    /// different messages into one, and sharing one between two lists would splice those.
    /// </summary>
    private static IReadOnlyList<WorkspaceDiagnosticResult> Carried(IReadOnlyList<WorkspaceDiagnosticResult> real,
                                                                     params (string List, IReadOnlyList<string> Hidden)[] carried)
    {
        var pending = carried.Where(item => item.Hidden.Count > 0).ToArray();
        if (pending.Length == 0)
            return [];

        var used = real.Select(item => item.Id)
                       .Where(id => id.StartsWith("d:", StringComparison.Ordinal))
                       .Select(id => int.TryParse(id[2..], out var number) ? number : 0)
                       .DefaultIfEmpty(0)
                       .Max();
        return Describe(pending.Select(item => (Microsoft.CodeAnalysis.WorkspaceDiagnostic)
                                           new CarriedProjects(item.List, string.Join(", ", item.Hidden)))
                               .ToArray(),
                        used);
    }

    private static ListEnvelope<WorkspaceDiagnosticResult> Empty() =>
        ResponseEnvelope.Create(Array.Empty<WorkspaceDiagnosticResult>(), null,
                                CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult);

    /// <summary>The names that did not fit, carried as a diagnostic so that the fragment machinery pages
    /// them like any other long message.</summary>
    private sealed class CarriedProjects(string list, string names)
        : Microsoft.CodeAnalysis.WorkspaceDiagnostic(Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Warning,
                                                     $"{list} continued: {names}");

    private static int Weight(IndexSolutionResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, CacheDetectiveJsonContext.Default.IndexSolutionResult).Length;

    private string SolutionNameOf(string path) =>
        NormalizePath(Path.GetRelativePath(_repositoryRoot!,
                                           Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_repositoryRoot!, path))));

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
            var coverage = loaded.Coverage;
            return Remember(solutionName,
                            new RememberedIndex(true, indexedAt, Describe(loaded.Diagnostics), null, coverage.LoadComplete,
                                                coverage.ProjectsExpected, coverage.ProjectsLoaded, coverage.MissingProjects,
                                                coverage.SkippedProjects, coverage.EmptyProjects),
                            diagnosticsPage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // A load that threw still knows what it had already reported, and that account is the only
            // evidence of how far it got before it stopped.
            var diagnostics = error is MsBuildLoadException failure ? failure.Diagnostics : loaded?.Diagnostics ?? [];
            return Remember(solutionName,
                            new RememberedIndex(false, null, Describe(diagnostics), error.Message, false, 0, 0, [], [], []),
                            diagnosticsPage);
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    private IndexSolutionResult Remember(string solutionName, RememberedIndex remembered, PageArguments? diagnosticsPage)
    {
        _lastIndex[solutionName] = remembered;
        return Recall(solutionName, remembered, diagnosticsPage);
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

    internal Task<VerifyFindingResult> VerifyFindingAsync(string findingId, bool refresh, PageArguments? page,
                                                          CancellationToken cancellationToken = default) =>
        VerifyFindingAsync(findingId, refresh, page, RunVerificationAsync, cancellationToken);

    /// <summary>
    /// Verification runs once for a finding and is remembered under the workspace it ran in, the finding,
    /// and the graph's version. A new version means a different graph and a different answer; a new
    /// workspace means the old answers are about somebody else's code.
    /// </summary>
    internal async Task<VerifyFindingResult> VerifyFindingAsync(string findingId, bool refresh, PageArguments? page,
                                                                Func<FindingSnapshot, CancellationToken, Task<VerificationRun>> run,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        ArgumentNullException.ThrowIfNull(run);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _findingCatalog.GetAll(Graph, _configuration?.Budgets);
            var snapshot = _findingCatalog.Get(findingId);
            var id = (_repositoryRoot ?? string.Empty, findingId, Graph.VersionToken);
            if (refresh)
            {
                _verifications.Remove(id);
            }

            if (!_verifications.TryGetValue(id, out var verification))
            {
                verification = await run(snapshot, cancellationToken).ConfigureAwait(false);
                _verifications[id] = verification;
            }

            return VerificationQueries.Present(findingId, verification, page);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The live run. Everything that stops it before a reading is taken comes back as a successful answer
    /// carrying <c>not_verifiable</c> and a code, because a caller asking for a verification is owed an
    /// account of why there is none rather than an error.
    /// </summary>
    private async Task<VerificationRun> RunVerificationAsync(FindingSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_configuration?.Verify is not { } verify)
        {
            return Refused(VerificationFailure.NotConfigured);
        }

        if (verify.Redis is null)
        {
            return Refused(VerificationFailure.NotConfigured);
        }

        string connectionString;
        try
        {
            connectionString = verify.ResolveRedis();
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            // The message can name an environment variable but never a connection string, so it is the
            // fixed code that travels rather than the exception's own text.
            return Refused(VerificationFailure.NotConfigured);
        }

        // Whether verification begins at all is settled before anything is opened. A key whose role is not
        // cache, a store the workspace did not list, or a key that depends on no table is owed that reason
        // and not "the cache was unreachable" — and opening a connection to find out would send the
        // client's own housekeeping commands for a run that was never going to read anything.
        var (key, missing) = Subject(snapshot.Item);
        if (key is null)
        {
            return Refused(missing ?? "the finding names no cache key to read");
        }

        string? refusal;
        try
        {
            // Refuse parses the connection string, so a malformed one throws here, before the try below.
            refusal = RedisReader.Refuse(connectionString);
        }
        catch (ArgumentException)
        {
            return Refused(VerificationFailure.NotConfigured);
        }

        var applicability = FindingVerifier.Assess(Graph, key, verify, refusal);
        var tables = applicability.Tables.Select(table => table.Name).Order(StringComparer.Ordinal).ToArray();
        if (!applicability.Starts)
        {
            return new VerificationRun(Empty(applicability.Reason), tables);
        }

        // The database half is checked here, once the run is known to be one that would read something —
        // both that it resolves and that what it resolves to is a connection string at all. Its only
        // resolution used to happen deep inside the reading, where neither exception it throws is one
        // Classify knows, so they escaped verify_finding as an error rather than an account of why there is
        // no verification; and a malformed string was found only after the cache had been scanned, so the
        // sample already in hand was thrown away for an empty refusal that did not even say it was partial.
        // Both messages can quote the string or name an environment variable, so the fixed code travels.
        try
        {
            _ = new SqlConnectionStringBuilder(verify.ResolveDatabase());
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException
                                              or ArgumentException or FormatException)
        {
            return new VerificationRun(Empty(VerificationFailure.NotConfigured), tables);
        }

        try
        {
            await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
            if (!multiplexer.IsConnected)
            {
                return Refused(VerificationFailure.CacheUnavailable);
            }

            if (RedisReader.UnsupportedTopology(multiplexer) is { } topology)
            {
                return Refused(topology);
            }

            return await ReadVerificationAsync(multiplexer, verify, key, applicability, tables, snapshot.Item.Table,
                                               cancellationToken)
                       .ConfigureAwait(false);
        }
        catch (Exception error) when (error is RedisConnectionException or RedisTimeoutException)
        {
            return Refused(VerificationFailure.CacheUnavailable);
        }
        catch (RedisServerException)
        {
            // The server answered and refused — an ACL without SCAN, most often. The server's own text can
            // quote a key, so the fixed code travels instead of the message.
            return Refused(VerificationFailure.CacheUnavailable);
        }
        catch (ArgumentException)
        {
            // A connection string neither client would parse. Its text is the string itself, so it never
            // leaves this method.
            return Refused(VerificationFailure.NotConfigured);
        }
        catch (DbException error)
        {
            return Refused(VerificationReader.IsPermissionDenied(error)
                               ? VerificationFailure.PermissionDenied
                               : VerificationFailure.DatabaseUnavailable);
        }
    }

    /// <summary>
    /// The key a finding is actually about. For most rules that is the key the finding names, but a
    /// <c>STALE_PARENT_KEY</c> finding is a claim about the <em>parent</em>: the catalogue puts the child's
    /// template in <c>keyTemplate</c>, and reading that instead let a healthy child refute a stale parent —
    /// a refutation through a different key, which R8 forbids outright.
    /// <para>The parent is chosen by template <em>and</em> store, both of which the catalogue now carries.
    /// The rule groups a key's dependencies by store and template together and never requires parent and
    /// child to share a store, so a template on its own does not name a key: a workspace holding
    /// <c>basket:{id}</c> in memory and again in redis has two of them. Preferring the child's store and
    /// falling back to a lone candidate read whichever one happened to be there, and its fields agreeing
    /// then refuted a finding made about the other — the same wrong-subject refutation, one store
    /// along.</para>
    /// </summary>
    private (CacheKey? Key, string? Reason) Subject(FindingItem item)
    {
        if (item.Rule != StaleParentKeyFinding.Rule)
        {
            return (Graph.CacheKeys.FirstOrDefault(candidate => candidate.Template == item.KeyTemplate &&
                                                                candidate.Store == item.Store), null);
        }

        if (item.ParentTemplate is not { } parent || item.ParentStore is not { } parentStore)
        {
            return (null, "the finding is about a parent key that the catalogue does not name, so there is nothing to read");
        }

        var chosen = Graph.CacheKeys.FirstOrDefault(candidate => candidate.Template == parent &&
                                                                 candidate.Store == parentStore);
        return chosen is not null
                   ? (chosen, null)
                   : (null, $"the parent key '{parent}' in store '{parentStore}' this finding is about is not in the graph, " +
                            "so there is nothing to read");
    }

    /// <summary>How the cache reader is built over a live connection. An instance property rather than
    /// shared state, so that a test can make the server refuse a command and see what this method does
    /// with the refusal — the catch below cannot be reached through the run seam, which answers before
    /// any of this happens.</summary>
    internal Func<IConnectionMultiplexer, ConfigurationOptions, string, RedisReader> OpenCacheReader { get; set; } =
        RedisReader.Create;

    /// <summary>The reading itself, once both ends are reachable.</summary>
    private async Task<VerificationRun> ReadVerificationAsync(IConnectionMultiplexer multiplexer, VerifyConfiguration verify,
                                                              CacheKey key, Applicability applicability,
                                                              IReadOnlyList<string> tables, string? refutingTable,
                                                              CancellationToken cancellationToken)
    {
        var reader = OpenCacheReader(multiplexer, ConfigurationOptions.Parse(verify.ResolveRedis()), verify.KeyPrefix);
        var scan = await reader.ScanAsync(key.Template).ConfigureAwait(false);

        // Every database reading is a later observation than the cache reading of the key it is about, and
        // the gap between those two moments is what brings them onto one timeline. The clock is the
        // reader's own, still running: each entry knows when it was read against it, so the gap is measured
        // per key rather than from the end of the whole sample.
        if (scan.NotVerifiableReason is not null)
        {
            return new VerificationRun(Empty(scan.NotVerifiableReason), tables);
        }

        SqlConnection connection;
        try
        {
            connection = VerificationReader.Connect(verify.ResolveDatabase());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (FindingVerifier.Classify(error) is { } code)
        {
            // The cache was read and the database could not be opened. The entries are already in hand, so
            // they are reported with the failure against them rather than discarded for an empty refusal.
            var unread = scan.Entries
                             .Select(entry => FindingVerifier.VerifyKey(entry.Key, [], NoTables(key, entry),
                                                                        _configuration?.Sensitive,
                                                                        FindingVerifier.ReadPayload(entry.Payload).Length,
                                                                        entry.FailureCode ?? code, entry.Reason))
                             .ToArray();
            return new VerificationRun(FindingVerifier.Verify(unread, scan, FindingVerifier.AfterScan(applicability, scan)), tables);
        }

        await using (connection)
        {
            return await VerifySampleAsync(new VerificationReader(connection), verify, key, applicability, tables, scan,
                                           () => reader.ElapsedSeconds, cancellationToken, refutingTable)
                       .ConfigureAwait(false);
        }
    }

    /// <summary>The sample against the database: the half of the working path that runs once both readers
    /// are in hand, so that a test can drive it with fake ones.</summary>
    /// <param name="now">The cache reader's clock, read afresh <em>after</em> each database reading returns.
    /// The gap for one key is this less that key's own <see cref="CacheEntry.ObservedSeconds"/>. It is a
    /// reading and not a stopwatch so that a test can state the moments it wants to check.</param>
    /// <param name="refutingTable">The one table the finding is about, when it names one, so that only
    /// agreement with that table's row can refute it.</param>
    internal async Task<VerificationRun> VerifySampleAsync(VerificationReader database, VerifyConfiguration verify, CacheKey key,
                                                            Applicability applicability, IReadOnlyList<string> tables,
                                                            CacheScanResult scan, Func<double> now,
                                                            CancellationToken cancellationToken, string? refutingTable = null)
    {
        // What the sample turned out to be decides the rest of the applicability: only now is it known
        // whether the keys read could each be assigned to the template in exactly one way.
        var afterScan = FindingVerifier.AfterScan(applicability, scan);
        var verified = new List<KeyVerification>();
        foreach (var entry in scan.Entries)
        {
            verified.Add(await VerifyEntryAsync(database, verify, key, entry, tables, now, refutingTable, cancellationToken)
                             .ConfigureAwait(false));
        }

        return new VerificationRun(FindingVerifier.Verify(verified, scan, afterScan), tables);
    }

    /// <summary>The declared tables, looked up without regard to case. Table names reach this from the
    /// code and the configuration is written by hand, so <c>dbo.Products</c> and <c>DBO.PRODUCTS</c> are
    /// the same table; the configuration reader refuses two keys that differ only in case, so the
    /// insensitive dictionary cannot lose an entry.</summary>
    private static Dictionary<string, VerifyTableConfiguration> Declared(VerifyConfiguration verify) =>
        new(verify.Tables ?? [], StringComparer.OrdinalIgnoreCase);

    private static AgeSignal NoTables(CacheKey key, CacheEntry entry) =>
        new(FindingVerifier.EntryAge(key, entry), null, VerificationOutcome.NotVerifiable, "the table's last write was not read");

    /// <summary>
    /// One entry against its tables. The last write is read for <em>every</em> dependent table, because
    /// <c>sys.dm_db_index_usage_stats</c> holds no row data and needs no <c>key</c>/<c>from</c> to be
    /// meaningful; only the row comparison is confined to the tables <c>verify.tables</c> names.
    /// </summary>
    private async Task<KeyVerification> VerifyEntryAsync(VerificationReader database, VerifyConfiguration verify, CacheKey key,
                                                         CacheEntry entry, IReadOnlyList<string> tables, Func<double> now,
                                                         string? refutingTable, CancellationToken cancellationToken)
    {
        var (document, length) = FindingVerifier.ReadPayload(entry.Payload);
        using (document)
        {
            var comparisons = new List<TableRowComparison>();
            var ages = new List<TableAge>();
            var entryAge = FindingVerifier.EntryAge(key, entry);
            var elapsed = now() - entry.ObservedSeconds;

            // A key the cache reader could not read carries its own code, and it has to reach the answer:
            // without it the run reported the reason and still called itself whole, so a sample where a key
            // had vanished was indistinguishable from one where every key was read.
            var code = entry.FailureCode;
            try
            {
                foreach (var name in tables)
                {
                    var (schema, table) = Split(name);
                    var lastWrite = await database.ReadLastWriteAsync(schema, table, cancellationToken).ConfigureAwait(false);

                    // Taken after the query returns, not before it: the number the server reports was true
                    // when it computed it, and the query's own duration is part of the gap. Reading the
                    // clock first credited the entry with a reading it could not have had yet, and a slow
                    // DMV query could carry a pair of readings past the clock margin without saying so.
                    elapsed = now() - entry.ObservedSeconds;
                    ages.Add(Age(name, lastWrite, elapsed, verify.ClockMarginSeconds));
                    if (document is null)
                        continue;

                    // A table verify.tables does not name is a comparison that did not happen, and R7 says
                    // a step that was skipped is recorded with its reason rather than passed over. Without
                    // this the key came back carrying only the age signal's reason, which says something
                    // else entirely. The lookup ignores case because a table name comes out of the code
                    // and the configuration is written by hand.
                    if (!Declared(verify).TryGetValue(name, out var declared))
                    {
                        // Its columns are read even so. Which names two dependent tables share is a fact
                        // about the tables, and the workspace not saying how to look up this one's row is
                        // not evidence that it lacks the column — the value may have been built partly from
                        // here. Reporting no columns made a name it shares look like the declared table's
                        // alone, and that table's agreement could then refute the finding. Only the
                        // catalogue is read; the rows of an undeclared table stay unread.
                        comparisons.Add(new TableRowComparison(name,
                            RowComparison.NotCompared($"'{name}' is not declared in verify.tables with both 'key' and 'from'",
                                                      await database.MatchedColumnsAsync(schema, table, document.RootElement,
                                                                                         cancellationToken)
                                                                    .ConfigureAwait(false))));
                        continue;
                    }

                    comparisons.Add(new TableRowComparison(name,
                        await database.CompareRowAsync(schema, table, declared, key.Template,
                                                       entry.Key.Values, document.RootElement, cancellationToken)
                                      .ConfigureAwait(false)));
                }
            }
            catch (Exception error) when (FindingVerifier.Classify(error) is { } classified)
            {
                code = classified;
            }

            // A refused idle time is recorded beside whatever else the key has to say. It is a reason and
            // not a code on purpose: the entry was read, its payload and TTL are in hand, and one optional
            // observation being unavailable does not make the run partial.
            var reported = entry.IdleReason is null || entry.Reason is null
                               ? entry.Reason ?? entry.IdleReason
                               : $"{entry.Reason}; {entry.IdleReason}";
            return FindingVerifier.VerifyKey(entry.Key, comparisons, FindingVerifier.Aggregate(entryAge, ages),
                                             _configuration?.Sensitive, length, code, reported, ages, elapsed,
                                             verify.ClockMarginSeconds, refutingTable);
        }
    }

    /// <summary>
    /// One table's last write, brought onto the cache reading's timeline. The three ways that can turn out
    /// are kept apart: the clock margin is announced only when it was actually exceeded — it used to be
    /// announced for a write that landed between the two readings as well, which is a signal rather than a
    /// gap, so a run forty seconds apart reported a sixty-second margin as broken.
    /// </summary>
    private static TableAge Age(string name, LastWrite lastWrite, double elapsed, double margin)
    {
        if (lastWrite.SecondsSinceLastWrite is not { } seconds)
            return new TableAge(name, null, lastWrite.Reason);

        var observed = FindingVerifier.AtObservation(seconds, elapsed, margin);
        return observed.Placement switch
        {
            FindingVerifier.Placement.BeyondMargin =>
                new TableAge(name, null,
                             lastWrite.Reason ?? $"the database was read {elapsed:F1}s after the cache, beyond the " +
                                                 $"{margin:F0}s clock margin, so the two readings cannot be placed on one timeline"),
            FindingVerifier.Placement.WrittenAfterObservation =>
                new TableAge(name, null, lastWrite.Reason ?? FindingVerifier.WrittenAfterObservationReason,
                             WrittenAfterObservation: true),
            _ => new TableAge(name, observed.SecondsAgo, lastWrite.Reason)
        };
    }

    private static (string Schema, string Table) Split(string qualified)
    {
        var separator = qualified.IndexOf('.', StringComparison.Ordinal);
        return separator < 0 ? ("dbo", qualified) : (qualified[..separator], qualified[(separator + 1)..]);
    }

    private static VerificationRun Refused(string reason) => new(Empty(reason), []);

    private static FindingVerification Empty(string? reason) =>
        new(VerificationOutcome.NotVerifiable, reason, [], 0, 0, 0, false, false);

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
                                         CurrentCounts(),
                                         _configuration.Verify?.Auto ?? false);
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

    /// <summary>
    /// How much a fragment of one diagnostic message may weigh once serialized. It is what is left of the
    /// response limit once the heaviest shell and the envelope around the fragment have both been paid
    /// for, so a fragment that passes this fits on a page beside any shell the result can produce. Setting
    /// it independently of the shell's budget is what made every page come back empty: a 4096-byte
    /// fragment cannot go on a page a 6144-byte shell left 2048 bytes of.
    /// <para>The measure is bytes of JSON rather than UTF-16 characters because the two are not the same
    /// size: a Cyrillic message costs two bytes a character before escaping and six after (<c>\uXXXX</c>),
    /// so 1500 characters could reach about 9000 bytes and <see cref="ResponseEnvelope"/> would answer
    /// with an empty page and a notice — no answer at all.</para>
    /// </summary>
    /// <summary>
    /// What the envelope around a single fragment costs, measured rather than estimated — and measured at
    /// its worst, with the counts and the notice <see cref="ResponseEnvelope"/> writes when it has had to
    /// reduce a page. It is declared before the budget it feeds, because static initialisers run in the
    /// order they are written.
    /// </summary>
    private static readonly int ENVELOPE_OVERHEAD = JsonSerializer.SerializeToUtf8Bytes(new ListEnvelope<WorkspaceDiagnosticResult>(int.MaxValue, int.MaxValue, int.MaxValue, [],
                                                                                            "Page size was reduced to stay under the response limit."),
                                                                                       CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult).Length;

    private static readonly int DIAGNOSTIC_FRAGMENT_BYTES =
        ResponseEnvelope.MaximumSerializedBytes - MaximumShellBytes - ENVELOPE_OVERHEAD;

    /// <summary>The largest fragment tried first. Cutting is by bytes, so the character count only bounds
    /// the search.</summary>
    private const int DIAGNOSTIC_FRAGMENT_CHARACTERS = 1500;

    internal static IReadOnlyList<WorkspaceDiagnosticResult> Describe(IReadOnlyList<Microsoft.CodeAnalysis.WorkspaceDiagnostic> diagnostics,
                                                                      int numberedFrom = 0)
    {
        var mapped = new List<WorkspaceDiagnosticResult>();
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            var id = $"d:{numberedFrom + index + 1}";
            var kind = diagnostic.Kind.ToString();
            var fragments = Fragments(diagnostic.Message ?? string.Empty, id, kind);
            for (var part = 0; part < fragments.Count; part++)
                mapped.Add(new WorkspaceDiagnosticResult(id, kind, fragments[part], part + 1, fragments.Count));
        }

        return mapped;
    }

    /// <summary>
    /// Cuts a message into pieces each of which fits when serialized. A cut never lands between the two
    /// halves of a surrogate pair, which would turn one character into two broken ones and make the
    /// reassembled text differ from the original.
    /// </summary>
    private static IReadOnlyList<string> Fragments(string message, string id, string kind)
    {
        if (message.Length == 0)
            return [string.Empty];

        var fragments = new List<string>();
        var start = 0;
        while (start < message.Length)
        {
            var length = Math.Min(DIAGNOSTIC_FRAGMENT_CHARACTERS, message.Length - start);
            length = WithoutSplitSurrogate(message, start, length);
            while (length > 1 && SerializedSize(message.Substring(start, length), id, kind) > DIAGNOSTIC_FRAGMENT_BYTES)
                length = WithoutSplitSurrogate(message, start, Math.Max(1, length / 2));

            fragments.Add(message.Substring(start, length));
            start += length;
        }

        return fragments;
    }

    private static int WithoutSplitSurrogate(string message, int start, int length) =>
        ResponseText.WithoutSplitSurrogate(message, start, length);

    private static int SerializedSize(string fragment, string id, string kind) =>
        JsonSerializer.SerializeToUtf8Bytes(new WorkspaceDiagnosticResult(id, kind, fragment, 1, 1),
                                            CacheDetectiveJsonContext.Default.WorkspaceDiagnosticResult).Length;

    /// <param name="reserve">How much the result carrying this envelope weighs around it. Without it the
    /// envelope fills the whole budget on its own and whatever wraps it pushes the response over.</param>
    internal static ListEnvelope<WorkspaceDiagnosticResult> PageDiagnostics(IReadOnlyList<WorkspaceDiagnosticResult> diagnostics,
                                                                            PageArguments? page, int reserve = 0) =>
        ResponseEnvelope.Create(diagnostics, page, CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult, reserve);
}

internal sealed record WorkspaceInitResult(WorkspaceConfiguration Configuration, bool Written);
internal sealed record SolutionStatus(string Path, bool Indexed, DateTimeOffset? IndexedAt);
internal sealed record WorkspaceCounts(int Vertices, int Edges, int Findings, int Unresolved,
                                       int Procedures, int Triggers, int Views, int Events, int ExternalSources, int Annotations);
internal sealed record DatabaseCounts(int Procedures, int Triggers, int Views, int Edges, int Unresolved);
internal sealed record IndexDatabaseResult(string Database, bool Succeeded, DateTimeOffset? IndexedAt,
                                           DatabaseCounts Added, WorkspaceCounts Counts,
                                           IReadOnlyList<string> UnresolvableObjects, string? Error);
/// <summary><paramref name="VerifyAuto"/> is the workspace's consent to runtime verification, surfaced
/// here so a caller can see whether it may verify without reading the configuration file itself.</summary>
internal sealed record WorkspaceStatusResult(ListEnvelope<SolutionStatus> Solutions, WorkspaceCounts Counts,
                                             bool VerifyAuto = false);
/// <summary>One diagnostic, or one fragment of one whose message is too long to travel whole: the
/// fragments of a message share an <paramref name="Id"/> and are numbered <paramref name="Part"/> of
/// <paramref name="Parts"/>, so a reader that pages to the end can put the message back together.</summary>
internal sealed record WorkspaceDiagnosticResult(string Id, string Kind, string Message, int Part, int Parts);
internal sealed record IndexSolutionResult(string Path, bool Succeeded, DateTimeOffset? IndexedAt,
                                           WorkspaceCounts Counts,
                                           ListEnvelope<WorkspaceDiagnosticResult> Diagnostics,
                                           string? Error,
                                           bool LoadComplete = true,
                                           int ProjectsExpected = 0,
                                           int ProjectsLoaded = 0,
                                           IReadOnlyList<string>? MissingProjects = null,
                                           int MissingProjectsHidden = 0,
                                           IReadOnlyList<string>? SkippedProjects = null,
                                           IReadOnlyList<string>? EmptyProjects = null,
                                           int SkippedProjectsHidden = 0,
                                           int EmptyProjectsHidden = 0);

/// <summary>What the last index of one solution said, so that asking for a second page of its
/// diagnostics reads them back instead of loading and indexing the solution all over again.</summary>
internal sealed record RememberedIndex(bool Succeeded, DateTimeOffset? IndexedAt,
                                       IReadOnlyList<WorkspaceDiagnosticResult> Diagnostics, string? Error,
                                       bool LoadComplete, int ProjectsExpected, int ProjectsLoaded,
                                       IReadOnlyList<string> MissingProjects, IReadOnlyList<string> SkippedProjects,
                                       IReadOnlyList<string> EmptyProjects);
