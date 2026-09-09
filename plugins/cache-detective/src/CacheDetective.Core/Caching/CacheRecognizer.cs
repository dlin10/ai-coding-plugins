using CacheDetective.Graph;

namespace CacheDetective.Caching;

public enum CacheSemantic
{
    Get,
    Set,
    Remove,
    RemoveByTag,
    RemoveByPrefix,
    Increment,
    Expire,
    Lock
}

public sealed record ConditionalSet(int ArgumentIndex, string ConstantName);

public sealed record CacheMethodRecognizer(string Name, CacheSemantic Semantic, int KeyArgumentIndex,
                                           int? TtlOrOptionsArgumentIndex = null, int? TagsArgumentIndex = null,
                                           ConditionalSet? ConditionalSet = null);

/// <summary>One place a key object meets its arguments: a declaring type, the methods on it that do the
/// substituting, which of their arguments carries the key object, and which carries the
/// <c>params</c>-style array whose expressions become the placeholder names.</summary>
public sealed record KeyObjectFactory(string TypeName, IReadOnlyList<string> Methods,
                                      int KeyObjectArgumentIndex, int ArgumentsArgumentIndex);

/// <summary>How to read a key that arrives as an object rather than a string expression: the object's
/// type, the constructor argument the template literal comes from, and the factories that substitute
/// into it. See <c>docs/adr/0016</c>.</summary>
public sealed record KeyObjectRecognizer(string TypeName, int TemplateArgumentIndex,
                                         IReadOnlyList<KeyObjectFactory> Factories);

public sealed record CacheRecognizer(string TypeName, string Store,
                                     IReadOnlyList<CacheMethodRecognizer> Methods,
                                     Confidence Confidence = Confidence.Confirmed, int? AnnotationId = null,
                                     KeyObjectRecognizer? KeyObject = null);

public sealed record CacheOperation(Handler Handler, CacheKey Key, CacheSemantic Semantic,
                                    bool IsConditionalSet, IReadOnlyList<Evidence> Evidence);

public static class CacheRecognizers
{
    public static IReadOnlyList<CacheRecognizer> All { get; } =
    [
        new("Microsoft.Extensions.Caching.Memory.IMemoryCache",
            "memory",
            [
                new("Get", CacheSemantic.Get, 0),
                new("TryGetValue", CacheSemantic.Get, 0),
                new("Set", CacheSemantic.Set, 0, 2),
                new("GetOrCreate", CacheSemantic.Set, 0),
                new("GetOrCreateAsync", CacheSemantic.Set, 0),
                new("Remove", CacheSemantic.Remove, 0)]),
        new("Microsoft.Extensions.Caching.Distributed.IDistributedCache",
            "distributed",
            [
                new("Get", CacheSemantic.Get, 0),
                new("GetAsync", CacheSemantic.Get, 0),
                new("GetString", CacheSemantic.Get, 0),
                new("GetStringAsync", CacheSemantic.Get, 0),
                new("Set", CacheSemantic.Set, 0, 2),
                new("SetAsync", CacheSemantic.Set, 0, 2),
                new("SetString", CacheSemantic.Set, 0, 2),
                new("SetStringAsync", CacheSemantic.Set, 0, 2),
                new("Remove", CacheSemantic.Remove, 0),
                new("RemoveAsync", CacheSemantic.Remove, 0)]),
        new("Microsoft.Extensions.Caching.Hybrid.HybridCache",
            "hybrid",
            [
                new("GetOrCreateAsync", CacheSemantic.Set, 0, 2, 3),
                new("SetAsync", CacheSemantic.Set, 0, 2, 3),
                new("RemoveAsync", CacheSemantic.Remove, 0),
                new("RemoveByTagAsync", CacheSemantic.RemoveByTag, 0)]),
        new("StackExchange.Redis.IDatabase",
            "redis",
            [
                new("StringGet*", CacheSemantic.Get, 0),
                new("StringSet*", CacheSemantic.Set, 0, 2, null,
                    new ConditionalSet(4, "When.NotExists")),
                new("KeyDelete*", CacheSemantic.Remove, 0),
                new("HashGet*", CacheSemantic.Get, 0),
                new("HashSet*", CacheSemantic.Set, 0),
                new("StringIncrement*", CacheSemantic.Increment, 0),
                new("KeyExpire*", CacheSemantic.Expire, 0, 1)])
    ];

    /// <summary>
    /// The one order a declared recognizer meets the built-in set in. Recognition takes the first matching
    /// type, so this decides what a declaration of a built-in type means, and it must mean the same thing
    /// wherever it is read: a declaration measured by <c>cachedet metrics</c> and the same declaration
    /// indexed in a session have to resolve to the same store, the same semantics and the same key folding,
    /// or the measurement is not a measurement of the tool.
    /// <para>A declaration wins. The repository is stating what its own libraries do, and a built-in that
    /// silently outranked it would make an explicit declaration inert — the one outcome the configuration
    /// schema refuses everywhere else, where a field it does not know is rejected rather than ignored. The
    /// built-in of that type is dropped rather than ordered behind, so the override holds however the
    /// consumer searches the list.</para>
    /// </summary>
    public static CacheRecognizer[] Merge(IEnumerable<CacheRecognizer> declared)
    {
        var overrides = declared.ToArray();
        var names = overrides.Select(recognizer => recognizer.TypeName).ToHashSet(StringComparer.Ordinal);
        return [.. overrides, .. All.Where(builtin => !names.Contains(builtin.TypeName))];
    }
}
