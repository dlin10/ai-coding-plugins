// Shaped like nopCommerce: a key object carrying a template, a defaults class of static properties
// holding constructions of it, and a key service whose factories substitute arguments into the holes.
using Microsoft.AspNetCore.Mvc;

namespace KeyObjectFixture;

public sealed class ObjectCacheKey
{
    public ObjectCacheKey(string key) => Key = key;

    public string Key { get; }
}

/// <summary>A key object of a type no recognizer describes.</summary>
public sealed class UndeclaredCacheKey
{
    public UndeclaredCacheKey(string key) => Key = key;

    public string Key { get; }
}

public interface IObjectKeyService
{
    ObjectCacheKey PrepareKey(ObjectCacheKey cacheKey, params object[] parameters);

    ObjectCacheKey PrepareKeyForDefaultCache(ObjectCacheKey cacheKey, params object[] parameters);
}

public interface IObjectCache : IObjectKeyService
{
    void Set(ObjectCacheKey key, object value);
}

public interface IUndeclaredCache
{
    void Set(UndeclaredCacheKey key, object value);
}

public static class FixtureDefaults
{
    public static ObjectCacheKey DirectKey => new ObjectCacheKey("fixture.direct");

    /// <summary>The escaped form nopCommerce's generic defaults class writes.</summary>
    public static ObjectCacheKey CategoriesAllKey => new ObjectCacheKey($"fixture.category.all.{{0}}-{{1}}-{{2}}");

    public static ObjectCacheKey PairKey => new ObjectCacheKey("fixture.pair.{0}-{1}");

    public static ObjectCacheKey RuntimeKey => new ObjectCacheKey(System.Guid.NewGuid().ToString());

    public static ObjectCacheKey BranchKey => new ObjectCacheKey("fixture.branch");
}

/// <summary>
/// nopCommerce's <c>NopEntityCacheDefaults&lt;TEntity&gt;</c> shape, and the one that matters: the template
/// interpolates the entity name, so the whole interpolation is <em>not</em> a compile-time constant. The
/// escaped hole therefore reaches the folder as interpolated-string syntax rather than as a decoded
/// constant value, which is a different code path from every other template in this fixture.
/// </summary>
public static class FixtureEntityDefaults
{
    public static string EntityTypeName => "category";

    public static ObjectCacheKey ByIdKey => new ObjectCacheKey($"fixture.{EntityTypeName}.byid.{{0}}");
}

public sealed class KeyObjectController : ControllerBase
{
    private readonly IObjectCache _cache = null!;
    private readonly IUndeclaredCache _undeclared = null!;

    /// <summary>A construction written at the call site.</summary>
    public void Direct() => _cache.Set(new ObjectCacheKey("fixture.inline"), 1);

    /// <summary>A defaults-class property, with no factory in between.</summary>
    public void FromDefaults() => _cache.Set(FixtureDefaults.DirectKey, 1);

    /// <summary>The nopCommerce shape: a factory substituting three arguments into three holes.</summary>
    public void Categories(int storeId, int[] roleIds, bool showHidden) =>
        _cache.Set(_cache.PrepareKeyForDefaultCache(FixtureDefaults.CategoriesAllKey, storeId, roleIds, showHidden), 1);

    /// <summary>Fewer arguments than holes: the holes with no argument are unknowable.</summary>
    public void FewerArguments(int storeId) =>
        _cache.Set(_cache.PrepareKey(FixtureDefaults.CategoriesAllKey, storeId), 1);

    /// <summary>More arguments than holes: the surplus is named nowhere.</summary>
    public void MoreArguments(int left, int right, int surplus) =>
        _cache.Set(_cache.PrepareKey(FixtureDefaults.PairKey, left, right, surplus), 1);

    /// <summary>A template that is not a literal, exactly as a dynamic string key is.</summary>
    public void Runtime() => _cache.Set(FixtureDefaults.RuntimeKey, 1);

    /// <summary>The key object reached through a local.</summary>
    public void ThroughLocal(int first, int second)
    {
        var key = FixtureDefaults.PairKey;
        _cache.Set(_cache.PrepareKey(key, first, second), 1);
    }

    /// <summary>A template whose interpolation is not constant, so its escaped hole never passes through
    /// the constant path that decodes one.</summary>
    public void EntityById(int entityId) =>
        _cache.Set(_cache.PrepareKey(FixtureEntityDefaults.ByIdKey, entityId), 1);

    /// <summary>The factory's result assigned to a local and the local passed on. This is nopCommerce's
    /// CategoryService shape, and the template and the factory are both in the compilation.</summary>
    public void FactoryThroughLocal(int left, int right)
    {
        var key = _cache.PrepareKey(FixtureDefaults.PairKey, left, right);
        _cache.Set(key, 1);
    }

    /// <summary>A local holding a factory result in one branch and a plain construction in the other: two
    /// shapes, one local, and the site may key on either.</summary>
    public void FactoryOrConstructionLocal(bool flag, int left, int right)
    {
        var key = FixtureDefaults.DirectKey;
        if (flag) key = _cache.PrepareKey(FixtureDefaults.PairKey, left, right);
        _cache.Set(key, 1);
    }

    /// <summary>A local assigned in two branches: the site may key on either, and must name both.</summary>
    public void BranchyLocal(bool flag)
    {
        var key = FixtureDefaults.DirectKey;
        if (flag) key = FixtureDefaults.BranchKey;
        _cache.Set(key, 1);
    }

    /// <summary>The same branch written as one expression, with the constructions inline.</summary>
    public void ConditionalKey(bool flag) =>
        _cache.Set(flag ? new ObjectCacheKey("fixture.when-true") : new ObjectCacheKey("fixture.when-false"), 1);

    /// <summary>An argument the compilation cannot name.</summary>
    public void Unnameable(int left) =>
        _cache.Set(_cache.PrepareKey(FixtureDefaults.PairKey, left, System.Guid.NewGuid()), 1);

    /// <summary>A cache whose key object no recognizer describes.</summary>
    public void Undeclared() => _undeclared.Set(new UndeclaredCacheKey("fixture.undeclared"), 1);
}
