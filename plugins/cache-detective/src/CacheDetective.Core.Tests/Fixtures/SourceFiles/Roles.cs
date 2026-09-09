using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

public sealed class RoleEntity
{
    public int Id { get; set; }
}

public sealed class RoleDbContext : DbContext
{
    public DbSet<RoleEntity> Rows { get; set; } = null!;
}

public interface IRoleReader
{
    object Read();
}

public sealed class RoleController : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly IDatabase _redis = null!;
    private readonly RoleDbContext _database = null!;
    private readonly IRoleReader _reader = null!;

    public void MaskedStore()
    {
        var value = _database.Rows.ToList();
        _cache.Set("session:user", value);
    }

    public void ComputedStore()
    {
        _cache.Set("computed:value", 42);
    }

    public void CounterStore()
    {
        _redis.StringIncrement("hits:global");
    }

    public void ExpiringStore()
    {
        _redis.KeyExpire("expiry:key", TimeSpan.FromMinutes(1));
    }

    public void ConditionalStore()
    {
        _redis.StringSet("conditional:key", "value", when: When.NotExists);
    }

    public void PlainCache()
    {
        var value = _database.Rows.ToList();
        _cache.Set("catalog:rows", value);
    }

    public void UnknownRole()
    {
        var value = _reader.Read();
        _cache.Set("unknown:reader", value);
    }
}
