// Removal sites whose key folds to one value or to several, so that a rule can be asked which of them
// grants a suppression. See docs/adr/0015 and CONTEXT.md on a merge never granting a suppression that
// one site alone would not.
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace MaySetFixture;

public sealed class Product
{
    public int Id { get; set; }
}

public sealed class ShopContext : DbContext
{
    public DbSet<Product> Products { get; set; } = null!;
}

public sealed class ReaderController : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Read() => _cache.Set("product:one", _db.Products.ToList());

    public void ReadOther() => _cache.Set("product:two", _db.Products.ToList());

    /// <summary>A key with a hole, so that a removal folding to <c>product:{?}</c> covers it by skeleton
    /// and the over-cap case is a real suppression to withhold rather than a mismatch.</summary>
    public void ReadTemplated(int id) => _cache.Set($"product:{id}", _db.Products.ToList());
}

/// <summary>One value, named certainly: the suppression this policy must leave alone.</summary>
public sealed class CertainWriter : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write()
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        _cache.Remove("product:one");
    }
}

/// <summary>Two values: the site removes one of them at run time, so neither is covered.</summary>
public sealed class BranchyWriter : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write(bool flag)
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        _cache.Remove(flag ? "product:one" : "product:two");
    }
}

/// <summary>A local built by updating itself. The folder never sees the compound assignment, so it folds
/// to the initialiser alone — a value the code may never produce.</summary>
public sealed class CompoundWriter : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write(string suffix)
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        var key = "product:one";
        key += suffix;
        _cache.Remove(key);
    }
}

/// <summary>The other spelling of the same thing.</summary>
public sealed class SelfAssignWriter : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write(string suffix)
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        var key = "product:one";
        key = key + suffix;
        _cache.Remove(key);
    }
}

/// <summary>One element, built over a part that outgrew the fold's bound.</summary>
public sealed class OverCapWriter : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write(int left, int right)
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        // Nine values, one past the bound, so the part becomes a single unknown — and the template around
        // it becomes one literal-bearing value that is nonetheless a choice.
        var choice = (left == 0 ? "a" : left == 1 ? "b" : "c") + (right == 0 ? "1" : right == 1 ? "2" : "3");
        _cache.Remove($"product:{choice}");
    }
}

/// <summary>Some members nameable, one not.</summary>
public sealed class MixedWriter : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly ShopContext _db = null!;

    public void Write(bool flag, string runtime)
    {
        _db.Products.Update(new Product());
        _db.SaveChanges();
        _cache.Remove(flag ? "product:one" : runtime);
    }
}
