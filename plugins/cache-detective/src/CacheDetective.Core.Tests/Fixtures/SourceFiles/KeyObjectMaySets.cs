// The shapes MaySets.cs gives a string key, given to a key object instead, so that a rule can be asked
// whether a removal keyed on an object that may be one of two templates grants a suppression. See
// docs/adr/0015 for the policy and docs/adr/0016 for the key object it now travels down.
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KeyObjectMayFixture;

public sealed class Product
{
    public int Id { get; set; }
}

public sealed class ShopContext : DbContext
{
    public DbSet<Product> Products { get; set; } = null!;
}

public sealed class MayCacheKey
{
    public MayCacheKey(string key) => Key = key;

    public string Key { get; }
}

public interface IMayKeyService
{
    MayCacheKey PrepareKey(MayCacheKey cacheKey, params object[] parameters);
}

public interface IMayCache : IMayKeyService
{
    void Set(MayCacheKey key, object value);

    void Remove(MayCacheKey key);
}

public static class MayDefaults
{
    public static MayCacheKey AlphaKey => new MayCacheKey("product:alpha");

    public static MayCacheKey BetaKey => new MayCacheKey("product:beta");
}

public sealed class ObjectReaderController : ControllerBase
{
    private readonly IMayCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void ReadAlpha() => _cache.Set(MayDefaults.AlphaKey, _db.Products.ToList());

    public void ReadBeta() => _cache.Set(MayDefaults.BetaKey, _db.Products.ToList());
}

/// <summary>The key object reached through a local assigned in two branches. The site removes one of the
/// two at run time, so it covers neither.</summary>
public sealed class ObjectBranchyWriter : ControllerBase
{
    private readonly IMayCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write(bool flag)
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        var key = MayDefaults.AlphaKey;
        if (flag) key = MayDefaults.BetaKey;
        _cache.Remove(key);
    }
}

/// <summary>The same branch written as one expression.</summary>
public sealed class ObjectConditionalWriter : ControllerBase
{
    private readonly IMayCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write(bool flag)
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        _cache.Remove(flag ? MayDefaults.AlphaKey : MayDefaults.BetaKey);
    }
}

/// <summary>One value, named certainly: the suppression this policy must leave alone.</summary>
public sealed class ObjectCertainWriter : ControllerBase
{
    private readonly IMayCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write()
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        _cache.Remove(MayDefaults.AlphaKey);
    }
}
