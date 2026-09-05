using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Rules;

namespace CacheDetective.Mcp;

internal sealed class FindingCatalog
{
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FindingSnapshot> _snapshots = new(StringComparer.Ordinal);
    private int _nextId = 1;

    internal IReadOnlyList<FindingSnapshot> GetAll(CacheGraph graph, IReadOnlyDictionary<string, double>? budgets)
    {
        var snapshots = new List<FindingSnapshot>();
        foreach (var finding in new UnguardedWriteRule().Evaluate(graph, budgets))
        {
            // The subject is the handler at the head of the chain, which the rule names: a hidden write's
            // Write.From is the procedure or the trigger that performed it, not the handler to fix.
            var handler = finding.Handler;
            var identity = Identity(finding.RuleName,
                                    handler.Solution,
                                    handler.Symbol,
                                    finding.Table.Name,
                                    finding.Key.Template,
                                    finding.Key.Store,
                                    EvidenceIdentity(finding.Write));
            snapshots.Add(Add(identity,
                              finding.RuleName,
                              finding.Confidence,
                              handler.Solution,
                              finding.Table.Name,
                              finding.Key.Template,
                              finding.Key.Store,
                              finding.Suppressed,
                              finding.TtlSeconds,
                              finding.BudgetSeconds,
                              null,
                              null,
                              finding.Chain, handler.Project, null, null, finding.EventChain, finding.SearchedProjects,
                              Array.IndexOf(finding.Chain.ToArray(), finding.Write)));
        }

        foreach (var finding in new ExternalNoTtlRule().Evaluate(graph))
        {
            snapshots.Add(Add(Identity(ExternalNoTtlFinding.Rule, finding.Handler.Solution, finding.Handler.Symbol,
                                       finding.Key.Template, finding.Key.Store, finding.Source.Kind, finding.Source.Method,
                                       finding.Source.Template, finding.Source.ClientName, finding.Source.Owner),
                              ExternalNoTtlFinding.Rule, finding.Confidence, finding.Handler.Solution, null,
                              finding.Key.Template, finding.Key.Store, finding.Suppressed, finding.TtlSeconds,
                              finding.BudgetSeconds, null, null, finding.Chain, finding.Handler.Project,
                              TraceQueries.NodeId(finding.Source)));
        }

        foreach (var finding in new StaleParentKeyRule().Evaluate(graph))
        {
            snapshots.Add(Add(Identity(StaleParentKeyFinding.Rule, finding.Handler.Solution, finding.Handler.Symbol,
                                       finding.Parent.Template, finding.Parent.Store, finding.Child.Template, finding.Child.Store),
                              StaleParentKeyFinding.Rule, finding.Confidence, finding.Handler.Solution, null,
                              finding.Child.Template, finding.Child.Store, false, finding.ParentTtlSeconds, null,
                              finding.Parent.Template, null, finding.Chain, finding.Handler.Project, null, finding.Parent.Template,
                              null, finding.SearchedProjects));
        }

        var invalidations = new OrphanInvalidationRule().Evaluate(graph);
        foreach (var finding in invalidations.Orphans)
        {
            var edge = finding.Invalidation;
            var handler = (Handler)edge.From;
            var key = (CacheKey)edge.To;
            var identity = Identity(OrphanInvalidationFinding.Rule,
                                    handler.Solution,
                                    handler.Symbol,
                                    key.Template,
                                    key.Store,
                                    Semantic(edge.Semantic),
                                    EvidenceIdentity(edge));
            snapshots.Add(Add(identity,
                              OrphanInvalidationFinding.Rule,
                              edge.Confidence,
                              handler.Solution,
                              null,
                              key.Template,
                              key.Store,
                              false,
                              null,
                              null,
                              null,
                              null,
                              [edge], handler.Project));
        }

        foreach (var finding in invalidations.PatternMismatches)
        {
            var edge = finding.Invalidation;
            var handler = (Handler)edge.From;
            var key = (CacheKey)edge.To;
            var identity = Identity(PatternMismatchFinding.Rule,
                                    handler.Solution,
                                    handler.Symbol,
                                    key.Template,
                                    key.Store,
                                    finding.CachedKey.Template,
                                    finding.Distance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    EvidenceIdentity(edge));
            snapshots.Add(Add(identity,
                              PatternMismatchFinding.Rule,
                              edge.Confidence,
                              handler.Solution,
                              null,
                              key.Template,
                              key.Store,
                              false,
                              null,
                              null,
                              finding.CachedKey.Template,
                              finding.Distance,
                              [edge], handler.Project));
        }

        return snapshots.OrderBy(snapshot => snapshot.Item.Rule, StringComparer.Ordinal)
                        .ThenBy(snapshot => snapshot.Item.Solution, StringComparer.Ordinal)
                        .ThenBy(snapshot => snapshot.Item.Table, StringComparer.Ordinal)
                        .ThenBy(snapshot => snapshot.Item.KeyTemplate, StringComparer.Ordinal)
                        .ThenBy(snapshot => snapshot.Item.Id, StringComparer.Ordinal)
                        .ToArray();
    }

