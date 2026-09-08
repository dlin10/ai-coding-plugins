namespace CacheDetective.Graph;

/// <summary>
/// The edges a walk needs, grouped by the vertex it leaves and built once per graph version. Without it
/// every visit runs <c>edges.OfType&lt;Calls&gt;().Where(...)</c> over the whole edge list: on a solution
/// with 24 000 edges the dependency closure asked that question millions of times per key, and the scan —
/// with the source id rebuilt at every comparison — was most of the cost.
/// <para>Keyed by the same source id the walk uses, so a lookup costs one string build rather than a pass
/// over every edge in the graph.</para>
/// </summary>
internal sealed class CacheGraphAdjacency
{
    private static readonly IReadOnlyList<Calls> NO_CALLS = [];
    private static readonly IReadOnlyList<Reads> NO_READS = [];
    private static readonly IReadOnlyList<Serves> NO_SERVES = [];
    private static readonly IReadOnlyList<Caches> NO_CACHES = [];

    private readonly Dictionary<string, List<Calls>> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Reads>> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<ExternalSource, List<Serves>> _serves = [];
    private readonly Dictionary<(string Template, string Store), List<Caches>> _caches = [];

    private CacheGraphAdjacency(IReadOnlyDictionary<string, View> views) => Views = views;

    /// <summary>The views by name. A read of a table of the same name continues into the view's own reads,
    /// so the walk asks this of every table it meets.</summary>
    public IReadOnlyDictionary<string, View> Views { get; }

    public static CacheGraphAdjacency Build(CacheGraph graph)
    {
        var adjacency = new CacheGraphAdjacency(graph.Views.ToDictionary(view => view.Name, StringComparer.Ordinal));
        foreach (var edge in graph.Edges)
        {
            switch (edge)
            {
                case Calls call when call.From is ReadSource from:
                    Add(adjacency._calls, SourceId(from), call);
                    break;
                case Reads read when read.From is ReadSource from:
                    Add(adjacency._reads, SourceId(from), read);
                    break;
                case Serves serves:
                    Add(adjacency._serves, (ExternalSource)serves.From, serves);
                    break;
                case Caches caches:
                    var key = (CacheKey)caches.To;
                    Add(adjacency._caches, (key.Template, key.Store), caches);
                    break;
            }
        }

        return adjacency;
    }

    public IReadOnlyList<Calls> CallsFrom(string source) =>
        _calls.TryGetValue(source, out var edges) ? edges : NO_CALLS;

    public IReadOnlyList<Reads> ReadsFrom(string source) =>
        _reads.TryGetValue(source, out var edges) ? edges : NO_READS;

    public IReadOnlyList<Serves> ServesFrom(ExternalSource source) =>
        _serves.TryGetValue(source, out var edges) ? edges : NO_SERVES;

    public IReadOnlyList<Caches> CachesInto((string Template, string Store) key) =>
        _caches.TryGetValue(key, out var edges) ? edges : NO_CACHES;

    /// <summary>The identity a walk stands on. Two <see cref="Handler"/> records that differ only in the
    /// line they were met at are one source, exactly as <c>Handler.Equals</c> says.</summary>
    public static string SourceId(ReadSource source) => source switch
    {
        Handler handler => $"handler:{handler.Solution}/{handler.Symbol}",
        StoredProcedure procedure => $"procedure:{procedure.Name}",
        Trigger trigger => $"trigger:{trigger.Name}",
        View view => $"view:{view.Name}",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static void Add<TKey, TEdge>(Dictionary<TKey, List<TEdge>> index, TKey key, TEdge edge) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var edges))
        {
            edges = [];
            index[key] = edges;
        }

        edges.Add(edge);
    }
}
