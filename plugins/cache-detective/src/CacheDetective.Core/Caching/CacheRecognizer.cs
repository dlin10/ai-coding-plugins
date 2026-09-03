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

public sealed record CacheRecognizer(string TypeName, string Store,
                                     IReadOnlyList<CacheMethodRecognizer> Methods);

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
}