    internal FindingSnapshot Get(string id) => _snapshots.TryGetValue(id, out var snapshot)
                                                   ? snapshot
                                                   : throw new InvalidOperationException($"Finding '{id}' was not found in this session.");

    internal void Reset()
    {
        _ids.Clear();
        _snapshots.Clear();
        _nextId = 1;
    }

    private FindingSnapshot Add(string identity, string rule, Confidence confidence, string solution, string? table, string? keyTemplate, string? store,
                                bool suppressed, double? ttl, double? budget, string? expectedTemplate, int? distance, IReadOnlyList<GraphEdge> chain,
                                string? project = null, string? external = null, string? parentTemplate = null,
                                IReadOnlyList<GraphEdge>? eventChain = null, IReadOnlyList<string>? searchedProjects = null,
                                int writeIndex = -1)
    {
        if (!_ids.TryGetValue(identity, out var id))
        {
            id = $"f:{_nextId++}";
            _ids.Add(identity, id);
        }

        var item = new FindingItem(id,
                                   rule,
                                   TraceQueries.ConfidenceName(confidence),
                                   solution,
                                   table,
                                   keyTemplate,
                                   store,
                                   suppressed,
                                   ttl,
                                   budget,
                                   expectedTemplate,
                                   distance, project, external, parentTemplate);
        var snapshot = new FindingSnapshot(item, chain, eventChain ?? [], searchedProjects ?? [], writeIndex);
        _snapshots[id] = snapshot;
        return snapshot;
    }

    private static string Identity(params string?[] parts) => string.Join('\u001f', parts);

    private static string EvidenceIdentity(GraphEdge edge) => string.Join(',', edge.Evidence.Select(evidence => evidence.Describe()));

    private static string Semantic(CacheSemantic semantic) => semantic.ToString();
}

internal static class FindingQueries
{
    internal static string KindName(UnresolvedKind kind) => Kind(kind);
    internal static FindingEnvelope FindUnguardedWrites(CacheGraph graph, IReadOnlyDictionary<string, double>? budgets, FindingCatalog catalog,
                                                        string? confidence, string? table, string? solution, bool includeSuppressed, PageArguments page)
    {
        var normalizedConfidence = ValidateConfidence(confidence);
        var matches = catalog.GetAll(graph, budgets)
                             .Where(snapshot => snapshot.Item.Rule == UnguardedWriteFinding.Rule)
                             .Where(snapshot => normalizedConfidence is null || snapshot.Item.Confidence == normalizedConfidence)
                             .Where(snapshot => table is null || snapshot.Item.Table == table)
                             .Where(snapshot => solution is null || snapshot.Item.Solution == solution)
                             .Select(snapshot => snapshot.Item)
                             .ToArray();
        return FindingEnvelope.Create(matches, includeSuppressed, page);
    }

    internal static FindingEnvelope FindIssues(CacheGraph graph, IReadOnlyDictionary<string, double>? budgets, FindingCatalog catalog, string? rule,
                                               string? confidence, bool includeSuppressed, PageArguments page)
    {
        var normalizedRule = ValidateRule(rule);
        var normalizedConfidence = ValidateConfidence(confidence);
        var matches = catalog.GetAll(graph, budgets)
                             .Select(snapshot => snapshot.Item)
                             .Where(item => normalizedRule is null || item.Rule == normalizedRule)
                             .Where(item => normalizedConfidence is null || item.Confidence == normalizedConfidence)
                             .ToArray();
        return FindingEnvelope.Create(matches, includeSuppressed, page);
    }

