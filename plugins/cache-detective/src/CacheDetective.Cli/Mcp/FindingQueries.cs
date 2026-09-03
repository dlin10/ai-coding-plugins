using System.Text.Json;
using CacheDetective.Caching;
using CacheDetective.Graph;
using CacheDetective.Rules;

namespace CacheDetective.Mcp;

internal sealed class FindingCatalog
{
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FindingSnapshot> _snapshots = new(StringComparer.Ordinal);
    private int _nextId = 1;

    internal IReadOnlyList<FindingSnapshot> GetAll(CacheGraph graph,
                                                   IReadOnlyDictionary<string, double>? budgets)
    {
        var snapshots = new List<FindingSnapshot>();
        foreach (var finding in new UnguardedWriteRule().Evaluate(graph, budgets))
        {
            var handler = (Handler)finding.Write.From;
            var identity = Identity(UnguardedWriteFinding.Rule, handler.Solution, handler.Symbol,
                finding.Table.Name, finding.Key.Template, finding.Key.Store, EvidenceIdentity(finding.Write));
            snapshots.Add(Add(identity, UnguardedWriteFinding.Rule, finding.Confidence,
                handler.Solution, finding.Table.Name, finding.Key.Template, finding.Key.Store,
                finding.Suppressed, finding.TtlSeconds, finding.BudgetSeconds, null, null, finding.Chain));
        }

        var invalidations = new OrphanInvalidationRule().Evaluate(graph);
        foreach (var finding in invalidations.Orphans)
        {
            var edge = finding.Invalidation;
            var handler = (Handler)edge.From;
            var key = (CacheKey)edge.To;
            var identity = Identity(OrphanInvalidationFinding.Rule, handler.Solution, handler.Symbol,
                key.Template, key.Store, Semantic(edge.Semantic), EvidenceIdentity(edge));
            snapshots.Add(Add(identity, OrphanInvalidationFinding.Rule, edge.Confidence,
                handler.Solution, null, key.Template, key.Store, false, null, null,
                null, null, [edge]));
        }
        foreach (var finding in invalidations.PatternMismatches)
        {
            var edge = finding.Invalidation;
            var handler = (Handler)edge.From;
            var key = (CacheKey)edge.To;
            var identity = Identity(PatternMismatchFinding.Rule, handler.Solution, handler.Symbol,
                key.Template, key.Store, finding.CachedKey.Template,
                finding.Distance.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EvidenceIdentity(edge));
            snapshots.Add(Add(identity, PatternMismatchFinding.Rule, edge.Confidence,
                handler.Solution, null, key.Template, key.Store, false, null, null,
                finding.CachedKey.Template, finding.Distance, [edge]));
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

    private FindingSnapshot Add(string identity, string rule, Confidence confidence, string solution,
                                string? table, string? keyTemplate, string? store, bool suppressed,
                                double? ttl, double? budget, string? expectedTemplate, int? distance,
                                IReadOnlyList<GraphEdge> chain)
    {
        if (!_ids.TryGetValue(identity, out var id))
        {
            id = $"f:{_nextId++}";
            _ids.Add(identity, id);
        }
        var item = new FindingItem(id, rule, TraceQueries.ConfidenceName(confidence), solution,
            table, keyTemplate, store, suppressed, ttl, budget, expectedTemplate, distance);
        var snapshot = new FindingSnapshot(item, chain);
        _snapshots[id] = snapshot;
        return snapshot;
    }

    private static string Identity(params string?[] parts) => string.Join('\u001f', parts);

    private static string EvidenceIdentity(GraphEdge edge) => string.Join(',', edge.Evidence.Select(
        evidence => $"{evidence.File}:{evidence.Line}"));

    private static string Semantic(CacheSemantic semantic) => semantic.ToString();
}

internal static class FindingQueries
{
    internal static FindingEnvelope FindUnguardedWrites(
        CacheGraph graph, IReadOnlyDictionary<string, double>? budgets, FindingCatalog catalog,
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

    internal static FindingEnvelope FindIssues(
        CacheGraph graph, IReadOnlyDictionary<string, double>? budgets, FindingCatalog catalog,
        string? rule, string? confidence, bool includeSuppressed, PageArguments page)
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

    internal static ListEnvelope<UnresolvedItem> GetUnresolved(CacheGraph graph, string? repositoryRoot,
                                                               string? kind, PageArguments page)
    {
        var normalizedKind = ValidateKind(kind);
        var items = graph.Unresolved.Where(item => normalizedKind is null || Kind(item.Kind) == normalizedKind)
            .OrderBy(item => item.Id)
            .Select(item => new UnresolvedItem($"u:{item.Id}", Kind(item.Kind), item.Solution,
                item.File, item.Line, item.Snippet, item.Reason,
                SourceContext.Read(item.File, item.Line, repositoryRoot)))
            .ToArray();
        return TraceQueries.Page(items, page);
    }

    internal static EvidenceResult GetEvidence(CacheGraph graph,
                                               IReadOnlyDictionary<string, double>? budgets,
                                               string? repositoryRoot, FindingCatalog catalog,
                                               string findingId, PageArguments page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        catalog.GetAll(graph, budgets);
        var snapshot = catalog.Get(findingId);
        return TraceQueries.Bounded(page, (effectivePage, notice) =>
        {
            var fragments = BuildFragments(snapshot, repositoryRoot);
            return new EvidenceResult(snapshot.Item.Id, snapshot.Item.Rule,
                TraceQueries.Page(fragments, effectivePage), notice);
        }, TraceQueries.TypeInfo<EvidenceResult>());
    }

    private static FindingEvidenceFragment[] BuildFragments(FindingSnapshot snapshot, string? repositoryRoot)
    {
        var fragments = new List<FindingEvidenceFragment>();
        for (var edgeIndex = 0; edgeIndex < snapshot.Chain.Count; edgeIndex++)
        {
            var edge = snapshot.Chain[edgeIndex];
            if (edge.Evidence.Count == 0)
            {
                fragments.Add(new FindingEvidenceFragment(edgeIndex + 1, TraceQueries.EdgeType(edge),
                    TraceQueries.NodeId(edge.From), TraceQueries.NodeId(edge.To),
                    TraceQueries.ConfidenceName(edge.Confidence), null, null, []));
                continue;
            }
            foreach (var evidence in edge.Evidence)
            {
                fragments.Add(new FindingEvidenceFragment(edgeIndex + 1, TraceQueries.EdgeType(edge),
                    TraceQueries.NodeId(edge.From), TraceQueries.NodeId(edge.To),
                    TraceQueries.ConfidenceName(edge.Confidence), evidence.File, evidence.Line,
                    SourceContext.Read(evidence.File, evidence.Line, repositoryRoot)));
            }
        }
        return fragments.ToArray();
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
        if (normalized is not (UnguardedWriteFinding.Rule or OrphanInvalidationFinding.Rule or
                               PatternMismatchFinding.Rule))
        {
            throw new ArgumentException(
                "rule must be UNGUARDED_WRITE, ORPHAN_INVALIDATION or PATTERN_MISMATCH", nameof(rule));
        }
        return normalized;
    }

    private static string? ValidateKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }
        var normalized = kind.Trim().ToLowerInvariant();
        if (normalized is not ("key" or "sql" or "call" or "cache_api" or "role"))
        {
            throw new ArgumentException("kind must be key, sql, call, cache_api or role", nameof(kind));
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal static class SourceContext
{
    private const int ContextRadius = 10;

    internal static IReadOnlyList<SourceLine> Read(string file, int line, string? repositoryRoot)
    {
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
            var start = Math.Max(1, line - ContextRadius);
            var end = Math.Min(lines.Length, line + ContextRadius);
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

internal sealed record FindingSnapshot(FindingItem Item, IReadOnlyList<GraphEdge> Chain);
internal sealed record FindingItem(string Id, string Rule, string Confidence, string Solution,
                                   string? Table, string? KeyTemplate, string? Store, bool Suppressed,
                                   double? Ttl, double? Budget, string? ExpectedTemplate, int? Distance);
internal sealed record SourceLine(int Line, string Text);
internal sealed record UnresolvedItem(string Id, string Kind, string Solution, string File, int Line,
                                      string Snippet, string Reason, IReadOnlyList<SourceLine> Context);
internal sealed record FindingEvidenceFragment(int Order, string Edge, string From, string To,
                                               string Confidence, string? File, int? Line,
                                               IReadOnlyList<SourceLine> Context);
internal sealed record EvidenceResult(string FindingId, string Rule,
                                      ListEnvelope<FindingEvidenceFragment> Fragments, string? Notice);

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
        for (var pageSize = arguments.PageSize; pageSize > 0; pageSize--)
        {
            var candidate = Build(visible, suppressed, arguments.Page, pageSize, arguments.PageSize);
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, typeInfo).Length <=
                ResponseEnvelope.MaximumSerializedBytes)
            {
                return candidate;
            }
        }
        return new FindingEnvelope(visible.Count, arguments.Page,
            visible.Count == 0 ? 0 : visible.Count, suppressed, [],
            $"Page omitted because one item exceeds the {ResponseEnvelope.MaximumSerializedBytes}-byte response limit.");
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
}
