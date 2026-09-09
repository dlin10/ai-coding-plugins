namespace CacheDetective.Graph;

public sealed record ServiceJoinResult(IReadOnlyList<Serves> Serves, IReadOnlyList<ServiceJoinGap> Gaps);
public sealed record ServiceJoinGap(ExternalSource Source, Handler Reader, Unresolved Unresolved);

public static class ServiceJoins
{
    public static ServiceJoinResult Derive(CacheGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.GetServiceJoins();
    }

    internal static ServiceJoinResult Build(CacheGraph graph)
    {
        var edges = graph.StoredEdges;
        var handlers = graph.Handlers.OrderBy(handler => handler.Solution, StringComparer.Ordinal).ThenBy(handler => handler.Symbol, StringComparer.Ordinal).ToArray();
        var serves = new List<Serves>();
        var gaps = new List<ServiceJoinGap>();
        foreach (var source in graph.ExternalSources.OrderBy(SourceId, StringComparer.Ordinal))
        {
            var readers = edges.OfType<Reads>().Where(edge => edge.To is ExternalSource candidate && candidate == source)
                               .Select(edge => edge.From as Handler).OfType<Handler>()
                               .Distinct().ToArray();
            if (readers.Length == 0) continue;

            var annotation = graph.ServesAnnotations.Where(item => item.Source == source).ToArray();
            if (annotation.Length > 0)
            {
                foreach (var item in annotation)
                    serves.Add(new Serves(source, item.Target, Confidence.Likely, [], "annotation") { AnnotationId = item.AnnotationId });
                continue;
            }

            var level = "route";
            var confidence = source.Kind == "grpc" ? Confidence.Confirmed : Confidence.Likely;
            Handler[] candidates;
            if (source.ClientName is not null && graph.ServiceMap.TryGetValue(source.ClientName, out var mapped))
            {
                var scope = FindScope(handlers, mapped, out var ambiguous);
                if (ambiguous) { AddGap($"Service '{mapped}' is ambiguous: {string.Join(", ", handlers.Where(handler => MatchesScope(handler, mapped)).Select(handler => handler.ServiceId()).Distinct())}"); continue; }
                candidates = scope is null ? [] : Match(source, scope, true);
                level = "services"; confidence = Confidence.Confirmed;
                if (candidates.Length == 0) { AddGap($"No endpoint in '{mapped}' matches {source.Method} {source.Template} — name the target."); continue; }
            }
            else
            {
                var ambiguous = false;
                var scope = source.ClientName is null ? null : FindScope(handlers, NormalizeClient(source.ClientName), out ambiguous);
                candidates = scope is null || ambiguous ? Match(source, handlers, false) : Match(source, scope, true);
                level = scope is null || ambiguous ? (source.Kind == "grpc" ? "grpc" : "route") : "client_name";
                if (scope is not null && !ambiguous && candidates.Length == 0)
                {
                    var service = scope.Select(handler => handler.Project ?? handler.Solution)
                                       .Distinct(StringComparer.OrdinalIgnoreCase).Single();
                    AddGap($"No endpoint in '{service}' matches {source.Method} {source.Template} — name the target.");
                    continue;
                }
                confidence = source.Kind == "grpc" ? Confidence.Confirmed : Confidence.Likely;
            }

            if (source.Kind == "grpc")
            {
                level = "grpc";
                confidence = Confidence.Confirmed;
            }

            var groups = candidates.GroupBy(handler => (handler.ServiceId(), handler.Symbol)).ToArray();
            if (groups.Length == 1)
            {
                foreach (var target in groups[0]) serves.Add(new Serves(source, target, confidence, [], level));
            }
            else if (groups.Length > 1)
            {
                AddGap($"Several endpoints match: {string.Join(", ", groups.Select(group => $"handler:{group.Key.Item1}/{group.Key.Symbol}"))} — name the target.");
            }

            void AddGap(string reason)
            {
                foreach (var reader in readers)
                {
                    var site = edges.OfType<Reads>().First(edge => edge.To is ExternalSource candidate && candidate == source && edge.From == reader)
                                    .Evidence.FirstOrDefault() ?? new Evidence(reader.File, reader.Line);
                    var id = graph.GetDerivedUnresolvedId($"serves|{SourceId(source)}|{reader.Solution}|{reader.Symbol}");
                    if (!graph.IsServiceJoinGapSuppressed(id))
                        gaps.Add(new ServiceJoinGap(source, reader, new Unresolved(id, UnresolvedKind.Call, reader.Solution, site,
                            $"{source.Method} {source.Template}", reason)));
                }
            }
        }
        return new ServiceJoinResult(serves, gaps);
    }