    internal static ListEnvelope<UnresolvedItem> GetUnresolved(CacheGraph graph, string? repositoryRoot, string? kind, PageArguments page)
    {
        var normalizedKind = ValidateKind(kind);
        // The gaps are derived here rather than stored, because which reason holds depends on whether a
        // database is indexed now; see ProcedureGaps.
        var serviceGaps = ServiceJoins.Derive(graph).Gaps;
        var items = graph.Unresolved.Concat(ProcedureGaps.Derive(graph).Select(gap => gap.Unresolved))
                         .Concat(EventGaps.Derive(graph).Select(gap => gap.Unresolved))
                         .Concat(serviceGaps.Select(gap => gap.Unresolved))
                         .Where(item => normalizedKind is null || Kind(item.Kind) == normalizedKind)
                         .OrderBy(item => item.Id)
                         .Select(item => new UnresolvedItem($"u:{item.Id}",
                                                            Kind(item.Kind),
                                                            item.Solution,
                                                            item.File,
                                                            item.Line,
                                                            item.Site.Database,
                                                            item.Site.ObjectName,
                                                            item.Snippet,
                                                            item.Reason,
                                                            SourceContext.Read(item.File, item.Line, repositoryRoot),
                                                            graph.TryGetExternalSource(item.Id, out var source) ? TraceQueries.NodeId(source) :
                                                            serviceGaps.FirstOrDefault(gap => gap.Unresolved.Id == item.Id) is { } gap ? TraceQueries.NodeId(gap.Source) : null))
                         .ToArray();
        return TraceQueries.Page(items, page);
    }

    internal static EvidenceResult GetEvidence(CacheGraph graph, IReadOnlyDictionary<string, double>? budgets, string? repositoryRoot, FindingCatalog catalog,
                                               string findingId, PageArguments page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        catalog.GetAll(graph, budgets);
        var snapshot = catalog.Get(findingId);
        return TraceQueries.Bounded(page,
                                    (effectivePage, notice) =>
                                    {
                                        var fragments = BuildFragments(graph, snapshot, repositoryRoot);
                                        return new EvidenceResult(snapshot.Item.Id, snapshot.Item.Rule, TraceQueries.Page(fragments, effectivePage),
                                                                  snapshot.SearchedProjects, notice);
                                    },
                                    TraceQueries.TypeInfo<EvidenceResult>());
    }

    private static FindingEvidenceFragment[] BuildFragments(CacheGraph graph, FindingSnapshot snapshot, string? repositoryRoot)
    {
        var fragments = new List<FindingEvidenceFragment>();
        var edges = snapshot.Chain.Concat(snapshot.EventChain).ToArray();
        for (var edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            var edge = edges[edgeIndex];
            var project = ProjectOf(edges, edgeIndex, snapshot);
            var annotation = edge.AnnotationId is { } id ? graph.Annotations.FirstOrDefault(item => item.Id == id)?.Note : null;
            if (edge.Evidence.Count == 0)
            {
                fragments.Add(new FindingEvidenceFragment(edgeIndex + 1,
                                                          TraceQueries.EdgeType(edge),
                                                          TraceQueries.NodeId(edge.From),
                                                          TraceQueries.NodeId(edge.To),
                                                          TraceQueries.ConfidenceName(edge.Confidence),
                                                          null,
                                                          null,
                                                          null,
                                                          null,
                                                          [], project, annotation, edge.Reason));
                continue;
            }

            foreach (var evidence in edge.Evidence)
            {
                fragments.Add(new FindingEvidenceFragment(edgeIndex + 1,
                                                          TraceQueries.EdgeType(edge),
                                                          TraceQueries.NodeId(edge.From),
                                                          TraceQueries.NodeId(edge.To),
                                                          TraceQueries.ConfidenceName(edge.Confidence),
                                                          evidence.File,
                                                          evidence.Line,
                                                          evidence.Database,
                                                          evidence.ObjectName,
                                                          SourceContext.Read(evidence.File, evidence.Line, repositoryRoot), project, annotation, edge.Reason));
            }
        }

        return fragments.ToArray();
    }

