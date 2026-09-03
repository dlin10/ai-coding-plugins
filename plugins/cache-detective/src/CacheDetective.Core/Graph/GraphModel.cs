using System.Collections.Frozen;
using CacheDetective.Caching;

namespace CacheDetective.Graph;

public enum Confidence
{
    Confirmed,
    Likely,
    Unknown
}

public enum UnresolvedKind
{
    Key,
    Sql,
    Call,
    CacheApi,
    Role
}

public sealed record Evidence(string File, int Line);

public abstract record GraphVertex;

public sealed record CacheKey : GraphVertex
{
    public CacheKey(string template, string store, TimeSpan? ttl, IEnumerable<string>? tags, string? role)
        : this(template, store, ttl, ToSet(tags), ToSet(tags), role)
    {
    }

    internal CacheKey(string template, string store, TimeSpan? ttl, IEnumerable<string> tagsAll,
                      IEnumerable<string> tagsAny, string? role)
    {
        Template = template;
        Store = store;
        Ttl = ttl;
        TagsAll = ToSet(tagsAll);
        TagsAny = ToSet(tagsAny);
        Role = role;
    }

    public string Template { get; }

    public string Store { get; }

    public TimeSpan? Ttl { get; }

    public double? TtlSeconds => Ttl?.TotalSeconds;

    public IReadOnlySet<string> TagsAll { get; }

    public IReadOnlySet<string> TagsAny { get; }

    public string? Role { get; }

    private static FrozenSet<string> ToSet(IEnumerable<string>? tags) =>
        (tags ?? []).ToFrozenSet(StringComparer.Ordinal);
}

public sealed record Table(string Name, string? Database = null) : GraphVertex
{
    public Table(string schema, string name, string? database)
        : this($"{schema}.{name}", database)
    {
    }
}

public sealed record Handler(string Solution, string Symbol, string Kind, string File, int Line) : GraphVertex;

public abstract record GraphEdge
{
    protected GraphEdge(GraphVertex from, GraphVertex to, Confidence confidence,
                        IEnumerable<Evidence>? evidence)
    {
        From = from;
        To = to;
        Confidence = confidence;
        Evidence = (evidence ?? []).ToArray();
    }

    public GraphVertex From { get; }

    public GraphVertex To { get; }

    public Confidence Confidence { get; }

    public IReadOnlyList<Evidence> Evidence { get; }
}

public sealed record Reads : GraphEdge
{
    public Reads(Handler from, Table to, Confidence confidence, IEnumerable<Evidence>? evidence = null)
        : base(from, to, confidence, evidence)
    {
    }

    public Reads(Handler from, CacheKey to, Confidence confidence, IEnumerable<Evidence>? evidence = null)
        : base(from, to, confidence, evidence)
    {
    }
}

public sealed record Writes : GraphEdge
{
    public Writes(Handler from, Table to, Confidence confidence, IEnumerable<Evidence>? evidence = null)
        : base(from, to, confidence, evidence)
    {
    }
}

public sealed record Caches : GraphEdge
{
    public Caches(Handler from, CacheKey to, Confidence confidence, IEnumerable<Evidence>? evidence = null,
                  bool isConditionalSet = false)
        : base(from, to, confidence, evidence)
    {
        IsConditionalSet = isConditionalSet;
    }

    public CacheSemantic Semantic => CacheSemantic.Set;

    public bool IsConditionalSet { get; }
}

public sealed record Invalidates : GraphEdge
{
    public Invalidates(Handler from, CacheKey to, Confidence confidence, IEnumerable<Evidence>? evidence = null,
                       CacheSemantic semantic = CacheSemantic.Remove)
        : base(from, to, confidence, evidence)
    {
        Semantic = semantic;
    }

    public CacheSemantic Semantic { get; }
}

public sealed record Calls : GraphEdge
{
    public Calls(Handler from, Handler to, Confidence confidence, IEnumerable<Evidence>? evidence = null)
        : base(from, to, confidence, evidence)
    {
    }
}

public sealed record Unresolved(int Id, UnresolvedKind Kind, string Solution, string File, int Line,
                                string Snippet, string Reason);
