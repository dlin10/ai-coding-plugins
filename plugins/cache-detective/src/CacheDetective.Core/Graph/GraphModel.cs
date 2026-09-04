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

public enum WriteEvent
{
    Insert,
    Update,
    Delete,
    Truncate
}

/// <summary>Where something was met: a code site, or a database object that carries no file and no line.</summary>
public sealed record Evidence
{
    public Evidence(string file, int line)
    {
        File = file;
        Line = line;
    }

    private Evidence(string objectName, string? database)
    {
        ObjectName = objectName;
        Database = database;
    }

    public static Evidence InDatabase(string objectName, string? database = null) => new(objectName, database);

    public string? File { get; }

    public int? Line { get; }

    public string? ObjectName { get; }

    public string? Database { get; }

    /// <summary>The one line a chain carries for this site: <c>file:line</c>, or the database object's name.</summary>
    public string Describe() => File is not null
        ? $"{File}:{Line}"
        : Database is null ? ObjectName! : $"{Database}.{ObjectName}";
}

/// <summary>Where a site came from: the solution the code half indexed, or the database the catalogue half read.</summary>
public sealed record GraphOrigin
{
    private GraphOrigin(string? solution, string? database)
    {
        Solution = solution;
        Database = database;
    }

    public static GraphOrigin ForSolution(string solution) => new(solution, null);

    public static GraphOrigin ForDatabase(string? database) => new(null, database);

    public string? Solution { get; }

    public string? Database { get; }
}

public abstract record GraphVertex;

/// <summary>A vertex a read can start from.</summary>
public abstract record ReadSource : GraphVertex;

/// <summary>A vertex a write can start from. A view is a read source and never one of these.</summary>
public abstract record WriteSource : ReadSource;

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

    private static FrozenSet<string> ToSet(IEnumerable<string>? tags) => (tags ?? []).ToFrozenSet(StringComparer.Ordinal);
}

public sealed record Table(string Name, string? Database = null) : GraphVertex
{
    public Table(string schema, string name, string? database)
        : this($"{schema}.{name}", database)
    {
    }
}

public sealed record Handler(string Solution, string Symbol, string Kind, string File, int Line) : WriteSource;

public sealed record StoredProcedure(string Name, string? Database = null) : WriteSource
{
    public StoredProcedure(string schema, string name, string? database)
        : this($"{schema}.{name}", database)
    {
    }
}

public sealed record View(string Name, string? Database = null) : ReadSource
{
    public View(string schema, string name, string? database)
        : this($"{schema}.{name}", database)
    {
    }
}

public sealed record Trigger : WriteSource
{
    public Trigger(string name, string table, IEnumerable<WriteEvent>? events = null, string? database = null)
    {
        Name = name;
        Table = table;
        Events = (events ?? []).ToFrozenSet();
        Database = database;
    }

    public Trigger(string schema, string name, string table, IEnumerable<WriteEvent>? events, string? database)
        : this($"{schema}.{name}", table, events, database)
    {
    }

    public string Name { get; }

    /// <summary>The <c>schema.name</c> of the table the trigger hangs on.</summary>
    public string Table { get; }

    public IReadOnlySet<WriteEvent> Events { get; }

    public string? Database { get; }
}

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

    public GraphVertex From { get; init; }

    public GraphVertex To { get; init; }

    public Confidence Confidence { get; }

    public IReadOnlyList<Evidence> Evidence { get; }
}

public sealed record Reads : GraphEdge
{
    public Reads(ReadSource from, Table to, Confidence confidence, IEnumerable<Evidence>? evidence = null)
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
    public Writes(WriteSource from, Table to, Confidence confidence, IEnumerable<Evidence>? evidence = null,
                  IEnumerable<WriteEvent>? events = null)
        : base(from, to, confidence, evidence)
    {
        Events = (events ?? []).ToFrozenSet();
    }

    public IReadOnlySet<WriteEvent> Events { get; }
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

    public Calls(Handler from, StoredProcedure to, Confidence confidence, IEnumerable<Evidence>? evidence = null)
        : base(from, to, confidence, evidence)
    {
    }

    public Calls(StoredProcedure from, StoredProcedure to, Confidence confidence,
                 IEnumerable<Evidence>? evidence = null)
        : base(from, to, confidence, evidence)
    {
    }
}

public sealed record Fires : GraphEdge
{
    public Fires(Table from, Trigger to, Confidence confidence, IEnumerable<Evidence>? evidence = null)
        : base(from, to, confidence, evidence)
    {
    }
}

public sealed record Unresolved
{
    public Unresolved(int id, UnresolvedKind kind, string solution, string file, int line,
                      string snippet, string reason)
        : this(id, kind, solution, new Evidence(file, line), snippet, reason)
    {
    }

    public Unresolved(int id, UnresolvedKind kind, string? solution, Evidence site, string snippet,
                      string reason)
    {
        Id = id;
        Kind = kind;
        Solution = solution;
        Site = site;
        Snippet = snippet;
        Reason = reason;
    }

    public int Id { get; }

    public UnresolvedKind Kind { get; }

    public string? Solution { get; }

    public Evidence Site { get; }

    public string Snippet { get; }

    public string Reason { get; }

    public string? File => Site.File;

    public int? Line => Site.Line;
}