    private static string ProjectOf(IReadOnlyList<GraphEdge> edges, int index, FindingSnapshot snapshot)
    {
        var edge = edges[index];
        if (edge.From is Handler from) return from.Project ?? from.Solution;
        if (edge.To is Handler to) return to.Project ?? to.Solution;
        var fallback = snapshot.Item.Project ?? snapshot.Item.Solution;
        if (snapshot.WriteIndex < 0) return fallback;
        var positions = index <= snapshot.WriteIndex
                            ? Enumerable.Range(0, index).Select(offset => index - offset - 1)
                            : Enumerable.Range(index + 1, edges.Count - index - 1);
        foreach (var position in positions)
        {
            if (edges[position].From is Handler before) return before.Project ?? before.Solution;
            if (edges[position].To is Handler after) return after.Project ?? after.Solution;
        }
        return fallback;
    }

    private static string? ValidateConfidence(string? confidence)
    {
        if (string.IsNullOrWhiteSpace(confidence))
        {
            return null;
        }

        var normalized = confidence.Trim().ToLowerInvariant();
        if (normalized is not ("confirmed" or "likely" or "unknown"))
        {
            throw new ArgumentException("confidence must be confirmed, likely or unknown", nameof(confidence));
        }

        return normalized;
    }

    private static string? ValidateRule(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
        {
            return null;
        }

        var normalized = rule.Trim().ToUpperInvariant();
        if (normalized is not (UnguardedWriteFinding.Rule or UnguardedWriteFinding.CrossServiceGapRule or ExternalNoTtlFinding.Rule or
                               StaleParentKeyFinding.Rule or OrphanInvalidationFinding.Rule or PatternMismatchFinding.Rule))
        {
            throw new ArgumentException("rule must be UNGUARDED_WRITE, CROSS_SERVICE_GAP, EXTERNAL_NO_TTL, STALE_PARENT_KEY, ORPHAN_INVALIDATION or PATTERN_MISMATCH", nameof(rule));
        }

        return normalized;
    }

    private static string? ValidateKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        var normalized = kind.Trim()
                             .ToLowerInvariant();
        if (normalized is not ("key" or "sql" or "call" or "cache_api" or "role" or "event" or "event_api"))
        {
            throw new ArgumentException("kind must be key, sql, call, cache_api, role, event or event_api", nameof(kind));
        }

        return normalized;
    }

    private static string Kind(UnresolvedKind kind) => kind switch
                                                       {
                                                           UnresolvedKind.Key => "key",
                                                           UnresolvedKind.Sql => "sql",
                                                           UnresolvedKind.Call => "call",
                                                           UnresolvedKind.CacheApi => "cache_api",
                                                           UnresolvedKind.Role => "role",
                                                           UnresolvedKind.Event => "event",
                                                           UnresolvedKind.EventApi => "event_api",
                                                           _ => throw new ArgumentOutOfRangeException(nameof(kind))
                                                       };
}

internal static class SourceContext
{
    private const int CONTEXT_RADIUS = 10;