    private static Handler[] Match(ExternalSource source, IEnumerable<Handler> handlers, bool tail) =>
        handlers.Where(handler => handler.Routes.Any(route => route.Kind == source.Kind &&
            (source.Kind == "grpc" ? route.Template == source.Template : MethodMatches(source.Method, route.Method) &&
             (tail ? PathMatches(source.Template, route.Template) : FullPathMatches(source.Template, route.Template)))))
                .ToArray();

    private static Handler[]? FindScope(Handler[] handlers, string value, out bool ambiguous)
    {
        var normalized = NormalizeScope(value);
        var projects = handlers.Where(handler => handler.Project is not null && NormalizeScope(handler.Project) == normalized).ToArray();
        if (projects.Length > 0) { ambiguous = projects.Select(handler => handler.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1; return projects; }
        var solutions = handlers.Where(handler => NormalizeScope(handler.Solution) == normalized || NormalizeScope(Path.GetFileNameWithoutExtension(handler.Solution)) == normalized).ToArray();
        ambiguous = solutions.Select(handler => handler.Solution).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        return solutions.Length == 0 ? null : solutions;
    }

    private static bool MatchesScope(Handler handler, string value) => NormalizeScope(handler.ServiceId()) == NormalizeScope(value);
    private static bool MethodMatches(string left, string right) => left == "*" || right == "*" || left == right;
    private static bool PathMatches(string call, string endpoint)
    {
        var segments = call.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var firstLiteral = Array.FindIndex(segments, segment => !segment.StartsWith('{'));
        if (firstLiteral < 0) return false;
        segments = segments[firstLiteral..];
        var marker = Array.FindLastIndex(segments, segment => segment.Contains("{?}", StringComparison.Ordinal));
        var tail = marker < 0 ? segments : segments[(marker + 1)..];
        if (tail.Length == 0 || !tail.Any(segment => !segment.StartsWith('{') && !segment.Contains("{?}", StringComparison.Ordinal))) return false;
        var target = endpoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return target.Length > 0 && tail.Reverse().Take(Math.Min(tail.Length, target.Length))
                   .Zip(target.Reverse(), SegmentMatches).All(match => match);
    }

    private static bool FullPathMatches(string call, string endpoint)
    {
        var left = call.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var right = endpoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return left.Length == right.Length && left.Zip(right, (a, b) => a == "{?}" ? b.StartsWith('{') :
            a.StartsWith('{') ? b.StartsWith('{') : a == b).All(match => match);
    }
    private static bool SegmentMatches(string left, string right) => left.StartsWith('{') ? right.StartsWith('{') :
        right == "{v}" && left.Length > 1 && left[0] == 'v' && left[1..].All(char.IsDigit) || left == right;

    private static string NormalizeClient(string value)
    {
        var part = value.Split(':') is [_, var middle, ..] ? middle : value;
        if (part.Length > 1 && part[0] == 'I' && char.IsUpper(part[1])) part = part[1..];
        return TrimSuffixes(part, ["Service", "Client", "Api", "Proxy", "Http", "Gateway"]);
    }
    private static string NormalizeScope(string value)
    {
        var name = value.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(value)
            : value;
        return TrimSuffixes(name, [".API", ".Api", ".Web", ".WebApi", ".Service", ".Host"]);
    }
    private static string TrimSuffixes(string value, string[] suffixes)
    {
        var next = value;
        do { value = next; next = suffixes.FirstOrDefault(suffix => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) is { } suffix ? value[..^suffix.Length] : value; } while (next != value);
        return next.ToLowerInvariant();
    }
    private static string SourceId(ExternalSource source) => $"{source.Kind}|{source.Method}|{source.Template}|{source.ClientName}|{source.Owner}";
}
