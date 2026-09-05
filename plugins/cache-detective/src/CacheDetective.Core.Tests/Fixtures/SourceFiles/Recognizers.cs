using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Some.Internal
{
    public interface IDbSetCache { }
}

namespace RecognizerFixture
{

public sealed class CacheController : ControllerBase
{
    private readonly IMemoryCache _memory = null!;
    private readonly IDistributedCache _distributed = null!;
    private readonly HybridCache _hybrid = null!;
    private readonly IDatabase _redis = null!;
    private readonly MysteryCache _mystery = new();

    public void Memory()
    {
        _memory.Set("memory-key", 1, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });
        _memory.Get<int>("memory-key");
        _memory.Remove("memory-key");
    }

    public void Distributed()
    {
        _distributed.SetString("distributed-key", "value", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(45)
        });
        _distributed.GetString("distributed-key");
        _distributed.Remove("distributed-key");
    }

    public async Task Hybrid()
    {
        await _hybrid.SetAsync("hybrid-key", "value", new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(60)
        }, ["tag-a"]);
        await _hybrid.GetOrCreateAsync("hybrid-read", cancellationToken =>
            ValueTask.FromResult("value"));
        await _hybrid.RemoveByTagAsync("tag-a");
    }

    public void Redis()
    {
        _redis.StringSet("redis-key", "value", TimeSpan.FromSeconds(90), when: When.NotExists);
        _redis.StringGet("redis-key");
        _redis.HashSet("hash-key", "field", "value");
        _redis.HashGet("hash-key", "field");
        _redis.StringIncrement("counter-key");
        _redis.KeyExpire("redis-key", TimeSpan.FromSeconds(120));
        _redis.KeyDelete("redis-key");
    }

    public void Unknown()
    {
        _mystery.Fetch("one");
        _mystery.Fetch("two");
    }

    [OutputCache]
    public void OutputCached() { }

    [OutputCache]
    public void AlsoOutputCached() { }

    public void Save(DbLike context) => context.SaveChangesAsync();
}

public sealed class DbLike : Some.Internal.IDbSetCache
{
    public void SaveChangesAsync() { }
}

public sealed class MysteryCache
{
    public void Fetch(string key) { }
}
}