    internal static IReadOnlyList<SourceLine> Read(string? file, int? line, string? repositoryRoot)
    {
        if (file is null || line is not { } lineNumber)
        {
            return [];
        }
        var path = Path.IsPathRooted(file) || repositoryRoot is null
            ? file
            : Path.Combine(repositoryRoot, file);
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            var lines = File.ReadAllLines(path);
            var start = Math.Max(1, lineNumber - CONTEXT_RADIUS);
            var end = Math.Min(lines.Length, lineNumber + CONTEXT_RADIUS);
            return Enumerable.Range(start, end - start + 1)
                .Select(number => new SourceLine(number, lines[number - 1]))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal sealed record FindingSnapshot(FindingItem Item, IReadOnlyList<GraphEdge> Chain,
                                       IReadOnlyList<GraphEdge> EventChain, IReadOnlyList<string> SearchedProjects, int WriteIndex);
internal sealed record FindingItem(string Id, string Rule, string Confidence, string Solution,
                                   string? Table, string? KeyTemplate, string? Store, bool Suppressed,
                                   double? Ttl, double? Budget, string? ExpectedTemplate, int? Distance,
                                   string? Project, string? External, string? ParentTemplate);
internal sealed record SourceLine(int Line, string Text);
internal sealed record UnresolvedItem(string Id, string Kind, string? Solution, string? File, int? Line,
                                      string? Database, string? ObjectName,
                                      string Snippet, string Reason, IReadOnlyList<SourceLine> Context, string? External);
internal sealed record FindingEvidenceFragment(int Order, string Edge, string From, string To,
                                               string Confidence, string? File, int? Line,
                                               string? Database, string? ObjectName,
                                               IReadOnlyList<SourceLine> Context, string Project, string? Annotation, string? Reason);
internal sealed record EvidenceResult(string FindingId, string Rule,
                                      ListEnvelope<FindingEvidenceFragment> Fragments, IReadOnlyList<string> InvalidationSearchedIn, string? Notice);

internal sealed record FindingEnvelope(int Total, int Page, int Pages, int Suppressed,
                                       List<FindingItem> Items, string? Notice)
{
    internal static FindingEnvelope Create(IReadOnlyList<FindingItem> source, bool includeSuppressed,
                                           PageArguments arguments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(arguments.Page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(arguments.PageSize);
        var suppressed = source.Count(item => item.Suppressed);
        var visible = includeSuppressed
            ? source
            : source.Where(item => !item.Suppressed).ToArray();
        var typeInfo = TraceQueries.TypeInfo<FindingEnvelope>();
        var regularPages = visible.Count == 0 ? 0 : (int)Math.Ceiling((double)visible.Count / arguments.PageSize);
        if (Enumerable.Range(1, regularPages).All(page => Fits(Build(visible, suppressed, page, arguments.PageSize, arguments.PageSize), typeInfo)))
            return Build(visible, suppressed, arguments.Page, arguments.PageSize, arguments.PageSize);

        const string notice = "Page size was reduced to stay under the response limit.";
        var partitions = Partition(visible, suppressed, arguments.PageSize, notice, typeInfo);
        if (partitions is null)
            return new FindingEnvelope(visible.Count, arguments.Page,
                visible.Count == 0 ? 0 : visible.Count, suppressed, [],
                $"Page omitted because one item exceeds the {ResponseEnvelope.MaximumSerializedBytes}-byte response limit.");
        var items = arguments.Page <= partitions.Count ? partitions[arguments.Page - 1] : [];
        return new FindingEnvelope(visible.Count, arguments.Page, partitions.Count, suppressed, items, notice);
    }

    private static FindingEnvelope Build(IReadOnlyList<FindingItem> source, int suppressed, int page,
                                         int pageSize, int requestedPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedPageSize);
        var pages = source.Count == 0 ? 0 : (int)Math.Ceiling((double)source.Count / pageSize);
        var skip = (long)(page - 1) * pageSize;
        var items = skip >= source.Count ? [] : source.Skip((int)skip).Take(pageSize).ToList();
        var notice = pageSize == requestedPageSize ? null
            : $"Page size was reduced from {requestedPageSize} to {pageSize} to stay under "
              + $"the {ResponseEnvelope.MaximumSerializedBytes}-byte response limit.";
        return new FindingEnvelope(source.Count, page, pages, suppressed, items, notice);
    }

    private static List<List<FindingItem>>? Partition(IReadOnlyList<FindingItem> source, int suppressed,
                                                        int requestedPageSize, string notice,
                                                        JsonTypeInfo<FindingEnvelope> typeInfo)
    {
        var partitions = new List<List<FindingItem>>();
        var offset = 0;
        while (offset < source.Count)
        {
            var count = Math.Min(requestedPageSize, source.Count - offset);
            while (count > 0 && !Fits(new FindingEnvelope(source.Count, source.Count, source.Count, suppressed,
                                                           source.Skip(offset).Take(count).ToList(), notice), typeInfo))
                count--;
            if (count == 0) return null;
            partitions.Add(source.Skip(offset).Take(count).ToList());
            offset += count;
        }
        return partitions;
    }

    private static bool Fits(FindingEnvelope candidate, JsonTypeInfo<FindingEnvelope> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(candidate, typeInfo).Length <= ResponseEnvelope.MaximumSerializedBytes;
}
